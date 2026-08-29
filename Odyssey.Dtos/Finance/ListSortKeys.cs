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
}

/// <summary>Sortable keys for the files list.</summary>
public enum FileSortBy
{
    Uploaded,
    Name,
    Size,
    Kind,
}

/// <summary>Sortable keys for the insurance-policies list.</summary>
public enum InsuranceSortBy
{
    Name,
    Type,
    RenewalEnd,
    Premium,
}

/// <summary>Sortable keys for the subscriptions list. <c>Interval</c> sorts by the enum's numeric order (Daily &lt; Weekly &lt; Monthly &lt; Yearly).</summary>
public enum SubscriptionSortBy
{
    Name,
    Amount,
    StartDate,
    Interval,
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
