using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

public sealed record WeatherPurgeCounts(int Steps, int Runs, int Observations, int Days, int Sites);

/// <summary>
/// The deletion half of the weather purge (issue #348 §5.10, N2, amendment 1), behind an interface so the
/// fast test tiers run the controller's whole success path with a tracked implementation while production
/// deletes in bulk.
///
/// <para>
/// <b>Order.</b> In batches of 10 000, one transaction each: steps, runs, observations, days, then the
/// <b>non-current</b> sites. Then one final transaction: the schedule row, the remaining current site and the
/// <see cref="WeatherPoint"/> itself (issue #387: the sites, schedule and stations cascade with the point, and
/// the point has no key back to its site, so the order only keeps the counts honest). Deleting the point sets
/// every linked <c>Locations.WeatherPointId</c> to null through its <c>SET NULL</c> key. The point stays fenced
/// by <c>PurgeStartedAt</c> until that final transaction commits, so an interruption anywhere leaves a state a
/// repeated purge completes.
/// </para>
/// </summary>
public interface IWeatherHistoryPurger
{
    Task<WeatherPurgeCounts> PurgeAsync(Guid pointId, WeatherPurgeMode mode, CancellationToken cancellationToken);
}

/// <summary>
/// Production: <c>ExecuteDeleteAsync</c>, justified by volume — a point-year is ≈ 0.8 M step rows, which a
/// tracked <c>RemoveRange</c> would materialize. Relational-only; covered by <c>Odyssey.IntegrationTests</c>.
/// </summary>
public sealed class RelationalWeatherHistoryPurger(OdysseyContext context) : IWeatherHistoryPurger
{
    public const int BatchSize = 10_000;

    public async Task<WeatherPurgeCounts> PurgeAsync(Guid pointId, WeatherPurgeMode mode, CancellationToken cancellationToken)
    {
        if (mode == WeatherPurgeMode.HistoryOnly)
        {
            return await PurgeHistoryAsync(pointId, cancellationToken);
        }

        // Steps go run by run, in id batches of at most BatchSize, so every statement is bounded however
        // large one run is; the run's own steps are found through its indexed (RunId, TimeUtc) key. The
        // runs themselves follow.
        var steps = 0;
        var runs = 0;
        while (true)
        {
            var runIds = await context.WeatherForecastRuns.AsNoTracking()
                .Where(r => r.WeatherPointId == pointId)
                .OrderBy(r => r.WeatherForecastRunId)
                .Select(r => r.WeatherForecastRunId)
                .Take(100)
                .ToListAsync(cancellationToken);
            if (runIds.Count == 0)
            {
                break;
            }

            foreach (var runId in runIds)
            {
                while (true)
                {
                    var stepIds = await context.WeatherForecastSteps.AsNoTracking()
                        .Where(s => s.WeatherForecastRunId == runId)
                        .OrderBy(s => s.WeatherForecastStepId)
                        .Select(s => s.WeatherForecastStepId)
                        .Take(BatchSize)
                        .ToListAsync(cancellationToken);
                    if (stepIds.Count == 0)
                    {
                        break;
                    }

                    steps += await context.WeatherForecastSteps
                        .Where(s => stepIds.Contains(s.WeatherForecastStepId))
                        .ExecuteDeleteAsync(cancellationToken);
                }
            }

            runs += await context.WeatherForecastRuns.Where(r => runIds.Contains(r.WeatherForecastRunId)).ExecuteDeleteAsync(cancellationToken);
        }

        var observations = await DeleteObservationsAsync(pointId, cancellationToken);

        var days = await context.WeatherDays.Where(d => d.WeatherPointId == pointId).ExecuteDeleteAsync(cancellationToken);

        var current = await context.WeatherPoints.AsNoTracking()
            .Where(w => w.WeatherPointId == pointId).Select(w => (int?)w.CurrentSiteVersion).FirstOrDefaultAsync(cancellationToken);
        var sites = await context.WeatherPointSites
            .Where(s => s.WeatherPointId == pointId && (current == null || s.SiteVersion != current))
            .ExecuteDeleteAsync(cancellationToken);

        // The final transaction: schedule, the current site, the point (its stations cascade with it).
        var strategy = context.Database.CreateExecutionStrategy();
        sites += await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.WeatherPointSchedules.Where(s => s.WeatherPointId == pointId).ExecuteDeleteAsync(cancellationToken);
            var remaining = await context.WeatherPointSites.Where(s => s.WeatherPointId == pointId).ExecuteDeleteAsync(cancellationToken);
            await context.WeatherPoints.Where(w => w.WeatherPointId == pointId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return remaining;
        });

