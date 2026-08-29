using System.Net;

namespace Odyssey.Api.Email;

/// <summary>
/// Composes the password-reset message (issue #405). Separate from <see cref="SmtpEmailSender"/>
/// because this is the one email Odyssey builds a link for itself — <c>MapIdentityApi</c>'s
/// <c>/forgotPassword</c> hands the sender a bare token, not a link, so there is nothing to rewrite —
/// and because the admin-initiated reset (#406) sends the same message from a different call site.
/// </summary>
public static class PasswordResetMail
{
    public const string Subject = "Reset your Odyssey password";

    /// <summary>
    /// The anchor's visible text. A descriptive phrase rather than the bare URL or "click here",
    /// so the link makes sense out of context (WCAG 2.4.4).
    /// </summary>
    public const string LinkText = "Reset my password";

    /// <summary>The client page the link lands on.</summary>
    public const string ClientPath = "reset-password";

    /// <summary>
    /// Builds the message for <paramref name="resetCode"/> — the HTML-encoded Base64Url token
    /// Identity generated — against the configured <paramref name="clientBaseUrl"/>.
    /// </summary>
    public static PasswordResetMessage Compose(string resetCode, string? clientBaseUrl)
    {
        var link = ComposeLink(resetCode, clientBaseUrl);

        // No client base URL configured: degrade to the bare code plus instructions rather than
        // mailing a broken link, matching SmtpEmailSender.RewriteToClient's posture.
        var body = link is null
            ? $"""
                <p>We received a request to reset your Odyssey password.</p>
                <p>Open the "Reset your password" page in Odyssey and paste in this code:</p>
                <p><strong>{resetCode}</strong></p>
                <p>The code expires in 1 hour. If you didn't request this, you can safely ignore this message.</p>
                """
            : $"""
                <p>We received a request to reset your Odyssey password.</p>
                <p><a href="{link}">{LinkText}</a></p>
                <p>The link expires in 1 hour. If you didn't request this, you can safely ignore this message.</p>
                """;

        return new PasswordResetMessage(body, link);
    }

    /// <summary>
    /// The client reset URL, or <see langword="null"/> when no client base URL is configured.
    /// <para>
    /// The user's email address is deliberately left out: the client is served by NGINX, whose access
    /// log records the query string, and the URL also lands in browser history and in mail-scanner
    /// logs. The reset page asks for the address instead.
    /// </para>
    /// </summary>
    private static string? ComposeLink(string resetCode, string? clientBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(clientBaseUrl))
        {
            return null;
        }

        // The token arrives HTML-encoded for an email body; decode it before it goes into a URL, then
        // percent-encode it into the query. Base64Url's alphabet needs neither step in practice, but
        // neither may be assumed away.
        var code = Uri.EscapeDataString(WebUtility.HtmlDecode(resetCode));
        return $"{clientBaseUrl.TrimEnd('/')}/{ClientPath}?code={code}";
    }
}

/// <summary>
/// A composed reset message. <paramref name="Link"/> is <see langword="null"/> when no client base
/// URL is configured, in which case <paramref name="Body"/> carries the bare code instead.
/// </summary>
public sealed record PasswordResetMessage(string Body, string? Link);
