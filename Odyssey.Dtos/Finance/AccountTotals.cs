using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Total assets, total liabilities and net worth converted into the user's main currency, plus the
/// accounts and properties whose value could not be converted (no rate to the main currency).
/// </summary>
/// <remarks>
/// <para>
/// <b>Property value is part of the figure only when <see cref="PropertiesIncluded"/> is true</b>
/// (issue #214). It is then inside <see cref="TotalAssets"/> and <see cref="NetWorth"/>, and also
/// reported on its own as <see cref="PropertyValue"/>, so the accounts-only figure stays readable.
/// Inclusion is decided by the caller's claims alone — <c>properties.read</c> and
/// <c>properties.estimates.read</c> — never by whether any property exists, so the flag discloses
/// nothing about the data. A consumer must branch on the flag, never on the nullness of a member.
/// </para>
/// <para>
/// Every property member is <b>non-<c>required</c></b> and defaults to its "not included" value, so a
/// payload from an older API deserializes as an accounts-only figure rather than failing.
/// </para>
/// </remarks>
public sealed record AccountTotals
{
    [StringLength(3)]
    public required string MainCurrencyCode { get; set; }

    /// <summary>Account assets, plus <see cref="PropertyValue"/> when properties are included.</summary>
    public required decimal TotalAssets { get; set; }

    /// <summary>Accounts only — a property contributes no liability (issue #214 D2).</summary>
    public required decimal TotalLiabilities { get; set; }

    /// <summary><see cref="TotalAssets"/> − <see cref="TotalLiabilities"/>.</summary>
    public required decimal NetWorth { get; set; }

    public List<UnconvertedAccount> UnconvertedAccounts { get; set; } = [];

    /// <summary>
    /// Whether property value is part of this figure. Claim-decided, never data-decided: <c>false</c>
    /// for a caller lacking either property claim however many properties exist.
    /// </summary>
    public bool PropertiesIncluded { get; set; }

    /// <summary>
    /// The converted in-force estimated value of every property held now. <c>null</c> when not
    /// included; <c>0</c> when included but nothing was valued or converted.
    /// </summary>
    public decimal? PropertyValue { get; set; }

    /// <summary>Properties held now, valued and converted. <c>0</c> when not included.</summary>
    public int ContributingPropertyCount { get; set; }

    /// <summary>Properties held now with no estimate in force, so they count as 0. <c>0</c> when not included.</summary>
    public int UnvaluedPropertyCount { get; set; }

    /// <summary>Properties held now and valued, but with no rate to the main currency. Empty when not included.</summary>
    public List<UnconvertedProperty> UnconvertedProperties { get; set; } = [];

    /// <summary>
    /// The figure's composition — one row per account and property that contributed a non-zero,
    /// converted value — for the dashboard's allocation donuts. A row's sign is its contribution to
    /// <see cref="NetWorth"/>, so the rows sum to it exactly: positive rows are the asset slices,
    /// negative rows the liability slices. Unclassified accounts and anything unconverted or unvalued
    /// contribute nothing and are absent. Property rows appear only when
    /// <see cref="PropertiesIncluded"/> is true.
    /// <para>
    /// The split is by <b>sign, not by account type</b> — deliberately, as the Accounts page's donuts
    /// and the design system's <c>AllocationDonuts</c> both split. An overdrawn asset account is a
    /// negative row and an overpaid credit card a positive one, so the sum of the positive rows equals
    /// <see cref="TotalAssets"/> (and the negative rows <see cref="TotalLiabilities"/>) only while no
    /// account is on the "wrong" side of zero. <see cref="NetWorth"/> is equal either way.
    /// </para>
    /// </summary>
    public List<NetWorthAllocation> Allocations { get; set; } = [];
}

/// <summary>What a <see cref="NetWorthAllocation"/> row is.</summary>
public enum NetWorthAllocationKind
{
    Account = 1,
    Property = 2,
}

/// <summary>
/// One contributor to <see cref="AccountTotals.NetWorth"/>, converted into the main currency. A
/// purpose-built minimal projection: the id, the name and the converted value, never an address,
/// registration number, VIN, note or the value in its own currency.
/// </summary>
public sealed record NetWorthAllocation
{
    [EnumDataType(typeof(NetWorthAllocationKind))]
    public required NetWorthAllocationKind Kind { get; set; }

    public required Guid Id { get; set; }

    [StringLength(256)]
    public required string Name { get; set; }

    /// <summary>Signed contribution to net worth, in <see cref="AccountTotals.MainCurrencyCode"/>.</summary>
    public required decimal Value { get; set; }
}

/// <summary>An account that contributed 0 to the totals because no rate to the main currency exists.</summary>
public sealed record UnconvertedAccount
{
    public required Guid AccountId { get; set; }

    [StringLength(256)]
    public required string Name { get; set; }

    [StringLength(3)]
    public required string CurrencyCode { get; set; }
}

/// <summary>
/// A held, valued property that contributed 0 because no rate to the main currency exists (issue #214).
/// A purpose-built minimal projection: no address, registration number, VIN, notes, description or
/// estimate value — those are <c>properties.read</c> personal data and go in no cross-claim projection.
/// </summary>
public sealed record UnconvertedProperty
{
    public required Guid PropertyId { get; set; }

    [StringLength(256)]
    public required string Name { get; set; }

    [StringLength(3)]
    public required string CurrencyCode { get; set; }
}
