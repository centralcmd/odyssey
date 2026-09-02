namespace Odyssey.Dtos.Finance;

/// <summary>
/// Which of a policy's <b>four</b> link collections a party belongs to — the role the New/Edit party
/// dialog picks (design system, <c>AddPolicyPartyModal</c>).
/// </summary>
/// <remarks>
/// Deliberately a separate enum from <see cref="InsuranceLinkKind"/>, which answers a different
/// question: which of the three <i>contact</i> collections names a given contact, and is what the
/// contact-deletion blockers speak. Widening that one to carry <see cref="InsuredAccount"/> would make
/// a contact blocker able to name a collection that never holds a contact.
/// </remarks>
public enum InsurancePartyRole
{
    Insurer = 0,
    InsuredAccount = 1,
    InsuredContact = 2,
    Beneficiary = 3,
}
