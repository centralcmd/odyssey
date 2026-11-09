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
/// The relational half of issue #409: the two migrations (the pre-validation, the idempotent copy, the parity check),
/// the generated guards' unique indexes, the CHECKs, the <c>Generation</c> token and the requester's <c>SET NULL</c>
/// key. None of it is observable on EF InMemory, which computes no column and enforces no index, CHECK or key.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class JobRunsRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_job_runs";
    private const string PreviousMigration = "_AddPhotoAlbumIconAndColor";
    private const string CopyMigration = "_AddJobRuns";
    private const string DropMigration = "_DropExchangeRateSyncRuns";

    private static readonly DateTime Started = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>AC 1, 27 and §11: every legacy row arrives with its id and fields, a replay copies nothing twice, a Running row and a deleted requester included.</summary>
    [SkippableFact]
    public async Task The_migrations_copy_every_legacy_row_once_and_drop_the_old_table()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        await using var context = NewContext();
        await MigrationSeam.MigrateToAsync(context, PreviousMigration);

        var userId = Guid.NewGuid().ToString();
        await InsertUserAsync(context, userId);
        var succeeded = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var running = Guid.NewGuid();
        await InsertLegacyAsync(context, succeeded, trigger: 1, status: 1, errorKind: null, finished: Started.AddMinutes(2), requester: userId, inserted: 10, deleted: 0, rejected: 1);
        await InsertLegacyAsync(context, failed, trigger: 0, status: 3, errorKind: 1, finished: Started.AddMinutes(1), requester: null, inserted: 0, deleted: 0, rejected: 0);
        await InsertLegacyAsync(context, running, trigger: 0, status: 0, errorKind: null, finished: null, requester: null, inserted: 0, deleted: 0, rejected: 0);

        await MigrationSeam.MigrateToAsync(context, CopyMigration);

        Assert.Equal(3L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobRuns` WHERE `JobType` = 1"));
        var copied = await MigrationSeam.RowAsync(context, $"SELECT * FROM `JobRuns` WHERE `JobRunId` = '{succeeded}'");
        Assert.NotNull(copied);
        Assert.Equal(userId, copied!["RequestedByUserId"]);
        Assert.Equal(120_000L, Convert.ToInt64(copied["DurationMs"]));
        Assert.Equal(10, Convert.ToInt32(copied["ItemsProcessed"]));
        Assert.Equal(1, Convert.ToInt32(copied["ItemsFailed"]));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `JobRuns` WHERE `JobRunId` = '{failed}' AND `FailureCategory` = 1 AND `ErrorKind` = 1"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `JobRuns` WHERE `JobRunId` = '{running}' AND `Status` = 0 AND `ExclusiveGuard` = 1 AND `FinishedAtUtc` IS NULL"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` = 'JobRunRetentionDays' AND `Value` = '90'"));
        Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(DISTINCT `Generation`) - COUNT(*) FROM `JobRuns`"));

        // A replay of the copy (an interruption after it committed) inserts only what is missing, nothing twice.
        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `JobRuns` WHERE `JobRunId` <> '{failed}'");
        await context.Database.ExecuteSqlRawAsync(CopySql);
        Assert.Equal(3L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobRuns`"));
        await context.Database.ExecuteSqlRawAsync(CopySql);
        Assert.Equal(3L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobRuns`"));

        await MigrationSeam.MigrateToAsync(context, DropMigration);
        Assert.False(await MigrationSeam.TableExistsAsync(context, "ExchangeRateSyncRuns"));

        // AC 27: deleting the requester nulls the attribution and keeps the row.
        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `AspNetUsers` WHERE `Id` = '{userId}'");
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `JobRuns` WHERE `JobRunId` = '{succeeded}' AND `RequestedByUserId` IS NULL"));
    }

    /// <summary>AC 34: the pre-validation names the first offending legacy id and creates nothing.</summary>
    [SkippableFact]
    public async Task The_copy_aborts_naming_a_legacy_row_that_violates_a_constraint()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        await using var context = NewContext();
        await MigrationSeam.MigrateToAsync(context, PreviousMigration);
        var bad = Guid.NewGuid();
        // Succeeded with no finish time: allowed by the old table, refused by CK_JobRuns_FinishedMatchesStatus.
        await InsertLegacyAsync(context, bad, trigger: 0, status: 1, errorKind: null, finished: null, requester: null, inserted: 0, deleted: 0, rejected: 0);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrationSeam.MigrateToAsync(context, CopyMigration));

        Assert.Contains(bad.ToString(), ex.GetBaseException().Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await MigrationSeam.TableExistsAsync(context, "JobRuns"));
    }

    /// <summary>AC 2, 34: the parity check refuses the drop on a missing id and on a counter mismatch.</summary>
    [SkippableFact]
    public async Task The_drop_aborts_and_keeps_the_table_when_a_copy_is_missing_or_differs()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        await using var context = NewContext();
        await MigrationSeam.MigrateToAsync(context, PreviousMigration);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await InsertLegacyAsync(context, first, trigger: 0, status: 1, errorKind: null, finished: Started.AddMinutes(1), requester: null, inserted: 4, deleted: 0, rejected: 0);
        await InsertLegacyAsync(context, second, trigger: 0, status: 1, errorKind: null, finished: Started.AddMinutes(1), requester: null, inserted: 4, deleted: 0, rejected: 0);
        await MigrationSeam.MigrateToAsync(context, CopyMigration);

        await context.Database.ExecuteSqlRawAsync($"UPDATE `JobRuns` SET `RowsInserted` = 5 WHERE `JobRunId` = '{first}'");
        var mismatch = await Assert.ThrowsAnyAsync<Exception>(() => MigrationSeam.MigrateToAsync(context, DropMigration));
        Assert.Contains(first.ToString(), mismatch.GetBaseException().Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await MigrationSeam.TableExistsAsync(context, "ExchangeRateSyncRuns"));

        await context.Database.ExecuteSqlRawAsync($"UPDATE `JobRuns` SET `RowsInserted` = 4 WHERE `JobRunId` = '{first}'");
        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `JobRuns` WHERE `JobRunId` = '{second}'");
        var missing = await Assert.ThrowsAnyAsync<Exception>(() => MigrationSeam.MigrateToAsync(context, DropMigration));
        Assert.Contains(second.ToString(), missing.GetBaseException().Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await MigrationSeam.TableExistsAsync(context, "ExchangeRateSyncRuns"));
    }

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

        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 9, 0, 0, NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `AnalysesFailed`) VALUES ('{id}', 6, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 2, 0, 1, NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 2, 0, 5, NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `HeldReason`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 2, 0, 1, 1, NOW(), NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `PointsFailed`) VALUES ('{id}', 2, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `PhotosParked`) VALUES ('{id}', 5, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `PricesInserted`) VALUES ('{id}', 8, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `HeldReason`, `WorkerId`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`) VALUES ('{id}', 5, 0, 5, 7, UUID(), NOW(), NOW(), UUID())");
        await AssertCheckAsync($"INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`, `FilesParked`) VALUES ('{id}', 7, 0, 1, NOW(), NOW(), NOW(), UUID(), -1)");
        await AssertCheckAsync($"INSERT INTO `PhotoThumbnailJobs` (`PhotoId`, `State`, `Generation`, `Attempts`, `LeaseExpiries`, `RerunRequested`, `Priority`, `AttemptedRecipeVersion`, `NextAttemptAtUtc`, `UpdatedAtUtc`, `ManualRequeues`) VALUES ('{id}', 3, 0, 0, 0, 0, 0, 0, NOW(), NOW(), 2)");
    }

    /// <summary>Issue #428: the photo-original move's rows — a finished burst and a back-off held period — are accepted.</summary>
    [SkippableFact]
    public async Task A_photo_original_move_run_and_its_back_off_period_are_accepted()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var burst = Running<PhotoOriginalBackfillJobRun>(JobRunTrigger.Scheduled);
        burst.PhotosMoved = 4;
        burst.Interruptions = 1;
        burst.Finish(JobRunStatus.PartiallySucceeded, JobFailureCategory.Storage, DateTime.UtcNow);

        await AddAsync(burst);
        await AddAsync(new PhotoOriginalBackfillJobRun
        {
            JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Scheduled, Status = JobRunStatus.Held, HeldReason = JobHeldReason.BackingOff,
            StartedAtUtc = DateTime.UtcNow, LastActivityAtUtc = DateTime.UtcNow, WorkerId = Guid.NewGuid(),
        });
        await using var context = NewContext();
        context.JobHeartbeats.Add(new JobHeartbeat { JobType = JobType.PhotoOriginalBackfill, WorkerId = Guid.NewGuid(), LastTickAtUtc = DateTime.UtcNow });
        await context.SaveChangesAsync();
        Assert.Equal(2, await context.Set<PhotoOriginalBackfillJobRun>().CountAsync());
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

    /// <summary>The copy statement of <c>AddJobRuns</c>, read from the migration so the test re-runs exactly what ships.</summary>
    private static string CopySql
    {
        get
        {
            var source = File.ReadAllText(Directory.GetFiles(
                Path.Combine(RepositoryRoot(), "Odyssey.Context", "Migrations"), "*_AddJobRuns.cs").Single());
            var start = source.IndexOf("INSERT INTO `JobRuns`", StringComparison.Ordinal);
            var end = source.IndexOf("\"\"\");", start, StringComparison.Ordinal);
            return source[start..end].Trim();
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static TRun Running<TRun>(JobRunTrigger trigger)
        where TRun : JobRun, new() => new()
        {
            JobRunId = Guid.NewGuid(), Trigger = trigger, Status = JobRunStatus.Running,
            StartedAtUtc = DateTime.UtcNow, LastActivityAtUtc = DateTime.UtcNow, ClaimedAtUtc = DateTime.UtcNow, WorkerId = Guid.NewGuid(),
        };

    private static PhotoThumbnailJobRun Held(Guid worker) => new()
    {
        JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Scheduled, Status = JobRunStatus.Held, HeldReason = JobHeldReason.DecoderPaused,
        StartedAtUtc = DateTime.UtcNow, LastActivityAtUtc = DateTime.UtcNow, WorkerId = worker,
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

    private static Task InsertLegacyAsync(
        OdysseyContext context, Guid id, int trigger, int status, int? errorKind, DateTime? finished, string? requester,
        int inserted, int deleted, int rejected) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `ExchangeRateSyncRuns` (`ExchangeRateSyncRunId`, `Trigger`, `RequestedByUserId`, `Provider`, `Status`, `ErrorKind`, "
            + "`StartedAtUtc`, `FinishedAtUtc`, `SpanFrom`, `SpanTo`, `CurrencyCodes`, `RowsInserted`, `RowsSkipped`, `RowsRejected`, `RowsDeleted`, `ClaimedAtUtc`) "
            + "VALUES ({0}, {1}, {2}, 1, {3}, {4}, {5}, {6}, '2026-10-01', '2026-10-31', 'EUR,USD', {7}, 2, {8}, {9}, {5})",
            id, trigger, requester!, status, errorKind!, Started, finished!, inserted, rejected, deleted);

    private static Task InsertUserAsync(OdysseyContext context, string id) =>
        context.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO `AspNetUsers`
                 (`Id`, `UserName`, `NormalizedUserName`, `Email`, `NormalizedEmail`, `EmailConfirmed`,
                  `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `PhoneNumberConfirmed`,
                  `TwoFactorEnabled`, `LockoutEnabled`, `AccessFailedCount`, `MustChangePassword`)
             VALUES
                 ({id}, 'jobs@runs.test', 'JOBS@RUNS.TEST', 'jobs@runs.test', 'JOBS@RUNS.TEST', 1,
                  'hash', 'stamp', 'concurrency', 0, 0, 1, 0, 0)
             """);

    private async Task MigratedAsync()
    {
        await fixture.CreateMigratedDatabaseAsync(Database);
    }

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
        .Options);

    private async Task RecreateAsync()
    {
        await using var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }
}
