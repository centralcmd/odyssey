namespace Odyssey.Dtos.Finance;

/// <summary>
/// A class of link that blocks a contact delete, and that the transactional detach valve would destroy
/// (issue #157 §7.3). What the valve requires of the caller is decided <b>per class actually present</b>:
/// demanding <c>contracts.update</c> of a caller whose contact has no contract links would de-authorize
/// a request that is legitimate today, and demanding neither would let a <c>contacts.delete</c> holder
/// destroy rows in two other domains.
/// </summary>
public enum ContactDeleteBlockerClass
{
    /// <summary>Insurer, insured-contact and beneficiary rows on insurance policies. Requires <c>insurance.update</c>.</summary>
    InsuranceLink = 0,

    /// <summary>Contract-party rows in the <c>Beneficiary</c> role. Requires <c>contracts.update</c>.</summary>
    ContractBeneficiary = 1,
}
