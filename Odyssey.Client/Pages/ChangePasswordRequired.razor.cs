using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient.Auth;
using Odyssey.Client.Auth;
using Odyssey.Client.Components;

namespace Odyssey.Client.Pages;

/// <summary>
/// The forced-reset gate at <c>/change-password-required</c> (issue #406 §3). Reached from
/// <c>MainLayout</c>'s gate check at sign-in, or mid-session from
/// <see cref="PasswordChangeRequiredHandler"/> intercepting a <c>403</c>.
/// </summary>
public partial class ChangePasswordRequired
{
    private OdsPasswordChangeForm? _form;
    private ElementReference _headingRef;
    private bool _saving;
    private string? _error;
    private string? _returnUrl;

    // Nullable, NOT `bool _hasFocusedHeading` — a non-nullable flag defaults to false, which is
    // indistinguishable from "not yet handled", and the identical guard bug was found live in #405's
    // draft. Set on the first render and never reset, so the heading takes focus exactly once no matter
    // how many re-renders typing in the form causes.
    private bool? _headingFocused;

    [Inject] private AuthApiClient AuthApiClient { get; set; } = default!;

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [Inject] private CookieAuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    protected override void OnInitialized() =>
        // Rejects anything that isn't an app-relative path, and refuses this page's own route so a
        // completed gate can't redirect to itself and spin.
        _returnUrl = LocalReturnUrl.FromQuery(
            NavigationManager.Uri, PasswordChangeRequiredHandler.GatePath);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _headingFocused is not null)
        {
            return;
        }

        _headingFocused = true;
        await _headingRef.FocusAsync();
    }

    private async Task SubmitAsync(OdsPasswordChangeForm.PasswordChange change)
    {
        _saving = true;
        _error = null;

        var result = await AuthApiClient.ChangePasswordAsync(change.CurrentPassword, change.NewPassword);

        _saving = false;

        if (!result.IsSuccess)
        {
            // The server's own wording, so a wrong current password reads differently from a rejected
            // new one — the two arrive as the same 400 and the user has to know which field to fix.
            _error = result.Error ?? "Unable to set your new password right now. Please try again.";
            return;
        }

        _form?.Reset();

        // The endpoint refreshed the auth cookie against the rotated security stamp, so the session
        // survives — but the flag is now clear, and the claims/profile the shell renders from were read
        // before it was. A full reload is the simplest way to have every gate re-evaluate from scratch.
        NavigationManager.NavigateTo(_returnUrl ?? "/", forceLoad: true);
    }

    private async Task SignOutAsync()
    {
        // The escape hatch for a user who does not know their current password: leave, then use the
        // emailed link or /forgot-password. POST /logout is one of the five endpoints the server keeps
        // open while the flag is set, precisely so this cannot dead-end.
        await AuthApiClient.LogoutAsync();
        await AuthStateProvider.RefreshAsync();
        NavigationManager.NavigateTo("/login", forceLoad: true);
    }
}
