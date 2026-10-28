using Odyssey.Core.Finance.ExchangeRateSync;

namespace Odyssey.Api.ExchangeRateSync;

/// <summary>Hands a started manual run to whatever executes it (issue #349 §3 "Scheduler lifecycle").</summary>
public interface IExchangeRateSyncDispatcher
{
    /// <summary>Called once the run's <c>Running</c> row exists. Returns once it is handed off — or, inline, done.</summary>
    Task DispatchAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// The production dispatcher, and it does nothing: the unclaimed <c>Running</c> row IS the hand-off.
/// <c>Odyssey.Worker</c>'s <see cref="ExchangeRateSyncJob"/> claims it on its next tick, so the API never
/// executes a run and a run outlives the request and the API process alike.
/// </summary>
public sealed class WorkerExchangeRateSyncDispatcher : IExchangeRateSyncDispatcher
{
    public Task DispatchAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The <c>Testing</c> dispatcher: claims and executes the run before the request returns, so a test's
/// <c>POST …/runs</c> never leaves a <c>Running</c> row holding the guard. No test host runs the worker.
/// </summary>
public sealed class InlineExchangeRateSyncDispatcher(IServiceScopeFactory scopes) : IExchangeRateSyncDispatcher
{
    public async Task DispatchAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<ExchangeRateSyncService>();
        if (await sync.ClaimRequestedRunAsync(cancellationToken) == runId)
        {
            await sync.ExecuteAsync(runId, cancellationToken);
        }
    }
}
