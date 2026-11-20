using Odyssey.Core.FileStorage;
using Odyssey.TestData;

namespace Odyssey.Worker.DemoData;

/// <summary>
/// Runs <see cref="DemoFileSeeder"/> once per process start, in Development and Testing only (<see cref="DemoDataGate"/>,
/// the gate the migrations job's demo seed reads). It first waits for the API to initialise the file volumes — the wait
/// the storage maintenance job uses — and a failed pass is retried a few times before it gives up, so a database or
/// volume that is briefly away does not leave a dev stack without its demo files.
/// </summary>
/// <remarks>
/// Not a catalogued <c>IPeriodicJob</c>: it has no schedule and records no runs, and once a pass succeeds there is nothing
/// left for it to do. The files therefore appear shortly <em>after</em> the stack starts, never before; a test that
/// needs them polls for them.
/// </remarks>
public sealed class DemoFileSeedingService(
    IHostEnvironment environment,
    IConfiguration configuration,
    FileStorageWorkerVerification verification,
    DemoFileSeeder seeder,
    TimeProvider time,
    ILogger<DemoFileSeedingService> logger) : BackgroundService
{
    public const int Attempts = 5;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!DemoDataGate.IsEnabled(environment, configuration, logger))
        {
            return;
        }

        // Yield first, so the host's start is never held by the volume wait below.
        await Task.Yield();
        await verification.EnsureVerifiedAsync(stoppingToken);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                await seeder.SeedAsync(DemoDataSet.Build(), stoppingToken);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Seeding the demo files failed, attempt {Attempt} of {Attempts} ({Failure}).",
                    attempt, Attempts, FileStorageLog.Describe(ex));
            }

            if (attempt < Attempts)
            {
                await Task.Delay(RetryDelay, time, stoppingToken);
            }
        }

        logger.LogError("The demo files were not seeded after {Attempts} attempts; they are retried at the next worker start.", Attempts);
    }
}
