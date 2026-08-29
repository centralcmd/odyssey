using System.Net;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="LegalComplianceHandler"/> is the only thing that surfaces a <em>mid-session</em>
/// compliance flip (issue #354 §5, AC 21): <c>MainLayout</c>'s gate resolves the claim once per full
/// page load and never re-fires on in-app navigation. These pin the three properties that make it work
/// — it triggers on the status and nothing else, it preserves where the user was, and it cannot send
/// the interstitial to itself.
/// </summary>
public class LegalComplianceHandlerTests
{
    private const string Base = "https://api.odyssey.test/";

    [Fact]
    public async Task A451_redirectsToTheInterstitialCarryingTheCurrentUriAsReturnUrl()
    {
        var navigation = new FakeNavigationManager("https://app.odyssey.test/", "transactions?status=open");
        using var client = ClientFor(navigation, HttpStatusCode.UnavailableForLegalReasons);

        await client.GetAsync("api/transactions");

        Assert.Equal(
            "/accept-terms?returnUrl=%2Ftransactions%3Fstatus%3Dopen",
            navigation.NavigatedTo);
    }

    /// <summary>
    /// The redirect must fire for any client, not just whatever the page happened to call — a
    /// background writer like PageStateService's debounced save is the likeliest thing to meet the
    /// status first, and it has no UI of its own to react.
    /// </summary>
    [Fact]
    public async Task A451_fromAnyRequest_triggersTheRedirect()
    {
        var navigation = new FakeNavigationManager("https://app.odyssey.test/", "budgets");
        using var client = ClientFor(navigation, HttpStatusCode.UnavailableForLegalReasons);

        await client.PutAsync("api/preferences/budgets-page", content: null);

        Assert.Equal("/accept-terms?returnUrl=%2Fbudgets", navigation.NavigatedTo);
    }

    /// <summary>
    /// Its own guard, not a reliance on the server's allowlist happening to cover every call the
    /// interstitial makes: without this, a 451 from that page would redirect it to itself and spin.
    /// </summary>
    [Theory]
    [InlineData("accept-terms")]
    [InlineData("accept-terms?returnUrl=%2Faccounts")]
    [InlineData("Accept-Terms")]
    public async Task A451_onTheInterstitialItself_doesNotRedirect(string relativePath)
    {
        var navigation = new FakeNavigationManager("https://app.odyssey.test/", relativePath);
        using var client = ClientFor(navigation, HttpStatusCode.UnavailableForLegalReasons);

        await client.GetAsync("api/legal/status");

        Assert.Null(navigation.NavigatedTo);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyOtherStatus_isLeftAlone(HttpStatusCode status)
    {
        var navigation = new FakeNavigationManager("https://app.odyssey.test/", "accounts");
        using var client = ClientFor(navigation, status);

        var response = await client.GetAsync("api/accounts");

        Assert.Null(navigation.NavigatedTo);
        Assert.Equal(status, response.StatusCode);
    }

    /// <summary>The response still reaches the caller, so each client's own error handling is unaffected.</summary>
    [Fact]
    public async Task The451_isStillReturnedToTheCaller()
    {
        var navigation = new FakeNavigationManager("https://app.odyssey.test/", "accounts");
        using var client = ClientFor(navigation, HttpStatusCode.UnavailableForLegalReasons);

        var response = await client.GetAsync("api/accounts");

        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, response.StatusCode);
    }

    private static HttpClient ClientFor(NavigationManager navigation, HttpStatusCode status) =>
        new(new LegalComplianceHandler(navigation) { InnerHandler = new StubHandler(status) })
        {
            BaseAddress = new Uri(Base),
        };

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class FakeNavigationManager : NavigationManager
    {
        public FakeNavigationManager(string baseUri, string relativePath) => Initialize(baseUri, baseUri + relativePath);

        public string? NavigatedTo { get; private set; }

        protected override void NavigateToCore(string uri, bool forceLoad) => NavigatedTo = uri;
    }
}
