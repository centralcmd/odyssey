using System.Net.Http.Headers;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Auth;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// <see cref="AuthApiClient.GetSessionAsync"/> must tell "signed out" from "could not find out"
/// (issue #250). Before, a network blip read as signed out and a failed claims fetch read as signed in
/// with zero permissions, so every page silently hid everything.
/// </summary>
public class AuthApiClientSessionTests
{
    private const string ClaimsJson = """[{"type":"permission","value":"accounts.read"},{"type":"sub","value":"u1"}]""";

    [Fact]
    public async Task BothProbesSucceed_IsAuthenticatedWithTheClaims()
    {
        var session = await SessionFor(Respond(HttpStatusCode.OK), Respond(HttpStatusCode.OK, ClaimsJson));

        Assert.Equal(AuthSessionStatus.Authenticated, session.Status);
        Assert.Equal(["permission=accounts.read", "sub=u1"], session.Claims.Select(c => $"{c.Type}={c.Value}"));
    }

    [Fact]
    public async Task InfoAnswers401_IsAnonymous_WithoutAskingForClaims()
    {
        var handler = new SessionHandler(Respond(HttpStatusCode.Unauthorized), Respond(HttpStatusCode.OK, ClaimsJson));

        var session = await Client(handler).GetSessionAsync();

        Assert.Equal(AuthSessionStatus.Anonymous, session.Status);
        Assert.Equal(0, handler.ClaimsCalls);
    }

    [Fact]
    public async Task ClaimsAnswers401_IsAnonymous()
    {
        var session = await SessionFor(Respond(HttpStatusCode.OK), Respond(HttpStatusCode.Unauthorized));

        Assert.Equal(AuthSessionStatus.Anonymous, session.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task InfoFaults_AreUnavailable_NotAnonymous(HttpStatusCode status)
    {
        var session = await SessionFor(Respond(status), Respond(HttpStatusCode.OK, ClaimsJson));

        Assert.Equal(AuthSessionStatus.Unavailable, session.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ClaimsFaults_AreUnavailable_NotZeroPermissions(HttpStatusCode status)
    {
        var session = await SessionFor(Respond(HttpStatusCode.OK), Respond(status));

        Assert.Equal(AuthSessionStatus.Unavailable, session.Status);
        Assert.Empty(session.Claims);
    }

    [Fact]
    public async Task ARetryAfterDelta_IsCarriedOnTheUnavailableSession()
    {
        var session = await SessionFor(
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42)) },
            },
            Respond(HttpStatusCode.OK, ClaimsJson));

        Assert.Equal(AuthSessionStatus.Unavailable, session.Status);
        Assert.Equal(TimeSpan.FromSeconds(42), session.RetryAfter);
    }

    [Fact]
    public async Task ARetryAfterDate_IsReadAsTheWaitFromNow()
    {
        var session = await SessionFor(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2)) },
            },
            Respond(HttpStatusCode.OK, ClaimsJson));

        Assert.InRange(session.RetryAfter!.Value, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(121));
    }

    [Fact]
    public async Task AForbiddenProbe_IsUnavailable_NotAnonymous()
    {
        // Only a 401 means signed out; a 403 (a gate, a proxy) says nothing definitive about the session.
        var info = await SessionFor(Respond(HttpStatusCode.Forbidden), Respond(HttpStatusCode.OK, ClaimsJson));
        var claims = await SessionFor(Respond(HttpStatusCode.OK), Respond(HttpStatusCode.Forbidden));

        Assert.Equal(AuthSessionStatus.Unavailable, info.Status);
        Assert.Equal(AuthSessionStatus.Unavailable, claims.Status);
    }

    [Fact]
    public async Task A401_IsAnonymous_EvenWithARetryAfter()
    {
        var session = await SessionFor(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) },
            },
            Respond(HttpStatusCode.OK, ClaimsJson));

        Assert.Equal(AuthSessionStatus.Anonymous, session.Status);
        Assert.Null(session.RetryAfter);
    }

    [Fact]
    public async Task NoRetryAfter_LeavesItNull()
    {
        var session = await SessionFor(Respond(HttpStatusCode.TooManyRequests), Respond(HttpStatusCode.OK));

        Assert.Null(session.RetryAfter);
    }

    [Fact]
    public async Task ClaimsBodyThatIsNotJson_IsUnavailable()
    {
        // The Release-under-Aspire shape: a same-origin SPA fallback answering index.html with a 200.
        var session = await SessionFor(Respond(HttpStatusCode.OK), Respond(HttpStatusCode.OK, "<!DOCTYPE html>"));

        Assert.Equal(AuthSessionStatus.Unavailable, session.Status);
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable_NotAnonymous()
    {
        var session = await SessionFor(_ => throw new HttpRequestException("offline"), Respond(HttpStatusCode.OK));

        Assert.Equal(AuthSessionStatus.Unavailable, session.Status);
    }

    [Fact]
    public async Task ATimeout_TheCallerDidNotAskFor_IsUnavailable()
    {
        // HttpClient.Timeout surfaces as a TaskCanceledException whose token the caller never cancelled.
        var session = await SessionFor(_ => throw new TaskCanceledException("timed out"), Respond(HttpStatusCode.OK));

        Assert.Equal(AuthSessionStatus.Unavailable, session.Status);
    }

    [Fact]
    public async Task CallerCancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Client(new SessionHandler(Respond(HttpStatusCode.OK), Respond(HttpStatusCode.OK, ClaimsJson)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSessionAsync(cts.Token));
    }

    private static Task<AuthSession> SessionFor(
        Func<HttpRequestMessage, HttpResponseMessage> info, Func<HttpRequestMessage, HttpResponseMessage> claims) =>
        Client(new SessionHandler(info, claims)).GetSessionAsync();

    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(HttpStatusCode status, string? body = null) =>
        _ => new HttpResponseMessage(status)
        {
            Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static AuthApiClient Client(SessionHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.odyssey.test/") },
            new AntiforgeryTokenStore(new ServiceCollection().BuildServiceProvider()));

    private sealed class SessionHandler(
        Func<HttpRequestMessage, HttpResponseMessage> info,
        Func<HttpRequestMessage, HttpResponseMessage> claims) : HttpMessageHandler
    {
        public int ClaimsCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/manage/info")
            {
                return Task.FromResult(info(request));
            }

            if (path == "/auth/claims")
            {
                ClaimsCalls++;
                return Task.FromResult(claims(request));
            }

            throw new InvalidOperationException($"Unexpected request {path}");
        }
    }
}
