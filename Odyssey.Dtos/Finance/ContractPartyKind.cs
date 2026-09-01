namespace Odyssey.Dtos.Finance;

/// <summary>Which of the two polymorphic targets an <see cref="ExistingContractParty"/> links to.</summary>
/// <remarks>
/// A third member, <c>InsurancePolicy = 2</c>, existed until the design system reduced parties to
/// one-of-two. The two surviving ordinals are unchanged, so no persisted or wire value shifts meaning.
/// </remarks>
public enum ContractPartyKind
{
    Account = 0,
    Institution = 1,
}
