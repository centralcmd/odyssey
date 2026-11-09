using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Finance.Instruments.Sync;

/// <summary>
/// The instrument-price sync schedule (issue #446 §3), run by <c>Odyssey.Worker</c>. Each tick writes the heartbeat,
/// claims and executes a manual run the API started, else starts and executes a scheduled run when one is due. It
/// keeps no schedule in memory; with no enabled binding to a registered source it writes nothing but the heartbeat.
/// Every replica runs it: the <c>JobRuns.ExclusiveGuard</c> unique index keeps two runs from starting.
/// </summary>
public sealed class InstrumentPriceSyncJob(IServiceScopeFactory scopes) : IPeriodicJob
{
    public static TimeSpan Tick => InstrumentPriceSyncConstants.SchedulerTick;

    public string Name => "instrument-price-sync";

    public TimeSpan Interval => Tick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobRunRecorder>().HeartbeatAsync(JobType.InstrumentPriceSync, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<JobRunSweeper>().SweepAsync(JobType.InstrumentPriceSync, cancellationToken);
        var sync = scope.ServiceProvider.GetRequiredService<InstrumentPriceSyncService>();
        if (await sync.ClaimRequestedRunAsync(cancellationToken) is { } runId)
        {
            await sync.ExecuteAsync(runId, cancellationToken);
            return;
        }

        await sync.RunScheduledIfDueAsync(cancellationToken);
    }
}
