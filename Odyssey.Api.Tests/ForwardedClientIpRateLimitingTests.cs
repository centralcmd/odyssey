using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The production path is Caddy → client nginx → api, so the API receives
/// <c>X-Forwarded-For: &lt;client&gt;, &lt;caddy&gt;</c> from nginx's container address (issue #244).
/// With the framework's default <c>ForwardLimit</c> of 1 only Caddy's entry was consumed, every
/// request partitioned on Caddy's IP, and one anonymous caller could lock the whole internet out of
/// sign-in. These tests drive the identity limiter through that two-hop shape and assert the
/// partition is the client.
///
/// <para>
/// <c>TestServer</c> leaves <c>RemoteIpAddress</c> null, which the forwarded-headers middleware treats
/// as an untrusted peer, so a startup filter stamps the transport peer ahead of the app's pipeline.
/// </para>
/// </summary>
public class ForwardedClientIpRateLimitingTests
{
    private const string NginxAddress = "172.18.0.4";
    private const string CaddyAddress = "172.18.0.3";

    private static WebApplicationFactory<Program> FactoryWithPeer(
        OdysseyApiFactory factory, string peerAddress) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new PeerAddressStartupFilter(IPAddress.Parse(peerAddress)))));

    private static OdysseyApiFactory FactoryWithLimit(int permitLimit) =>
        new(permissions: [], configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:Identity:PermitLimit"] = permitLimit.ToString(),
            ["RateLimiting:Identity:WindowSeconds"] = "60",
        });

    private static Task<HttpResponseMessage> AttemptLoginAsync(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = JsonContent.Create(new { email = "nobody@example.com", password = "wrong-password" }),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task BehindCaddyAndNginx_EachClientGetsItsOwnPartition()
    {
        using var baseFactory = FactoryWithLimit(permitLimit: 2);
        using var factory = FactoryWithPeer(baseFactory, NginxAddress);
        var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var allowed = await AttemptLoginAsync(client, $"203.0.113.10, {CaddyAddress}");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var throttled = await AttemptLoginAsync(client, $"203.0.113.10, {CaddyAddress}");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // The regression: under ForwardLimit = 1 both clients partitioned on Caddy's address, so the
        // first client's exhausted window refused the second one too.
        var otherClient = await AttemptLoginAsync(client, $"203.0.113.11, {CaddyAddress}");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherClient.StatusCode);
    }

    [Fact]
    public async Task AnEntryBeyondTheTwoProxyHops_IsNotHonoured()
    {
        // A leftmost entry the client wrote itself must not mint a fresh partition per request. The
        // client here is on a private (LAN) address, which the walk trusts, so only the hop count stops
        // it reaching the spoofed entry. Caddy overwrites an inbound header, so this shape only arises
        // if it stops doing so — the limit is what keeps the walk from following it anyway.
        using var baseFactory = FactoryWithLimit(permitLimit: 2);
        using var factory = FactoryWithPeer(baseFactory, NginxAddress);
        var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var allowed = await AttemptLoginAsync(client, $"198.51.100.{attempt}, 10.9.9.9, {CaddyAddress}");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var throttled = await AttemptLoginAsync(client, $"198.51.100.3, 10.9.9.9, {CaddyAddress}");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task ADirectlyConnectingPublicPeer_CannotSpoofItsPartition()
    {
        using var baseFactory = FactoryWithLimit(permitLimit: 2);
        using var factory = FactoryWithPeer(baseFactory, "198.51.100.7");
        var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var allowed = await AttemptLoginAsync(client, $"203.0.113.{attempt}, 172.18.0.{attempt}");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var throttled = await AttemptLoginAsync(client, "203.0.113.3, 172.18.0.3");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task ForwardLimit_CanBeOverriddenByConfiguration()
    {
        // A deployment with one proxy hop (nginx exposed directly) sets the limit back to 1, after
        // which the rightmost entry is the partition and the one before it is ignored. The rightmost
        // entry is a TRUSTED address on purpose: under the default of 2 the walk would continue past
        // it to the varying client entry and nothing would throttle, so this fails if the override
        // is not read.
        using var baseFactory = new OdysseyApiFactory(permissions: [], configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:Identity:PermitLimit"] = "2",
            ["RateLimiting:Identity:WindowSeconds"] = "60",
            ["ForwardedHeaders:ForwardLimit"] = "1",
        });
        using var factory = FactoryWithPeer(baseFactory, NginxAddress);
        var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var allowed = await AttemptLoginAsync(client, $"203.0.113.{attempt}, {CaddyAddress}");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var throttled = await AttemptLoginAsync(client, $"203.0.113.3, {CaddyAddress}");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ANonPositiveForwardLimit_FailsStartup(string value)
    {
        using var factory = new OdysseyApiFactory(permissions: [], configuration: new Dictionary<string, string?>
        {
            ["ForwardedHeaders:ForwardLimit"] = value,
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ForwardedHeaders:ForwardLimit", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class PeerAddressStartupFilter(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = peer;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
