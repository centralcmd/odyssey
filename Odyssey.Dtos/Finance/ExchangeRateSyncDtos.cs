using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>One currency's sync configuration (issue #349 §5.1). Carries no user id.</summary>
public sealed record ExistingExchangeRateSyncCurrency
{
    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    [StringLength(256)]
    public required string Name { get; set; }

    /// <summary>Whether the provider publishes it; null while the provider list is unavailable — unknown, never false.</summary>
    public bool? Supported { get; set; }

    /// <summary>For NOK: true while any other currency is enabled, since it is part of every fetched pair.</summary>
    public bool Enabled { get; set; }

    /// <summary>True for NOK only: the base, with no rows of its own.</summary>
    public bool Locked { get; set; }

    public DateOnly? HistoryStartDate { get; set; }

    public DateOnly EffectiveHistoryStartDate { get; set; }

    public DateOnly? FirstAvailableDate { get; set; }

    public DateOnly? OldestFetchedDate { get; set; }

    public DateOnly? NewestFetchedDate { get; set; }

    public bool BackfillComplete { get; set; }

    /// <summary>Fetched rows with this currency on either side — what a purge would delete. 0 for NOK.</summary>
    public int FetchedRowCount { get; set; }
}

/// <summary>Turns sync on or off for one currency and sets its optional start-date override.</summary>
public sealed record ExchangeRateSyncCurrencyUpdate
{
    /// <summary>Nullable so an omitted value is a <c>400</c>, never a silent <c>false</c>.</summary>
    [Required]
    public bool? Enabled { get; set; }

    /// <summary>Null falls back to the global setting. On or after 1980-01-01 and on or before today.</summary>
    public DateOnly? HistoryStartDate { get; set; }
}

/// <summary>One sync run or purge (issue #349 §5.4). Carries no user id.</summary>
public sealed record ExistingExchangeRateSyncRun
{
    public Guid Id { get; set; }

    public ExchangeRateSyncTrigger Trigger { get; set; }

    public ExchangeRateSyncRunStatus Status { get; set; }

    public ExchangeRateSyncErrorKind? ErrorKind { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public DateOnly? SpanFrom { get; set; }

    public DateOnly? SpanTo { get; set; }

    public List<string> CurrencyCodes { get; set; } = [];

    public int RowsInserted { get; set; }

    public int RowsSkipped { get; set; }

    public int RowsRejected { get; set; }

    public int RowsDeleted { get; set; }
}

/// <summary>The run or purge holding the guard right now.</summary>
public sealed record ExchangeRateSyncCurrentRun
{
    public Guid Id { get; set; }

    public ExchangeRateSyncTrigger Trigger { get; set; }

    public DateTime StartedAtUtc { get; set; }
}

/// <summary>
/// The sync panel's status (issue #349 §5.3). The constants and names after
/// <see cref="UnsupportedEnabledCodes"/> are the server's own, sent so the client prints them instead of
/// holding copies.
/// </summary>
public sealed record ExchangeRateSyncStatus
{
    public ExchangeRateProvider Provider { get; set; }

    public string Attribution { get; set; } = string.Empty;

    public ExchangeRateSyncState State { get; set; }

    public int EnabledCount { get; set; }

    public int IntervalHours { get; set; }

    public DateOnly? GlobalHistoryStartDate { get; set; }

    public DateOnly EffectiveGlobalHistoryStartDate { get; set; }

    public bool BackfillComplete { get; set; }

    /// <summary>The last FINISHED run, any trigger — never a running one.</summary>
    public ExistingExchangeRateSyncRun? LastRun { get; set; }

    public ExchangeRateSyncCurrentRun? CurrentRun { get; set; }

    /// <summary>When <c>POST …/runs</c> stops returning <c>429</c>; null when no cooldown applies.</summary>
    public DateTime? ManualRunAvailableAtUtc { get; set; }

    public bool SupportedListAvailable { get; set; }

    public DateTime? NextRunAtUtc { get; set; }

    public List<string> UnsupportedEnabledCodes { get; set; } = [];

    public int ConsecutiveFailures { get; set; }

    public int CatchUpDelayMinutes { get; set; }

    public int RunTimeoutMinutes { get; set; }

    public int ManualRunCooldownSeconds { get; set; }

    public string BaseCurrencyCode { get; set; } = string.Empty;

    public string ProviderDisplayName { get; set; } = string.Empty;

    public string FetchedRateLocalTime { get; set; } = string.Empty;

    public string FetchedRateTimeZone { get; set; } = string.Empty;

    public string FetchedRateTimeZoneLabel { get; set; } = string.Empty;
}

public sealed record ExchangeRateSyncRunStarted
{
    public Guid RunId { get; set; }
}

public sealed record ExchangeRatePurgeResult
{
    public int RowsDeleted { get; set; }
}
