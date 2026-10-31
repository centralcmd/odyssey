// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a constant or a Guid this
// test generated itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #346: the migration and its constraints, the reservation's independent commit, the
/// database's RESTRICT key as the re-check for an unknown feature, concurrent writers, the maintenance lease, and the
/// no-op rule on a relational host. None of it is observable on the EF InMemory provider, which honours no
/// transaction, unique index, CHECK or foreign key.
/// </summary>
[Collection(MariaDbCollection.Name)]
public sealed class FileStorageRelationalTests(MariaDbFixture fixture) : IAsyncLifetime
{
    private const string Database = "odyssey_file_storage";
    private const string PreviousMigration = "_AddExchangeRateProviderSeries";
    private const string ThisMigration = "_AddFileSystemObjectStorage";

    private readonly DirectoryInfo volumes = Directory.CreateTempSubdirectory("odyssey-fs-it-");
    private readonly List<FileStorageTestHost> hosts = [];
    private readonly TestClock clock = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in hosts)
        {
            await host.DisposeAsync();
        }

        volumes.Delete(recursive: true);
    }

    [SkippableFact]
    public async Task The_migration_creates_the_tables_with_their_keys_checks_and_seeds_and_its_down_removes_them()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync(migrate: false);
        await using var context = NewContext();
        await MigrationSeam.MigrateToAsync(context, ThisMigration);

        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` = 'FileStorageDeletionGraceHours' AND `Value` = '24'"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `FileStorageMaintenanceLeases` WHERE `Id` = 1"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'StoredObjects' AND REFERENCED_TABLE_NAME = 'FileStorageVolumes' AND DELETE_RULE = 'RESTRICT'"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'FileStorageOrphanSightings' AND DELETE_RULE = 'CASCADE'"));

        var volumeId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO `FileStorageVolumes` (`Id`, `Name`, `LastSeenAtUtc`, `ReconciliationPass`, `QuarantinedFileCount`) VALUES ('{volumeId}', 'primary', NOW(), 0, 0)");
        await InsertObjectAsync(context, volumeId, "test-files/a/Photo.JPG", state: 1, hash: "'h'");

        // The unique index is case-insensitive, as the store's collision rule assumes.
        await Assert.ThrowsAsync<MySqlException>(() => InsertObjectAsync(context, volumeId, "test-files/a/photo.jpg", state: 1, hash: "'h'"));
        // CK_StoredObjects_StateRange, _HashUnlessPending, _DeleteRequestedWhenPendingDelete, _SizeNonNegative.
        await Assert.ThrowsAsync<MySqlException>(() => InsertObjectAsync(context, volumeId, "test-files/a/1.jpg", state: 3, hash: "'h'"));
        await Assert.ThrowsAsync<MySqlException>(() => InsertObjectAsync(context, volumeId, "test-files/a/2.jpg", state: 1, hash: "NULL"));
        await Assert.ThrowsAsync<MySqlException>(() => InsertObjectAsync(context, volumeId, "test-files/a/3.jpg", state: 2, hash: "'h'"));
        await Assert.ThrowsAsync<MySqlException>(() => InsertObjectAsync(context, volumeId, "test-files/a/4.jpg", state: 1, hash: "'h'", size: -1));
        await InsertObjectAsync(context, volumeId, "test-files/a/5.jpg", state: 0, hash: "NULL");
        // CK_FileStorageMaintenanceLeases_Singleton.
        await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO `FileStorageMaintenanceLeases` (`Id`, `Version`) VALUES (2, '{Guid.NewGuid()}')"));
        // RESTRICT: a volume holding objects cannot be removed.
        await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync($"DELETE FROM `FileStorageVolumes` WHERE `Id` = '{volumeId}'"));

        await context.Database.ExecuteSqlRawAsync("DELETE FROM `StoredObjects`");
        await MigrationSeam.MigrateToAsync(context, PreviousMigration);
        Assert.False(await MigrationSeam.TableExistsAsync(context, "StoredObjects"));
        Assert.False(await MigrationSeam.TableExistsAsync(context, "FileStorageVolumes"));
        Assert.False(await MigrationSeam.TableExistsAsync(context, "FileStorageOrphanSightings"));
        Assert.False(await MigrationSeam.TableExistsAsync(context, "FileStorageMaintenanceLeases"));
        Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` = 'FileStorageDeletionGraceHours'"));
    }

    [SkippableFact]
    public async Task Two_concurrent_puts_of_one_name_get_two_paths_and_both_commit()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var host = await HostAsync();
        var owner = Guid.NewGuid();

        var rows = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PutCommittedAsync(host, owner, "IMG_2041.jpg")));

        Assert.Equal(4, rows.Select(r => r.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        await using var context = NewContext();
        Assert.Equal(4L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `StoredObjects` WHERE `State` = 1"));
    }

    [SkippableFact]
    public async Task A_case_only_variant_collides_through_the_store()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var host = await HostAsync();
        var owner = Guid.NewGuid();

        await PutCommittedAsync(host, owner, "Photo.JPG");
        var second = await PutCommittedAsync(host, owner, "photo.jpg");

        Assert.EndsWith("/photo (2).jpg", second.RelativePath);
    }

    [SkippableFact]
    public async Task A_reservation_commits_on_its_own_whatever_the_callers_transaction_does()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var host = await HostAsync();
        StoredObject row = null!;

        await using (var scope = host.Scope())
        {
            var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync();
                context.TestFileAttachments.Add(new TestFileAttachment { Id = Guid.NewGuid() });
                row = await store.PutAsync("TestFiles", Guid.NewGuid(), "x.jpg", null, "image/jpeg", new MemoryStream(new byte[100]), 1000, default);
                await transaction.RollbackAsync();
            });
        }

        await using (var fresh = NewContext())
        {
            Assert.Equal(1L, await MigrationSeam.CountAsync(fresh, $"SELECT COUNT(*) FROM `StoredObjects` WHERE `Id` = '{row.Id}' AND `State` = 0"));
            Assert.Equal(0L, await MigrationSeam.CountAsync(fresh, "SELECT COUNT(*) FROM `TestFileAttachments`"));
        }

        var path = Path.Combine(volumes.FullName, "primary", row.RelativePath);
        Assert.True(File.Exists(path));

        clock.Advance(TimeSpan.FromHours(2));
        await using (var scope = host.Scope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<FileStorageSweeper>().SweepAbandonedWritesAsync(default));
        }

        Assert.False(File.Exists(path));
        await using var after = NewContext();
        Assert.Equal(0L, await MigrationSeam.CountAsync(after, "SELECT COUNT(*) FROM `StoredObjects`"));
    }

    [SkippableFact]
    public async Task The_reservation_context_carries_the_retrying_execution_strategy()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var host = await HostAsync();

        await using var scope = host.Scope();
        await using var reservation = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<OdysseyContext>>().CreateDbContextAsync();

        Assert.True(reservation.Database.CreateExecutionStrategy().RetriesOnFailure);
    }

    [SkippableFact]
    public async Task An_unknown_features_release_still_named_by_a_restrict_key_keeps_its_row_and_file()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var host = await HostAsync();
        var row = await PutCommittedAsync(host, Guid.NewGuid(), "secret.jpg", TestStorageFeatures.Secrets);
        await using (var scope = host.Scope())
        {
            // Released without dropping the reference: a consumer defect only the database can catch once the
            // feature has left the catalogue.
            await scope.ServiceProvider.GetRequiredService<IObjectStore>().StageReleaseAsync(row.Id, immediate: true, default);
            await scope.ServiceProvider.GetRequiredService<OdysseyContext>().SaveChangesAsync();
        }

        var rolledBack = await HostAsync([TestStorageFeatures.Files, TestStorageFeatures.Dated]);
        for (var tick = 0; tick < 2; tick++)
        {
            await using var scope = rolledBack.Scope();
            await scope.ServiceProvider.GetRequiredService<FileStorageSweeper>().SweepDeferredDeletesAsync(24, default);
        }

        await using var context = NewContext();
        Assert.Equal(1L, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `StoredObjects` WHERE `Id` = '{row.Id}' AND `State` = 2"));
        Assert.True(File.Exists(Path.Combine(volumes.FullName, "primary", row.RelativePath)));
        Assert.Equal(2, rolledBack.Logs.At(LogLevel.Error).Count(m => m.Contains("still referenced", StringComparison.Ordinal)));

        // Once the reference goes, the next tick erases it.
        await context.Database.ExecuteSqlRawAsync("DELETE FROM `TestSecretAttachments`");
        await using (var scope = rolledBack.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<FileStorageSweeper>().SweepDeferredDeletesAsync(24, default);
        }

        Assert.Equal(0L, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `StoredObjects` WHERE `Id` = '{row.Id}'"));
        Assert.False(File.Exists(Path.Combine(volumes.FullName, "primary", row.RelativePath)));
    }

    [SkippableTheory]
    [InlineData(".quarantine/x/y.jpg")]
    [InlineData(".tmp/x/y.jpg")]
    [InlineData("lost+found/x/y.jpg")]
    [InlineData("../x/y.jpg")]
    [InlineData("test-files/x/live.jpg")]
    public async Task A_hand_inserted_unknown_feature_row_with_a_hostile_path_is_refused(string tampered)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var host = await HostAsync();
        var target = Path.GetFullPath(Path.Combine(volumes.FullName, "primary", tampered));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "keep");
        var volumeId = host.Registry.Find("primary")!.Id;

        await using (var context = NewContext())
        {
            await InsertObjectAsync(context, volumeId, tampered, state: 2, hash: "'h'", feature: "GoneFeature", deleteRequested: true);
            await InsertObjectAsync(context, volumeId, tampered + ".x.jpg", state: 0, hash: "NULL", feature: "GoneFeature", createdHoursAgo: 3);
        }

        await using (var scope = host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<FileStorageSweeper>().RunAsync(null, default);
        }

        await using var after = NewContext();
        Assert.Equal(2L, await MigrationSeam.CountAsync(after, "SELECT COUNT(*) FROM `StoredObjects` WHERE `FeatureKey` = 'GoneFeature'"));
        Assert.True(File.Exists(target));
        Assert.Contains(host.Logs.At(LogLevel.Error), m => m.StartsWith("Security:", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Two_lease_claims_at_one_moment_elect_exactly_one_runner_and_an_expired_lease_is_taken_over()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var first = await HostAsync();
        var second = await HostAsync();

        var claims = await Task.WhenAll(
            first.Services.GetRequiredService<FileStorageMaintenanceJob>().TryClaimAsync(Guid.NewGuid(), default),
            second.Services.GetRequiredService<FileStorageMaintenanceJob>().TryClaimAsync(Guid.NewGuid(), default));
        Assert.Single(claims, claimed => claimed);

        clock.Advance(FileStorageMaintenanceJob.LeaseDuration + TimeSpan.FromSeconds(1));
        await second.Services.GetRequiredService<FileStorageMaintenanceJob>().RunOnceAsync(default);

        await using var context = NewContext();
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `FileStorageMaintenanceLeases` WHERE `HolderId` IS NULL AND `LastCompletedAtUtc` IS NOT NULL"));
    }

    /// <summary>
    /// The overdue count's SQL on the real engine — both branches (immediate and graced) and the per-volume grouping —
    /// and a worker's verdict written beside a lease claim.
    /// </summary>
    [SkippableFact]
    public async Task Overdue_deletes_are_counted_per_volume_and_a_failed_worker_check_is_recorded_and_reported()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var api = await HostAsync();
        await PutCommittedAsync(api, Guid.NewGuid(), "kept.jpg");
        var archiveId = Guid.NewGuid();
        await using (var context = NewContext())
        {
            var primaryId = (await context.FileStorageVolumes.SingleAsync()).Id;
            await context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO `FileStorageVolumes` (`Id`, `Name`, `LastSeenAtUtc`, `ReconciliationPass`, `QuarantinedFileCount`) VALUES ('{archiveId}', 'archive', NOW(), 0, 0)");
            await InsertObjectAsync(context, primaryId, "test-files/a/immediate.jpg", state: 2, hash: "'h'", deleteRequested: true);
            await InsertObjectAsync(context, primaryId, "test-files/a/graced.jpg", state: 2, hash: "'h'", deleteRequested: true, deleteImmediately: false);
            await InsertObjectAsync(context, archiveId, "test-files/a/graced.jpg", state: 2, hash: "'h'", deleteRequested: true, deleteImmediately: false);
        }

        clock.Advance(FileStorageStatusService.OverdueAfter - TimeSpan.FromSeconds(1));
        Assert.Equal((0, 0), await OverdueAsync(api, archiveId));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((1, 0), await OverdueAsync(api, archiveId));
        clock.Advance(TimeSpan.FromHours(Dtos.SystemSettingsDefaults.FileStorageDeletionGraceHours));
        Assert.Equal((2, 1), await OverdueAsync(api, archiveId));

        // The worker's mount is wrong: an empty directory where the volume should be.
        var root = Path.Combine(volumes.FullName, "primary");
        Directory.Move(root, root + "-aside");
        Directory.CreateDirectory(root);
        var worker = FileStorageTestHost.Create(UseDatabase,
            new Dictionary<string, string> { ["primary"] = root },
            TestStorageFeatures.All.ToDictionary(f => f.Key, _ => "primary"),
            TestStorageFeatures.All,
            clock);
        hosts.Add(worker);
        var job = worker.Services.GetRequiredService<FileStorageMaintenanceJob>();
        Assert.True(await job.TryClaimAsync(Guid.NewGuid(), default)); // a lease claim alongside the verdict write

        using var stopping = new CancellationTokenSource();
        var run = job.RunOnceAsync(stopping.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((await StatusAsync(api, "primary")).MaintenanceHaltedCheck is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The worker's verdict was never recorded.");
            await Task.Delay(50);
        }

        var status = await StatusAsync(api, "primary");
        Assert.Equal(("VolumeLost", clock.GetUtcNow().UtcDateTime, 1),
            (status.MaintenanceHaltedCheck, status.MaintenanceHaltedAtUtc, status.MaintenanceHaltedWorkerCount));
        var workerId = worker.Services.GetRequiredService<FileStorageWorkerVerification>().WorkerId;
        await using (var context = NewContext())
        {
            Assert.Equal(1L, await MigrationSeam.CountAsync(context,
                $"SELECT COUNT(*) FROM `FileStorageWorkerVerdicts` WHERE `WorkerId` = '{workerId}' AND `FailedCheck` = 'VolumeLost'"));
        }

        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static async Task<(int Primary, int Archive)> OverdueAsync(FileStorageTestHost host, Guid archiveId)
    {
        await using var scope = host.Scope();
        var all = await scope.ServiceProvider.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default);
        return (all.Single(v => v.Name == "primary").OverdueDeleteCount, all.Single(v => v.Name == "archive").OverdueDeleteCount);
    }

    private static async Task<Dtos.Application.FileStorageVolumeStatus> StatusAsync(FileStorageTestHost host, string volume)
    {
        await using var scope = host.Scope();
        return (await scope.ServiceProvider.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default)).Single(v => v.Name == volume);
    }

    [SkippableFact]
    public async Task A_relational_host_with_a_catalogue_and_no_volumes_fails_B1_and_one_with_nothing_set_up_is_a_no_op()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var withCatalogue = FileStorageTestHost.Create(UseDatabase, new Dictionary<string, string>(), clock: clock);
        hosts.Add(withCatalogue);
        var error = await Assert.ThrowsAsync<FileStorageMisconfiguredException>(() => withCatalogue.VerifyAsync());
        Assert.Equal("UnboundFeature", error.Check);

        var nothing = FileStorageTestHost.Create(UseDatabase, new Dictionary<string, string>(), features: [], clock: clock);
        hosts.Add(nothing);
        Assert.True((await nothing.VerifyAsync()).NoOp);
        Assert.False(nothing.Registry.IsActive);
    }

    private async Task<FileStorageTestHost> HostAsync(IEnumerable<StorageFeature>? features = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(volumes.FullName, "primary")).FullName;
        var catalogue = (features ?? TestStorageFeatures.All).ToList();
        var host = FileStorageTestHost.Create(UseDatabase,
            new Dictionary<string, string> { ["primary"] = root },
            catalogue.ToDictionary(f => f.Key, _ => "primary"),
            catalogue,
            clock);
        hosts.Add(host);
        await host.VerifyAsync();
        return host;
    }

    private static async Task<StoredObject> PutCommittedAsync(FileStorageTestHost host, Guid owner, string name, StorageFeature? feature = null)
    {
        feature ??= TestStorageFeatures.Files;
        await using var scope = host.Scope();
        var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync(feature.Key, owner, name, null, "image/jpeg", new MemoryStream(new byte[1000]), 10_000, default);
        if (feature == TestStorageFeatures.Secrets)
        {
            context.TestSecretAttachments.Add(new TestSecretAttachment { Id = Guid.NewGuid(), StoredObjectId = row.Id });
        }
        else
        {
            context.TestFileAttachments.Add(new TestFileAttachment { Id = Guid.NewGuid(), StoredObjectId = row.Id });
        }

        await context.SaveChangesAsync();
        return row;
    }

    private static Task InsertObjectAsync(
        OdysseyContext context, Guid volumeId, string path, int state, string hash, long size = 1, string feature = "TestFiles",
        bool deleteRequested = false, int createdHoursAgo = 0, bool? deleteImmediately = null) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `StoredObjects` (`Id`, `VolumeId`, `FeatureKey`, `RelativePath`, `SizeBytes`, `Sha256Hash`, `State`, `CreatedAtUtc`, "
            + "`DeleteRequestedAtUtc`, `DeleteImmediately`) VALUES "
            + $"('{Guid.NewGuid()}', '{volumeId}', '{feature}', {{0}}, {size}, {hash}, {state}, DATE_SUB('2026-10-25 09:00:00', INTERVAL {createdHoursAgo} HOUR), "
            + $"{(deleteRequested ? "'2026-10-25 09:00:00'" : "NULL")}, {((deleteImmediately ?? deleteRequested) ? 1 : 0)})",
            [path]);

    private void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options) =>
        options.UseMySql(fixture.ConnectionStringFor(Database), new MariaDbServerVersion(new Version(11, 4)), mySql => mySql.EnableRetryOnFailure());

    /// <summary>The base context: a derived one would see none of <c>OdysseyContext</c>'s migrations.</summary>
    private OdysseyContext NewContext() =>
        new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), new MariaDbServerVersion(new Version(11, 4))).Options);

    private async Task RecreateAsync(bool migrate = true)
    {
        await using (var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, new MariaDbServerVersion(new Version(11, 4))).Options))
        {
            await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
            await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        if (!migrate)
        {
            return;
        }

        await using var context = NewContext();
        await context.Database.MigrateAsync();
        foreach (var sql in TestStorageContext.CreateTablesSql)
        {
            await context.Database.ExecuteSqlRawAsync(sql);
        }
    }
}
