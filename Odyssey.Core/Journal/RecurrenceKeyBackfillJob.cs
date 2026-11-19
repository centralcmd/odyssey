using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Journal;

/// <summary>
/// Keys every recurring series written before issue #519 (§11, AR4, SR10): runs in <c>Odyssey.Worker</c>, a batch of
/// un-keyed series per tick, each in its own locked transaction with the same routine a series' first write uses
/// (<see cref="RecurrenceSeries.KeyInPlace"/>). It is idempotent and resumable, and never holds the API down: a series
/// that fails is logged by id, skipped and retried on the next tick, and reads as a legacy series until then. Idle for
/// good once every series is keyed.
/// </summary>
/// <remarks>
/// Every replica runs it. Two processes keying the same series serialize on its row lock, and the second finds it keyed
/// and does nothing, so no lease is needed.
/// </remarks>
public sealed class RecurrenceKeyBackfillJob(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<RecurrenceKeyBackfillJob> logger) : IPeriodicJob
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>A tick stops starting new series once it has run this long.</summary>
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(60);

    /// <summary>Series read per query.</summary>
    public const int BatchSize = 50;

    public string Name => "recurrence-key-backfill";

    public TimeSpan Interval => Tick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var recorder = Recorder();
        await recorder.HeartbeatAsync(JobType.RecurrenceKeyBackfill, cancellationToken);
        var manualRunId = await recorder.ClaimManualAsync(JobType.RecurrenceKeyBackfill, cancellationToken);
        var tally = new Tally();
        JobFailureCategory? failure = null;
        try
        {
            await KeyPendingAsync(tally, cancellationToken);
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? JobFailureCategory.Cancelled : JobFailureCategory.Internal;
            throw;
        }
        finally
        {
            if (manualRunId is { } runId)
            {
                await recorder.FinishAsync<RecurrenceKeyBackfillJobRun>(JobType.RecurrenceKeyBackfill, runId, tally.AddTo,
                    failure is { } category ? (JobRunStatus.Failed, category) : null, CancellationToken.None);
            }
            else if (tally.HasWork)
            {
                await recorder.ExtendBurstAsync<RecurrenceKeyBackfillJobRun>(JobType.RecurrenceKeyBackfill, tally.AddTo, CancellationToken.None);
            }
            else
            {
                await recorder.CloseBurstAsync(JobType.RecurrenceKeyBackfill, CancellationToken.None);
            }
        }
    }

    /// <summary>One tick's work, without the run log: the series keyed, failed and rows detached.</summary>
    internal async Task KeyPendingAsync(Tally tally, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        var failed = new HashSet<Guid>();
        while (time.GetUtcNow() - started < RunBudget)
        {
            List<Guid> pending;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
                pending = await context.RecurrencePatterns.AsNoTracking()
                    .Where(p => p.KeysBackfilledAt == null && !failed.Contains(p.RecurrencePatternId))
                    .OrderBy(p => p.RecurrencePatternId)
                    .Select(p => p.RecurrencePatternId)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
            }

            if (pending.Count == 0)
            {
                return;
            }

            foreach (var id in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var detached = await KeyOneAsync(id, cancellationToken);
                    if (detached is { } count)
                    {
                        tally.Keyed++;
                        tally.Detached += count;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add(id);
                    tally.Failed++;
                    logger.LogWarning("Keying calendar series {RecurrencePatternId} failed ({Failure}); it is retried on the next tick.",
                        id, ex.GetType().Name);
                }
            }
        }
    }

    // Null when another writer keyed it first.
    private async Task<int?> KeyOneAsync(Guid patternId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await CalendarSeriesTransactions.ExecuteAsync<int?>(context, async () =>
        {
            await CalendarSeriesTransactions.LockSeriesAsync(context, [patternId], cancellationToken);
            var state = await RecurrenceSeriesState.LoadAsync(context, patternId, tracked: true, cancellationToken);
            if (state is null || state.Pattern.KeysBackfilledAt is not null)
            {
                return null;
            }

            var detached = RecurrenceSeries.KeyInPlace(
                state, time.GetUtcNow().UtcDateTime, userId: null, x => context.RecurrenceExclusions.Add(x));
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Calendar series {RecurrencePatternId} keyed by the backfill: {DetachedCount} row(s) detached, {ExclusionCount} exclusion(s).",
                patternId, detached, state.Exclusions.Count);
            return detached;
        }, cancellationToken, logger);
    }

    private JobRunRecorder Recorder()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
    }

    /// <summary>The counts of one tick.</summary>
    internal sealed class Tally
    {
        public int Keyed { get; set; }

        public int Failed { get; set; }

        public int Detached { get; set; }

        public bool HasWork => Keyed + Failed > 0;

        public void AddTo(RecurrenceKeyBackfillJobRun run)
        {
            run.SeriesKeyed += Keyed;
            run.SeriesKeyFailures += Failed;
            run.KeyRowsDetached += Detached;
        }
    }
}
