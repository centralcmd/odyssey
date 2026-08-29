using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Auth;
using Odyssey.Client.Authorization;
using Odyssey.Client.Pages;

namespace Odyssey.Client.Layout;

public partial class MainLayout
{
    private string _current = "";
    private bool _switcherOpen;
    private bool _paletteOpen;
    private bool _isDark;

    // Onboarding gate (issue #316 §5): the app body renders only once completeness is resolved.
    private bool _gateChecked;

    private System.Security.Claims.ClaimsPrincipal? _user;
    private Func<NavPage, bool> _canView = p => p.Claim is null;

    private NavRail? _navRail;

    // On close (Escape / click-away), focus returns to the control that opened the overlay.
    private enum FocusTarget { None, Search, Chip }
    private FocusTarget _pendingFocus;

    private DotNetObjectReference<MainLayout>? _selfRef;
    private IJSObjectReference? _jsModule;

    protected override async Task OnInitializedAsync()
    {
        _current = NavModel.NormalizePath(NavigationManager.ToBaseRelativePath(NavigationManager.Uri));
        NavigationManager.LocationChanged += OnLocationChanged;
        UserPreferences.DarkModeChanged += OnDarkModeChanged;
        // A mid-session forced reset (an admin triggers one while the user is working) never reaches the
        // check below — that runs once per full page load. PasswordChangeRequiredHandler sees the 403 on
        // whatever call fails first and raises this instead (issue #406 §7).
        PasswordChangeRequired.PasswordChangeRequired += OnPasswordChangeRequired;

        if (!OperatingSystem.IsBrowser())
        {
            // No profile fetch off-browser (prerender): don't hold the shell hostage to a call we can't make.
            _gateChecked = true;
            return;
        }

        _user = await AuthStateProvider.GetUserAsync();
        _canView = page => page.Claim is null || (_user?.HasPermission(page.Claim) ?? false);

        // The three gates run in the same order the SERVER enforces them, which is what keeps a user who
        // owes more than one from being bounced between them:
        //
        //   1. A forced password change (issue #406) outranks everything. Its middleware refuses every
        //      endpoint except five — POST /api/legal/respond is NOT among them — so sending a gated user
        //      to /accept-terms first would let them read the terms and then meet a 403 on Accept.
        //   2. Legal acceptance (issue #354 §5) outranks onboarding: a user who owes a response shouldn't
        //      be asked to complete a profile for an account they may be about to decline terms for.
        //   3. Profile completeness (issue #316 §5).
        //
        // All three run before the app body renders, so none of them flashes the shell first.
        //
        // The profile fetch is hoisted above the legal gate to serve gate 1. That is safe where the
        // preference load below is not: GET /api/profile is on the legal middleware's allowlist (and is
        // one of the five password-gate exemptions), whereas preferences are on neither — for a gated
        // user that call would fail and a handler would redirect from an unexpected direction. So
        // preferences stay below both gates.
        var profile = (await Profile.GetAsync()).Value;

        if (profile is not null && EnforcePasswordGate(profile))
        {
            return;
        }

        if (EnforceLegalGate())
        {
            return;
        }

        _isDark = await UserPreferences.GetDarkModePreferencesAsync();

        EnforceOnboardingGate(profile);
    }

    /// <summary>
    /// Redirect to the forced-reset gate if the user owes an admin-initiated password change (issue #406
    /// §3). Returns true when it has navigated away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs <b>before</b> the onboarding gate below: a forced credential change outranks profile
    /// onboarding, so a user with both pending sets their password first. It reads the same profile
    /// payload the onboarding gate already fetches, so it costs no extra round trip.
    /// </para>
    /// <para>
    /// Presentation only, and it may safely fail open, because it is not what enforces anything — the
    /// API refuses every non-exempt endpoint while the flag is set. Deleting this check would mean a
    /// gated user meets a screen of failed requests (which
    /// <see cref="PasswordChangeRequiredHandler"/> then turns into this same redirect) instead of a form.
    /// </para>
    /// </remarks>
    private bool EnforcePasswordGate(ProfileDto profile) =>
        profile.MustChangePassword && RedirectToGate(PasswordChangeRequiredHandler.GatePath);

    /// <summary>
    /// Redirect to the acceptance interstitial if the principal carries a pending-acceptance claim.
    /// Returns true when it has navigated away.
    /// </summary>
    /// <remarks>
    /// This handles the login-time case only — it runs once per full page load and does not re-fire on
    /// in-app navigation, so a mid-session compliance flip is surfaced by
    /// <see cref="LegalComplianceHandler"/> intercepting a 451 instead. Unlike the onboarding gate this
    /// one is not merely a UX aid, but it is still not the security boundary: the server's middleware
    /// is, and a bypass here only means the user meets a 451 instead of the interstitial.
    /// </remarks>
    private bool EnforceLegalGate() =>
        _user?.HasPendingLegalAcceptance() == true && RedirectToGate(LegalComplianceHandler.InterstitialPath);

