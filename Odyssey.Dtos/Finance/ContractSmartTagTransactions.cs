namespace Odyssey.Dtos.Finance;

// The contract smart-tag transactions envelope (issue #226 §5). Read-only response types, never bound
// to a Blazor form, so they use `init` accessors — the same stated exception as PagedResult<T>.

/// <summary>
/// Why a contract's smart-tag transaction match is structurally empty. Crosses the wire as an ORDINAL
/// (no string-enum converter is registered); ordinals are a wire contract and are never renumbered — a
/// new reason appends. Precedence when several apply: <see cref="NoSmartTags"/>, then
/// <see cref="NoContactParties"/>.
/// </summary>
public enum ContractSmartTagEmptyReason
{
    /// <summary>The query ran; it may still have matched nothing.</summary>
    None = 0,

    /// <summary>The contract has no smart tags.</summary>
    NoSmartTags = 1,

    /// <summary>The contract has no party with a contact, so no merchant can match.</summary>
    NoContactParties = 2,

    /// <summary>
    /// RESERVED — no longer emitted (issue #308). It meant "the term ends before it starts"; the match
    /// now reads the smart-tag scope, whose order a CHECK constraint holds, so an empty window is
    /// unrepresentable. Kept so the ordinal is never reused.
    /// </summary>
    InvalidTerm = 3,
}

/// <summary>The scope the server actually applied to a contract's smart-tag transaction match.</summary>
public sealed record ContractSmartTagScope
{
    /// <summary>Inclusive lower bound applied to the transaction timestamp; <c>null</c> = unbounded.</summary>
    public DateTime? From { get; set; }

    /// <summary>Exclusive upper bound applied to the transaction timestamp; <c>null</c> = unbounded.</summary>
    public DateTime? ToExclusive { get; set; }

    /// <summary>Smart tags configured on the contract.</summary>
    public int SmartTagCount { get; set; }

    /// <summary>Distinct contacts holding a party role on the contract.</summary>
    public int PartyContactCount { get; set; }

    public ContractSmartTagEmptyReason EmptyReason { get; set; }
}

/// <summary>Matched-spend totals for one currency. No FX is applied and nothing sums across currencies.</summary>
public sealed record ContractSmartTagCurrencyTotal
{
    public required string CurrencyCode { get; set; }

    public int TransactionCount { get; set; }

    /// <summary>Sum of non-negative amounts; a zero amount counts here.</summary>
    public decimal TotalIn { get; set; }

    /// <summary>Absolute sum of negative amounts — a positive magnitude.</summary>
    public decimal TotalOut { get; set; }

    /// <summary><see cref="TotalIn"/> minus <see cref="TotalOut"/>, signed.</summary>
    public decimal Net { get; set; }
}

/// <summary>
/// Totals over the WHOLE matched set — independent of search, sort and paging. One row per currency,
/// ordered by currency code (ordinal); never <c>null</c>.
/// </summary>
public sealed record ContractSmartTagSummary
{
    public int TransactionCount { get; set; }

    public IReadOnlyList<ContractSmartTagCurrencyTotal> ByCurrency { get; set; } = [];
}

/// <summary>The response of <c>GET /api/contracts/{contractId}/smart-tag-transactions</c>.</summary>
public sealed record ContractSmartTagTransactionsResult
{
    public required ContractSmartTagScope Scope { get; set; }

    public required ContractSmartTagSummary Summary { get; set; }

    public required PagedResult<ExistingTransaction> Page { get; set; }
}
