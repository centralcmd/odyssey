using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Jobs;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>The maintenance job, its lease, its liveness and the reference-key guard (A3.1, #362 v7.1 §3; AC 15, A3-5, 32).</summary>
public class FileStorageMaintenanceTests : FileStorageTestBase
{
    [Fact]
    public async Task The_job_runs_hourly_and_does_nothing_while_the_store_is_not_set_up()
    {
        var host = await NewHostAsync(new Dictionary<string, string>(), new Dictionary<string, string>(), features: []);
        var job = host.Services.GetRequiredService<FileStorageMaintenanceJob>();

        Assert.Equal(TimeSpan.FromHours(1), ((IPeriodicJob)job).Interval);
        await job.RunOnceAsync(default);

        await using var scope = host.Scope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageMaintenanceLeases.ToListAsync());
    }

    [Fact]
    public async Task A_completed_run_releases_the_lease_and_stamps_its_completion()
    {
        var host = await NewHostAsync();

        await host.Services.GetRequiredService<FileStorageMaintenanceJob>().RunOnceAsync(default);

        var lease = await LeaseAsync(host);
        Assert.Null(lease.HolderId);
        Assert.Null(lease.LeaseUntilUtc);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, lease.LastCompletedAtUtc);
    }

    [Fact]
    public async Task A_held_lease_skips_the_tick_and_an_expired_one_is_taken_over()
    {
        var host = await NewHostAsync();
        var job = host.Services.GetRequiredService<FileStorageMaintenanceJob>();
        var otherHolder = Guid.NewGuid();
        Assert.True(await job.TryClaimAsync(otherHolder, default));

        await job.RunOnceAsync(default);
        Assert.Null((await LeaseAsync(host)).LastCompletedAtUtc);
        Assert.Equal(otherHolder, (await LeaseAsync(host)).HolderId);

        Clock.Advance(FileStorageMaintenanceJob.LeaseDuration + TimeSpan.FromSeconds(1)); // the holder crashed
        await job.RunOnceAsync(default);

        var lease = await LeaseAsync(host);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, lease.LastCompletedAtUtc);
        Assert.Null(lease.HolderId);
    }

    [Fact]
    public async Task Two_replicas_running_one_tick_run_one_pass()
    {
        var first = await NewHostAsync();
        var second = await NewHostAsync();
        var holder = Guid.NewGuid();
        Assert.True(await first.Services.GetRequiredService<FileStorageMaintenanceJob>().TryClaimAsync(holder, default));
        Assert.False(await second.Services.GetRequiredService<FileStorageMaintenanceJob>().TryClaimAsync(Guid.NewGuid(), default));
        Assert.True(await first.Services.GetRequiredService<FileStorageMaintenanceJob>().TryClaimAsync(holder, default)); // a renewal
    }

    [Fact]
    public async Task Maintenance_turns_stale_after_three_hours_without_a_completed_pass_and_fresh_after_the_next()
    {
        var host = await NewHostAsync();
        var job = host.Services.GetRequiredService<FileStorageMaintenanceJob>();

        // Never completed, and the bound volume registered just now: not yet stale.
        Assert.False((await StatusAsync(host)).Single().MaintenanceStale);
        Clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        Assert.True((await StatusAsync(host)).Single().MaintenanceStale);

        await job.RunOnceAsync(default);
        Assert.False((await StatusAsync(host)).Single().MaintenanceStale);

        Clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        var status = (await StatusAsync(host)).Single();
        Assert.True(status.MaintenanceStale);
        Assert.NotNull(status.MaintenanceLastCompletedAtUtc);
    }

    [Fact]
    public async Task Every_volume_entry_carries_the_same_maintenance_fields_and_they_change_together()
    {
        var host = await NewHostAsync(new Dictionary<string, string> { ["a"] = Root("a"), ["b"] = Root("b") },
            new Dictionary<string, string> { ["TestFiles"] = "a", ["TestSecrets"] = "b", ["TestDated"] = "b" });

        await host.Services.GetRequiredService<FileStorageMaintenanceJob>().RunOnceAsync(default);
        var before = await StatusAsync(host);
        Clock.Advance(TimeSpan.FromHours(1));
        await host.Services.GetRequiredService<FileStorageMaintenanceJob>().RunOnceAsync(default);
        var after = await StatusAsync(host);

        Assert.Single(before.Select(v => (v.MaintenanceLastCompletedAtUtc, v.MaintenanceStale)).Distinct());
        Assert.Single(after.Select(v => (v.MaintenanceLastCompletedAtUtc, v.MaintenanceStale)).Distinct());
        Assert.NotEqual(before[0].MaintenanceLastCompletedAtUtc, after[0].MaintenanceLastCompletedAtUtc);
    }

    [Fact]
    public async Task The_status_reports_usage_pending_deletes_faults_and_the_last_scrub_per_volume()
    {
        var host = await NewHostAsync();
        var kept = await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid(), length: 100);
        var released = await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid(), length: 200);
        await ObjectStoreTests.ReleaseAsync(host, released.Id);
        await File.WriteAllBytesAsync(Path.Combine(PrimaryRoot, kept.RelativePath), [1]);
        await SweepAsync(host, s => s.ScrubAsync(default));

        var status = (await StatusAsync(host)).Single();

        Assert.Equal(Dtos.Application.FileStorageVolumeState.Available, status.State);
        var usage = Assert.Single(status.Features);
        Assert.Equal(("TestFiles", 1, 100L), (usage.FeatureKey, usage.ObjectCount, usage.CommittedBytes));
        Assert.Equal(1, status.PendingDeleteCount);
        Assert.Equal(1, status.IntegrityFaultCount);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, status.LastScrubAtUtc);
        Assert.Equal(host.Space.Override!.Value.Free, status.FreeBytes);
        Assert.Equal(["TestDated", "TestFiles", "TestSecrets"], status.BoundFeatures);
    }

    /// <summary>
    /// AC 15: every foreign key to <c>StoredObjects</c> belongs to exactly one catalogue feature and is RESTRICT. The
    /// production model has no feature and must therefore have no such key; the test model has three, one per feature.
    /// </summary>
    [Fact]
    public void Every_key_to_stored_objects_belongs_to_one_feature_and_is_restrict()
    {
        using var production = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase("guard-prod").Options);
        Assert.Empty(ReferenceViolations(production.Model, new StorageFeatureCatalogue([])));
        Assert.Empty(KeysTo(production.Model));

        using var test = new TestStorageContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase("guard-test").Options);
        Assert.Equal(3, KeysTo(test.Model).Count);
        Assert.Empty(ReferenceViolations(test.Model, new StorageFeatureCatalogue(TestStorageFeatures.All)));

        // And it bites: drop a feature from the catalogue and its key is reported.
        Assert.Single(ReferenceViolations(test.Model, new StorageFeatureCatalogue([TestStorageFeatures.Files, TestStorageFeatures.Dated])));
    }

    internal static IReadOnlyList<IForeignKey> KeysTo(IModel model) =>
        [.. model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).Where(fk => fk.PrincipalEntityType.ClrType == typeof(StoredObject))];

    internal static IReadOnlyList<string> ReferenceViolations(IModel model, StorageFeatureCatalogue catalogue) =>
        [.. KeysTo(model).SelectMany(fk =>
        {
            var problems = new List<string>();
            var property = fk.Properties.Single().Name;
            var owners = catalogue.Features.Count(f => f.Reference.EntityType == fk.DeclaringEntityType.ClrType && f.Reference.PropertyName == property);
            if (owners != 1)
            {
                problems.Add($"{fk.DeclaringEntityType.ClrType.Name}.{property} belongs to {owners} catalogue features, not one");
            }

            if (fk.DeleteBehavior != DeleteBehavior.Restrict)
            {
                problems.Add($"{fk.DeclaringEntityType.ClrType.Name}.{property} is {fk.DeleteBehavior}, not Restrict");
            }

            return problems;
        })];

    private static Task<IReadOnlyList<Dtos.Application.FileStorageVolumeStatus>> StatusAsync(FileStorageTestHost host) =>
        InScopeAsync(host, sp => sp.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default));

    private static async Task<FileStorageMaintenanceLease> LeaseAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageMaintenanceLeases.AsNoTracking().SingleAsync();
    }
}

