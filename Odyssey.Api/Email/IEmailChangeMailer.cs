namespace Odyssey.Api.Email;

/// <summary>
/// The two messages the first-party email change sends (issue #246): the confirmation link to the
/// <em>new</em> address and the security notice to the <em>current</em> one.
/// </summary>
/// <remarks>
/// A seam of its own rather than a call through <c>IEmailSender&lt;ApplicationUser&gt;</c>, because
/// neither message fits that interface: its <c>SendConfirmationLinkAsync</c> is skipped while
/// <c>EmailRequireConfirmation</c> is off — which would make an email change impossible to confirm —
/// and it has no notice at all. Implemented by <see cref="SmtpEmailSender"/>, so both go through the
/// same composition, transport snapshot and fail-closed rules as every other message.
/// </remarks>
public interface IEmailChangeMailer
{
    /// <summary>
    /// Mails the confirmation link to <paramref name="newEmail"/>. <paramref name="confirmationLink"/> is
    /// the raw API <c>/confirmEmail</c> URL; it is rewritten onto the client's <c>confirm-email</c> page
    /// with its query preserved. Subject to the per-recipient send throttle, like every message that can
    /// be pointed at an address the caller does not own.
    /// </summary>
    Task SendChangeConfirmationAsync(string newEmail, string confirmationLink);

    /// <summary>
    /// Tells <paramref name="currentEmail"/> that a change of sign-in address was requested. <b>Not</b>
    /// throttled: the per-recipient budget is keyed by address and can be spent anonymously through
    /// <c>/forgotPassword</c>, so a throttled notice could be suppressed by whoever wants it unseen.
    /// </summary>
    /// <remarks>
    /// Takes no cancellation token, on purpose: a caller-side token would be the request's, and a client
    /// that disconnects mid-request must not be able to abort the one message that warns the owner.
    /// </remarks>
    Task SendChangeNoticeAsync(string currentEmail, string newEmail);
}
