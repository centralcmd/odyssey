using Odyssey.Dtos.Authorization;
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

    // True while the shell was resolved against an unavailable session and is subscribed for its recovery.
    private bool _awaitingSession;
    private bool _resolvingShell;
    private bool _resolveAgain;
    private bool _focusMainAfterRender;
    private ElementReference _main;

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

        await ResolveShellAsync();
    }

    /// <summary>
    /// Reads the session and resolves the gates and preferences that hang off it. Runs once per load —
    /// and once more if that first read had no definitive answer (issue #278): the provider announces
    /// the recovered session, and without this re-run the shell would keep the unavailable principal and
    /// render a nav with no permissions under the page the router has just authorized.
    /// </summary>
    private async Task ResolveShellAsync()
    {
        _user = await AuthStateProvider.GetUserAsync();
        _canView = page => page.Claim is null || (_user?.HasPermission(page.Claim) ?? false);

        if (SessionUnavailable.Is(_user))
        {
            // Nothing to gate on and nothing to fetch: every call would fail the way the probe did. Let
            // the body render, which is App.razor's retry panel, and wait for the recovery.
            if (!_awaitingSession)
            {
                _awaitingSession = true;
                AuthStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
            }

            _gateChecked = true;
            return;
        }

        if (_awaitingSession)
        {
            _awaitingSession = false;
            AuthStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        }

        // All three gates are resolved in one place, in the order the SERVER enforces them — see
        // FirstRunGateChain for what that order buys and what it cost when it was three chained
        // "did I navigate?" booleans instead.
        //
        // The profile fetch is hoisted above the chain because two of the three gates read it. That is
        // safe where the preference load below is not: GET /api/profile is on the legal middleware's
        // allowlist (and is one of the five password-gate exemptions), whereas preferences are on
        // neither — for a gated user that call fails and a handler redirects from an unexpected
        // direction. So preferences stay strictly below every gate.
        var profile = (await Profile.GetAsync()).Value;

        if (FirstRunGateChain.Owed(profile, _user) is { } gatePath)
        {
            // Reset rather than left, since a re-run after a recovered session starts from true. Keep
            // _gateChecked false — the layout is being left rather than rendered, so the app body
            // never flashes behind a gate. That holds whether or not RedirectToGate actually navigates.
            _gateChecked = false;
            RedirectToGate(gatePath);
            return;
        }

        _isDark = await UserPreferences.GetDarkModePreferencesAsync();
        _gateChecked = true;
    }

    /// <summary>
    /// Navigate to a blocking gate at <paramref name="gatePath"/>, carrying the current route so the gate
    /// can return the user to it. A no-op when the browser is already on that gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The self-reference guard is not defensive padding, and it is reachable in normal operation:
    /// <c>AuthorizeRouteView</c> renders <c>DefaultLayout</c> — this layout — for the whole async
    /// authorizing phase, so <c>OnInitializedAsync</c> runs on a cold load of every <c>[Authorize]</c>
    /// gate page regardless of that page's own <c>@layout</c>. Without the guard, each gate would
    /// redirect to itself and spin.
    /// </para>
    /// <para>
    /// <b>Not navigating is not the same as the gate not applying.</b> This returns nothing so the two
    /// cannot be confused again: whether the chain continues is <see cref="FirstRunGateChain"/>'s
    /// answer, and it does not depend on the current route.
    /// </para>
    /// </remarks>
    private void RedirectToGate(string gatePath)
    {
        var requested = "/" + NavModel.NormalizePath(NavigationManager.ToBaseRelativePath(NavigationManager.Uri));
        if (requested.StartsWith(gatePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        NavigationManager.NavigateTo($"{gatePath}?returnUrl={Uri.EscapeDataString(requested)}");
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!OperatingSystem.IsBrowser())
            return;

        if (_focusMainAfterRender)
        {
            _focusMainAfterRender = false;
            try
            {
                await _main.FocusAsync();
            }
            catch (Exception)
            {
                // Best-effort focus move; the page is usable either way.
            }
        }

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

    private void OnAuthenticationStateChanged(Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> state) =>
        _ = InvokeAsync(async () =>
        {
            // One re-run at a time: the recovery loop and a Retry can announce back to back, and two
            // overlapping runs would fetch the profile twice and race on _gateChecked. An announcement
            // that lands mid-run is not dropped — it queues exactly one more pass, so the last word on
            // the session is always the one the shell ends up resolved against.
            if (_resolvingShell)
            {
                _resolveAgain = true;
                return;
            }

            _resolvingShell = true;
            try
            {
                do
                {
                    _resolveAgain = false;
                    await ResolveShellAsync();
                }
                while (_resolveAgain);
            }
            finally
            {
                _resolvingShell = false;
            }

            // The retry panel that held focus is gone and no route change fired FocusOnNavigate, so hand
            // focus to the main landmark rather than letting it fall to <body> (WCAG 2.4.3).
            _focusMainAfterRender = !_awaitingSession;
            StateHasChanged();
        });

    public async ValueTask DisposeAsync()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        if (_awaitingSession)
        {
            AuthStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        }

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
