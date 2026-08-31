using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Auth;
using Odyssey.Client.Pages;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The interstitial's yield to the forced-reset gate.
/// </summary>
/// <remarks>
/// <para>
/// A user who owes <b>both</b> a password change and an acceptance is refused with <c>403</c> on
/// <c>GET /api/legal/status</c> — the password gate outranks this one server-side and that endpoint is
/// deliberately not in <c>PasswordChangeExemptRoutes.Expected</c>. <c>PasswordChangeRequiredHandler</c>
/// turns that into a <c>PasswordChangeRequiredNotifier</c> signal, but <c>MainLayout</c> — the only
/// other subscriber — is not mounted while this page renders under its own nav-less
/// <c>AcceptTermsLayout</c>, so before this the refusal went nowhere and the page dead-ended.
/// </para>
/// <para>
/// The re-entrancy guard is the load-bearing half and is why this is worth a component test rather
/// than an assertion about one call: the notifier fires once per refused request and several can fail
/// in a single render pass, so an unguarded handler pushes a history entry per failure and buries the
/// user's own back button. Removing <c>_yieldedToPasswordGate</c> turns the second assertion here from
/// one navigation into three.
/// </para>
/// </remarks>
public class AcceptTermsPasswordGateYieldTests
{
    /// <summary>Where the interstitial was going to send the user, and so what the gate must carry on.</summary>
    private const string Destination = "/accounts";

    [Fact]
    public async Task ARefusedRequest_YieldsToThePasswordGate_CarryingTheReturnUrl()
    {
        await using var ctx = NewContext();
        ctx.Render<AcceptTerms>();

        ctx.Services.GetRequiredService<PasswordChangeRequiredNotifier>().NotifyPasswordChangeRequired();

        var navigation = (BunitNavigationManager)ctx.Services.GetRequiredService<NavigationManager>();
        Assert.Equal(
            $"{PasswordChangeRequiredHandler.GatePath}?returnUrl={Uri.EscapeDataString(Destination)}",
            "/" + navigation.ToBaseRelativePath(navigation.Uri));
    }

    /// <summary>
    /// Several calls can be refused in one render pass. One navigation, not one per refusal.
    /// </summary>
    [Fact]
    public async Task RepeatedRefusals_NavigateOnce()
    {
        await using var ctx = NewContext();
        ctx.Render<AcceptTerms>();

        var notifier = ctx.Services.GetRequiredService<PasswordChangeRequiredNotifier>();
        var navigation = (BunitNavigationManager)ctx.Services.GetRequiredService<NavigationManager>();
        var before = navigation.History.Count;

        notifier.NotifyPasswordChangeRequired();
        notifier.NotifyPasswordChangeRequired();
        notifier.NotifyPasswordChangeRequired();

        Assert.Equal(1, navigation.History.Count - before);
    }

    /// <summary>
    /// The subscription is released with the component. A notifier is a singleton and outlives every
    /// page that listens to it, so a leaked handler would navigate from a disposed component.
    /// </summary>
    [Fact]
    public async Task OnceDisposed_ARefusalNoLongerNavigates()
    {
        await using var ctx = NewContext();
        var page = ctx.Render<AcceptTerms>();

        var notifier = ctx.Services.GetRequiredService<PasswordChangeRequiredNotifier>();
        var navigation = (BunitNavigationManager)ctx.Services.GetRequiredService<NavigationManager>();

        await page.Instance.DisposeAsync();
        var before = navigation.History.Count;

        notifier.NotifyPasswordChangeRequired();

        Assert.Equal(before, navigation.History.Count);
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();

        // Loose: the page's own interop (the end-of-document IntersectionObserver) is behind an
        // OperatingSystem.IsBrowser() guard and never runs here; what remains is MudBlazor's own
        // initialisation, which no assertion reads.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.AddAuthorization().SetAuthorized("gated-user");

        // Never called: OnInitializedAsync subscribes to the notifier BEFORE its IsBrowser() guard, and
        // LoadAsync sits after it. That ordering is what makes this component testable at all — the
        // stub only has to satisfy DI.
        ctx.Services.AddSingleton(new Mock<ILegalApiClient>(MockBehavior.Strict).Object);
        ctx.Services.AddSingleton<PasswordChangeRequiredNotifier>();

        ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"{LegalComplianceHandler.InterstitialPath}?returnUrl={Uri.EscapeDataString(Destination)}");

        // MudBlazor refuses to initialise an overlay without a provider hosted in the tree; the app's
        // layout supplies one. Hosted once per context — a second subscriber to its outlet throws.
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }
}
