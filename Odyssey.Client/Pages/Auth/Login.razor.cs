using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Odyssey.ApiClient.Auth;
using Odyssey.ApiClient.Contracts;
using Odyssey.Client.Auth;

namespace Odyssey.Client.Pages.Auth;

/// <summary>
/// The sign-in page at <c>/login</c>: password, then the second factor when Identity asks for one, then
/// on to <see cref="Destination"/>.
/// </summary>
public partial class Login
{
    private enum LoginPhase { Password, TwoFactor }

    private readonly LoginRequest _request = new();
    private LoginPhase _phase = LoginPhase.Password;
    private string _code = string.Empty;
    private bool _useRecoveryCode;
    private bool _rememberDevice;
    private string? _errorMessage;
    private bool _isSubmitting;

    [SupplyParameterFromQuery]
    public string? ReturnUrl { get; set; }

    /// <summary>This page's own route, which is never a valid return target.</summary>
    internal const string LoginPath = "/login";

    /// <summary>
    /// Where a completed sign-in lands. <paramref name="returnUrl"/> comes from the query string, so it
    /// is attacker-supplied: it is validated by <see cref="LocalReturnUrl"/> — the single implementation
    /// of this check — and anything that isn't an app-relative path falls back to the dashboard.
    /// </summary>
    /// <remarks>
    /// The check this replaced was <c>$"/{ReturnUrl.TrimStart('/')}"</c>, which neutralised a
    /// protocol-relative <c>//evil.example.com</c> but not <c>/\evil.example.com</c> — a browser's URL
    /// parser reads that backslash as a slash in the authority position, so the freshly authenticated
    /// user's browser left the origin (issue #408, CWE-601).
    /// </remarks>
    internal static string Destination(string? returnUrl) =>
        LocalReturnUrl.Parse(returnUrl, LoginPath) ?? "/";

    /// <summary>
    /// The sign-in URL that brings the user back to <paramref name="baseRelativePath"/> once they are
    /// authenticated. Used by the redirect stubs that send an unauthenticated visitor here.
    /// </summary>
    /// <remarks>
    /// The leading slash is the load-bearing part. <see cref="NavigationManager.ToBaseRelativePath"/>
    /// yields the route <em>without</em> it, and <see cref="Destination"/> refuses anything unrooted — so
    /// an unrooted value is not a cosmetic difference: it is silently dropped at the far end and the user
    /// lands on the dashboard instead of the page they asked for. Producing the whole URL here rather
    /// than in each stub is what lets a test round-trip it through <see cref="Destination"/>; two
    /// hand-written copies is how the two ends drifted apart in the first place (issue #408).
    /// </remarks>
    internal static string SignInUrlFor(string baseRelativePath) =>
        $"{LoginPath}?returnUrl={Uri.EscapeDataString("/" + baseRelativePath)}";

    private async Task SignInAsync()
    {
        _errorMessage = null;
        _request.TwoFactorCode = null;
        _request.TwoFactorRecoveryCode = null;
        _isSubmitting = true;

        var outcome = await AuthApiClient.LoginAsync(_request);

        _isSubmitting = false;
        await HandleOutcomeAsync(outcome);
    }

    private async Task SubmitTwoFactorAsync()
    {
        var entered = _code.Trim();
        if (string.IsNullOrEmpty(entered))
        {
            return;
        }

        _errorMessage = null;
        if (_useRecoveryCode)
        {
            _request.TwoFactorRecoveryCode = entered;
            _request.TwoFactorCode = null;
        }
        else
        {
            _request.TwoFactorCode = entered;
            _request.TwoFactorRecoveryCode = null;
        }

        _isSubmitting = true;
        var outcome = await AuthApiClient.LoginAsync(_request);

        // The built-in /login remembers this browser on a TOTP sign-in (rememberClient
        // follows useCookies). Honour the opt-in: unless the user ticked "remember this
        // device" on the authenticator path, clear that cookie so the next sign-in still
        // requires the code. Recovery-code logins are never remembered by Identity.
        var trustDevice = _rememberDevice && !_useRecoveryCode;
        if (outcome == LoginOutcome.Success && !trustDevice)
        {
            await AuthApiClient.ForgetTwoFactorMachineAsync();
        }

        _isSubmitting = false;
        await HandleOutcomeAsync(outcome);
    }

    private async Task HandleOutcomeAsync(LoginOutcome outcome)
    {
        switch (outcome)
        {
            case LoginOutcome.Success:
                await AuthStateProvider.RefreshAsync();
                NavigationManager.NavigateTo(Destination(ReturnUrl));
                break;

            case LoginOutcome.RequiresTwoFactor:
                // Identity has accepted the password and issued its short-lived pending
                // cookie; collect the second factor and re-submit to finish signing in.
                _phase = LoginPhase.TwoFactor;
                break;

            case LoginOutcome.LockedOut:
                // A locked-out result covers a few cases the client can't tell apart: too many failed
                // attempts, an account disabled by an admin, or a new account still awaiting approval.
                _errorMessage = "Your account isn't active. It may be awaiting administrator approval, "
                    + "disabled, or temporarily locked after too many attempts. Contact an administrator if this persists.";
                break;

            case LoginOutcome.RateLimited:
                // The per-IP limiter rejects the request before any credential is checked, so the
                // generic message below would be actively wrong here — it would send a user who typed
                // everything correctly off to re-check their password. Phrased for the shared-IP case
                // (an office NAT is one partition key) rather than blaming this user's own attempts.
                _errorMessage = "Too many sign-in attempts from your network. Please wait a minute and try again.";
                _code = string.Empty;
                break;

            default:
                _errorMessage = _phase == LoginPhase.TwoFactor
                    ? "Incorrect code. Please try again."
                    : "Unable to sign in. Please check your username/email and password.";
                _code = string.Empty;
                break;
        }
    }

    private void ToggleRecoveryCode()
    {
        _useRecoveryCode = !_useRecoveryCode;
        _code = string.Empty;
        _errorMessage = null;
    }

    private void BackToPassword()
    {
        _phase = LoginPhase.Password;
        _code = string.Empty;
        _useRecoveryCode = false;
        _rememberDevice = false;
        _errorMessage = null;
        _request.TwoFactorCode = null;
        _request.TwoFactorRecoveryCode = null;
    }

    private async Task OnPasswordKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !_isSubmitting)
        {
            await SignInAsync();
        }
    }

    private async Task OnCodeKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !_isSubmitting)
        {
            await SubmitTwoFactorAsync();
        }
    }
}
