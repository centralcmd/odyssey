using System.Net;
using Odyssey.Client.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A <c>401</c> on a domain call means the session is gone (issue #250); on the Identity and auth
/// surfaces it is an ordinary answer — <c>/login</c> returns it for <c>RequiresTwoFactor</c>, and
/// <c>manage/info</c> is the probe that learns nobody is signed in — and must not bounce the user.
/// </summary>
public class UnauthorizedHandlerTests
{
    [Theory]
    [InlineData("https://api.odyssey.test/", "api/accounts")]
    [InlineData("https://api.odyssey.test/", "api/user-preferences/accounts-page")]
    [InlineData("https://odyssey.test/api/", "api/accounts")] // Compose: same-origin /api/ proxy
    public async Task A401OnADomainCall_raisesTheNotifier(string baseAddress, string path)
    {
        var (raised, _) = await SendAsync(baseAddress, path, HttpStatusCode.Unauthorized);

        Assert.True(raised);
    }

    [Theory]
    [InlineData("https://api.odyssey.test/", "login?useCookies=true")]
    [InlineData("https://api.odyssey.test/", "manage/info")]
    [InlineData("https://api.odyssey.test/", "manage/2fa")]
    [InlineData("https://api.odyssey.test/", "auth/claims")]
    [InlineData("https://api.odyssey.test/", "logout")]
    [InlineData("https://odyssey.test/api/", "login?useCookies=true")]
    [InlineData("https://odyssey.test/api/", "manage/info")]
    public async Task A401OnTheAuthSurface_doesNotRaiseIt(string baseAddress, string path)
    {
        var (raised, _) = await SendAsync(baseAddress, path, HttpStatusCode.Unauthorized);

        Assert.False(raised);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherStatuses_doNotRaiseIt_andPassThroughUntouched(HttpStatusCode status)
    {
        var (raised, response) = await SendAsync("https://api.odyssey.test/", "api/accounts", status);

        Assert.False(raised);
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task A401OnAnotherOrigin_doesNotRaiseIt()
    {
        var (raised, _) = await SendAsync("https://api.odyssey.test/", "https://elsewhere.test/api/accounts", HttpStatusCode.Unauthorized);

        Assert.False(raised);
    }

    private static async Task<(bool Raised, HttpResponseMessage Response)> SendAsync(
        string baseAddress, string path, HttpStatusCode status)
    {
        var notifier = new SessionExpiredNotifier();
        var raised = false;
        notifier.SessionExpired += () => raised = true;
        using var client = new HttpClient(
            new UnauthorizedHandler(notifier, new Uri(baseAddress)) { InnerHandler = new StatusHandler(status) })
        {
            BaseAddress = new Uri(baseAddress),
        };

        var response = await client.GetAsync(path);
        return (raised, response);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
