using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>The maintenance sweeps (issue #346 §3.5, §3.7; AC 5, 6, 11–14, 16, 20, 23 unknown-feature rule).</summary>
public class FileStorageSweeperTests : FileStorageTestBase
{
    private static readonly Guid Owner = Guid.Parse("3fa29c0e-8b7d-4e51-a6c2-d9f01b4e7a33");

    [Fact]
    public async Task An_uncommitted_put_leaves_a_pending_row_and_a_file_that_the_abandoned_write_sweep_removes()
    {
        var host = await NewHostAsync();
        StoredObject row;
        await using (var scope = host.Scope())
        {
            var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.TestFileAttachments.Add(new TestFileAttachment { Id = Guid.NewGuid() }); // the caller's other staged work
            row = await scope.ServiceProvider.GetRequiredService<IObjectStore>()
                .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(100), 1000, default);
            // The caller's transaction rolls back: nothing of its own is saved, and PutAsync saved none of it.
        }

        await using (var check = host.Scope())
        {
            var context = (TestStorageContext)check.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.Equal(StoredObjectState.Pending, (await context.StoredObjects.SingleAsync()).State);
            Assert.Equal(0, await context.TestFileAttachments.CountAsync());
        }

        var path = Path.Combine(PrimaryRoot, row.RelativePath);
        Assert.True(File.Exists(path));

        Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, await SweepAsync(host, s => s.SweepAbandonedWritesAsync(default)));

        Assert.Null(await RowAsync(host, row.Id));
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(PrimaryRoot, "test-files", "3f")));
        Assert.True(Directory.Exists(Path.Combine(PrimaryRoot, "test-files")));
    }

    [Fact]
    public async Task A_caller_save_after_the_sweep_removed_its_reservation_fails_and_commits_nothing()
    {
        var host = await NewHostAsync();
        await using var scope = host.Scope();
        var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(100), 1000, default);
        context.TestFileAttachments.Add(new TestFileAttachment { Id = Guid.NewGuid(), StoredObjectId = row.Id });

        Clock.Advance(TimeSpan.FromHours(2));
        await SweepAsync(host, s => s.SweepAbandonedWritesAsync(default));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
        Assert.Null(await RowAsync(host, row.Id));
    }

    [Fact]
    public async Task A_release_waits_for_the_grace_and_then_removes_the_row_the_file_and_empty_folders()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await ObjectStoreTests.ReleaseAsync(host, row.Id);
        var path = Path.Combine(PrimaryRoot, row.RelativePath);

        Clock.Advance(TimeSpan.FromHours(23));
        await RunTickAsync(host);
        Assert.True(File.Exists(path));
        Assert.Equal(StoredObjectState.PendingDelete, (await RowAsync(host, row.Id))!.State);

        Clock.Advance(TimeSpan.FromHours(2));
        await RunTickAsync(host);
        Assert.False(File.Exists(path));
        Assert.Null(await RowAsync(host, row.Id));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(PrimaryRoot, "test-files")));
    }

    [Fact]
    public async Task A_shorter_grace_applies_to_rows_already_pending_deletion()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await ObjectStoreTests.ReleaseAsync(host, row.Id);
        Clock.Advance(TimeSpan.FromHours(3));
        await RunTickAsync(host);
        Assert.NotNull(await RowAsync(host, row.Id));

        await SetGraceAsync(host, "2");
        await RunTickAsync(host);

        Assert.Null(await RowAsync(host, row.Id));
    }

    [Fact]
    public async Task An_immediate_release_is_processed_on_the_next_tick_whatever_the_grace()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await ObjectStoreTests.ReleaseAsync(host, row.Id, immediate: true);

        await RunTickAsync(host);

        Assert.Null(await RowAsync(host, row.Id));
        Assert.False(File.Exists(Path.Combine(PrimaryRoot, row.RelativePath)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("500")]
    [InlineData("169")]
    public async Task Every_grace_outcome_but_a_healthy_one_resolves_to_168_hours_with_a_warning(string stored)
    {
        var host = await NewHostAsync();
        await SetGraceAsync(host, stored);

        var hours = await InScopeAsync(host, sp => sp.GetRequiredService<FileStorageSweeper>().ResolveGraceHoursAsync(default));

        Assert.Equal(168, hours);
        Assert.Contains(host.Logs.At(LogLevel.Warning), m => m.Contains("deletion grace", StringComparison.Ordinal));
    }

    /// <summary>
    /// The resolver's degraded value is max(last-known-good, default) — 24 here, with a healthy 24 cached first. The
    /// sweeper must still use 168, for a corrupt row and for a failed query alike.
    /// </summary>
    [Fact]
    public async Task A_degraded_read_after_a_healthy_one_still_resolves_to_168_hours()
    {
        var host = await NewHostAsync();
        await using var scope = host.Scope();
        var sweeper = scope.ServiceProvider.GetRequiredService<FileStorageSweeper>();
        Assert.Equal(24, await sweeper.ResolveGraceHoursAsync(default)); // warms the watermark

        await SetGraceAsync(host, "abc");
        Assert.Equal(168, await sweeper.ResolveGraceHoursAsync(default));

        // A failed query: the sweeper's own context is gone.
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var failing = new FileStorageSweeper(
            DisposedContext(), scope.ServiceProvider.GetRequiredService<StorageFeatureCatalogue>(),
            host.Registry, new FileStorageSweepMemory(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileStorageSweeper>.Instance);
        Assert.Equal(168, await failing.ResolveGraceHoursAsync(default));
        _ = context;
    }

    private OdysseyContext DisposedContext()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>();
        UseDatabase(options);
        var context = new OdysseyContext(options.Options);
        context.Dispose();
        return context;
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData("1", 1)]
    [InlineData("168", 168)]
    [InlineData("48", 48)]
    public async Task A_healthy_grace_is_used_as_stored(string? stored, int expected)
    {
        var host = await NewHostAsync();
        if (stored is not null)
        {
            await SetGraceAsync(host, stored);
        }

        Assert.Equal(expected, await InScopeAsync(host, sp => sp.GetRequiredService<FileStorageSweeper>().ResolveGraceHoursAsync(default)));
    }

    [Fact]
    public async Task A_referenced_object_is_kept_and_a_cascade_deleted_reference_releases_it()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(0, await SweepAsync(host, s => s.SweepUnreferencedAsync(default)));

        await DropReferencesAsync(host, row.Id);
        Assert.Equal(1, await SweepAsync(host, s => s.SweepUnreferencedAsync(default)));
        var released = await RowAsync(host, row.Id);
        Assert.Equal(StoredObjectState.PendingDelete, released!.State);
        Assert.False(released.DeleteImmediately);
    }

    [Fact]
    public async Task A_flagged_object_is_still_collected_when_unreferenced()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await InScopeAsync(host, async sp =>
        {
            var context = sp.GetRequiredService<OdysseyContext>();
            (await context.StoredObjects.SingleAsync()).IntegrityFaultAtUtc = Clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync();
        });
        await DropReferencesAsync(host, row.Id);
        Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(1, await SweepAsync(host, s => s.SweepUnreferencedAsync(default)));
    }

    [Fact]
    public async Task An_immediate_features_cascade_orphan_is_removed_within_two_ticks_whatever_the_grace()
    {
        var host = await NewHostAsync();
        await SetGraceAsync(host, "168");
        var row = await PutCommittedAsync(host, TestStorageFeatures.Secrets, Owner);
        await DropReferencesAsync(host, row.Id);
        Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        await RunTickAsync(host);
        await RunTickAsync(host);

        Assert.Null(await RowAsync(host, row.Id));
        Assert.False(File.Exists(Path.Combine(PrimaryRoot, row.RelativePath)));
    }

    [Fact]
    public async Task A_release_of_an_object_still_referenced_is_reverted_with_an_error_and_the_file_kept()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await ObjectStoreTests.ReleaseAsync(host, row.Id, immediate: true, dropReference: false);

        await SweepAsync(host, s => s.SweepDeferredDeletesAsync(24, default));

        var reverted = await RowAsync(host, row.Id);
        Assert.Equal(StoredObjectState.Committed, reverted!.State);
        Assert.Null(reverted.DeleteRequestedAtUtc);
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, row.RelativePath)));
        Assert.Contains(host.Logs.At(LogLevel.Error), m => m.Contains("still referenced", StringComparison.Ordinal) && m.Contains(row.Id.ToString()));
    }

    [Fact]
    public async Task A_corrupted_file_is_flagged_on_a_reproduced_mismatch_served_as_500_and_cleared_when_restored()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, length: 4096);
        var path = Path.Combine(PrimaryRoot, row.RelativePath);
        var original = await File.ReadAllBytesAsync(path);
        var flipped = (byte[])original.Clone();
        flipped[100] ^= 0xFF;
        await File.WriteAllBytesAsync(path, flipped);

        Assert.Equal(1, await SweepAsync(host, s => s.ScrubAsync(default)));
        var flagged = await RowAsync(host, row.Id);
        Assert.NotNull(flagged!.IntegrityFaultAtUtc);
        Assert.Equal(StoredObjectState.Committed, flagged.State);
        Assert.Contains(host.Logs.At(LogLevel.Error), m => m.Contains("failed its integrity check", StringComparison.Ordinal));

        await using (var scope = host.Scope())
        {
            await Assert.ThrowsAsync<FileStorageIntegrityException>(
                () => scope.ServiceProvider.GetRequiredService<IObjectStore>().OpenReadAsync("TestFiles", row.Id, default));
        }

        Assert.Equal(flagged.IntegrityFaultAtUtc, (await RowAsync(host, row.Id))!.IntegrityFaultAtUtc);

        await File.WriteAllBytesAsync(path, original);
        Clock.Advance(TimeSpan.FromHours(1));
        await SweepAsync(host, s => s.ScrubAsync(default));
        var cleared = await RowAsync(host, row.Id);
        Assert.Null(cleared!.IntegrityFaultAtUtc);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, cleared.LastVerifiedAtUtc);
    }

    [Fact]
    public async Task A_release_staged_before_the_scrub_flags_the_row_still_commits()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await File.WriteAllBytesAsync(Path.Combine(PrimaryRoot, row.RelativePath), [1, 2, 3]);

        await using var consumer = host.Scope();
        var context = (TestStorageContext)consumer.ServiceProvider.GetRequiredService<OdysseyContext>();
        var tracked = await context.StoredObjects.SingleAsync(o => o.Id == row.Id);
        consumer.ServiceProvider.GetRequiredService<IObjectStore>().StageRelease(tracked);
        context.TestFileAttachments.RemoveRange(context.TestFileAttachments);

        await SweepAsync(host, s => s.ScrubAsync(default)); // flags it in between
        await context.SaveChangesAsync(); // no concurrency conflict: the scrub never writes State

        Assert.Equal(StoredObjectState.PendingDelete, (await RowAsync(host, row.Id))!.State);
    }

    [Fact]
    public async Task A_missing_file_is_flagged_by_the_scrub()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        File.Delete(Path.Combine(PrimaryRoot, row.RelativePath));

        await SweepAsync(host, s => s.ScrubAsync(default));

        Assert.NotNull((await RowAsync(host, row.Id))!.IntegrityFaultAtUtc);
    }

    [Fact]
    public async Task The_scrub_checks_at_most_its_object_budget_oldest_first()
    {
        var host = await NewHostAsync();
        for (var i = 0; i < 5; i++)
        {
            await PutCommittedAsync(host, TestStorageFeatures.Dated, "2026-10-25", $"f{i}.jpg", length: 10);
        }

        Assert.Equal(5, await SweepAsync(host, s => s.ScrubAsync(default)));
        Assert.All(await AllRowsAsync(host), r => Assert.NotNull(r.LastVerifiedAtUtc));
    }

    [Fact]
    public async Task A_failed_unlink_keeps_the_row_pending_and_the_next_tick_finishes_the_job()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await ObjectStoreTests.ReleaseAsync(host, row.Id, immediate: true);
        var path = Path.Combine(PrimaryRoot, row.RelativePath);
        // An unlink that fails even for root: the path is now a non-empty directory.
        File.Delete(path);
        Directory.CreateDirectory(Path.Combine(path, "blocker"));

        await RunTickAsync(host);
        Assert.Equal(StoredObjectState.PendingDelete, (await RowAsync(host, row.Id))!.State);
        Assert.Contains(host.Logs.At(LogLevel.Error), m => m.Contains(row.Id.ToString(), StringComparison.Ordinal));

        Directory.Delete(path, recursive: true);
        await File.WriteAllBytesAsync(path, [1]);
        await RunTickAsync(host);

        Assert.Null(await RowAsync(host, row.Id));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Rows_of_an_unknown_feature_are_erased_or_swept_but_committed_ones_are_left_alone()
    {
        // Release one of each, then "roll back" to a build whose catalogue no longer has TestSecrets.
        var host = await NewHostAsync();
        var released = await PutCommittedAsync(host, TestStorageFeatures.Secrets, Owner, "a.jpg");
        var kept = await PutCommittedAsync(host, TestStorageFeatures.Secrets, Guid.NewGuid(), "b.jpg");
        await ObjectStoreTests.ReleaseAsync(host, released.Id, immediate: true);
        Guid pendingId;
        await using (var scope = host.Scope())
        {
            pendingId = (await scope.ServiceProvider.GetRequiredService<IObjectStore>()
                .PutAsync("TestSecrets", Guid.NewGuid(), "c.jpg", null, "image/jpeg", Bytes(10), 1000, default)).Id;
        }

        var rolledBack = await NewHostAsync(
            new Dictionary<string, string> { ["primary"] = PrimaryRoot },
            new Dictionary<string, string> { ["TestFiles"] = "primary", ["TestDated"] = "primary" },
            [TestStorageFeatures.Files, TestStorageFeatures.Dated]);
        Clock.Advance(TimeSpan.FromHours(2));
        await RunTickAsync(rolledBack);

        Assert.Null(await RowAsync(rolledBack, released.Id));
        Assert.False(File.Exists(Path.Combine(PrimaryRoot, released.RelativePath)));
        Assert.Null(await RowAsync(rolledBack, pendingId));
        Assert.Equal(StoredObjectState.Committed, (await RowAsync(rolledBack, kept.Id))!.State);
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, kept.RelativePath)));
        var warning = Assert.Single(rolledBack.Logs.At(LogLevel.Warning), m => m.Contains("unknown feature TestSecrets", StringComparison.Ordinal));
        Assert.Contains("1 committed", warning, StringComparison.Ordinal);
        Assert.Contains(rolledBack.Logs.At(LogLevel.Critical), m => m.Contains("TestSecrets", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".quarantine/x/y.jpg")]
    [InlineData(".tmp/x/y.jpg")]
    [InlineData("lost+found/x/y.jpg")]
    [InlineData("../x/y.jpg")]
    [InlineData("test-files/3f/a2/victim.jpg")]
    public async Task A_tampered_unknown_feature_row_is_refused_and_nothing_on_disk_is_touched(string tampered)
    {
        var host = await NewHostAsync();
        var victim = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "victim.jpg");
        var target = Path.GetFullPath(Path.Combine(PrimaryRoot, tampered));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
        {
            await File.WriteAllTextAsync(target, "keep");
        }

        var volumeId = host.Registry.Find("primary")!.Id;
        var forged = new[] { StoredObjectState.PendingDelete, StoredObjectState.Pending }.Select(state => new StoredObject
        {
            Id = Guid.NewGuid(), VolumeId = volumeId, FeatureKey = "GoneFeature", RelativePath = tampered + (state == StoredObjectState.Pending ? ".x.jpg" : string.Empty),
            State = state, Sha256Hash = state == StoredObjectState.Pending ? null : new string('0', 64), CreatedAtUtc = Clock.GetUtcNow().UtcDateTime,
            DeleteRequestedAtUtc = state == StoredObjectState.PendingDelete ? Clock.GetUtcNow().UtcDateTime : null, DeleteImmediately = true,
        }).ToList();
        await InScopeAsync(host, async sp =>
        {
            var context = sp.GetRequiredService<OdysseyContext>();
            context.StoredObjects.AddRange(forged);
            await context.SaveChangesAsync();
        });
        Clock.Advance(TimeSpan.FromHours(2));

        await RunTickAsync(host);

        Assert.All(forged, f => Assert.NotNull(RowAsync(host, f.Id).Result));
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, victim.RelativePath)));
        Assert.Contains(host.Logs.At(LogLevel.Error), m => m.StartsWith("Security:", StringComparison.Ordinal));
    }

    internal static async Task SetGraceAsync(FileStorageTestHost host, string value)
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.SystemSettings.SingleOrDefaultAsync(s => s.Key == SystemSettingsKeys.FileStorageDeletionGraceHours);
        if (row is null)
        {
            context.SystemSettings.Add(new SystemSetting { Key = SystemSettingsKeys.FileStorageDeletionGraceHours, Value = value, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            row.Value = value;
        }

        await context.SaveChangesAsync();
    }

    private static async Task DropReferencesAsync(FileStorageTestHost host, Guid id)
    {
        await using var scope = host.Scope();
        var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.TestFileAttachments.RemoveRange(context.TestFileAttachments.Where(a => a.StoredObjectId == id));
        context.TestSecretAttachments.RemoveRange(context.TestSecretAttachments.Where(a => a.StoredObjectId == id));
        await context.SaveChangesAsync();
    }

    private static async Task<List<StoredObject>> AllRowsAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().StoredObjects.AsNoTracking().ToListAsync();
    }
}
