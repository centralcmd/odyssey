using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// The weather schedule (issue #348 §3), run by <c>Odyssey.Worker</c> once a minute: the forecasts of the
/// tracked points within the cap, then the observations of every history-enabled point (issue #387: one
/// dataset per point, however many places link to it). Requests go out one
/// at a time, at most one a second. Every worker replica runs it; claiming on
/// <see cref="WeatherPointSchedule.Version"/> is what keeps two from fetching the same point.
/// </summary>
/// <remarks>
/// Nothing registers it in the API or any test host, so no test tier sends a request to MET; the tests
/// drive <see cref="RunOnceAsync"/> directly. A host shutdown cancels the run, and a cancelled fetch
/// records no failure (§3 step 8).
/// </remarks>
public sealed class WeatherScheduleJob(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<WeatherScheduleJob> logger) : IPeriodicJob
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    /// <summary>
    /// The pause between two requests to MET. It is per process: the deployment runs ONE worker, so this is the
    /// instance's rate. Running several workers multiplies it — the rows coordinate who fetches what, not how fast.
    /// </summary>
    public static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnreadableLogInterval = TimeSpan.FromHours(1);

    private DateTime? lastUnreadableLog;

    public string Name => "weather";

    public TimeSpan Interval => Tick;

    /// <summary>
    /// One run. The heartbeat comes first, before the not-configured return (issue #409 §3.3 step 1, A-N3). A manual
    /// run the API requested is claimed and executed into its own row; otherwise the tick's work extends this
    /// process's burst, and a tick that sent no request closes it (§3.2).
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var recorder = services.GetRequiredService<JobRunRecorder>();
        await recorder.HeartbeatAsync(JobType.Weather, cancellationToken);

        var context = services.GetRequiredService<OdysseyContext>();
        var settings = await services.GetRequiredService<IWeatherSettingsLookup>().GetAsync(cancellationToken);
        if (!settings.IsConfigured)
        {
            await recorder.CloseBurstAsync(JobType.Weather, cancellationToken);
            return;
        }

        if (await recorder.ClaimManualAsync(JobType.Weather, cancellationToken) is { } manualId)
        {
            await RunManualAsync(recorder, context, settings.MaxTrackedPoints, manualId, cancellationToken);
            return;
        }

        var tally = new WeatherTickTally();
        await FetchForecastsAsync(context, settings.MaxTrackedPoints, WeatherFetchMode.Scheduled, tally, cancellationToken);
        await FetchObservationsAsync(context, tally, cancellationToken);

        if (tally.Attempted > 0)
        {
            await recorder.ExtendBurstAsync<WeatherJobRun>(JobType.Weather, tally.AddTo, cancellationToken);
        }
        else
        {
            await recorder.CloseBurstAsync(JobType.Weather, cancellationToken);
        }
    }

    /// <summary>
    /// A manual run (§3.5): every tracked point within the cap, through the per-point refresh path — which bypasses
    /// the refresh interval but never MET's <c>Expires</c> or a provider pause — one request at a time, spaced.
    /// </summary>
    private async Task RunManualAsync(
        JobRunRecorder recorder, OdysseyContext context, int maxTrackedPoints, Guid runId, CancellationToken cancellationToken)
    {
        var tally = new WeatherTickTally();
        (JobRunStatus, JobFailureCategory?)? outcome = null;
        try
        {
            await FetchForecastsAsync(context, maxTrackedPoints, WeatherFetchMode.Refresh, tally, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = (JobRunStatus.Failed, JobFailureCategory.Cancelled);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual weather run {RunId} failed unexpectedly.", runId);
            outcome = (JobRunStatus.Failed, JobFailureCategory.Internal);
        }

        await recorder.FinishAsync<WeatherJobRun>(JobType.Weather, runId, tally.AddTo, outcome, CancellationToken.None);
    }

    private async Task FetchForecastsAsync(
        OdysseyContext context, int maxTrackedPoints, WeatherFetchMode mode, WeatherTickTally tally, CancellationToken cancellationToken)
    {
        var tracked = await WeatherPointService.TrackedQueue(context)
            .Select(w => w.WeatherPointId)
            .Take(maxTrackedPoints)
            .ToListAsync(cancellationToken);
        foreach (var pointId in tracked)
        {
            // A fresh scope per point, so one point's tracked entities or failed save cannot leak.
            await using var inner = scopes.CreateAsyncScope();
            WeatherAttempt attempt;
            try
            {
                attempt = await inner.ServiceProvider.GetRequiredService<WeatherFetchService>()
                    .FetchForecastAsync(pointId, mode, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One point's unexpected failure must not end the run: the queue is ordered, so it would
                // come first again on every tick and starve every place behind it.
                logger.LogError(ex, "Weather forecast fetch for point {WeatherPointId} failed unexpectedly.", pointId);
                tally.Forecast(new WeatherAttempt(WeatherAttemptStatus.Failed, FailureKind: WeatherFetchFailureKind.ProviderError));
                await Task.Delay(RequestSpacing, time, cancellationToken);
                continue;
            }

            tally.Forecast(attempt);
            if (attempt.Status is not (WeatherAttemptStatus.Skipped or WeatherAttemptStatus.NotConfigured))
            {
                await Task.Delay(RequestSpacing, time, cancellationToken);
            }
        }
    }

    private async Task FetchObservationsAsync(OdysseyContext context, WeatherTickTally tally, CancellationToken cancellationToken)
    {
        var enabled = await (
                from weather in context.WeatherPoints.AsNoTracking()
                join schedule in context.WeatherPointSchedules.AsNoTracking() on weather.WeatherPointId equals schedule.WeatherPointId
                where weather.HistoryEnabled && weather.PurgeStartedAt == null
                orderby weather.EnabledAt, weather.WeatherPointId
                select new
                {
                    weather.WeatherPointId,
                    schedule.NextObservationFetchAfterUtc,
                    // WeatherFetchService.FirstLookupPending, spelled out so the query translates to SQL.
                    FirstLookupPending = weather.StationResolvedAtUtc == null
                        && schedule.ObservationsCheckedThroughDate == null
                        && schedule.ObservationFailureKind == null,
                })
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var row in enabled.Where(r => r.FirstLookupPending || r.NextObservationFetchAfterUtc is null || r.NextObservationFetchAfterUtc <= now))
        {
            await using var inner = scopes.CreateAsyncScope();
            SecretReadState state;
            var observations = new WeatherObservationTally();
            try
            {
                state = await inner.ServiceProvider.GetRequiredService<WeatherFetchService>()
                    .FetchObservationsAsync(row.WeatherPointId, cancellationToken, observations);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // As for forecasts: one point's unexpected failure must not starve the places behind it.
                logger.LogError(ex, "Weather observation fetch for point {WeatherPointId} failed unexpectedly.", row.WeatherPointId);
                tally.Observations(observations, failedUnexpectedly: true);
                await Task.Delay(RequestSpacing, time, cancellationToken);
                continue;
            }

            tally.Observations(observations, failedUnexpectedly: false);
            if (state == SecretReadState.Unreadable)
            {
                LogUnreadable();
                break;
            }

            if (state == SecretReadState.NotSet)
            {
                break;
            }

            await Task.Delay(RequestSpacing, time, cancellationToken);
        }
    }

    private void LogUnreadable()
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (lastUnreadableLog is { } last && now - last < UnreadableLogInterval)
        {
            return;
        }

        lastUnreadableLog = now;
        logger.LogError(
            "The Frost client id cannot be decrypted, so no weather observations are fetched. Re-enter it in System settings → Weather.");
    }
}

