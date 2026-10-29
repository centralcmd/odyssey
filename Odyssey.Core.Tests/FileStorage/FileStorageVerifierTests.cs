using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>Every startup check of issue #346 §3.6, and the worker's verify-only mode (AC 22–25, A2, A3-2).</summary>
public class FileStorageVerifierTests : FileStorageTestBase
{
    private static readonly Dictionary<string, string> AllBound = TestStorageFeatures.All.ToDictionary(f => f.Key, _ => "primary");

    private Task<FileStorageMisconfiguredException> FailsAsync(
        IReadOnlyDictionary<string, string> volumes, IReadOnlyDictionary<string, string> bindings,
        IEnumerable<StorageFeature>? features = null, FileStorageForbiddenRoots? forbidden = null) =>
        Assert.ThrowsAsync<FileStorageMisconfiguredException>(async () =>
            await NewHostAsync(volumes, bindings, features, forbidden));

    private Dictionary<string, string> Primary() => new() { ["primary"] = Root("primary") };

    [Fact]
    public async Task C1_an_invalid_volume_name_or_binding_key_fails()
    {
        Assert.Equal("InvalidName", (await FailsAsync(new Dictionary<string, string> { ["bad-name"] = Root("bad") }, new Dictionary<string, string>(), [])).Check);
        var binding = await FailsAsync(Primary(), new Dictionary<string, string>(AllBound) { ["Bad_Key"] = "primary" });
        Assert.Equal("InvalidName", binding.Check);
        Assert.Contains("FileStorage:Bindings:Bad_Key", binding.Message);
    }

    [Fact]
    public async Task C2_a_relative_root_fails() =>
        Assert.Equal("InvalidRootPath", (await FailsAsync(new Dictionary<string, string> { ["primary"] = "relative/path" }, AllBound)).Check);

    [Fact]
    public async Task C3_overlapping_volumes_fail_including_through_a_link()
    {
        var outer = Root("outer");
        var inner = Directory.CreateDirectory(Path.Combine(outer, "inner")).FullName;
        Assert.Equal("OverlappingVolumes",
            (await FailsAsync(new Dictionary<string, string> { ["outer"] = outer, ["inner"] = inner }, new Dictionary<string, string>(), [])).Check);

        var link = Path.Combine(Base.FullName, "alias");
        Directory.CreateSymbolicLink(link, Root("target"));
        Assert.Equal("OverlappingVolumes",
            (await FailsAsync(new Dictionary<string, string> { ["one"] = Root("target"), ["two"] = link }, new Dictionary<string, string>(), [])).Check);
    }

    [Fact]
    public async Task C4_a_root_overlapping_a_forbidden_location_or_the_filesystem_root_fails()
    {
        var keys = Root("keys");
        var forbidden = new FileStorageForbiddenRoots([("Data Protection key ring", keys)]);
        var error = await FailsAsync(new Dictionary<string, string> { ["primary"] = Path.Combine(keys, "..", "keys") }, AllBound, forbidden: forbidden);
        Assert.Equal("ForbiddenLocation", error.Check);
        Assert.Contains("key ring", error.Message);

        var link = Path.Combine(Base.FullName, "linked-keys");
        Directory.CreateSymbolicLink(link, keys);
        Assert.Equal("ForbiddenLocation", (await FailsAsync(new Dictionary<string, string> { ["primary"] = link }, AllBound, forbidden: forbidden)).Check);

        Assert.Equal("ForbiddenLocation", (await FailsAsync(new Dictionary<string, string> { ["primary"] = "/" }, AllBound)).Check);
    }

    [Fact]
    public void C4_the_default_forbidden_locations_name_the_key_ring_the_app_the_content_root_and_temp()
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataProtection:KeysPath"] = "/var/odyssey/dataprotection-keys" }).Build())
            .AddFileStorage()
            .BuildServiceProvider();

        var labels = services.GetRequiredService<FileStorageForbiddenRoots>().Locations.ToDictionary(l => l.Label, l => l.Path);

