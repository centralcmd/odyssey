namespace Odyssey.Dtos.Finance;

/// <summary>
/// A class of link that blocks a contact delete, and that the transactional detach valve would destroy
/// (issue #157 §7.3). What the valve requires of the caller is decided <b>per class actually present</b>:
/// demanding <c>contracts.update</c> of a caller whose contact has no contract links would de-authorize
/// a request that is legitimate today, and demanding nothing would let a <c>contacts.delete</c> holder
/// destroy rows in another domain.
/// </summary>
/// <remarks>
/// Ordinal <c>0</c> is a permanent hole: it held <c>InsuranceLink</c> until the standalone
/// insurance-policy feature was removed. One member today, but the per-class machinery stays — it is
/// what keeps the claim determination and the destruction reading one snapshot (CWE-367).
/// </remarks>
public enum ContactDeleteBlockerClass
{
    /// <summary>Contract-party rows in the <c>Beneficiary</c> role. Requires <c>contracts.update</c>.</summary>
    ContractBeneficiary = 1,
}
