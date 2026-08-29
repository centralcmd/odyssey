using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The per-recipient throttle seen from the endpoint (issue #393). The behaviour that matters here is
/// what a caller can observe: a skipped send must be invisible, because a <c>429</c> — or any other
/// difference — would tell an attacker that this address recently received mail, and therefore that
/// it is registered. That is exactly the enumeration leak <c>/forgotPassword</c> exists to avoid.
/// </summary>
public class MailRecipientThrottleApiTests
{
    private const string Registered = "registered@example.com";

    [Fact]
    public async Task ASkippedSend_StillAnswersWithTheNormalSuccessResponse()
    {
        var throttle = new RecordingThrottle { Allow = false };
        using var factory = CreateFactory(throttle);
        var client = factory.CreateClient();
        await SeedConfirmedUserAsync(factory);

        var response = await client.PostAsJsonAsync("/forgotPassword", new { email = Registered });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Registered], throttle.Recipients);
    }

    [Fact]
    public async Task AnUnregisteredAddress_AnswersIdenticallyAndNeverReachesTheThrottle()
    {
        // Identity short-circuits before the sender for an unknown address, so the throttle sees
        // nothing — the response must be indistinguishable from the throttled registered case above.
        var throttle = new RecordingThrottle { Allow = false };
        using var factory = CreateFactory(throttle);
        var client = factory.CreateClient();
        await SeedConfirmedUserAsync(factory);

        var registered = await client.PostAsJsonAsync("/forgotPassword", new { email = Registered });
        var unregistered = await client.PostAsJsonAsync("/forgotPassword", new { email = "nobody@example.com" });

        Assert.Equal(registered.StatusCode, unregistered.StatusCode);
        Assert.Equal(
            await registered.Content.ReadAsStringAsync(),
            await unregistered.Content.ReadAsStringAsync());
        Assert.Equal([Registered], throttle.Recipients);
    }

    private static OdysseyApiFactory CreateFactory(IEmailSendThrottle throttle) =>
        new(
            permissions: [],
            configuration: new Dictionary<string, string?>
            {
                // Without a host the sender short-circuits to its dev link-logging path before any
                // throttle decision, so the throttle would never be consulted.
                ["Email:SmtpHost"] = "smtp.invalid.test",
                ["Email:FromAddress"] = "no-reply@odyssey.test",
                // Generous per-IP limits so nothing here is throttled by the other layer.
                ["RateLimiting:Identity:PermitLimit"] = "1000",
                ["RateLimiting:IdentityEmail:PermitLimit"] = "1000",
            },
            configureServices: services =>
            {
                services.RemoveAll<IEmailSendThrottle>();
                services.AddSingleton(throttle);
            });

    private static async Task SeedConfirmedUserAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.Users.Add(new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = Registered,
            NormalizedUserName = Registered.ToUpperInvariant(),
            Email = Registered,
            NormalizedEmail = Registered.ToUpperInvariant(),
            EmailConfirmed = true,
        });
        await context.SaveChangesAsync();
    }

    private sealed class RecordingThrottle : IEmailSendThrottle
    {
        public bool Allow { get; init; }

        public List<string> Recipients { get; } = [];

        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey)
        {
            Recipients.Add(emailAddress);
            return Allow;
        }
    }
}
