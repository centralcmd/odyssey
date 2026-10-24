using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Weather;

namespace Odyssey.Api.Weather;

/// <summary>
/// The weather scheduler (issue #348 §3): once a minute, the forecasts of the tracked locations within the
/// cap, then the observations of every enabled location. Requests go out one at a time, at most one a
/// second. Every replica runs one; claiming on <see cref="WeatherLocationSchedule.Version"/> is what keeps
/// two from fetching the same job.
/// </summary>
/// <remarks>
/// Not registered under <c>UseInMemoryDatabase</c> or the <c>Testing</c> environment, so no test tier sends a
/// request to MET. A host shutdown cancels the tick, and a cancelled fetch records no failure (§3 step 8).
/// </remarks>
public sealed class WeatherScheduler(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<WeatherScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnreadableLogInterval = TimeSpan.FromHours(1);

    private DateTime? lastUnreadableLog;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // One failed tick must not end the scheduler; the next tick retries from the stored state.
                logger.LogError(exception, "A weather scheduler tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One tick. Public so a test can drive it without the timer.</summary>
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

        await LogArchivedSkipsAsync(context, cancellationToken);

        var locations = services.GetRequiredService<WeatherLocationService>();
        var tracked = await locations.TrackedQueue()
            .Select(w => w.LocationId)
            .Take(settings.MaxTrackedLocations)
            .ToListAsync(cancellationToken);
        foreach (var locationId in tracked)
        {
            // A fresh scope per location, so one location's tracked entities or failed save cannot leak.
            await using var inner = scopes.CreateAsyncScope();
            var attempt = await inner.ServiceProvider.GetRequiredService<WeatherFetchService>()
                .FetchForecastAsync(locationId, WeatherFetchMode.Scheduled, cancellationToken);
            if (attempt.Status is not (WeatherAttemptStatus.Skipped or WeatherAttemptStatus.NotConfigured))
            {
                await Task.Delay(RequestSpacing, time, cancellationToken);
            }
        }

        var enabled = await (
                from weather in context.WeatherLocations.AsNoTracking()
                join location in context.Locations.AsNoTracking() on weather.LocationId equals location.LocationId
                join schedule in context.WeatherLocationSchedules.AsNoTracking() on weather.LocationId equals schedule.LocationId
                where weather.Enabled && weather.PurgeStartedAt == null && location.Archived == null
                orderby weather.EnabledAt, weather.LocationId
                select new { weather.LocationId, schedule.NextObservationFetchAfterUtc })
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var row in enabled.Where(r => r.NextObservationFetchAfterUtc is null || r.NextObservationFetchAfterUtc <= now))
        {
            await using var inner = scopes.CreateAsyncScope();
            var state = await inner.ServiceProvider.GetRequiredService<WeatherFetchService>()
                .FetchObservationsAsync(row.LocationId, cancellationToken);
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

    /// <summary>An archived location with weather on is skipped silently, after one Information line (§3).</summary>
    private async Task LogArchivedSkipsAsync(OdysseyContext context, CancellationToken cancellationToken)
    {
        var rows = await (
                from schedule in context.WeatherLocationSchedules
                join weather in context.WeatherLocations on schedule.LocationId equals weather.LocationId
                join location in context.Locations on weather.LocationId equals location.LocationId
                where weather.Enabled && weather.PurgeStartedAt == null
                      && (location.Archived != null) != schedule.ArchivedSkipLogged
                select new { schedule, Archived = location.Archived != null })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            if (row.Archived)
            {
                logger.LogInformation(
                    "Weather for location {LocationId} is on but the location is archived; skipping it until it is restored.",
                    row.schedule.LocationId);
            }

            row.schedule.ArchivedSkipLogged = row.Archived;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A fetch or an Admin action rotated the version meanwhile; the flag is re-evaluated next tick.
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }
}
