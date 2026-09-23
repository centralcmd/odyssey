namespace Odyssey.Dtos.Finance;

// Per-resource allowlisted sort keys for the server-side list endpoints (issue #277). Each member is
// a sortable column the resource's list surface exposes; the query params bind SortBy as one of these
// (an unbindable value is rejected, not silently coerced). Member names match the client's camelCase
// sort keys case-insensitively. A resource's service orders by the keys it can and stably falls back
// to its natural default for the rest.

/// <summary>Sortable keys for the accounts list.</summary>
public enum AccountSortBy
{
    Name,
    Balance,
    Type,
    Opened,
    TxnCount,
}

/// <summary>Sortable keys for the budgets list.</summary>
public enum BudgetSortBy
{
    StartDate,
    Name,
    EndDate,
}

/// <summary>Sortable keys for the budget-items list.</summary>
public enum BudgetItemSortBy
{
    Name,
    PlannedAmount,
    Category,
}

/// <summary>Sortable keys for the contracts list.</summary>
public enum ContractSortBy
{
    Name,
    StartDate,
    EndDate,
    Type,
    Status,
    /// <summary>Appended (issue #181) — never renumber the members above. Nulls sort last both ways.</summary>
    ReferenceNumber,
}

/// <summary>
/// Sortable keys for a contract's event log (issue #138 §5.1). <c>Description</c> and <c>Notes</c> are
/// deliberately absent: sorting a record by a paragraph of prose is not a use case, and every key
/// costs an index decision.
/// </summary>
public enum ContractEventSortBy
{
    /// <summary>The default — newest first.</summary>
    OccurredAt,
    Title,
    Type,
    CreatedAtUtc,
}

/// <summary>Sortable keys for the currencies list.</summary>
public enum CurrencySortBy
{
    Code,
    Name,
    Symbol,
    MinorUnits,
    Status,
}

/// <summary>Sortable keys for the exchange-rates list.</summary>
public enum ExchangeRateSortBy
{
    AsOf,
    Pair,
    Rate,
    Status,
    CreatedAt,

    /// <summary>The reciprocal rate (1 / Rate) shown in the Inverse column. Rate is constrained
    /// greater than zero on every write path, so this is exactly <see cref="Rate"/> reversed.</summary>
    Inverse,
}

/// <summary>Sortable keys for the files list.</summary>
public enum FileSortBy
{
    Uploaded,
    Name,
    Size,
    Kind,
}

/// <summary>Sortable keys for the tax-statements list.</summary>
public enum TaxStatementSortBy
{
    FiscalYear,
    Name,
    Status,
}

/// <summary>Sortable keys for the transactions list.</summary>
public enum TransactionSortBy
{
    Date,
    Amount,
    Desc,
    Contact,
    Account,
    Status,
}

/// <summary>Sortable keys for the transaction-tags list.</summary>
public enum TransactionTagSortBy
{
    Name,
    Description,
    Status,
}

/// <summary>Sortable keys for the file-analysis audit log.</summary>
public enum FileAnalysisAuditSortBy
{
    At,
    Status,
}
