using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;

namespace Odyssey.Dtos.Finance;

// Per-endpoint list-query records (issue #277). Each closes the generic QueryParams base over its own
// sort-key enum (search + SortBy + sort direction + offset/limit) and adds only the filters that
// endpoint exposes, so a list action binds — and passes to its service — the whole query as one object.
// SortBy and the filters are their real types (sort-key enums, domain enums, the derived status enums,
// Guid), so an unbindable value is rejected rather than silently dropped; only currency codes stay
// strings. Array filters bind case-insensitively from the matching camelCase query key
// (types → Types, accountIds → AccountIds).

/// <summary>Accounts list query: filter by account type(s) and derived status(es).</summary>
public sealed class AccountsQueryParams : QueryParams<AccountSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public AccountType[]? Types { get; set; }

    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public AccountStatus[]? Statuses { get; set; }
}

/// <summary>Budgets list query: filter by archival status.</summary>
public sealed class BudgetsQueryParams : QueryParams<BudgetSortBy>
{
    public ArchivalStatus? Status { get; set; }
}

/// <summary>Budget-items list query: filter by owning budget and category type(s).</summary>
public sealed class BudgetItemsQueryParams : QueryParams<BudgetItemSortBy>
{
    public Guid? BudgetId { get; set; }

    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public BudgetCategoryType[]? Categories { get; set; }
}

/// <summary>Contracts list query: filter by contract type(s) and derived status(es).</summary>
public sealed class ContractsQueryParams : QueryParams<ContractSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public ContractType[]? Types { get; set; }

    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public ContractStatus[]? Statuses { get; set; }
}

/// <summary>Currencies list query: filter by archival status.</summary>
public sealed class CurrenciesQueryParams : QueryParams<CurrencySortBy>
{
    public ArchivalStatus? Status { get; set; }
}

/// <summary>Exchange-rates list query: filter by target currency code(s) and current/historical status.</summary>
public sealed class ExchangeRatesQueryParams : QueryParams<ExchangeRateSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public string[]? ToCurrencies { get; set; }

    public ExchangeRateStatus? Status { get; set; }
}

/// <summary>Files list query: filter by upload date bounds (UTC) and derived kind.</summary>
public sealed class FilesQueryParams : QueryParams<FileSortBy>
{
    public DateTime? UploadedFromUtc { get; set; }

    public DateTime? UploadedToUtc { get; set; }

    public FileKind? Kind { get; set; }
}

/// <summary>Insurance-policies list query: filter by policy type(s) and derived coverage status(es).</summary>
public sealed class InsurancePoliciesQueryParams : QueryParams<InsuranceSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public InsurancePolicyType[]? Types { get; set; }

    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public CoverageStatus[]? Statuses { get; set; }

    /// <summary>
    /// Matches policies where the given contact appears in <b>any</b> of the three contact collections
    /// — insurers, insured contacts or beneficiaries (issue #27 §6). <b>API-only in v1</b>: there is no
    /// filter-bar control and no query-string binding on the page (Non-Goal 7). A row does not indicate
    /// <i>why</i> it matched; a future filter UI that needs to explain the match will need a
    /// matched-kind projection.
    /// </summary>
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public Guid[]? ContactIds { get; set; }
}

/// <summary>Subscriptions list query: filter by billing interval(s) and derived lifecycle status(es).</summary>
public sealed class SubscriptionsQueryParams : QueryParams<SubscriptionSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public BillingInterval[]? Intervals { get; set; }

    /// <summary>Filter by the derived single lifecycle status (Active/Paused/Ended/Archived); empty = all.</summary>
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public SubscriptionStatusFilter[]? Statuses { get; set; }
}

/// <summary>Tax-statements list query: filter by status(es), including the derived Archived bucket.</summary>
public sealed class TaxStatementsQueryParams : QueryParams<TaxStatementSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public TaxStatementStatusFilter[]? Statuses { get; set; }
}

/// <summary>Transactions list query: filter by account(s), status(es), tag(s), direction and date bounds.</summary>
public sealed class TransactionsQueryParams : QueryParams<TransactionSortBy>
{
    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public Guid[]? AccountIds { get; set; }

    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public TransactionStatus[]? Statuses { get; set; }

    [MaxLength(ListDefaults.MaxFilterArrayLength)]
    public Guid[]? TagIds { get; set; }

    public TransactionDirection? Direction { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

/// <summary>Transaction-tags list query: filter by archival status.</summary>
public sealed class TransactionTagsQueryParams : QueryParams<TransactionTagSortBy>
{
    public ArchivalStatus? Status { get; set; }
}
