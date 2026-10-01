using System.Net;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.ApiClient.Auth;
using Odyssey.Client.Auth;
using Odyssey.Client.Pages.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// What the router renders for a page the caller is not authorized for (issue #278). A session probe with
/// no definitive answer must show the retry panel — a signed-in user's cookie is still valid, and sending
/// them to <c>/login</c> during an outage (or a <c>429</c>) is the bug. A genuine <c>401</c> still redirects.
/// </summary>
public class NotAuthorizedFallbackTests
{
    private const string Route = "/accounts";

    [Fact]
    public async Task AnUnavailableSession_ShowsTheRetryPanel_AndDoesNotNavigate()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.ServiceUnavailable });
        var before = Navigation(ctx).History.Count;

        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, SessionUnavailable.Principal));

        Assert.Contains("Unable to reach Odyssey", cut.Markup);
        Assert.NotNull(cut.Find("[role=alert]"));
        Assert.False(Retry(cut).HasAttribute("disabled"));
        Assert.Equal(before, Navigation(ctx).History.Count);
    }

    [Fact]
    public async Task AGenuineSignOut_StillRedirectsToSignIn()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.Unauthorized });
        var anonymous = (await ctx.Services.GetRequiredService<AuthenticationStateProvider>()
            .GetAuthenticationStateAsync()).User;

        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, anonymous));

        Assert.DoesNotContain("Unable to reach Odyssey", cut.Markup);
        cut.WaitForAssertion(() => Assert.Equal(
            Login.SignInUrlFor(Route.TrimStart('/')), Navigation(ctx).History.First().Uri));
    }

    [Fact]
    public async Task Retry_WhenTheServerAnswers_AnnouncesTheSession()
    {
        var api = new SessionApi { Info = HttpStatusCode.ServiceUnavailable };
        await using var ctx = NewContext(api);
        var provider = ctx.Services.GetRequiredService<AuthenticationStateProvider>();
        var announced = new TaskCompletionSource<AuthenticationState>();
        provider.AuthenticationStateChanged += async task => announced.TrySetResult(await task);
        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, SessionUnavailable.Principal));

        api.Info = HttpStatusCode.OK;
        await Retry(cut).ClickAsync(new());

        var state = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Retry_WhileStillDown_AnnouncesEveryAttempt_AndStaysPut()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.ServiceUnavailable });
        var before = Navigation(ctx).History.Count;
        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, SessionUnavailable.Principal));

        await Retry(cut).ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Contains("(attempt 1)", cut.Find("[role=status]").TextContent));

        // A second failure must still change the live region's text, or nothing is announced.
        await Retry(cut).ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Contains("(attempt 2)", cut.Find("[role=status]").TextContent));

        Assert.Equal(before, Navigation(ctx).History.Count);
    }

    [Fact]
    public async Task ThePanel_IsTheHeadedPage_AndRetryNeverDisablesItself()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.ServiceUnavailable });
        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, SessionUnavailable.Principal));

        // FocusOnNavigate targets h1, and the heading names the page (WCAG 2.4.2, 1.3.1).
        var heading = cut.Find("h1");
        Assert.Equal("Unable to reach Odyssey", heading.TextContent.Trim());
        Assert.Equal("-1", heading.GetAttribute("tabindex"));

        // A disabled button drops keyboard focus mid-retry (WCAG 2.4.3).
        await Retry(cut).ClickAsync(new());
        Assert.False(Retry(cut).HasAttribute("disabled"));
    }

    [Fact]
    public async Task RedirectToAuthorizedFallback_OnItsOwn_NeverNavigatesOnAnUnavailableAnswer()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.ServiceUnavailable });
        var before = Navigation(ctx).History.Count;

        ctx.Render<RedirectToAuthorizedFallback>();

        Assert.Equal(before, Navigation(ctx).History.Count);
    }

    [Fact]
    public async Task AnAuthenticatedUserWithoutTheClaim_GoesToNotAuthorized()
    {
        await using var ctx = NewContext(new SessionApi { Info = HttpStatusCode.OK });
        var user = (await ctx.Services.GetRequiredService<AuthenticationStateProvider>()
            .GetAuthenticationStateAsync()).User;

        var cut = ctx.Render<NotAuthorizedFallback>(p => p.Add(c => c.User, user));

        cut.WaitForAssertion(() => Assert.Equal("/not-authorized", Navigation(ctx).History.First().Uri));
    }

    [Fact]
    public void TheRouter_HandsItsAuthenticationStateToTheFallback()
    {
        var app = File.ReadAllText(Path.Combine(ClientSource.Root, "App.razor"));

        Assert.Contains("""<NotAuthorized Context="authState">""", app, StringComparison.Ordinal);
        Assert.Contains("""<NotAuthorizedFallback User="@authState.User" />""", app, StringComparison.Ordinal);
        Assert.DoesNotContain("<RedirectToAuthorizedFallback", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>AuthorizeRouteView</c> renders a page's <c>NotAuthorized</c> content as <c>MainLayout</c>'s body, and
    /// the layout's own <c>AuthorizeView</c> is evaluated first. An unconditional redirect there pre-empted the
    /// router's retry panel, so an API outage sent a signed-in user to <c>/login</c> (found by live testing):
    /// the shell must hand its state to the same fallback.
    /// </summary>
    [Fact]
    public void TheShell_HandsItsAuthenticationStateToTheFallback()
    {
        var layout = File.ReadAllText(Path.Combine(ClientSource.Root, "Layout", "MainLayout.razor"));

        Assert.Contains("""<NotAuthorized Context="authState">""", layout, StringComparison.Ordinal);
        Assert.Contains("""<NotAuthorizedFallback User="@authState.User" />""", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<RedirectToLogin", layout, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(ClientSource.Root, "Pages", "Auth", "RedirectToLogin.razor")),
            "RedirectToLogin navigates on a non-definitive answer; use NotAuthorizedFallback instead.");
    }

    /// <summary>
    /// The shell's re-run after a recovered session (issue #278) sits behind <c>OperatingSystem.IsBrowser()</c>,
    /// which bUnit cannot satisfy, so the ordering that keeps the app body from rendering behind a gate is
    /// pinned in source: the gate branch must reset <c>_gateChecked</c> before redirecting, because the
    /// unavailable pass already set it true.
    /// </summary>
    [Fact]
    public void MainLayout_ResetsTheGateFlag_BeforeRedirectingToAGate_OnARecoveredSession()
    {
        var layout = File.ReadAllText(Path.Combine(ClientSource.Root, "Layout", "MainLayout.razor.cs"));
        var branch = layout.IndexOf("FirstRunGateChain.Owed(profile, _user) is { } gatePath", StringComparison.Ordinal);
        var reset = layout.IndexOf("_gateChecked = false;", branch, StringComparison.Ordinal);
        var redirect = layout.IndexOf("RedirectToGate(gatePath);", branch, StringComparison.Ordinal);

        Assert.True(branch >= 0 && reset > branch && reset < redirect,
            "The gate branch of ResolveShellAsync must set _gateChecked = false before RedirectToGate.");
        Assert.Contains("SessionUnavailable.Is(_user)", layout, StringComparison.Ordinal);
        Assert.Contains("AuthenticationStateChanged += OnAuthenticationStateChanged", layout, StringComparison.Ordinal);
    }

    private static AngleSharp.Dom.IElement Retry(IRenderedComponent<NotAuthorizedFallback> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Retry"));

    private static BunitNavigationManager Navigation(BunitContext ctx) =>
        (BunitNavigationManager)ctx.Services.GetRequiredService<NavigationManager>();

    private static BunitContext NewContext(SessionApi api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AuthenticationStateProvider>(sp => new CookieAuthenticationStateProvider(
            new AuthApiClient(
                new HttpClient(api) { BaseAddress = new Uri("https://api.odyssey.test/") },
                new AntiforgeryTokenStore(sp)),
            [TimeSpan.Zero],
            _ => new TaskCompletionSource().Task));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(Route);
        return ctx;
    }

    private sealed class SessionApi : HttpMessageHandler
    {
        public HttpStatusCode Info { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            return request.RequestUri!.AbsolutePath switch
            {
                "/manage/info" => new HttpResponseMessage(Info),
                "/auth/claims" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """[{"type":"permission","value":"accounts.read"}]""", Encoding.UTF8, "application/json"),
                },
                _ => throw new InvalidOperationException(request.RequestUri.ToString()),
            };
        }
    }
}
