using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Moq;
using Odyssey.ApiClient.Auth;
using Odyssey.Client.Auth;
using Odyssey.Client.Pages.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The shell's answer to a mid-session <c>401</c> (issue #250): ask, then reload to the sign-in page
/// carrying the route and the expiry reason. Asking first is the accessibility half (WCAG 2.2.1,
/// 3.3.4) — a silent reload would discard unsaved input with no warning.
/// </summary>
public class SessionExpiryRedirectTests
{
    private const string Route = "/accounts";

    [Fact]
    public async Task ConfirmingThePrompt_ReloadsToSignIn_WithTheRouteAndTheReason()
    {
        await using var ctx = NewContext(authenticated: true);
        var dialogs = ctx.Render<MudDialogProvider>();
        ctx.Render<SessionExpiryRedirect>();

        Signal(ctx);
        dialogs.WaitForAssertion(() => Assert.Contains("Your session has expired", dialogs.Markup));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Sign in").Click();

        var entry = Navigation(ctx).History.First();
        Assert.Equal($"/login?returnUrl={Uri.EscapeDataString(Route)}&reason={Login.SessionExpiredReason}", entry.Uri);
        Assert.True(entry.Options.ForceLoad);
    }

    [Fact]
    public async Task StayingOnThePage_DoesNotNavigate_AndALaterFailureAsksAgain()
    {
        await using var ctx = NewContext(authenticated: true);
        var dialogs = ctx.Render<MudDialogProvider>();
        ctx.Render<SessionExpiryRedirect>();
        var before = Navigation(ctx).History.Count;

        Signal(ctx);
        dialogs.WaitForAssertion(() => Assert.Contains("Your session has expired", dialogs.Markup));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Stay on this page").Click();
        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Your session has expired", dialogs.Markup));

        Signal(ctx);
        dialogs.WaitForAssertion(() => Assert.Contains("Your session has expired", dialogs.Markup));
        Assert.Equal(before, Navigation(ctx).History.Count);
    }

    [Fact]
    public async Task RepeatedFailures_InOnePass_ShowOnePrompt()
    {
        await using var ctx = NewContext(authenticated: true);
        var dialogs = ctx.Render<MudDialogProvider>();
        ctx.Render<SessionExpiryRedirect>();

        Signal(ctx);
        Signal(ctx);
        Signal(ctx);

        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindAll(".mud-dialog")));
    }

    [Fact]
    public async Task AnAnonymousVisitor_IsNeverPromptedOrMoved()
    {
        // The guard that keeps the sign-in page from reloading itself in a loop.
        await using var ctx = NewContext(authenticated: false);
        var dialogs = ctx.Render<MudDialogProvider>();
        ctx.Render<SessionExpiryRedirect>();
        var before = Navigation(ctx).History.Count;

        Signal(ctx);

        Assert.DoesNotContain("Your session has expired", dialogs.Markup);
        Assert.Equal(before, Navigation(ctx).History.Count);
    }

    [Fact]
    public async Task OnceDisposed_ASignalIsIgnored()
    {
        await using var ctx = NewContext(authenticated: true);
        var dialogs = ctx.Render<MudDialogProvider>();
        var redirect = ctx.Render<SessionExpiryRedirect>();

        redirect.Instance.Dispose();
        Signal(ctx);

        Assert.DoesNotContain("Your session has expired", dialogs.Markup);
    }

    [Fact]
    public async Task APromptThatCannotBeShown_StillSendsTheUserToSignIn()
    {
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(d => d.ShowMessageBoxAsync(It.IsAny<MessageBoxOptions>(), It.IsAny<DialogOptions>()))
            .ThrowsAsync(new InvalidOperationException("no dialog provider"));
        await using var ctx = NewContext(authenticated: true, dialogs.Object);
        var redirect = ctx.Render<SessionExpiryRedirect>();

        Signal(ctx);

        redirect.WaitForAssertion(() =>
        {
            var entry = Navigation(ctx).History.First();
            Assert.StartsWith("/login?", entry.Uri);
            Assert.True(entry.Options.ForceLoad);
        });
    }

    /// <summary>
    /// Both sign-out paths rely on the reload, not a refresh, to drop the cached auth state and every
    /// app-lifetime cache with it.
    /// </summary>
    [Theory]
    [InlineData("Pages/Account.razor.cs")]
    [InlineData("Pages/ChangePasswordRequired.razor.cs")]
    public void SignOut_NavigatesWithAFullReload(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, relativePath));

        var signOut = System.Text.RegularExpressions.Regex.Match(
            source, @"await AuthApiClient\.LogoutAsync\(\);\s*(?<next>[^;]*;)");
        Assert.True(signOut.Success, $"no LogoutAsync call in {relativePath}");
        Assert.Equal("NavigationManager.NavigateTo(\"/login\", forceLoad: true);", signOut.Groups["next"].Value);
    }

    [Fact]
    public async Task TheSignInPage_ExplainsWhyTheUserIsThere()
    {
        await using var ctx = NewContext(authenticated: false);
        Navigation(ctx).NavigateTo(Login.SignInUrlFor("accounts", Login.SessionExpiredReason));

        var page = ctx.Render<Login>();

        var notice = page.Find("[role=status]");
        Assert.Contains("Your session has expired", notice.TextContent);
    }

    [Fact]
    public async Task TheSignInPage_ShowsNoNotice_WithoutTheReason()
    {
        await using var ctx = NewContext(authenticated: false);
        Navigation(ctx).NavigateTo(Login.SignInUrlFor("accounts"));

        var page = ctx.Render<Login>();

        Assert.DoesNotContain("Your session has expired", page.Markup);
    }

    private static void Signal(BunitContext ctx) =>
        ctx.Services.GetRequiredService<SessionExpiredNotifier>().NotifySessionExpired();

    private static BunitNavigationManager Navigation(BunitContext ctx) =>
        (BunitNavigationManager)ctx.Services.GetRequiredService<NavigationManager>();

    private static BunitContext NewContext(bool authenticated, IDialogService? dialogs = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var auth = ctx.AddAuthorization();
        if (authenticated)
        {
            auth.SetAuthorized("signed-in-user");
        }

        ctx.Services.AddSingleton<SessionExpiredNotifier>();
        if (dialogs is not null)
        {
            ctx.Services.AddSingleton(dialogs);
        }

        // Never called: the sign-in page only needs it to resolve.
        ctx.Services.AddSingleton(sp => new AuthApiClient(new HttpClient(), new AntiforgeryTokenStore(sp)));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(Route);
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }
}
