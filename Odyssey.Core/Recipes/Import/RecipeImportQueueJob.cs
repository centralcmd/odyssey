using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Recipes.Import;

/// <summary>
/// The recipe-import queue, run by <c>Odyssey.Worker</c> (issue #602 §3.4). Every tick: the heartbeat, the stale sweep,
/// then at most ONE claimed import run to completion. Its own job, so recipe imports never wait behind statement
/// analyses, nor statements behind recipes. Replicas coordinate only through the rows.
/// </summary>
public sealed class RecipeImportQueueJob(IServiceScopeFactory scopes, ILogger<RecipeImportQueueJob> logger) : IPeriodicJob
{
    public string Name => "recipe-import";

    public TimeSpan Interval => RecipeImportJobTiming.Tick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        JobRunRecorder recorder;
        await using (var recorderScope = scopes.CreateAsyncScope())
        {
            recorder = recorderScope.ServiceProvider.GetRequiredService<JobRunRecorder>();
        }

        await recorder.HeartbeatAsync(JobType.RecipeImport, cancellationToken);

        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<RecipeImportQueue>();
        try
        {
            await queue.SweepAsync(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The recipe-import stale sweep failed ({Failure}).", ex.GetType().Name);
        }

        if (await queue.ClaimNextAsync(cancellationToken) is not { } claimed)
        {
            await recorder.CloseBurstAsync(JobType.RecipeImport, cancellationToken);
            return;
        }

        await using var runScope = scopes.CreateAsyncScope();
        var result = await runScope.ServiceProvider.GetRequiredService<RecipeImportRunner>().RunAsync(claimed, cancellationToken);
        await recorder.ExtendBurstAsync<RecipeImportJobRun>(JobType.RecipeImport, run =>
        {
            switch (result)
            {
                case RecipeImportRunResult.Completed:
                    run.ImportsCompleted++;
                    break;
                case RecipeImportRunResult.Failed:
                    run.ImportsFailed++;
                    break;
                case RecipeImportRunResult.Refused:
                    run.ImportsRefused++;
                    break;
            }
        }, CancellationToken.None);
    }
}
