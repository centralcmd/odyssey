namespace Odyssey.Dtos.Finance;

/// <summary>
/// Which of a policy's three <b>contact</b> link collections names a given contact (issue #27 §7 #5).
/// Insured accounts are absent by construction: they link an <c>Account</c>, never a contact, and
/// cascade rather than restrict.
/// </summary>
public enum InsuranceLinkKind
{
    Insurer = 0,
    InsuredContact = 1,
    Beneficiary = 2,
}