/// <summary>One tick's (or one manual run's) weather counts, folded into its run row (issue #409 §4.2).</summary>
internal sealed class WeatherTickTally
{
    public int Attempted { get; private set; }

    public int SkippedNotExpired { get; private set; }

    public int Failed { get; private set; }

    public int ForecastsStored { get; private set; }

    public int ObservationDaysStored { get; private set; }

    public WeatherFetchFailureKind? FailureKind { get; private set; }

    public void Forecast(WeatherAttempt attempt)
    {
        switch (attempt.Status)
        {
            case WeatherAttemptStatus.Skipped:
                SkippedNotExpired++;
                break;
            case WeatherAttemptStatus.NotConfigured:
                break;
            case WeatherAttemptStatus.Failed:
                Attempted++;
                Failed++;
                FailureKind = attempt.FailureKind ?? WeatherFetchFailureKind.ProviderError;
                break;
            default:
                Attempted++;
                if (attempt.StoredNewRun)
                {
                    ForecastsStored++;
                }

                break;
        }
    }

    public void Observations(WeatherObservationTally observations, bool failedUnexpectedly)
    {
        Attempted += Math.Max(observations.Attempted, failedUnexpectedly ? 1 : 0);
        Failed += Math.Max(observations.Failed, failedUnexpectedly ? 1 : 0);
        ObservationDaysStored += observations.DaysStored;
        if (observations.FailureKind is { } kind)
        {
            FailureKind = kind;
        }
        else if (failedUnexpectedly)
        {
            FailureKind = WeatherFetchFailureKind.ProviderError;
        }
    }

    public void AddTo(WeatherJobRun run)
    {
        run.PointsAttempted += Attempted;
        run.PointsSkippedNotExpired += SkippedNotExpired;
        run.PointsFailed += Failed;
        run.ForecastsStored += ForecastsStored;
        run.ObservationDaysStored += ObservationDaysStored;
        if (FailureKind is { } kind)
        {
            run.FailureKind = kind;
        }
    }
}
