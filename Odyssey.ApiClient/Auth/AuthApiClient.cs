using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Odyssey.ApiClient.Contracts;

namespace Odyssey.ApiClient.Auth;

public sealed class AuthApiClient(HttpClient httpClient, AntiforgeryTokenStore antiforgeryTokens)
{
    /// <summary>
    /// Posts credentials to the built-in <c>POST /login</c> endpoint. When the account has
    /// 2FA enabled and no code is supplied, Identity refuses with <c>401 RequiresTwoFactor</c>
    /// (and sets its short-lived pending cookie); the caller then re-invokes this with
    /// <see cref="LoginRequest.TwoFactorCode"/> or <see cref="LoginRequest.TwoFactorRecoveryCode"/>
    /// to finish the sign-in.
    /// </summary>
    public async Task<LoginOutcome> LoginAsync(LoginRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "login?useCookies=true")
        {
            Content = JsonContent.Create(request)
        };

        var response = await httpClient.SendAsync(httpRequest);
        if (response.IsSuccessStatusCode)
        {
            // Signing in re-issues the antiforgery cookie for the new identity, so the token cached
            // for the anonymous session no longer pairs with it and every subsequent write would 400.
            // The browser app happens to escape this by force-reloading after login (a fresh scope,
            // hence a fresh store); a non-browser consumer keeps the same store, so drop it here.
            // Symmetric with LogoutAsync.
            antiforgeryTokens.Invalidate();
            return LoginOutcome.Success;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var detail = await ReadProblemDetailAsync(response);
            if (string.Equals(detail, "RequiresTwoFactor", StringComparison.OrdinalIgnoreCase))
            {
                return LoginOutcome.RequiresTwoFactor;
            }

            if (string.Equals(detail, "LockedOut", StringComparison.OrdinalIgnoreCase))
            {
                return LoginOutcome.LockedOut;
            }
        }

