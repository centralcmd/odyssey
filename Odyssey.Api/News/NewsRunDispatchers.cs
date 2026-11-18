using Odyssey.Context;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Core.Jobs;

namespace Odyssey.Api.News;

/// <summary>Hands a started manual news run to whatever executes it (issue #470 §5.5 item 27).</summary>
public interface INewsRunDispatcher
{
    /// <summary>Called once the run's <c>Running</c> row exists. Returns once it is handed off — or, inline, done.</summary>
    Task DispatchAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// The production dispatcher, and it does nothing: the unclaimed <c>Running</c> row is the hand-off, which the first
/// worker replica's <see cref="NewsFeedJob"/> tick claims.
/// </summary>
public sealed class WorkerNewsRunDispatcher : INewsRunDispatcher
{
    public Task DispatchAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The <c>Testing</c> dispatcher: claims the run and executes it before the request returns — marks the eligible feeds
/// and finishes it <c>Succeeded</c>, as a worker tick would — so no test leaves a <c>Running</c> row holding the guard.
/// </summary>
public sealed class InlineNewsRunDispatcher(IServiceScopeFactory scopes, JobRunRecorder recorder, INewsFetchStateFlags flags, TimeProvider time)
    : INewsRunDispatcher
{
    public async Task DispatchAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await NewsManualRun.ExecuteClaimedAsync(recorder, context, flags, time.GetUtcNow().UtcDateTime, cancellationToken);
    }
}
