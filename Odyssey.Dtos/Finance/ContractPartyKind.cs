namespace Odyssey.Dtos.Finance;

/// <summary>Which of the three polymorphic targets an <see cref="ExistingContractParty"/> links to.</summary>
/// <remarks>
/// Not persisted: derived from which target column is set. Ordinal <c>2</c> is a permanent hole — it was
/// <c>InsurancePolicy</c> until the design system reduced parties to one-of-two, and a stale client or
/// cached payload reading <c>2</c> must never come to mean something else. <see cref="Property"/>
/// therefore takes <c>3</c> (issue #208).
/// </remarks>
public enum ContractPartyKind
{
    Account = 0,
    Institution = 1,
    // 2 = InsurancePolicy, retired — permanent hole, never reused.
    Property = 3,
}
