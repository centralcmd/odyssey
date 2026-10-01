using System.Net;

namespace Odyssey.Api.Email;

/// <summary>
/// Composes the two email-change messages (issue #246). Kept beside <see cref="PasswordResetMail"/> for
/// the same reason: the bodies are testable without a relay.
/// </summary>
public static class EmailChangeMail
{
    public const string ConfirmationSubject = "Confirm your new Odyssey email address";

    public const string NoticeSubject = "Your Odyssey sign-in email is being changed";

    /// <summary>
    /// The anchor's visible text — a descriptive phrase rather than the bare URL (WCAG 2.4.4).
    /// </summary>
    public const string LinkText = "Confirm my new email";

    /// <summary>The client page the confirmation link lands on — the same one sign-up uses.</summary>
    public const string ClientPath = "confirm-email";

    /// <summary>The confirmation body. <paramref name="link"/> is HTML-encoded here.</summary>
    public static string ConfirmationBody(string link) =>
        $"""
        <p>You asked to make this address the sign-in email for your Odyssey account.</p>
        <p><a href="{WebUtility.HtmlEncode(link)}">{LinkText}</a></p>
        <p>Your sign-in email stays the same until you confirm. If you didn't request this, ignore this
        message and the change will not happen.</p>
        """;

    /// <summary>
    /// The notice body. The new address is masked: the notice goes to a mailbox that may no longer be the
    /// account owner's, and the owner — who made the request — already knows where it is going. Enough is
    /// left for the owner to recognise an address that is not theirs.
    /// </summary>
    public static string NoticeBody(string newEmail) =>
        $"""
        <p>Someone signed in to your Odyssey account asked to change its sign-in email to
        <strong>{WebUtility.HtmlEncode(Mask(newEmail))}</strong>.</p>
        <p>Nothing changes until the new address is confirmed. If this was you, there is nothing to do.</p>
        <p>If it wasn't you, sign in and change your password now — that cancels the pending change — and
        contact your administrator.</p>
        """;

    /// <summary>
    /// Keeps the first character of the local part and the whole domain: <c>j•••@example.com</c>.
    /// </summary>
    public static string Mask(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0)
        {
            return "•••";
        }

        return $"{email[0]}•••{email[at..]}";
    }
}