        // Distinct from Failed on purpose: the per-IP limiter on the Identity endpoints rejects the
        // attempt before any credential is looked at, so folding it into Failed tells the user their
        // password is wrong when nothing about it was checked.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return LoginOutcome.RateLimited;
        }

        return LoginOutcome.Failed;
    }

    public async Task<(bool Succeeded, string? Error)> RegisterAsync(RegisterRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "register")
        {
            Content = JsonContent.Create(request)
        };

        var response = await httpClient.SendAsync(httpRequest);

        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        var payload = await response.Content.ReadAsStringAsync();
        return (false, string.IsNullOrWhiteSpace(payload) ? "Unable to register user." : payload);
    }

    /// <summary>
    /// Confirms an email address against the built-in <c>GET /confirmEmail</c> endpoint using the
    /// <c>userId</c> and <c>code</c> carried in the confirmation link. The values are re-encoded
    /// into the query exactly as Identity expects (the <c>code</c> is a URL-safe base64url token).
    /// When the link came from the email-change flow it also carries <paramref name="changedEmail"/>,
    /// which must be forwarded — otherwise Identity confirms the existing address instead of applying
    /// the change and the token is rejected.
    /// </summary>
    public async Task<bool> ConfirmEmailAsync(string userId, string code, string? changedEmail = null)
    {
        var url = $"confirmEmail?userId={Uri.EscapeDataString(userId)}&code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrEmpty(changedEmail))
        {
            url += $"&changedEmail={Uri.EscapeDataString(changedEmail)}";
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);

        var response = await httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Requests a fresh confirmation link via <c>POST /resendConfirmationEmail</c>. Identity always
    /// returns 200 here (it won't reveal whether the address exists), so a non-200 indicates a
    /// transport/server problem rather than an unknown email — with one expected exception: the
    /// endpoint is rate limited (issue #393) and answers <c>429</c> with a ProblemDetails body whose
    /// <c>detail</c> tells the user to wait. Returning that text lets the caller show it verbatim
    /// instead of a generic failure.
    /// </summary>
    public async Task<(bool Succeeded, string? Error)> ResendConfirmationAsync(string email)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "resendConfirmationEmail")
        {
            Content = JsonContent.Create(new { email })
        };

        var response = await httpClient.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadProblemDetailAsync(response));
    }

    /// <summary>
    /// Asks for a password-reset link via <c>POST /forgotPassword</c>. Identity answers <c>200</c> for
    /// every address — registered, unknown, unconfirmed or recipient-throttled alike — so
    /// <see cref="PasswordResetRequestOutcome.Sent"/> reports only that the call succeeded and never
    /// that an account exists. The caller must not branch on anything else, or it reintroduces the
    /// enumeration leak the endpoint is designed to avoid.
    /// </summary>
    public async Task<PasswordResetRequestOutcome> RequestPasswordResetAsync(string email)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "forgotPassword")
            {
                Content = JsonContent.Create(new { email })
            };

            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return PasswordResetRequestOutcome.Sent;
            }

            return response.StatusCode == HttpStatusCode.TooManyRequests
                ? PasswordResetRequestOutcome.RateLimited
                : PasswordResetRequestOutcome.Failed;
        }
        catch (HttpRequestException)
        {
            return PasswordResetRequestOutcome.Failed;
        }
    }

    /// <summary>
    /// Completes a reset via <c>POST /resetPassword</c> with the code carried by the emailed link.
    /// </summary>
    /// <remarks>
    /// The page has three different things to do with a <c>400</c> — send the user off to request a
    /// new link, keep the form mounted with a policy message, or show a generic retry — so the status
    /// class is carried in the outcome rather than flattened into a bool plus a string. Identity
    /// answers a rejected token with a <c>ValidationProblemDetails</c> whose <c>errors</c> keys are
    /// <c>IdentityError.Code</c> values; <c>InvalidToken</c> is the one that means the link itself is
    /// spent (pinned server-side by <c>PasswordResetApiTests</c>). Anything else on a <c>400</c> is a
    /// password-policy rejection, whose first message is handed back for inline display.
    /// </remarks>
    public async Task<(PasswordResetOutcome Outcome, string? Error)> ResetPasswordAsync(
        string email, string resetCode, string newPassword)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "resetPassword")
            {
                Content = JsonContent.Create(new { email, resetCode, newPassword })
            };

            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return (PasswordResetOutcome.Success, null);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (PasswordResetOutcome.RateLimited, await ReadProblemDetailAsync(response));
            }

            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                return (PasswordResetOutcome.Failed, null);
            }

            var errors = await ReadValidationErrorsAsync(response);
            if (errors is null or { Count: 0 })
            {
                return (PasswordResetOutcome.Failed, null);
            }

            if (errors.ContainsKey(InvalidTokenErrorCode))
            {
                return (PasswordResetOutcome.InvalidToken, null);
            }

            var firstMessage = errors.Values
                .SelectMany(messages => messages ?? [])
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            return (PasswordResetOutcome.PasswordRejected, firstMessage);
        }
        catch (HttpRequestException)
        {
            return (PasswordResetOutcome.Failed, null);
        }
    }

    /// <summary>
    /// The <c>IdentityError.Code</c> Identity keys a rejected reset token under. A wire contract, not
    /// a display string — see <see cref="ResetPasswordAsync"/>.
    /// </summary>
    private const string InvalidTokenErrorCode = "InvalidToken";

    private static async Task<Dictionary<string, string[]>?> ReadValidationErrorsAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
            return problem?.Errors;
        }
        catch
        {
            // A 400 whose body isn't the expected shape tells the page nothing actionable.
            return null;
        }
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "manage/info");
            var response = await httpClient.SendAsync(request);
            return response.StatusCode != HttpStatusCode.Unauthorized;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<Claim>> GetClaimsAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "auth/claims");

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var claims = await response.Content.ReadFromJsonAsync<List<ClaimResponse>>() ?? [];
        return claims
            .Select(claim => new Claim(claim.Type, claim.Value))
            .ToList();
    }

    public async Task LogoutAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "logout");
        await httpClient.SendAsync(request);

        // Drop the cached antiforgery token so the next session fetches a fresh one.
        antiforgeryTokens.Invalidate();
    }

    /// <summary>
    /// Reads the signed-in user's identity info (email + confirmation status) from the
    /// standard ASP.NET Core Identity <c>GET manage/info</c> endpoint.
    /// </summary>
    public async Task<UserInfo?> GetInfoAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "manage/info");

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<UserInfo>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Changes the signed-in user's password (requires the current one) via Odyssey's first-party
    /// <c>POST api/account/password</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Moved off Identity's <c>POST manage/info</c> with issue #406. That endpoint changes the password
    /// <em>and</em> the email address, which made it impossible to leave open to a session that is
    /// blocked pending a forced password change without also letting it move the account's sign-in
    /// identity to another mailbox. The first-party endpoint does one thing, so it can be the single
    /// exemption — and it refreshes the auth cookie against the rotated security stamp, so a password
    /// change no longer quietly signs the user out a minute later.
    /// </para>
    /// <para>
    /// It also answers RFC 7807, unlike <c>manage/info</c>'s plain-string failures — which is why this
    /// returns <see cref="ApiResult"/> rather than the old <c>(bool, string?)</c>: a caller that
    /// rendered the previous <c>Error</c> verbatim would now be putting a raw JSON document on the page.
    /// <see cref="ApiProblem.Message"/> carries the server's own wording, so the gate page and
    /// <c>/account</c> can tell a wrong current password from a policy rejection.
    /// </para>
    /// </remarks>
    public async Task<ApiResult> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        // The same shape OdysseyApi.SendAsync produces. This client predates IOdysseyApi and takes the
        // HttpClient directly (every consumer, including the E2E fixture, constructs it that way), so
        // the result is built here rather than by taking on a second transport dependency.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/account/password")
            {
                Content = JsonContent.Create(new { currentPassword, newPassword }),
            };

            using var response = await httpClient.SendAsync(request);
            return response.IsSuccessStatusCode
                ? ApiResult.Success(response.StatusCode)
                : ApiResult.Failure(response.StatusCode, await response.ReadProblemAsync());
        }
        catch (Exception ex)
        {
            return ApiResult.Failure(ex);
        }
    }

    // ── Two-factor authentication ─────────────────────────────────────────────
    // All of these wrap the single built-in Identity endpoint POST /manage/2fa
    // (from MapIdentityApi). The request flags select the operation; the response
    // is always the current TwoFactorStatus.

    /// <summary>
    /// Reads the current 2FA state. Posting an empty body is side-effect-free except that
    /// Identity generates (but does not activate) an authenticator key if none exists, so
    /// the returned <see cref="TwoFactorStatus.SharedKey"/> is ready for the setup wizard.
    /// </summary>
    public Task<TwoFactorStatus?> GetTwoFactorStatusAsync() =>
        PostTwoFactorAsync(new { });

    /// <summary>
    /// Verifies <paramref name="code"/> against the pending authenticator key and turns 2FA on.
    /// Always (re)generates the recovery codes (<c>resetRecoveryCodes</c>) so the caller can show
    /// a fresh set: Identity otherwise returns none when re-enabling while old codes are still on
    /// file, which would let setup finish without the user ever seeing fallback codes. Returns the
    /// status carrying the one-time recovery codes, or <c>null</c> when the code is rejected.
    /// </summary>
    public Task<TwoFactorStatus?> EnableTwoFactorAsync(string code) =>
        PostTwoFactorAsync(new { enable = true, twoFactorCode = code, resetRecoveryCodes = true });

    /// <summary>Turns 2FA off. The active session is the authorization.</summary>
    public async Task<bool> DisableTwoFactorAsync() =>
        await PostTwoFactorAsync(new { enable = false }) is not null;

    /// <summary>
    /// Resets the authenticator shared secret (which also disables 2FA) and returns the new
    /// <see cref="TwoFactorStatus.SharedKey"/> so the wizard can re-enrol without a round-trip.
    /// </summary>
    public Task<TwoFactorStatus?> ResetTwoFactorKeyAsync() =>
        PostTwoFactorAsync(new { resetSharedKey = true });

    /// <summary>Replaces the recovery-code set with 10 fresh codes (invalidating the old set).</summary>
    public Task<TwoFactorStatus?> RegenerateRecoveryCodesAsync() =>
        PostTwoFactorAsync(new { resetRecoveryCodes = true });

    /// <summary>
    /// Clears Identity's "remember this device" cookie. The built-in <c>/login</c> sets that
    /// cookie on a successful 2FA sign-in whenever the browser uses a persistent cookie
    /// (<c>rememberClient</c> follows <c>useCookies</c>), which would let later password-only
    /// logins skip the challenge. The login page calls this right after a 2FA sign-in unless
    /// the user explicitly opted to trust the device.
    /// </summary>
    public async Task<bool> ForgetTwoFactorMachineAsync() =>
        await PostTwoFactorAsync(new { forgetMachine = true }) is not null;

    private async Task<TwoFactorStatus?> PostTwoFactorAsync(object body)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "manage/2fa")
            {
                Content = JsonContent.Create(body)
            };

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TwoFactorStatus>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetail>();
            return problem?.Detail;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ClaimResponse(string Type, string Value);

    private sealed record ProblemDetail(string? Detail);

    /// <summary>The <c>ValidationProblemDetails</c> shape Identity returns on a rejected write.</summary>
    private sealed record ValidationProblem(Dictionary<string, string[]>? Errors);

    /// <summary>Shape of the Identity <c>manage/info</c> response we consume.</summary>
    public sealed record UserInfo(string Email, bool IsEmailConfirmed);

    /// <summary>Shape of the Identity <c>manage/2fa</c> (<c>TwoFactorResponse</c>) payload.</summary>
    public sealed record TwoFactorStatus(
        string SharedKey,
        int RecoveryCodesLeft,
        string[]? RecoveryCodes,
        bool IsTwoFactorEnabled,
        bool IsMachineRemembered);
}

