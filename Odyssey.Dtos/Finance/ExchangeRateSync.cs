namespace Odyssey.Dtos.Finance;

/// <summary>
/// Where a fetched exchange rate came from (issue #349 §4). A manual rate has no provider — the column is
/// <c>NULL</c> — so there is deliberately no <c>Manual</c> member and <c>0</c> is a permanent hole.
/// </summary>
public enum ExchangeRateProvider
{
    NorgesBank = 1,
}

/// <summary>Whether a rate was entered by hand or fetched; derived from <see cref="ExchangeRateProvider"/>.</summary>
public enum ExchangeRateSource
{
    Manual,
    Fetched,
}

/// <summary>What started a sync run. A purge holds the same run guard, so it is logged the same way.</summary>
public enum ExchangeRateSyncTrigger
{
    Scheduled = 0,
    Manual = 1,
    Purge = 2,
}

public enum ExchangeRateSyncRunStatus
{
    Running = 0,
    Succeeded = 1,
    PartiallySucceeded = 2,
    Failed = 3,
    Skipped = 4,
}

/// <summary>Why a run failed or only partly succeeded (issue #349 §9). <c>0</c> is a hole: a run without an error has <c>null</c>.</summary>
public enum ExchangeRateSyncErrorKind
{
    ProviderUnavailable = 1,
    ProviderRateLimited = 2,
    ProviderMalformed = 3,
    UnsupportedCurrency = 4,
    SettingsUnusable = 5,
    TimeZoneUnavailable = 6,
    Cancelled = 7,
    Abandoned = 8,

    /// <summary>An unforeseen fault inside the run itself, not the provider's — logged with its exception.</summary>
    Internal = 9,
}

/// <summary>The scheduler's state, derived from the run log on every read.</summary>
public enum ExchangeRateSyncState
{
    Idle,
    Scheduled,
    CatchingUp,
    Running,
    BackingOff,
}
