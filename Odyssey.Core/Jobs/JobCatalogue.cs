using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Core.Weather;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Core.Jobs;

/// <summary>
/// One background job as the run log knows it (issue #409 §3.1). The overview, the stale-run sweep, the manual-run
/// endpoint and its authorization all read this entry, and every value the client needs travels on the overview DTO —
/// nothing here is restated client-side (A10).
/// </summary>
/// <param name="JobType">The job, and the run log's discriminator.</param>
/// <param name="JobClass">The <see cref="IPeriodicJob"/> that runs it; a guard test matches the worker's registrations.</param>
/// <param name="Tick">The job's interval.</param>
/// <param name="Cadence">
/// The expected time between scheduled runs, for a job that has one; <c>null</c> for a work-driven job. The
/// exchange-rate sync's cadence comes from its schedule setting, so it is <see cref="DynamicCadence"/> instead.
/// </param>
/// <param name="DynamicCadence">The cadence is read at runtime (the exchange-rate sync's interval setting).</param>
/// <param name="StaleAfter">How long a <c>Running</c> row may go without activity before the sweep abandons it.</param>
/// <param name="ManualRunClaims">The claims a manual run needs beyond <c>system-settings.update</c> (S1). Never sent.</param>
/// <param name="DetailReadClaim">The claim beyond <c>system-settings.read</c> that reveals the job's own fields (A1/S5).</param>
public sealed record JobCatalogueEntry(
    JobType JobType,
    Type JobClass,
    TimeSpan Tick,
    TimeSpan? Cadence,
    bool DynamicCadence,
    TimeSpan StaleAfter,
    IReadOnlyList<string> ManualRunClaims,
    string? DetailReadClaim)
{
    /// <summary>Every job writes its heartbeat at most this often (§3.3 step 1).</summary>
    public TimeSpan HeartbeatInterval => JobRunRecorder.HeartbeatInterval;

    /// <summary><c>max(3 × heartbeat interval, 300 s)</c> (A3).</summary>
    public int NotTickingAfterSeconds => Math.Max(3 * (int)HeartbeatInterval.TotalSeconds, 300);

    public bool IsWorkDriven => Cadence is null && !DynamicCadence;
}

/// <summary>The code-declared job catalogue (issue #409 §3.1): each recorded job exactly once.</summary>
public static class JobCatalogue
{
    /// <summary>Every claim a manual run needs, before the job's own: the action policy on the endpoint.</summary>
    public const string ManualRunBaseClaim = PermissionClaims.SystemSettingsUpdate;

    /// <summary>The claim every endpoint of the page needs: the controller's class-level policy.</summary>
    public const string ReadBaseClaim = PermissionClaims.SystemSettingsRead;

    /// <summary>How long a manual request may wait for a worker before it is given up (generalised from the sync).</summary>
    public static readonly TimeSpan UnclaimedRequestTimeout = ExchangeRateSyncConstants.UnclaimedRequestTimeout;

    /// <summary>The cooldown between two manual runs of one job (generalised from the sync's 60 s).</summary>
    public static readonly TimeSpan ManualRunCooldown = TimeSpan.FromSeconds(ExchangeRateSyncConstants.ManualRunCooldownSeconds);

    public static readonly IReadOnlyList<JobCatalogueEntry> Entries =
    [
        new(JobType.ExchangeRateSync, typeof(ExchangeRateSyncJob), ExchangeRateSyncConstants.SchedulerTick,
            Cadence: null, DynamicCadence: true,
            StaleAfter: TimeSpan.FromMinutes(2 * ExchangeRateSyncConstants.RunTimeoutMinutes),
            ManualRunClaims: [],
            DetailReadClaim: null),

        // The stale window uses the tracked-points cap's ceiling rather than the live setting: the sweep then never
        // needs the settings store, and the window can only be longer than the spec's, never shorter than a live burst.
        new(JobType.Weather, typeof(WeatherScheduleJob), WeatherScheduleJob.Tick,
            Cadence: null, DynamicCadence: false,
            StaleAfter: (SystemSettingsBounds.WeatherMaxTrackedPointsMax * WeatherScheduleJob.RequestSpacing * 2) + TimeSpan.FromMinutes(5),
            ManualRunClaims: [PermissionClaims.WeatherPointsUpdate],
            DetailReadClaim: PermissionClaims.WeatherPointsRead),

        // The manual pass is gated on file-storage.read BY DECISION (A-N4, S-N5): no file-storage write claim exists,
        // the claim is Admin-only, and the pass is identical to a scheduled one. AuthorizationPolicyTests pins it, so
        // adding a write claim later is the trigger to move this gate.
        new(JobType.FileStorageMaintenance, typeof(FileStorageMaintenanceJob), FileStorageMaintenanceJob.Tick,
            Cadence: FileStorageMaintenanceJob.PassInterval, DynamicCadence: false,
            StaleAfter: FileStorageMaintenanceJob.LeaseDuration + TimeSpan.FromMinutes(5),
            ManualRunClaims: [PermissionClaims.FileStorageRead],
            DetailReadClaim: PermissionClaims.FileStorageRead),

        new(JobType.PhotoThumbnails, typeof(PhotoThumbnailJob), PhotoThumbnailTiming.Interval,
            Cadence: null, DynamicCadence: false,
            StaleAfter: PhotoThumbnailTiming.RunBudget + PhotoThumbnailTiming.JobDeadline + TimeSpan.FromMinutes(5),
            ManualRunClaims: [PermissionClaims.PhotosUpdate],
            DetailReadClaim: PermissionClaims.PhotosRead),
    ];

    /// <summary>Periodic jobs that keep the run log itself; they record no runs and have no entry.</summary>
    public static readonly IReadOnlyList<Type> Infrastructure = [typeof(JobRunMaintenanceJob)];

    private static readonly Dictionary<JobType, JobCatalogueEntry> ByType = Entries.ToDictionary(entry => entry.JobType);

    public static JobCatalogueEntry Get(JobType jobType) => ByType[jobType];

    /// <summary>
    /// Resolves a route segment to a job: a member NAME (any case) of a catalogued job, never a number and never the
    /// <c>Unknown</c> sentinel — anything else is an unknown job, which the endpoint answers <c>404</c> (A14).
    /// </summary>
    public static bool TryResolve(string? value, out JobCatalogueEntry entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(value) || !char.IsLetter(value[0])
            || !Enum.TryParse<JobType>(value, ignoreCase: true, out var jobType)
            || !ByType.TryGetValue(jobType, out var found))
        {
            return false;
        }

        entry = found;
        return true;
    }
}
