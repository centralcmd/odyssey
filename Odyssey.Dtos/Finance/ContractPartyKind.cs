namespace Odyssey.Dtos.Finance;

/// <summary>Which of the three polymorphic targets a <see cref="ExistingContractParty"/> links to.</summary>
public enum ContractPartyKind
{
    Account = 0,
    Institution = 1,
    InsurancePolicy = 2,
}
