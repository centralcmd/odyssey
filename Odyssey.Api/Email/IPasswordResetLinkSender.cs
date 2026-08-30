namespace Odyssey.Api.Email;

/// <summary>
/// Sends the password-reset mail from a call site that needs to know whether it went out (issue #406
/// §5.1) — today, the admin-initiated reset on <c>POST /api/users/{id}/password-reset</c>.
/// </summary>
/// <remarks>
/// <para>
/// A narrower seam over the same composition and send code <see cref="SmtpEmailSender"/> already uses
/// for Identity's <c>/forgotPassword</c>, not a parallel implementation of it. The two differ in one
/// respect only: <c>IEmailSender&lt;ApplicationUser&gt;</c> returns <c>Task</c>, so its endpoints must
/// answer identically whether or not the relay accepted the message — correct for an anonymous flow
/// that must not disclose whether an address is registered, and wrong for an admin who is waiting to be
/// told what happened.
/// </para>
/// <para>
/// <b>This member does not acquire a per-recipient send permit.</b> The caller acquires one before it
/// mutates anything, so acquiring again here would consume two permits per reset and could drop the mail
/// <em>after</em> the stamp rotation and flag write were committed, with no way to report it — the false
/// success this seam exists to eliminate.
/// </para>
/// </remarks>
public interface IPasswordResetLinkSender
{
    /// <summary>
    /// Composes the client reset link for <paramref name="base64UrlCode"/> — the Base64Url-encoded token,
    /// in the shape <c>MapIdentityApi</c> produces — and attempts delivery to <paramref name="email"/>.
    /// </summary>
    Task<PasswordResetLinkDelivery> SendResetLinkAsync(
        string email, string base64UrlCode, CancellationToken cancellationToken = default);
}

/// <summary>What became of a reset link handed to <see cref="IPasswordResetLinkSender"/>.</summary>
public enum PasswordResetLinkDelivery
{
    /// <summary>Handed to the relay without error.</summary>
    Delivered,

    /// <summary>
    /// The message could not be sent and nothing was transmitted. Two conditions reach this state, and
    /// issue #8 §11.1 keeps them distinct everywhere except here:
    /// <list type="bullet">
    /// <item><em>Unconfigured</em> — no SMTP host is set, so the link was logged instead. The intended
    /// development behaviour, and reported to the admin as delivered: in that environment logging
    /// <em>is</em> the delivery mechanism. Production reaches this too now — the startup gate on
    /// <c>Email:SmtpHost</c> went away with the setting's move into the store, so a deployment sends
    /// nothing until an administrator configures a relay at <c>/settings</c>.</item>
    /// <item><em>Degraded</em> — a stored transport value is present and unusable, so the send fails
    /// closed rather than substituting a default. Logged as an error, naming the keys and never the
    /// values.</item>
    /// </list>
    /// They share an outcome because the caller's choice is the same either way: report that no mail
    /// went out. The distinction that matters is on the settings page and in the log, not here.
    /// </summary>
    NotConfigured,

    /// <summary>The send was attempted and threw. Reported honestly.</summary>
    Failed,
}
