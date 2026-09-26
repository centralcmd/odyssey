using Odyssey.Dtos;
namespace Odyssey.Dtos.Finance;

/// <summary>
/// Minimal, data-minimised projection of an account linked as a contract party (issue #174 §10 #2).
/// Deliberately drops <c>AccountNumber</c>, balances and notes so a caller holding only
/// <c>contracts.read</c> cannot read the richer record gated by <c>accounts.read</c>.
/// </summary>
public sealed record ContractAccountReference
{
    public required Guid AccountId { get; set; }

    public required string Name { get; set; }

    public AccountType Type { get; set; }
}

/// <summary>
/// Minimal, data-minimised projection of a contact ("institution") linked as a contract party
/// (issue #174 §10 #2). Drops <c>OrganizationNumber</c> and free-text <c>Description</c> so a
/// <c>contracts.read</c>-only caller cannot read the record gated by <c>contacts.read</c>.
/// </summary>
public sealed record ContractContactReference
{
    public required Guid ContactId { get; set; }

    public required string Name { get; set; }

    public ContactType Type { get; set; }
}

/// <summary>
/// Minimal, data-minimised projection of a property linked as a contract party (issue #208 §7.3).
/// Carries the id, name and type and nothing else: the description, notes, currency, dates, estimates
/// and every detail-row field (address, cadastral numbers, registration number, VIN) stay behind
/// <c>properties.read</c>, so a <c>contracts.read</c>-only caller cannot read the richer record.
/// </summary>
public sealed record ContractPropertyReference
{
    public required Guid PropertyId { get; set; }

    public required string Name { get; set; }

    public PropertyType Type { get; set; }
}
