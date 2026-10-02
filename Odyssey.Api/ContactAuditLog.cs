using System.Security.Claims;

namespace Odyssey.Api;

/// <summary>
/// The contact audit trail, written under one log category whichever contact controller performs the
/// change (issue #287 M2 split those controllers per sub-resource). It lives in the API rather than in
/// the domain service because the service has no <see cref="ClaimsPrincipal"/>.
/// </summary>
public sealed class ContactAuditLog(ILogger<ContactAuditLog> logger)
{
    /// <summary>
    /// The actor slot for a principal with no user id. The writes behind these lines refuse such a
    /// principal before they run, but the read-side vCard export does not, and an audit line must never
    /// render an empty actor that reads as a logging fault.
    /// </summary>
    internal const string NoActor = "(no user id)";

    /// <summary>
    /// A structured, <b>value-free</b> audit event for a change to the personal data issue #48 adds
    /// (§10.9). <c>Contact.UpdatedAt</c> records <i>that</i> something changed and never <i>who</i> or
    /// <i>what</i>, which cannot answer "who recorded this?" or, after an incident, "whose maiden
    /// names were read?" — precisely what GDPR Art. 33 breach scoping and Art. 5(2) accountability
    /// require.
    ///
    /// <para>
    /// It carries the actor, the contact id and the action, and <b>never the alias value, the label
    /// or the date</b>, so §10.6's no-echo rule is unchanged.
    /// </para>
    /// </summary>
    public void ContactChanged(ClaimsPrincipal actor, Guid contactId, string action) =>
        logger.LogInformation(
            "Contact {ContactId} {Action} by {ActorUserId}.", contactId, action, actor.ActingUserId() ?? NoActor);

    /// <summary>
    /// The bulk-read counterpart (§10.11). A <c>contacts.read</c> holder — <b>Guest included</b> — can
    /// download the whole corpus, maiden names and dates of death with it, in one request. Row count
    /// and whether filters were applied; no names, no values.
    /// </summary>
    public void VCardExported(ClaimsPrincipal actor, int rowCount, bool filtered) =>
        logger.LogInformation(
            "Contacts vCard export of {RowCount} contact(s) ({Scope}) by {ActorUserId}.",
            rowCount, filtered ? "filtered" : "all", actor.ActingUserId() ?? NoActor);
}
