using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Jobs;

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

    /// <summary>One run.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<OdysseyContext>();
        var settings = await services.GetRequiredService<IWeatherSettingsLookup>().GetAsync(cancellationToken);
        if (!settings.IsConfigured)
        {
            return;
        }

        var tracked = await WeatherPointService.TrackedQueue(context)
            .Select(w => w.WeatherPointId)
            .Take(settings.MaxTrackedPoints)
            .ToListAsync(cancellationToken);
        foreach (var pointId in tracked)
        {
            // A fresh scope per point, so one point's tracked entities or failed save cannot leak.
            await using var inner = scopes.CreateAsyncScope();
            WeatherAttempt attempt;
            try
            {
                attempt = await inner.ServiceProvider.GetRequiredService<WeatherFetchService>()
                    .FetchForecastAsync(pointId, WeatherFetchMode.Scheduled, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One point's unexpected failure must not end the run: the queue is ordered, so it would
                // come first again on every tick and starve every place behind it.
                logger.LogError(ex, "Scheduled weather forecast fetch for point {WeatherPointId} failed unexpectedly.", pointId);
                await Task.Delay(RequestSpacing, time, cancellationToken);
                continue;
            }

            if (attempt.Status is not (WeatherAttemptStatus.Skipped or WeatherAttemptStatus.NotConfigured))
            {
                await Task.Delay(RequestSpacing, time, cancellationToken);
            }
        }

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
            try
            {
                state = await inner.ServiceProvider.GetRequiredService<WeatherFetchService>()
                    .FetchObservationsAsync(row.WeatherPointId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // As for forecasts: one point's unexpected failure must not starve the places behind it.
                logger.LogError(ex, "Weather observation fetch for point {WeatherPointId} failed unexpectedly.", row.WeatherPointId);
                await Task.Delay(RequestSpacing, time, cancellationToken);
                continue;
            }

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