        Assert.Equal("/var/odyssey/dataprotection-keys", labels["Data Protection key ring"]);
        Assert.Equal(AppContext.BaseDirectory, labels["application directory"]);
        Assert.Equal(Path.GetTempPath(), labels["temp directory"]);
        Assert.Contains("content root", labels.Keys);
    }

    [Fact]
    public async Task C5_a_missing_root_fails_and_is_not_created()
    {
        var missing = Path.Combine(Base.FullName, "never-created");
        var error = await FailsAsync(new Dictionary<string, string> { ["primary"] = missing }, AllBound);

        Assert.Equal("RootNotFound", error.Check);
        Assert.Contains("FileStorage:Volumes:primary:RootPath", error.Message);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task B1_an_unbound_feature_fails() =>
        Assert.Equal("UnboundFeature", (await FailsAsync(Primary(), new Dictionary<string, string> { ["TestFiles"] = "primary" })).Check);

    [Fact]
    public async Task B2_a_binding_to_an_unknown_feature_fails() =>
        Assert.Equal("UnknownFeature", (await FailsAsync(Primary(), new Dictionary<string, string>(AllBound) { ["PhotoThumbnail"] = "primary" })).Check);

    [Fact]
    public async Task B3_a_binding_to_an_unconfigured_volume_fails() =>
        Assert.Equal("UnknownVolume", (await FailsAsync(Primary(), new Dictionary<string, string>(AllBound) { ["TestFiles"] = "fastssd" })).Check);

    [Fact]
    public async Task Configuration_lookups_are_case_insensitive_and_keys_are_returned_canonical()
    {
        var host = await NewHostAsync(new Dictionary<string, string> { ["Primary"] = Root("primary") },
            TestStorageFeatures.All.ToDictionary(f => f.Key.ToLowerInvariant(), _ => "PRIMARY"));

        Assert.Equal("primary", Assert.Single(host.Registry.Volumes).Name);
        Assert.Equal(["TestDated", "TestFiles", "TestSecrets"], host.Registry.Bindings.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task B4_live_rows_of_a_removed_feature_do_not_stop_startup_and_are_reported()
    {
        var host = await NewHostAsync();
        await PutCommittedAsync(host, TestStorageFeatures.Secrets, Guid.NewGuid());

        var rolledBack = await NewHostAsync(Primary(),
            new Dictionary<string, string> { ["TestFiles"] = "primary", ["TestDated"] = "primary" },
            [TestStorageFeatures.Files, TestStorageFeatures.Dated], verify: false);
        var result = await rolledBack.VerifyAsync();

        Assert.Equal(["TestSecrets"], result.OrphanedFeatures);
        Assert.Contains(rolledBack.Logs.At(LogLevel.Critical), m => m.Contains("TestSecrets", StringComparison.Ordinal));
        var status = await InScopeAsync(rolledBack, sp => sp.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default));
        Assert.Equal(["TestSecrets"], Assert.Single(status).OrphanedFeatures);
    }

    [SkippableFact]
    public async Task V1_an_unwritable_root_fails()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix modes only");
        var root = Root("primary");
        if (!OperatingSystem.IsWindows())
        {
            new DirectoryInfo(root).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        }

        // Decided by what the process can actually do, not by its name: a privileged process writes through any mode.
        Skip.If(VolumeFileSystem.TryProbeWrite(root, out _), "this process can write through mode 0500 (it is privileged)");

        Assert.Equal("NotWritable", (await FailsAsync(Primary(), AllBound)).Check);
    }

    [Fact]
    public async Task V5_an_empty_root_is_initialised_and_V2_then_recognises_it()
    {
        var host = await NewHostAsync();
        var marker = JsonMarker();
        var row = await VolumeRowAsync(host);

        Assert.Equal("primary", marker.Name);
        Assert.Equal(row.Id, marker.VolumeId);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, row.InitializedAtUtc);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(VolumeFileSystem.FileMode, File.GetUnixFileMode(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName)));
        }

        Clock.Advance(TimeSpan.FromHours(1));
        var again = await NewHostAsync();
        Assert.Equal(row.Id, again.Registry.Find("primary")!.Id);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, (await VolumeRowAsync(again)).LastSeenAtUtc);
    }

    /// <summary>
    /// The marker race (AC 23): a marker that a racing writer created between the emptiness check and the write is
    /// never overwritten — the write is create-new and reports the collision, and the verifier then evaluates the
    /// marker that exists (V2/V3).
    /// </summary>
    [Fact]
    public void A_marker_is_created_new_and_never_overwritten()
    {
        var root = Root("primary");
        var marker = Path.Combine(root, StorageNames.MarkerFileName);
        var theirs = new VolumeMarker(Guid.NewGuid(), "primary", DateTime.UtcNow, 1);
        Assert.True(FileStorageVerifier.TryWriteMarker(marker, theirs));
        var written = File.ReadAllText(marker);

        Assert.False(FileStorageVerifier.TryWriteMarker(marker, new VolumeMarker(Guid.NewGuid(), "primary", DateTime.UtcNow, 1)));
        Assert.Equal(written, File.ReadAllText(marker));
    }

    [Fact]
    public async Task A_root_holding_only_lost_and_found_counts_as_empty()
    {
        Directory.CreateDirectory(Path.Combine(Root("primary"), "lost+found"));

        var host = await NewHostAsync();

        Assert.True(host.Registry.IsActive);
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName)));
    }

    [Fact]
    public async Task V3_a_marker_of_another_name_or_another_id_is_the_wrong_volume()
    {
        await NewHostAsync(new Dictionary<string, string> { ["other"] = Root("other") }, new Dictionary<string, string>(), []);
        var wrongName = await FailsAsync(new Dictionary<string, string> { ["primary"] = Root("other") }, AllBound);
        Assert.Equal("WrongVolume", wrongName.Check);

        await NewHostAsync();
        File.Delete(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName));
        await File.WriteAllTextAsync(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName),
            $"{{\"volumeId\":\"{Guid.NewGuid()}\",\"name\":\"primary\",\"initializedAtUtc\":\"2026-10-22T10:00:00Z\",\"layoutVersion\":1}}");
        Assert.Equal("WrongVolume", (await FailsAsync(Primary(), AllBound)).Check);
    }

    [Fact]
    public async Task V4_after_a_database_reset_every_marked_empty_volume_is_adopted()
    {
        var volumes = new Dictionary<string, string> { ["a"] = Root("a"), ["b"] = Root("b") };
        var bindings = new Dictionary<string, string> { ["TestFiles"] = "a", ["TestSecrets"] = "b", ["TestDated"] = "b" };
        var original = await NewHostAsync(volumes, bindings);
        var ids = original.Registry.Volumes.ToDictionary(v => v.Name, v => v.Id);
        await ResetDatabaseAsync(original);

        var adopted = await NewHostAsync(volumes, bindings);

        Assert.Equal(ids["a"], adopted.Registry.Find("a")!.Id);
        Assert.Equal(ids["b"], adopted.Registry.Find("b")!.Id);
        Assert.Equal(2, adopted.Logs.At(LogLevel.Warning).Count(m => m.Contains("Adopted", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task V4_a_foreign_marker_on_a_database_that_has_volumes_is_an_unknown_marker()
    {
        var foreign = Root("foreign");
        await File.WriteAllTextAsync(Path.Combine(foreign, StorageNames.MarkerFileName),
            $"{{\"volumeId\":\"{Guid.NewGuid()}\",\"name\":\"second\",\"initializedAtUtc\":\"2026-10-22T10:00:00Z\",\"layoutVersion\":1}}");
        await NewHostAsync();

        var error = await FailsAsync(new Dictionary<string, string> { ["primary"] = PrimaryRoot, ["second"] = foreign }, AllBound);

        Assert.Equal("UnknownMarker", error.Check);
    }

    [Fact]
    public async Task V4_a_marked_volume_that_still_holds_files_is_not_adopted_by_a_reset_database()
    {
        var host = await NewHostAsync();
        await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid());
        await ResetDatabaseAsync(host);

        Assert.Equal("UnknownMarker", (await FailsAsync(Primary(), AllBound)).Check);
    }

    [Fact]
    public async Task V6_an_empty_unmarked_root_whose_row_has_live_objects_is_lost_and_no_marker_is_written()
    {
        var host = await NewHostAsync();
        await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid());
        foreach (var entry in new DirectoryInfo(PrimaryRoot).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory)
            {
                directory.Delete(recursive: true);
            }
            else
            {
                entry.Delete();
            }
        }

        var error = await FailsAsync(Primary(), AllBound);

        Assert.Equal("VolumeLost", error.Check);
        Assert.False(File.Exists(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName)));
    }

    [Fact]
    public async Task V5_an_empty_unmarked_root_whose_row_holds_nothing_live_is_reinitialised_with_its_id()
    {
        var host = await NewHostAsync();
        var id = host.Registry.Find("primary")!.Id;
        File.Delete(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName));

        var again = await NewHostAsync();

        Assert.Equal(id, again.Registry.Find("primary")!.Id);
        Assert.Equal(id, JsonMarker().VolumeId);
    }

    [Fact]
    public async Task V7_an_unmarked_root_with_content_is_unrecognised()
    {
        await File.WriteAllTextAsync(Path.Combine(Root("primary"), "something.txt"), "x");

        Assert.Equal("UnrecognisedContent", (await FailsAsync(Primary(), AllBound)).Check);
    }

    [Fact]
    public async Task V8_a_volume_with_live_objects_removed_from_configuration_is_missing_and_one_without_only_warns()
    {
        var volumes = new Dictionary<string, string> { ["a"] = Root("a"), ["b"] = Root("b"), ["c"] = Root("c") };
        var host = await NewHostAsync(volumes, new Dictionary<string, string> { ["TestFiles"] = "a", ["TestSecrets"] = "b", ["TestDated"] = "b" });
        await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid());

        var withoutC = await NewHostAsync(new Dictionary<string, string> { ["a"] = Root("a"), ["b"] = Root("b") },
            new Dictionary<string, string> { ["TestFiles"] = "a", ["TestSecrets"] = "b", ["TestDated"] = "b" });
        Assert.Contains(withoutC.Logs.At(LogLevel.Warning), m => m.Contains("no longer configured", StringComparison.Ordinal));
        var status = await InScopeAsync(withoutC, sp => sp.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default));
        Assert.Equal(Dtos.Application.FileStorageVolumeState.NotConfigured, status.Single(v => v.Name == "c").State);

        Assert.Equal("MissingVolume", (await FailsAsync(new Dictionary<string, string> { ["b"] = Root("b") },
            new Dictionary<string, string> { ["TestFiles"] = "b", ["TestSecrets"] = "b", ["TestDated"] = "b" })).Check);
    }

    [Fact]
    public async Task A_rebinding_sends_new_writes_to_the_new_volume_while_old_objects_still_read_from_the_old()
    {
        var volumes = new Dictionary<string, string> { ["a"] = Root("a"), ["b"] = Root("b") };
        var onA = await NewHostAsync(volumes, new Dictionary<string, string> { ["TestFiles"] = "a", ["TestSecrets"] = "a", ["TestDated"] = "a" });
        var old = await PutCommittedAsync(onA, TestStorageFeatures.Files, Guid.NewGuid());

        var onB = await NewHostAsync(volumes, new Dictionary<string, string> { ["TestFiles"] = "b", ["TestSecrets"] = "b", ["TestDated"] = "b" });
        var fresh = await PutCommittedAsync(onB, TestStorageFeatures.Files, Guid.NewGuid());

        Assert.Equal(onB.Registry.Find("a")!.Id, old.VolumeId);
        Assert.Equal(onB.Registry.Find("b")!.Id, fresh.VolumeId);
        await using (var scope = onB.Scope())
        {
            await using var content = (await scope.ServiceProvider.GetRequiredService<IObjectStore>().OpenReadAsync("TestFiles", old.Id, default)).Content;
            Assert.Equal(1024, content.Length);
        }

        Assert.Equal("MissingVolume", (await FailsAsync(new Dictionary<string, string> { ["b"] = Root("b") },
            new Dictionary<string, string> { ["TestFiles"] = "b", ["TestSecrets"] = "b", ["TestDated"] = "b" })).Check);
    }

    [Fact]
    public async Task On_InMemory_with_no_volumes_the_store_is_a_no_op_even_with_a_catalogue()
    {
        var host = await NewHostAsync(new Dictionary<string, string>(), new Dictionary<string, string>(), TestStorageFeatures.All);

        Assert.False(host.Registry.IsActive);
        Assert.Empty(await InScopeAsync(host, sp => sp.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default)));
    }

    [Fact]
    public async Task The_worker_waits_for_the_api_to_initialise_a_new_volume_then_gives_up_naming_it()
    {
        var worker = await NewHostAsync(Primary(), AllBound, verify: false);
        var result = await worker.VerifyAsync(FileStorageVerifyMode.VerifyOnly);

        Assert.Equal(["primary"], result.PendingVolumes);
        Assert.False(worker.Registry.IsActive);
        Assert.False(File.Exists(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName)));
        var error = await Assert.ThrowsAsync<FileStorageMisconfiguredException>(
            () => worker.Services.VerifyFileStorageForWorkerAsync(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal("NotInitialised", error.Check);
        Assert.Contains("primary", error.Message);

        await NewHostAsync(); // the API initialises it
        await worker.Services.VerifyFileStorageForWorkerAsync(TimeSpan.Zero, TimeSpan.Zero);
        Assert.True(worker.Registry.IsActive);
    }

    [Fact]
    public async Task The_worker_fails_at_once_on_a_lost_volume()
    {
        var host = await NewHostAsync();
        await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid());
        Directory.Delete(PrimaryRoot, recursive: true);
        Directory.CreateDirectory(PrimaryRoot);

        var worker = await NewHostAsync(Primary(), AllBound, verify: false);
        var error = await Assert.ThrowsAsync<FileStorageMisconfiguredException>(
            () => worker.Services.VerifyFileStorageForWorkerAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2)));
        Assert.Equal("VolumeLost", error.Check);
    }

    private VolumeMarker JsonMarker() =>
        System.Text.Json.JsonSerializer.Deserialize<VolumeMarker>(File.ReadAllText(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName)))!;

    private static async Task<FileStorageVolume> VolumeRowAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageVolumes.AsNoTracking().SingleAsync(v => v.Name == "primary");
    }

    /// <summary>What the reset-environment skill does to the database (it empties the volumes separately).</summary>
    private static async Task ResetDatabaseAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.TestFileAttachments.RemoveRange(context.TestFileAttachments);
        context.TestSecretAttachments.RemoveRange(context.TestSecretAttachments);
        context.TestDatedAttachments.RemoveRange(context.TestDatedAttachments);
        context.StoredObjects.RemoveRange(context.StoredObjects);
        context.FileStorageOrphanSightings.RemoveRange(context.FileStorageOrphanSightings);
        context.FileStorageVolumes.RemoveRange(context.FileStorageVolumes);
        await context.SaveChangesAsync();
    }
}
