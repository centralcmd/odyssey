using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Weather;

public sealed record WeatherPurgeCounts(int Steps, int Runs, int Observations, int Days, int Sites);

/// <summary>
/// The deletion half of the weather purge (issue #348 §5.10, N2, amendment 1), behind an interface so the
/// fast test tiers run the controller's whole success path with a tracked implementation while production
/// deletes in bulk.
///
/// <para>
/// <b>Order.</b> In batches of 10 000, one transaction each: steps, runs, observations, days, then the
/// <b>non-current</b> sites. Then one final transaction: the schedule row, the <see cref="WeatherLocation"/>
/// row, and the remaining current site — in that order, because the composite key from the weather row to
/// its current site is <c>RESTRICT</c>. The location stays fenced by <c>PurgeStartedAt</c> until that final
/// transaction commits, so an interruption anywhere leaves a state a repeated purge completes.
/// </para>
/// </summary>
public interface IWeatherHistoryPurger
{
    Task<WeatherPurgeCounts> PurgeAsync(Guid locationId, CancellationToken cancellationToken);
}

/// <summary>
/// Production: <c>ExecuteDeleteAsync</c>, justified by volume — a location-year is ≈ 0.8 M step rows, which a
/// tracked <c>RemoveRange</c> would materialize. Relational-only; covered by <c>Odyssey.IntegrationTests</c>.
/// </summary>
public sealed class RelationalWeatherHistoryPurger(OdysseyContext context) : IWeatherHistoryPurger
{
    public const int BatchSize = 10_000;

    public async Task<WeatherPurgeCounts> PurgeAsync(Guid locationId, CancellationToken cancellationToken)
    {
        // Steps go by run, each run's steps in one statement: a step row has no index on LocationId alone
        // that a LIMITed DELETE could walk cheaply, while its run FK is indexed. The runs themselves follow.
        var steps = 0;
        var runs = 0;
        while (true)
        {
            var runIds = await context.WeatherForecastRuns.AsNoTracking()
                .Where(r => r.LocationId == locationId)
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

        var observations = 0;
        while (true)
        {
            var ids = await context.WeatherObservations.AsNoTracking()
                .Where(o => o.LocationId == locationId)
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

        var days = await context.WeatherDays.Where(d => d.LocationId == locationId).ExecuteDeleteAsync(cancellationToken);

        var current = await context.WeatherLocations.AsNoTracking()
            .Where(w => w.LocationId == locationId).Select(w => (int?)w.CurrentSiteVersion).FirstOrDefaultAsync(cancellationToken);
        var sites = await context.WeatherLocationSites
            .Where(s => s.LocationId == locationId && (current == null || s.SiteVersion != current))
            .ExecuteDeleteAsync(cancellationToken);

        // The final transaction: schedule, the weather row, the current site.
        var strategy = context.Database.CreateExecutionStrategy();
        sites += await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.WeatherLocationSchedules.Where(s => s.LocationId == locationId).ExecuteDeleteAsync(cancellationToken);
            await context.WeatherLocations.Where(w => w.LocationId == locationId).ExecuteDeleteAsync(cancellationToken);
            var remaining = await context.WeatherLocationSites.Where(s => s.LocationId == locationId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return remaining;
        });

        return new WeatherPurgeCounts(steps, runs, observations, days, sites);
    }
}

/// <summary>
/// The fast tiers: tracked <c>RemoveRange</c> in the same order and batches, so <c>Odyssey.Api.Tests</c>
/// runs the purge's whole success path on the EF InMemory provider, where <c>ExecuteDeleteAsync</c> throws.
/// </summary>
public sealed class TrackedWeatherHistoryPurger(OdysseyContext context) : IWeatherHistoryPurger
{
    public async Task<WeatherPurgeCounts> PurgeAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var steps = await RemoveInBatchesAsync(context.WeatherForecastSteps.Where(s => s.LocationId == locationId), cancellationToken);
        var runs = await RemoveInBatchesAsync(context.WeatherForecastRuns.Where(r => r.LocationId == locationId), cancellationToken);
        var observations = await RemoveInBatchesAsync(context.WeatherObservations.Where(o => o.LocationId == locationId), cancellationToken);
        var days = await RemoveInBatchesAsync(context.WeatherDays.Where(d => d.LocationId == locationId), cancellationToken);

        var current = await context.WeatherLocations.AsNoTracking()
            .Where(w => w.LocationId == locationId).Select(w => (int?)w.CurrentSiteVersion).FirstOrDefaultAsync(cancellationToken);
        var sites = await RemoveInBatchesAsync(
            context.WeatherLocationSites.Where(s => s.LocationId == locationId && (current == null || s.SiteVersion != current)),
            cancellationToken);

        context.WeatherLocationSchedules.RemoveRange(
            await context.WeatherLocationSchedules.Where(s => s.LocationId == locationId).ToListAsync(cancellationToken));
        context.WeatherLocations.RemoveRange(
            await context.WeatherLocations.Where(w => w.LocationId == locationId).ToListAsync(cancellationToken));
        var remaining = await context.WeatherLocationSites.Where(s => s.LocationId == locationId).ToListAsync(cancellationToken);
        context.WeatherLocationSites.RemoveRange(remaining);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        return new WeatherPurgeCounts(steps, runs, observations, days, sites + remaining.Count);
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
