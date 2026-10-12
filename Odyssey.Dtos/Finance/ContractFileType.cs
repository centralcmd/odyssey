namespace Odyssey.Dtos.Finance;

public enum ContractFileType
{
    /// <summary>
    /// The executed agreement — the document of record. Self-asserted by any <c>contracts.update</c>
    /// holder, so it is unverified metadata and never evidence that the agreement was executed.
    /// </summary>
    Signed = 0,
    Amendment = 1,
    Correspondence = 2,
    Other = 3,

    /// <summary>
    /// A pre-signature version of the agreement (proposal, negotiation or review draft). A label only —
    /// it has no effect on contract status. Appended at 4: the ordinal is a wire and persistence contract.
    /// </summary>
    Draft = 4,
}
