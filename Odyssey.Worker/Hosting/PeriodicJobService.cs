using Odyssey.Core.Jobs;

namespace Odyssey.Worker.Hosting;

/// <summary>
/// Runs one <see cref="IPeriodicJob"/>: immediately on start, then every <see cref="IPeriodicJob.Interval"/>
/// until the host stops. A failed run is logged and the next one goes ahead, so one bad run never ends the
/// job. Runs never overlap: <see cref="PeriodicTimer"/> waits for the run, and ticks missed meanwhile are
/// skipped rather than queued.
/// </summary>
internal sealed class PeriodicJobService<TJob>(TJob job, TimeProvider time, ILogger<PeriodicJobService<TJob>> logger)
    : BackgroundService
    where TJob : class, IPeriodicJob
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job {Job} started; it runs every {Interval}.", job.Name, job.Interval);
        using var timer = new PeriodicTimer(job.Interval, time);
        try
        {
            do
            {
                try
                {
                    await job.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "A run of job {Job} failed; the next run goes ahead.", job.Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping between runs.
        }
    }
}

internal static class PeriodicJobServiceCollectionExtensions
{
    /// <summary>Registers <typeparamref name="TJob"/> as a singleton and runs it on its own interval.</summary>
    public static IServiceCollection AddPeriodicJob<TJob>(this IServiceCollection services)
        where TJob : class, IPeriodicJob
    {
        services.AddSingleton<TJob>();
        services.AddHostedService<PeriodicJobService<TJob>>();
        return services;
    }
}
