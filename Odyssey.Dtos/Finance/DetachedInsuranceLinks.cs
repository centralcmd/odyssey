namespace Odyssey.Dtos.Finance;

/// <summary>
/// What <c>DELETE /api/contacts/{id}?detachInsuranceLinks=true</c> destroyed (issue #27 §7 #6): the
/// link rows removed per kind and the policies affected, all in the one transaction that also deleted
/// the contact.
///
/// <para>
/// Carries no contact name and no policy name — the caller asked to erase a contact, so the response
/// re-stating its name would defeat the point, and the policy names are not needed to describe what was
/// removed. The ids are enough to go and look.
/// </para>
/// </summary>
public sealed record DetachedInsuranceLinks
{
    public List<InsuranceLinkKindCount> Kinds { get; set; } = new();

    public int TotalLinks { get; set; }

    /// <summary>The policies that lost at least one link.</summary>
    public List<Guid> AffectedPolicyIds { get; set; } = new();
}