/// <summary>Outcome of a <see cref="AuthApiClient.LoginAsync"/> attempt.</summary>
public enum LoginOutcome
{
    Success,
    RequiresTwoFactor,
    LockedOut,

    /// <summary>
    /// Rate limited (<c>429</c>) by the per-IP Identity limiter — the attempt never reached a
    /// credential check, so the caller must not report it as a bad username or password.
    /// </summary>
    RateLimited,

    Failed,
}

/// <summary>Outcome of a <see cref="AuthApiClient.RequestPasswordResetAsync"/> call.</summary>
public enum PasswordResetRequestOutcome
{
    /// <summary>The request succeeded. Says nothing about whether an account exists.</summary>
    Sent,

    /// <summary>Rate limited (<c>429</c>) — the caller should keep the form and say so.</summary>
    RateLimited,

    /// <summary>Any other failure, including a transport error.</summary>
    Failed,
}

/// <summary>Outcome of a <see cref="AuthApiClient.ResetPasswordAsync"/> call.</summary>
public enum PasswordResetOutcome
{
    /// <summary>The password was changed.</summary>
    Success,

    /// <summary>The link is spent: invalid, expired, already used, or the wrong address for it.</summary>
    InvalidToken,

    /// <summary>The new password failed the server's policy; the accompanying message says how.</summary>
    PasswordRejected,

    /// <summary>Rate limited (<c>429</c>).</summary>
    RateLimited,

    /// <summary>Any other failure, including a transport error and an unparseable response.</summary>
    Failed,
}
