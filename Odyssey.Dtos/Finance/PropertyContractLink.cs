namespace Odyssey.Dtos.Finance;

/// <summary>
/// One contract naming a property as a party, for the property record's Contracts section (issue #208).
/// One row per CONTRACT, carrying every role the property holds on it.
/// </summary>
/// <remarks>
/// Field-for-field the shape of <see cref="AccountContractLink"/>, kept as its own record rather than a
/// shared base: the account one is already on the wire, and DTOs are <c>sealed</c>. A guard test pins
/// the two shapes together. The route is gated on <c>properties.read</c> AND <c>contracts.read</c>.
/// </remarks>
public sealed record PropertyContractLink
{
    public required Guid ContractId { get; set; }

    public required string Name { get; set; }

    public ContractType Type { get; set; }

    /// <summary>The derived status — the same derivation the contracts list reports.</summary>
    public ContractStatus Status { get; set; }

    /// <summary>
    /// The roles the property holds on this contract, in party order (by party id). Never empty: a
    /// contract appears only because at least one of its parties names the property.
    /// </summary>
    public List<ContractPartyRole> Roles { get; set; } = new();
}
