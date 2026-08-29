namespace Odyssey.Dtos.Finance;

/// <summary>Account counts bucketed by derived status (issue #372). Status is derived from the
/// <c>Archived</c>/<c>Closed</c> columns: archived wins over closed, closed over open.</summary>
public sealed record AccountStatusCounts
{
    public int Open { get; set; }

    public int Closed { get; set; }

    public int Archived { get; set; }
}

/// <summary>A typed count used by the account summary rollup (mirrors <c>ContractTypeCount</c>).</summary>
public sealed record AccountTypeCount
{
    public AccountType Type { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// One account's contribution to the allocation donuts: its name, its own currency, and its signed
/// effective value under the net-worth replace policy (issue #182 §9 — the in-force estimate when one
/// exists, otherwise the transaction balance).
/// </summary>
public sealed record AccountAllocation
{
    public required Guid AccountId { get; set; }

    public required string Name { get; set; }

    public required string CurrencyCode { get; set; }

    /// <summary>Signed: positive accounts are assets, negative are liabilities.</summary>
    public decimal Value { get; set; }
}

/// <summary>
/// Summary rollup for the accounts page header (issue #372): counts by status and type, the combined
/// value, and the per-account allocation rows the asset/liability donuts render. Replaces the page's
/// former whole-table fetch — every figure here is computed server-side from a lean projection.
/// </summary>
/// <remarks>
/// Archived accounts are counted in <see cref="CountsByStatus"/> but excluded from
/// <see cref="CountsByType"/>, the value aggregates and <see cref="Allocations"/> — closed accounts
/// still count toward all of them, matching the page's long-standing balance rule. The value
/// aggregates are naive cross-currency sums (no FX on this path); the per-account rows keep their own
/// currency so the legend can format each one correctly.
/// </remarks>
public sealed record AccountSummary
{
    public int TotalAccounts { get; set; }

    public required AccountStatusCounts CountsByStatus { get; set; }

    /// <summary>Per-type counts over the live (non-archived) set — the "By type" breakdown tile.</summary>
    public List<AccountTypeCount> CountsByType { get; set; } = new();

    /// <summary>Combined effective value across non-archived accounts (assets plus liabilities).</summary>
    public decimal CombinedValue { get; set; }

    public decimal TotalAssets { get; set; }

    /// <summary>The signed total of the negative-value accounts (so it reads negative).</summary>
    public decimal TotalLiabilities { get; set; }

    /// <summary>Non-archived accounts with a non-zero effective value, largest asset first.</summary>
    public List<AccountAllocation> Allocations { get; set; } = new();
}
