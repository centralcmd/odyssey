using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// The exchange-rate sync schedule (issue #349 §3), run by <c>Odyssey.Worker</c>. Each tick first claims
/// and executes a manual run the API started, else starts and executes a scheduled run when one is due. It
/// keeps no schedule in memory: <see cref="ExchangeRateSyncService"/> derives "due" from the run log on
/// every call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every worker replica runs it.</b> The run guard (the <c>JobRuns.ExclusiveGuard</c> unique index) keeps two
/// scheduled runs from starting, and the <c>ClaimedAtUtc</c> concurrency token keeps two replicas from
/// executing one manual run.
/// </para>
/// <para>
/// <b>The tick bounds how long Run now waits</b> to be picked up; the status panel shows the run as
/// <c>Running</c> from the moment the API inserts it. A host shutdown cancels the run, which then ends
/// <c>Failed / Cancelled</c>; if the process dies first, the next tick anywhere marks it <c>Abandoned</c>.
/// </para>
/// <para>
/// Nothing registers it in the API or any test host, so no test tier sends a request to Norges Bank on a
/// timer; the tests drive <see cref="RunOnceAsync"/> directly.
/// </para>
/// </remarks>
public sealed class ExchangeRateSyncJob(IServiceScopeFactory scopes) : IPeriodicJob
{
    public string Name => "exchange-rate-sync";

    public TimeSpan Interval => ExchangeRateSyncConstants.SchedulerTick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        // The heartbeat first, before any early return (issue #409 §3.3 step 1): an idle sync is alive.
        await scope.ServiceProvider.GetRequiredService<JobRunRecorder>().HeartbeatAsync(JobType.ExchangeRateSync, cancellationToken);
        var sync = scope.ServiceProvider.GetRequiredService<ExchangeRateSyncService>();
        if (await sync.ClaimRequestedRunAsync(cancellationToken) is { } runId)
        {
            await sync.ExecuteAsync(runId, cancellationToken);
            return;
        }

        await sync.RunScheduledIfDueAsync(cancellationToken);
    }
}