/// <summary>AC 8: a 50 MiB put and read stream through bounded buffers. Isolated, because it measures the whole process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileStorageMemoryCollection
{
    public const string Name = "FileStorageMemory";
}

[Collection(FileStorageMemoryCollection.Name)]
public class FileStorageMemoryTests : FileStorageTestBase
{
    [Fact]
    public async Task A_fifty_mebibyte_put_and_read_allocate_under_two_mebibytes()
    {
        var host = await NewHostAsync();
        const int size = 50 * 1024 * 1024;
        var source = new GeneratedStream(size);
        await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid(), length: 1); // warm the paths up

        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        StoredObject row;
        await using (var scope = host.Scope())
        {
            var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            row = await scope.ServiceProvider.GetRequiredService<IObjectStore>()
                .PutAsync("TestFiles", Guid.NewGuid(), "big.jpg", null, "image/jpeg", source, 64L * 1024 * 1024, default);
            context.TestFileAttachments.Add(new TestFileAttachment { Id = Guid.NewGuid(), StoredObjectId = row.Id });
            await context.SaveChangesAsync();

            await using var content = (await scope.ServiceProvider.GetRequiredService<IObjectStore>().OpenReadAsync("TestFiles", row.Id, default)).Content;
            var buffer = new byte[81920];
            while (await content.ReadAsync(buffer) > 0)
            {
            }
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(allocated < 2 * 1024 * 1024, $"Allocated {allocated:N0} bytes.");
    }

    /// <summary>A stream of the given length that never holds its content in memory.</summary>
    private sealed class GeneratedStream(long length) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - position);
            for (var i = 0; i < n; i++)
            {
                buffer[offset + i] = (byte)(position + i);
            }

            position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
