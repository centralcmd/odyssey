// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a constant or a Guid
// this test generated itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #409: the generated guards' unique indexes, the CHECKs and the <c>Generation</c>
/// token (the requester's <c>SET NULL</c> key is pinned with the others by <c>UserAttributionForeignKeyTests</c>).
/// None of it is observable on EF InMemory, which computes no column and enforces no index, CHECK or key.
/// </summary>
[Collection(MariaDbPrivateDatabaseCollection.Name)]
public class JobRunsRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_job_runs";

    /// <summary>AC 3: the exclusive guard admits one running sync or file-storage run, and one manual run per job.</summary>
    [SkippableFact]
    public async Task The_exclusive_guard_admits_one_exclusive_run_per_job()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        await AddAsync(Running<FileStorageMaintenanceJobRun>(JobRunTrigger.Scheduled));
        await AssertDuplicateAsync(Running<FileStorageMaintenanceJobRun>(JobRunTrigger.Manual));
        await AddAsync(Running<ExchangeRateSyncJobRun>(JobRunTrigger.Manual));
        await AssertDuplicateAsync(Running<ExchangeRateSyncJobRun>(JobRunTrigger.Purge));

        // Scheduled bursts of the work-driven jobs run side by side; a manual run of each is exclusive.
        await AddAsync(Running<WeatherJobRun>(JobRunTrigger.Scheduled));
        await AddAsync(Running<WeatherJobRun>(JobRunTrigger.Scheduled));
        await AddAsync(Running<WeatherJobRun>(JobRunTrigger.Manual));
        await AssertDuplicateAsync(Running<WeatherJobRun>(JobRunTrigger.Manual));
        await AddAsync(Running<PhotoThumbnailJobRun>(JobRunTrigger.Manual));
    }

    /// <summary>One open held period per process and job; a closed one frees the slot.</summary>
    [SkippableFact]
    public async Task One_process_holds_one_open_period_per_job()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var worker = Guid.NewGuid();

        var first = Held(worker);
        await AddAsync(first);
        await AssertDuplicateAsync(Held(worker));
        await AddAsync(Held(Guid.NewGuid()));

        await using (var context = NewContext())
        {
            var row = await context.JobRuns.SingleAsync(r => r.JobRunId == first.JobRunId);
            row.Finish(JobRunStatus.Held, null, DateTime.UtcNow);
            await context.SaveChangesAsync();
        }

        await AddAsync(Held(worker));
    }

    [SkippableFact]
    public async Task The_checks_refuse_an_inconsistent_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var id = Guid.NewGuid();

        // An unknown job type: 10 is the recurrence-key backfill's since issue #519, 11 and 12 the podcast audio jobs'
        // since issue #556, 13 and 14 the recipe-import queue and retention since issue #602.
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 15, 0, 0, NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `ItemsRevised`) VALUES ('{id}', 9, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `AnalysesFailed`) VALUES ('{id}', 6, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 2, 0, 1, NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 2, 0, 5, NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `HeldReason`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 2, 0, 1, 1, NOW(), NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `PointsFailed`) VALUES ('{id}', 2, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        // 5 and 7 are the retired file moves' ordinals, and 6 the retired BackingOff held reason: never stored again.
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 5, 0, 1, NOW(), NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 7, 0, 1, NOW(), NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobHeartbeats` (`JobType`, `WorkerId`, `LastTickAtUtc`) VALUES (5, UUID(), NOW())");
        await AssertCheckAsync($"INSERT INTO `JobHeartbeats` (`JobType`, `WorkerId`, `LastTickAtUtc`) VALUES (7, UUID(), NOW())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `HeldReason`, `WorkerId`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 4, 0, 5, 6, UUID(), NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `PricesInserted`) VALUES ('{id}', 8, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `HeldReason`, `WorkerId`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 4, 0, 5, 7, UUID(), NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `PhotoThumbnailJobs` (`PhotoId`, `State`, `Generation`, `Attempts`, `LeaseExpiries`, `RerunRequested`, `Priority`, `AttemptedRecipeVersion`, `NextAttemptAtUtc`, `UpdatedAtUtc`, `ManualRequeues`) VALUES ('{id}', 3, 0, 0, 0, 0, 0, 0, NOW(), NOW(), 2)");
    }

    /// <summary>Issue #444: the file-analysis queue's burst and heartbeat rows are accepted by the widened CHECKs.</summary>
    [SkippableFact]
    public async Task A_file_analysis_run_and_its_heartbeat_are_accepted()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var burst = Running<FileAnalysisJobRun>(JobRunTrigger.Scheduled);
        burst.AnalysesCompleted = 2;
        burst.AnalysesFailed = 1;
        burst.AnalysesRefused = 1;
        burst.Finish(JobRunStatus.PartiallySucceeded, JobFailureCategory.ProviderUnavailable, DateTime.UtcNow);

        await AddAsync(burst);
        await using var context = NewContext();
        context.JobHeartbeats.Add(new JobHeartbeat { JobType = JobType.FileAnalysis, WorkerId = Guid.NewGuid(), LastTickAtUtc = DateTime.UtcNow });
        await context.SaveChangesAsync();
        var stored = await context.Set<FileAnalysisJobRun>().SingleAsync();
        Assert.Equal((2, 1, 1), (stored.AnalysesCompleted, stored.AnalysesFailed, stored.AnalysesRefused));
    }

    /// <summary>AC 9 on real MariaDB: the second writer of one row loses on its Generation.</summary>
    [SkippableFact]
    public async Task A_second_writer_loses_on_the_generation()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var run = Running<WeatherJobRun>(JobRunTrigger.Scheduled);
        await AddAsync(run);

        await using var first = NewContext();
        await using var second = NewContext();
        var a = await first.JobRuns.SingleAsync(r => r.JobRunId == run.JobRunId);
        var b = await second.JobRuns.SingleAsync(r => r.JobRunId == run.JobRunId);
        a.Finish(JobRunStatus.Failed, JobFailureCategory.Abandoned, DateTime.UtcNow);
        await first.SaveChangesAsync();
        b.Finish(JobRunStatus.Succeeded, null, DateTime.UtcNow);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var verify = NewContext();
        Assert.Equal(JobRunStatus.Failed, (await verify.JobRuns.SingleAsync(r => r.JobRunId == run.JobRunId)).Status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────

    private static TRun Running<TRun>(JobRunTrigger trigger)
        where TRun : JobRun, new() => new()
        {
            JobRunId = Guid.NewGuid(),
            Trigger = trigger,
            Status = JobRunStatus.Running,
            StartedAtUtc = DateTime.UtcNow,
            LastActivityAtUtc = DateTime.UtcNow,
            ClaimedAtUtc = DateTime.UtcNow,
            WorkerId = Guid.NewGuid(),
        };

    private static PhotoThumbnailJobRun Held(Guid worker) => new()
    {
        JobRunId = Guid.NewGuid(),
        Trigger = JobRunTrigger.Scheduled,
        Status = JobRunStatus.Held,
        HeldReason = JobHeldReason.DecoderPaused,
        StartedAtUtc = DateTime.UtcNow,
        LastActivityAtUtc = DateTime.UtcNow,
        WorkerId = worker,
    };

    private async Task AddAsync(JobRun run)
    {
        await using var context = NewContext();
        context.JobRuns.Add(run);
        await context.SaveChangesAsync();
    }

    private async Task AssertDuplicateAsync(JobRun run)
    {
        await using var context = NewContext();
        context.JobRuns.Add(run);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(1062, (ex.GetBaseException() as MySqlException)?.Number);
    }

    private async Task AssertCheckAsync(string sql)
    {
        await using var context = NewContext();
        var ex = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(4025, ex.Number);
    }

    private async Task MigratedAsync()
    {
        await fixture.CreateMigratedDatabaseAsync(Database);
    }

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
        .Options);
}