    /// <summary>
    /// Navigate to a blocking gate at <paramref name="gatePath"/>, carrying the current route so the gate
    /// can return the user to it. Returns <see langword="true"/> when it navigated — callers leave
    /// <c>_gateChecked</c> false on that path, because the layout is being left rather than rendered.
    /// </summary>
    /// <remarks>
    /// The self-reference guard is not defensive padding. Each gate renders under its own nav-less layout,
    /// so this should be unreachable from the gate itself — but if it ever were, the result would be a
    /// redirect loop, which is a hard failure rather than a cosmetic one. Shared by both gates so neither
    /// can acquire the guard without the other.
    /// </remarks>
    private bool RedirectToGate(string gatePath)
    {
        var requested = "/" + NavModel.NormalizePath(NavigationManager.ToBaseRelativePath(NavigationManager.Uri));
        if (requested.StartsWith(gatePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        NavigationManager.NavigateTo($"{gatePath}?returnUrl={Uri.EscapeDataString(requested)}");
        return true;
    }

    // Resolve profile completeness before the app body renders (anti-flash). An incomplete profile is
    // routed to /onboarding with the requested route captured; a failed fetch (a null profile) fails
    // OPEN (the gate is a UX aid, not a security control — spec §5). Not a security boundary: a bypass
    // degrades to the server resolver's safe fallback.
    private void EnforceOnboardingGate(ProfileDto? profile)
    {
        if (profile is { IsComplete: false })
        {
            var requested = "/" + NavModel.NormalizePath(NavigationManager.ToBaseRelativePath(NavigationManager.Uri));
            var returnUrl = requested == Onboarding.OnboardingPath ? "/" : requested;
            NavigationManager.NavigateTo(
                $"{Onboarding.OnboardingPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return; // leave _gateChecked false — we're leaving this layout
        }

        _gateChecked = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!OperatingSystem.IsBrowser())
            return;

        if (firstRender)
        {
            _selfRef = DotNetObjectReference.Create(this);
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/nav-shell.js");
            await _jsModule.InvokeVoidAsync("register", _selfRef);
        }

        // Restore focus after an overlay closes (the target is no longer inert by this render).
        if (_pendingFocus != FocusTarget.None && _navRail is not null)
        {
            var target = _pendingFocus;
            _pendingFocus = FocusTarget.None;
            try
            {
                await (target == FocusTarget.Search ? _navRail.FocusSearchAsync() : _navRail.FocusChipAsync());
            }
            catch (Exception)
            {
                // Best-effort focus return.
            }
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        _current = NavModel.NormalizePath(NavigationManager.ToBaseRelativePath(e.Location));
        // A navigation supersedes any open overlay; FocusOnNavigate owns focus, so no focus-return here.
        _switcherOpen = false;
        _paletteOpen = false;
        _pendingFocus = FocusTarget.None;
        InvokeAsync(StateHasChanged);
    }

    private void OnDarkModeChanged(bool isDark)
    {
        _isDark = isDark;
        InvokeAsync(StateHasChanged);
    }

    // Several calls can fail in one render pass, so this fires more than once; navigating to the page
    // we're already on is a no-op, and RedirectToGate's self-reference guard keeps a failing call made BY
    // the gate page from bouncing it off itself.
    private void OnPasswordChangeRequired() =>
        InvokeAsync(() => RedirectToGate(PasswordChangeRequiredHandler.GatePath));

    // ⌘K from the global listener toggles the palette (and closes the switcher).
    [JSInvokable]
    public Task OnCommandKey()
    {
        if (_paletteOpen)
        {
            ClosePalette();
        }
        else
        {
            OpenPalette();
        }

        return InvokeAsync(StateHasChanged);
    }

    private async Task OnNavigate(NavPage page)
    {
        // Dismissed by navigation — no focus-return (the page takes focus).
        _switcherOpen = false;
        _paletteOpen = false;
        _pendingFocus = FocusTarget.None;
        if (page.External)
        {
            await JS.InvokeVoidAsync("open", page.Href, "_blank", "noopener");
            return;
        }

        NavigationManager.NavigateTo(page.Href);
    }

    private Task GoModule(NavModule module)
    {
        _switcherOpen = false;
        var first = NavModel.VisibleItems(module, _canView).FirstOrDefault();
        return first is null ? Task.CompletedTask : OnNavigate(first);
    }

    private void ToggleSwitcher()
    {
        _switcherOpen = !_switcherOpen;
        _paletteOpen = false;
    }

    private void OpenPalette()
    {
        _paletteOpen = true;
        _switcherOpen = false;
    }

    // Dismiss handlers (Escape / click-away) — close and hand focus back to the opener.
    private void ClosePalette()
    {
        _paletteOpen = false;
        _pendingFocus = FocusTarget.Search;
    }

    private void CloseSwitcher()
    {
        _switcherOpen = false;
        _pendingFocus = FocusTarget.Chip;
    }

    // Flip + persist the dark-mode preference. PreviewDarkMode fires DarkModeChanged, which flips the
    // theme immediately and updates _isDark via OnDarkModeChanged; the save persists it.
    private async Task ToggleDark()
    {
        var next = !_isDark;
        UserPreferences.PreviewDarkMode(next);
        await UserPreferences.SaveUserPreferencesAsync(UserPreferences.Current with { DarkModeEnabled = next });
    }

    public async ValueTask DisposeAsync()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        UserPreferences.DarkModeChanged -= OnDarkModeChanged;
        PasswordChangeRequired.PasswordChangeRequired -= OnPasswordChangeRequired;

        if (_jsModule is not null)
        {
            try
            {
                await _jsModule.InvokeVoidAsync("unregister");
                await _jsModule.DisposeAsync();
            }
            catch (Exception)
            {
                // Circuit already gone; nothing to clean up.
            }
        }

        _selfRef?.Dispose();
    }
}
