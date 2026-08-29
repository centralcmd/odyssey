namespace Odyssey.Dtos.Authorization;

/// <summary>
/// The pending-legal-acceptance claim (issue #354 §5). Not a permission claim: it is not held by a role
/// and grants nothing — it is computed per user at every sign-in and at every
/// <c>SecurityStampValidator</c> revalidation, and its <em>presence</em> is what the server-side gate
/// and the client's app-shell check both read.
/// </summary>
/// <remarks>
/// One claim is emitted per outstanding document, so a principal carrying no claim of this type is
/// compliant. Values are the <c>LegalDocumentType</c> names ("License", "TermsOfService"), letting the
/// client render only the outstanding half of the interstitial without a round trip.
///
/// It lives beside <see cref="PermissionClaims"/> for the same reason: the server writes these strings
/// onto the principal and the client reads them, so a single definition keeps the two from drifting.
/// </remarks>
public static class LegalClaims
{
    public const string PendingAcceptanceType = "legal_pending_acceptance";

    public const string License = "License";

    public const string TermsOfService = "TermsOfService";
}