        return new WeatherPurgeCounts(steps, runs, observations, days, sites);
    }

    private async Task<WeatherPurgeCounts> PurgeHistoryAsync(Guid pointId, CancellationToken cancellationToken)
    {
        var observations = await DeleteObservationsAsync(pointId, cancellationToken);

        // A day with no forecast half was measured only; the rest lose their measured half.
        var days = await context.WeatherDays
            .Where(d => d.WeatherPointId == pointId && d.ForecastCoverageHours == null)
            .ExecuteDeleteAsync(cancellationToken);
        days += await context.WeatherDays
            .Where(d => d.WeatherPointId == pointId && d.ObservedFetchedAtUtc != null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(d => d.ObservedMinTempC, (decimal?)null)
                .SetProperty(d => d.ObservedMaxTempC, (decimal?)null)
                .SetProperty(d => d.ObservedMeanTempC, (decimal?)null)
                .SetProperty(d => d.ObservedMeanWindSpeedMps, (decimal?)null)
                .SetProperty(d => d.ObservedMaxWindGustMps, (decimal?)null)
                .SetProperty(d => d.ObservedPrecipitationMm, (decimal?)null)
                .SetProperty(d => d.ObservedFetchedAtUtc, (DateTime?)null)
                .SetProperty(d => d.ObservedSiteVersion, (int?)null), cancellationToken);

        // The final transaction: forget the station and the observation markers, then lift the fence.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.WeatherPointSchedules.Where(s => s.WeatherPointId == pointId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.ObservationsCheckedThroughDate, (DateOnly?)null)
                    .SetProperty(s => s.LastObservationSuccessUtc, (DateTime?)null)
                    .SetProperty(s => s.ObservationFailureKind, (WeatherFetchFailureKind?)null)
                    .SetProperty(s => s.ObservationConsecutiveFailures, 0)
                    .SetProperty(s => s.NextObservationFetchAfterUtc, (DateTime?)null)
                    .SetProperty(s => s.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(s => s.Version, Guid.NewGuid()), cancellationToken);
            await context.WeatherStations.Where(st => st.WeatherPointId == pointId).ExecuteDeleteAsync(cancellationToken);
            // What was checked goes with what was measured (issue #436 §3.4); the windows are settings and stay.
            await context.WeatherObservationCoverages.Where(c => c.WeatherPointId == pointId).ExecuteDeleteAsync(cancellationToken);
            await context.WeatherPoints.Where(w => w.WeatherPointId == pointId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(w => w.StationResolvedAtUtc, (DateTime?)null)
                    .SetProperty(w => w.PurgeStartedAt, (DateTime?)null)
                    .SetProperty(w => w.PurgeMode, (WeatherPurgeMode?)null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new WeatherPurgeCounts(0, 0, observations, days, 0);
    }

    private async Task<int> DeleteObservationsAsync(Guid pointId, CancellationToken cancellationToken)
    {
        var observations = 0;
        while (true)
        {
            var ids = await context.WeatherObservations.AsNoTracking()
                .Where(o => o.WeatherPointId == pointId)
                .OrderBy(o => o.WeatherObservationId)
                .Select(o => o.WeatherObservationId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            observations += await context.WeatherObservations.Where(o => ids.Contains(o.WeatherObservationId)).ExecuteDeleteAsync(cancellationToken);
        }

        return observations;
    }
}

/// <summary>
/// The fast tiers: tracked <c>RemoveRange</c> in the same order and batches, so <c>Odyssey.Api.Tests</c>
/// runs the purge's whole success path on the EF InMemory provider, where <c>ExecuteDeleteAsync</c> throws.
/// </summary>
public sealed class TrackedWeatherHistoryPurger(OdysseyContext context) : IWeatherHistoryPurger
{
    public async Task<WeatherPurgeCounts> PurgeAsync(Guid pointId, WeatherPurgeMode mode, CancellationToken cancellationToken)
    {
        if (mode == WeatherPurgeMode.HistoryOnly)
        {
            return await PurgeHistoryAsync(pointId, cancellationToken);
        }

        var steps = await RemoveInBatchesAsync(context.WeatherForecastSteps.Where(s => s.WeatherPointId == pointId), cancellationToken);
        var runs = await RemoveInBatchesAsync(context.WeatherForecastRuns.Where(r => r.WeatherPointId == pointId), cancellationToken);
        var observations = await RemoveInBatchesAsync(context.WeatherObservations.Where(o => o.WeatherPointId == pointId), cancellationToken);
        var days = await RemoveInBatchesAsync(context.WeatherDays.Where(d => d.WeatherPointId == pointId), cancellationToken);

        var current = await context.WeatherPoints.AsNoTracking()
            .Where(w => w.WeatherPointId == pointId).Select(w => (int?)w.CurrentSiteVersion).FirstOrDefaultAsync(cancellationToken);
        var sites = await RemoveInBatchesAsync(
            context.WeatherPointSites.Where(s => s.WeatherPointId == pointId && (current == null || s.SiteVersion != current)),
            cancellationToken);

        context.WeatherPointSchedules.RemoveRange(
            await context.WeatherPointSchedules.Where(s => s.WeatherPointId == pointId).ToListAsync(cancellationToken));
        // The database cascades the stations with the point and sets the linked places' WeatherPointId to null;
        // the in-memory provider does neither for untracked rows, so both are done by hand here.
        context.WeatherStations.RemoveRange(
            await context.WeatherStations.Where(st => st.WeatherPointId == pointId).ToListAsync(cancellationToken));
        // Windows and coverage cascade on MariaDB; the in-memory provider has no cascade.
        context.WeatherPointHistoryWindows.RemoveRange(
            await context.WeatherPointHistoryWindows.Where(w => w.WeatherPointId == pointId).ToListAsync(cancellationToken));
        context.WeatherObservationCoverages.RemoveRange(
            await context.WeatherObservationCoverages.Where(c => c.WeatherPointId == pointId).ToListAsync(cancellationToken));
        var remaining = await context.WeatherPointSites.Where(s => s.WeatherPointId == pointId).ToListAsync(cancellationToken);
        context.WeatherPointSites.RemoveRange(remaining);
        foreach (var linked in await context.Locations.Where(l => l.WeatherPointId == pointId).ToListAsync(cancellationToken))
        {
            linked.WeatherPointId = null;
        }

        context.WeatherPoints.RemoveRange(
            await context.WeatherPoints.Where(w => w.WeatherPointId == pointId).ToListAsync(cancellationToken));
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        return new WeatherPurgeCounts(steps, runs, observations, days, sites + remaining.Count);
    }

    private async Task<WeatherPurgeCounts> PurgeHistoryAsync(Guid pointId, CancellationToken cancellationToken)
    {
        var observations = await RemoveInBatchesAsync(context.WeatherObservations.Where(o => o.WeatherPointId == pointId), cancellationToken);
        var days = await RemoveInBatchesAsync(
            context.WeatherDays.Where(d => d.WeatherPointId == pointId && d.ForecastCoverageHours == null), cancellationToken);

        foreach (var day in await context.WeatherDays.Where(d => d.WeatherPointId == pointId && d.ObservedFetchedAtUtc != null).ToListAsync(cancellationToken))
        {
            day.ObservedMinTempC = null;
            day.ObservedMaxTempC = null;
            day.ObservedMeanTempC = null;
            day.ObservedMeanWindSpeedMps = null;
            day.ObservedMaxWindGustMps = null;
            day.ObservedPrecipitationMm = null;
            day.ObservedFetchedAtUtc = null;
            day.ObservedSiteVersion = null;
            days++;
        }

        var schedule = await context.WeatherPointSchedules.FirstOrDefaultAsync(s => s.WeatherPointId == pointId, cancellationToken);
        if (schedule is not null)
        {
            schedule.ObservationsCheckedThroughDate = null;
            schedule.LastObservationSuccessUtc = null;
            schedule.ObservationFailureKind = null;
            schedule.ObservationConsecutiveFailures = 0;
            schedule.NextObservationFetchAfterUtc = null;
            schedule.LeaseUntilUtc = null;
            schedule.Version = Guid.NewGuid();
        }

        var weather = await context.WeatherPoints.FirstOrDefaultAsync(w => w.WeatherPointId == pointId, cancellationToken);
        if (weather is not null)
        {
            weather.StationResolvedAtUtc = null;
            weather.PurgeStartedAt = null;
            weather.PurgeMode = null;
        }

        context.WeatherStations.RemoveRange(
            await context.WeatherStations.Where(st => st.WeatherPointId == pointId).ToListAsync(cancellationToken));
        // What was checked goes with what was measured (issue #436 §3.4); the windows are settings and stay.
        context.WeatherObservationCoverages.RemoveRange(
            await context.WeatherObservationCoverages.Where(c => c.WeatherPointId == pointId).ToListAsync(cancellationToken));

        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
        return new WeatherPurgeCounts(0, 0, observations, days, 0);
    }

    private async Task<int> RemoveInBatchesAsync<T>(IQueryable<T> rows, CancellationToken cancellationToken)
        where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await rows.Take(RelationalWeatherHistoryPurger.BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return total;
            }

            context.RemoveRange(batch);
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            total += batch.Count;
        }
    }
}
