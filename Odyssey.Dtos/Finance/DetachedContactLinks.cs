namespace Odyssey.Dtos.Finance;

/// <summary>
/// What <c>DELETE /api/contacts/{id}?detachBlockingLinks=true</c> destroyed (issue #157 §5.4): the
/// contract-party rows removed and the contracts affected, all in the one transaction that also
/// deleted the contact.
///
/// <para>
/// Carries no contact name and no contract name — the caller asked to erase a contact, so the response
/// re-stating its name would defeat the point, and the contract names are not needed to describe what
/// was removed. The ids are enough to go and look.
/// </para>
/// </summary>
public sealed record DetachedContactLinks
{
    /// <summary>
    /// Contract-party rows in the <c>Beneficiary</c> role that the transaction destroyed. Those are
    /// the only class of link that blocks a contact delete.
    /// </summary>
    public int ContractBeneficiaryLinks { get; set; }

    /// <summary>The contracts that lost at least one beneficiary party. Ids only, for the reason above.</summary>
    public List<Guid> AffectedContractIds { get; set; } = new();
}
