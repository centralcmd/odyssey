namespace Odyssey.Dtos.Finance;

/// <summary>
/// One contract naming an account as a party, for the account record's Contracts section. One row per
/// CONTRACT, carrying every role the account holds on it — a record may be linked in several roles, and
/// listing the contract once per link would read as several agreements.
/// </summary>
/// <remarks>
/// A deliberately lean projection: the fields the tile renders and nothing else. The route is gated on
/// <c>accounts.read</c> AND <c>contracts.read</c>, so it discloses nothing the contract detail would
/// not, but the full contract is one click away on the Contracts page and does not need to travel here.
/// </remarks>
public sealed record AccountContractLink
{
    public required Guid ContractId { get; set; }

    public required string Name { get; set; }

    public ContractType Type { get; set; }

    /// <summary>The derived status — the same derivation the contracts list reports.</summary>
    public ContractStatus Status { get; set; }

    /// <summary>
    /// The roles the account holds on this contract, in party order (by party id, the order the
    /// contract detail reads its parties in). Never empty: a contract appears only because at least
    /// one of its parties names the account.
    /// </summary>
    public List<ContractPartyRole> Roles { get; set; } = new();
}
