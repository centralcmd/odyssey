namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// The fixed product limits of the exchange-rate sync (issue #349 §3 "Fetch limits"). Constants, not
/// settings: they bound one client's load on a public service and one run's duration, and the status
/// endpoint sends the ones the panel prints so the client holds no copy.
/// </summary>
public static class ExchangeRateSyncConstants
{
    /// <summary>The currency every fetched pair is quoted against, and the conversion hub.</summary>
    public const string BaseCurrencyCode = "NOK";

    public const string ProviderDisplayName = "Norges Bank";

    public const string Attribution = "Source: Norges Bank (NLOD 2.0)";

    /// <summary>Fetched rates are stamped at this local time, when Norges Bank publishes.</summary>
    public static readonly TimeOnly FetchedRateLocalTime = new(16, 0);

    public const string FetchedRateTimeZone = "Europe/Oslo";

    public const string FetchedRateTimeZoneLabel = "Norwegian time";

    public const int DaysPerRequest = 365;

    public const int RequestsPerRun = 10;

    /// <summary>Forward fill may use at most this many of <see cref="RequestsPerRun"/>, so backfill always progresses.</summary>
    public const int ForwardFillRequestsPerRun = 5;

    public static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>A <c>Retry-After</c> longer than this is honoured by waiting for the next tick instead.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    public const int CatchUpDelayMinutes = 5;

    public const int RunTimeoutMinutes = 10;

    public const int ManualRunCooldownSeconds = 60;

    public static readonly TimeSpan SchedulerTick = TimeSpan.FromSeconds(60);

    /// <summary>After this many consecutive failed sync runs the interval doubles, capped at <see cref="MaxBackoffHours"/>.</summary>
    public const int BackoffAfterFailures = 3;

    public const int MaxBackoffHours = 168;

    public const long MaxResponseBytes = 10L * 1024 * 1024;

    public static readonly TimeSpan SupportedListTtl = TimeSpan.FromHours(24);

    public static readonly TimeSpan SupportedListFailureTtl = TimeSpan.FromMinutes(5);

    /// <summary>A series whose newest observation is older than this is not offered.</summary>
    public const int SupportedFreshnessDays = 30;

    public const int RunRetention = 500;

    /// <summary>A purge removes fetched rows in batches of this size, clearing the change tracker between them.</summary>
    public const int PurgeBatchSize = 5000;

    public const string UserAgent = "Odyssey";
}
