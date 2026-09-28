namespace Odyssey.Dtos.Finance;

// The contract smart-tag transactions envelope (issue #226 §5). Read-only response types, never bound
// to a Blazor form, so they use `init` accessors — the same stated exception as PagedResult<T>.

/// <summary>
/// Why a contract's smart-tag transaction match is structurally empty. Crosses the wire as an ORDINAL
/// (no string-enum converter is registered); ordinals are a wire contract and are never renumbered — a
/// new reason appends. Precedence when several apply: <see cref="NoSmartTags"/>, then
/// <see cref="NoContactParties"/>, then <see cref="InvalidTerm"/>.
/// </summary>
public enum ContractSmartTagEmptyReason
{
    /// <summary>The query ran; it may still have matched nothing.</summary>
    None = 0,

    /// <summary>The contract has no smart tags.</summary>
    NoSmartTags = 1,

    /// <summary>The contract has no party with a contact, so no merchant can match.</summary>
    NoContactParties = 2,

    /// <summary>The contract's start date falls after its end date, so the term window is empty.</summary>
    InvalidTerm = 3,
}

/// <summary>The scope the server actually applied to a contract's smart-tag transaction match.</summary>
public sealed record ContractSmartTagScope
{
    /// <summary>Inclusive lower bound applied to the transaction timestamp; <c>null</c> = unbounded.</summary>
    public DateTime? From { get; init; }

    /// <summary>Exclusive upper bound applied to the transaction timestamp; <c>null</c> = unbounded.</summary>
    public DateTime? ToExclusive { get; init; }

    /// <summary>Smart tags configured on the contract.</summary>
    public int SmartTagCount { get; init; }

    /// <summary>Distinct contacts holding a party role on the contract.</summary>
    public int PartyContactCount { get; init; }

    public ContractSmartTagEmptyReason EmptyReason { get; init; }
}

/// <summary>Matched-spend totals for one currency. No FX is applied and nothing sums across currencies.</summary>
public sealed record ContractSmartTagCurrencyTotal
{
    public required string CurrencyCode { get; init; }

    public int TransactionCount { get; init; }

    /// <summary>Sum of non-negative amounts; a zero amount counts here.</summary>
    public decimal TotalIn { get; init; }

    /// <summary>Absolute sum of negative amounts — a positive magnitude.</summary>
    public decimal TotalOut { get; init; }

    /// <summary><see cref="TotalIn"/> minus <see cref="TotalOut"/>, signed.</summary>
    public decimal Net { get; init; }
}

/// <summary>
/// Totals over the WHOLE matched set — independent of search, sort and paging. One row per currency,
/// ordered by currency code (ordinal); never <c>null</c>.
/// </summary>
public sealed record ContractSmartTagSummary
{
    public int TransactionCount { get; init; }

    public IReadOnlyList<ContractSmartTagCurrencyTotal> ByCurrency { get; init; } = [];
}

/// <summary>The response of <c>GET /api/contracts/{contractId}/smart-tag-transactions</c>.</summary>
public sealed record ContractSmartTagTransactionsResult
{
    public required ContractSmartTagScope Scope { get; init; }

    public required ContractSmartTagSummary Summary { get; init; }

    public required PagedResult<ExistingTransaction> Page { get; init; }
}
