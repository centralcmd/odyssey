using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Finance;

/// <summary>
/// The file-analysis extraction queue, run by <c>Odyssey.Worker</c> (issue #444 §3). Every tick: the heartbeat,
/// the stale sweep, then at most ONE claimed job, run to completion inside the tick. One job per process at a
/// time is deliberate — the periodic loop is serial, and one 32 MiB statement plus its base64 copy is the
/// worker's largest allocation. The worst case blocks this replica's tick for the provider budget (240 s at
/// the default timeout), which is inside the lease and the catalogue's 300 s not-ticking threshold; other
/// replicas keep sweeping and claiming. Replicas coordinate only through the job rows.
/// </summary>
public sealed class FileAnalysisQueueJob(
    IServiceScopeFactory scopes,
    ILogger<FileAnalysisQueueJob> logger) : IPeriodicJob
{
    public string Name => "file-analysis";

    public TimeSpan Interval => FileAnalysisJobTiming.Tick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var recorder = Recorder();
        await recorder.HeartbeatAsync(JobType.FileAnalysis, cancellationToken);

        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<FileAnalysisJobQueue>();

        try
        {
            await queue.SweepAsync(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The sweep is housekeeping; a failed one must not stop the claim below.
            logger.LogWarning("The file-analysis stale sweep failed ({Failure}).", ex.GetType().Name);
        }

        var jobId = await queue.ClaimNextAsync(cancellationToken);
        if (jobId is not { } claimed)
        {
            // Idle: close any open burst so the run log shows the work as finished.
            await recorder.CloseBurstAsync(JobType.FileAnalysis, cancellationToken);
            return;
        }

        // A fresh scope for the run, so the claim's tracked state cannot leak into it.
        await using var runScope = scopes.CreateAsyncScope();
        var runner = runScope.ServiceProvider.GetRequiredService<FileAnalysisExtractionRunner>();
        // The runner writes every terminal state itself (with CancellationToken.None), so a shutdown mid-run
        // still leaves the job Failed / interrupted rather than Running.
        var result = await runner.RunAsync(claimed, cancellationToken);

        await recorder.ExtendBurstAsync<FileAnalysisJobRun>(JobType.FileAnalysis, run =>
        {
            switch (result)
            {
                case FileAnalysisRunResult.Completed:
                    run.AnalysesCompleted++;
                    break;
                case FileAnalysisRunResult.Failed:
                    run.AnalysesFailed++;
                    break;
                case FileAnalysisRunResult.Refused:
                    run.AnalysesRefused++;
                    break;
            }
        }, CancellationToken.None);
    }

    private JobRunRecorder Recorder()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
    }
}
