using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>The disk reconciliation and its safeguards (issue #346 §3.7; AC 19).</summary>
public class DiskReconciliationTests : FileStorageTestBase
{
    [Fact]
    public async Task An_orphan_is_sighted_in_one_pass_quarantined_in_the_next_and_purged_after_seven_days()
    {
        var host = await NewHostAsync();
        var orphan = PlantOrphan("test-files/aa/bb/owner/stray.jpg");
        File.SetLastWriteTimeUtc(orphan, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // years old: still only sighted

        await ReconcileAsync(host);
        Assert.True(File.Exists(orphan));
        var sighting = Assert.Single(await SightingsAsync(host));
        Assert.Equal(1, sighting.LastSeenPass);

        Clock.Advance(TimeSpan.FromHours(25));
        await ReconcileAsync(host);

        Assert.False(File.Exists(orphan));
        var quarantined = Path.Combine(PrimaryRoot, ".quarantine", Clock.GetUtcNow().ToString("yyyy-MM-dd"), sighting.Id.ToString("N"),
            "test-files", "aa", "bb", "owner", "stray.jpg");
        Assert.True(File.Exists(quarantined));
        Assert.Empty(await SightingsAsync(host));
        await ReconcileAsync(host); // the purge recounts what is set aside
        Assert.Equal(1, (await VolumeRowAsync(host)).QuarantinedFileCount);

        Clock.Advance(TimeSpan.FromDays(7));
        await ReconcileAsync(host);
        Assert.False(File.Exists(quarantined));
        Assert.Equal(0, (await VolumeRowAsync(host)).QuarantinedFileCount);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task An_orphan_seen_for_less_than_a_day_is_not_quarantined_yet()
    {
        var host = await NewHostAsync();
        var orphan = PlantOrphan("test-files/aa/bb/owner/stray.jpg");

        await ReconcileAsync(host);
        Clock.Advance(TimeSpan.FromHours(2));
        await ReconcileAsync(host);

        Assert.True(File.Exists(orphan));
        Assert.Equal(2, Assert.Single(await SightingsAsync(host)).LastSeenPass);
    }

    [Fact]
    public async Task Two_orphans_with_the_same_relative_path_quarantined_on_one_day_do_not_collide()
    {
        var host = await NewHostAsync();
        const string relative = "test-files/aa/bb/owner/same.jpg";
        PlantOrphan(relative, "one");
        await ReconcileAsync(host);
        Clock.Advance(TimeSpan.FromHours(25));
        await ReconcileAsync(host); // "one" quarantined today

        PlantOrphan(relative, "two");
        await ReconcileAsync(host); // sighted
        await InScopeAsync(host, async sp =>
        {
            // Back-date the sighting so the second quarantine falls on the same calendar day as the first.
            var context = sp.GetRequiredService<OdysseyContext>();
            (await context.FileStorageOrphanSightings.SingleAsync()).FirstSeenAtUtc = Clock.GetUtcNow().UtcDateTime.AddDays(-2);
            await context.SaveChangesAsync();
        });
        await ReconcileAsync(host); // "two" quarantined today too

        var quarantined = Directory.EnumerateFiles(Path.Combine(PrimaryRoot, ".quarantine"), "same.jpg", SearchOption.AllDirectories).ToList();
        Assert.Equal(2, quarantined.Count);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(PrimaryRoot, ".quarantine")));
        Assert.Equal(["one", "two"], quarantined.Select(File.ReadAllText).Order());
    }

    [Fact]
    public async Task A_file_with_a_row_in_any_state_is_never_an_orphan_and_a_sighting_whose_file_gains_a_row_is_dropped()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Guid.NewGuid());
        PlantOrphan("test-files/aa/bb/owner/x.jpg");
        await ReconcileAsync(host);
        Assert.Single(await SightingsAsync(host));

        await InScopeAsync(host, async sp =>
        {
            var context = sp.GetRequiredService<OdysseyContext>();
            context.StoredObjects.Add(new StoredObject
            {
                Id = Guid.NewGuid(), VolumeId = row.VolumeId, FeatureKey = "TestFiles", RelativePath = "test-files/aa/bb/owner/X.JPG",
                State = StoredObjectState.Pending, CreatedAtUtc = Clock.GetUtcNow().UtcDateTime,
            });
            await context.SaveChangesAsync();
        });
        Clock.Advance(TimeSpan.FromHours(25));
        await ReconcileAsync(host);

        Assert.True(File.Exists(Path.Combine(PrimaryRoot, "test-files", "aa", "bb", "owner", "x.jpg")));
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, row.RelativePath)));
        Assert.Empty(await SightingsAsync(host));
    }

    [Fact]
    public async Task A_restart_mid_pass_resumes_at_the_cursor_and_keeps_first_seen()
    {
        // 300 folders, so a pass spans two ticks; 90 hold orphans (below the breaker's floor of 100), the rest a
        // file with a row.
        var host = await NewHostAsync();
        var volumeId = host.Registry.Find("primary")!.Id;
        var orphans = new List<string>();
        await InScopeAsync(host, async sp =>
        {
            var context = sp.GetRequiredService<OdysseyContext>();
            for (var i = 0; i < 300; i++)
            {
                var relative = $"test-files/{i / 256:x2}/{i % 256:x2}/o/f.jpg";
                PlantOrphan(relative);
                if (i % 10 < 3)
                {
                    orphans.Add(relative);
                }
                else
                {
                    context.StoredObjects.Add(new StoredObject
                    {
                        Id = Guid.NewGuid(), VolumeId = volumeId, FeatureKey = "TestFiles", RelativePath = relative,
                        State = StoredObjectState.Committed, Sha256Hash = new string('0', 64), CreatedAtUtc = Clock.GetUtcNow().UtcDateTime,
                    });
                }
            }

            await context.SaveChangesAsync();
        });

        await ReconcileAsync(host); // 256 folders: an unfinished pass
        var volume = await VolumeRowAsync(host);
        Assert.NotNull(volume.ReconciliationCursor);
        Assert.Equal(1, volume.ReconciliationPass);
        var firstSeen = (await SightingsAsync(host)).ToDictionary(s => s.RelativePath, s => s.FirstSeenAtUtc);
        Assert.NotEmpty(firstSeen);
        Assert.True(firstSeen.Count < orphans.Count);

        var restarted = await NewHostAsync(); // a new process over the same database and disk
        Clock.Advance(TimeSpan.FromHours(1));
        await RunPassAsync(restarted);

        var after = await VolumeRowAsync(restarted);
        Assert.Equal(2, after.ReconciliationPass);
        Assert.Equal(orphans.Count, after.LastPassOrphanCount);
        Assert.Null(after.ReconciliationHaltedAtUtc);
        var sightings = await SightingsAsync(restarted);
        Assert.Equal(orphans.Order(StringComparer.Ordinal), sightings.Select(s => s.RelativePath).Order(StringComparer.Ordinal));
        Assert.All(sightings.Where(s => firstSeen.ContainsKey(s.RelativePath)), s => Assert.Equal(firstSeen[s.RelativePath], s.FirstSeenAtUtc));

        Clock.Advance(TimeSpan.FromHours(25));
        var third = await NewHostAsync();
        await RunPassAsync(third);

        Assert.Empty(await SightingsAsync(third));
        Assert.Equal(orphans.Count, Directory.EnumerateFiles(Path.Combine(PrimaryRoot, ".quarantine"), "*", SearchOption.AllDirectories).Count());
        // A sweep catches and logs per folder, so a defect would otherwise surface only as a pass that never ends.
        Assert.DoesNotContain(host.Logs.Entries.Concat(restarted.Logs.Entries).Concat(third.Logs.Entries), e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task More_orphans_than_the_absolute_floor_halt_the_volume_and_nothing_is_quarantined()
    {
        var host = await NewHostAsync();
        await SeedLiveRowsAsync(host, 1000);
        for (var i = 0; i < 150; i++)
        {
            PlantOrphan($"test-files/{i:x2}/00/o/f.jpg");
        }

        await RunPassAsync(host);
        var volume = await VolumeRowAsync(host);
        Assert.NotNull(volume.ReconciliationHaltedAtUtc);
        Assert.Equal(150, volume.LastPassOrphanCount);
        Assert.Contains(host.Logs.At(LogLevel.Critical), m => m.Contains("Quarantine is halted", StringComparison.Ordinal));

        Clock.Advance(TimeSpan.FromHours(25));
        await RunPassAsync(host);
        Assert.False(Directory.Exists(Path.Combine(PrimaryRoot, ".quarantine")));
        Assert.NotNull((await VolumeRowAsync(host)).ReconciliationHaltedAtUtc);

        var status = await InScopeAsync(host, sp => sp.GetRequiredService<FileStorageStatusService>().GetVolumesAsync(default));
        Assert.True(Assert.Single(status).ReconciliationHalted);
        Assert.Equal(150, status[0].LastPassOrphanCount);

        // The operator restores the matching rows: the next pass is below the threshold and clears the halt.
        await InScopeAsync(host, async sp =>
        {
            var context = sp.GetRequiredService<OdysseyContext>();
            var volumeId = host.Registry.Find("primary")!.Id;
            for (var i = 0; i < 150; i++)
            {
                context.StoredObjects.Add(Live(volumeId, $"test-files/{i:x2}/00/o/f.jpg"));
            }

            await context.SaveChangesAsync();
        });
        await RunPassAsync(host);
        Assert.Null((await VolumeRowAsync(host)).ReconciliationHaltedAtUtc);
    }

    [Theory]
    [InlineData(250, true)]
    [InlineData(150, false)]
    public async Task The_ratio_term_sets_the_threshold_on_a_large_volume_across_a_restart(int orphans, bool halted)
    {
        var host = await NewHostAsync();
        await SeedLiveRowsAsync(host, 20_000);
        for (var i = 0; i < orphans; i++)
        {
            PlantOrphan($"test-files/{i % 200:x2}/{i / 200:x2}/o/f.jpg");
        }

        await ReconcileAsync(host); // ≤ 256 folders: this pass spans two ticks
        var restarted = await NewHostAsync();
        await RunPassAsync(restarted);

        var volume = await VolumeRowAsync(restarted);
        Assert.Equal(orphans, volume.LastPassOrphanCount);
        Assert.Equal(halted, volume.ReconciliationHaltedAtUtc is not null);
    }

    [Fact]
    public async Task An_unsharded_layout_is_walked_completely_in_ordinal_order_and_resumes_after_a_restart()
    {
        var host = await NewHostAsync();
        var days = Enumerable.Range(0, 300).Select(i => new DateOnly(2025, 1, 1).AddDays(i).ToString("yyyy-MM-dd")).ToList();
        foreach (var day in days)
        {
            PlantOrphan($"test-dated/{day}/f.jpg");
        }

        await ReconcileAsync(host);
        var cursor = (await VolumeRowAsync(host)).ReconciliationCursor;
        Assert.Equal($"test-dated/{days[254]}", cursor); // the feature root itself is the first folder visited

        var restarted = await NewHostAsync();
        await ReconcileAsync(restarted);

        Assert.Equal(2, (await VolumeRowAsync(restarted)).ReconciliationPass);
        Assert.Equal(300, (await SightingsAsync(restarted)).Count);
    }

    [Fact]
    public async Task Sibling_folders_are_compared_segment_by_segment_so_a_resumed_walk_visits_every_leaf_once()
    {
        var host = await NewHostAsync();
        PlantOrphan("test-dated/a/x/f.jpg");
        PlantOrphan("test-dated/a-b/f.jpg");
        PlantOrphan("test-dated/a-b/g.jpg");

        // An interrupted walk whose last visited folder was `a-b`. Whole-string order would put `a-b` before
        // `a/x` (0x2D < 0x2F) and resume into `a/x` again; segment-wise, `a` and `a/x` come first.
        await InScopeAsync(host, async sp =>
        {
            var context = sp.GetRequiredService<OdysseyContext>();
            var volume = await context.FileStorageVolumes.SingleAsync();
            volume.ReconciliationPass = 1;
            volume.ReconciliationCursor = "test-dated/a/x";
            await context.SaveChangesAsync();
        });

        await ReconcileAsync(host);

        var sightings = await SightingsAsync(host);
        Assert.Equal(["test-dated/a-b/f.jpg", "test-dated/a-b/g.jpg"], sightings.Select(s => s.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(2, (await VolumeRowAsync(host)).ReconciliationPass);
        Assert.True(FileStorageSweeper.CompareSegments(["test-dated", "a", "x"], ["test-dated", "a-b"]) < 0);
        Assert.True(string.CompareOrdinal("test-dated/a/x", "test-dated/a-b") > 0);
    }

    [Fact]
    public async Task Non_conforming_files_and_reserved_entries_are_left_alone_and_counted_once_per_pass()
    {
        var host = await NewHostAsync();
        var odd = PlantOrphan("test-files/aa/BB/x.jpg");
        var dotfile = PlantOrphan("test-files/aa/bb/.hidden.jpg");
        var lostFound = Directory.CreateDirectory(Path.Combine(PrimaryRoot, "lost+found"));
        await File.WriteAllTextAsync(Path.Combine(lostFound.FullName, "inode"), "x");
        var unrelated = PlantOrphan("not-a-feature/aa/f.jpg");

        await ReconcileAsync(host);
        Clock.Advance(TimeSpan.FromHours(25));
        await ReconcileAsync(host);
        Clock.Advance(TimeSpan.FromHours(25));
        await ReconcileAsync(host);

        Assert.True(File.Exists(odd));
        Assert.True(File.Exists(dotfile));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(Path.Combine(lostFound.FullName, "inode")));
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, StorageNames.MarkerFileName)));
        Assert.Empty(await SightingsAsync(host));
        Assert.Equal(3, host.Logs.At(LogLevel.Warning).Count(m => m.Contains("do not match the store's naming rules", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_symlinked_quarantine_makes_the_move_refuse_and_nothing_outside_the_volume_moves()
    {
        var host = await NewHostAsync();
        var orphan = PlantOrphan("test-files/aa/bb/o/stray.jpg");
        var outside = Directory.CreateDirectory(Path.Combine(Base.FullName, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(PrimaryRoot, ".quarantine"), outside.FullName);

        await ReconcileAsync(host);
        Clock.Advance(TimeSpan.FromHours(25));
        await ReconcileAsync(host);

        Assert.True(File.Exists(orphan));
        Assert.Empty(outside.EnumerateFileSystemInfos());
        Assert.Contains(host.Logs.At(LogLevel.Error), m => m.StartsWith("Security:", StringComparison.Ordinal) && m.Contains(".quarantine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_purge_deletes_a_planted_link_as_a_link_and_leaves_a_symlinked_date_folder_alone()
    {
        var host = await NewHostAsync();
        var outside = Directory.CreateDirectory(Path.Combine(Base.FullName, "outside"));
        var target = Path.Combine(outside.FullName, "target.txt");
        await File.WriteAllTextAsync(target, "survives");
        var old = Directory.CreateDirectory(Path.Combine(PrimaryRoot, ".quarantine", "2026-01-01", Guid.NewGuid().ToString("N")));
        File.CreateSymbolicLink(Path.Combine(old.FullName, "link.txt"), target);
        Directory.CreateSymbolicLink(Path.Combine(old.FullName, "dirlink"), outside.FullName);
        var linkedDay = Path.Combine(PrimaryRoot, ".quarantine", "2026-01-02");
        Directory.CreateSymbolicLink(linkedDay, outside.FullName);

        await ReconcileAsync(host);

        Assert.False(Directory.Exists(Path.Combine(PrimaryRoot, ".quarantine", "2026-01-01")));
        Assert.True(File.Exists(target));
        Assert.Equal("survives", await File.ReadAllTextAsync(target));
        Assert.True(new DirectoryInfo(linkedDay).LinkTarget is not null);
        Assert.Contains(host.Logs.At(LogLevel.Error), m => m.Contains("date folder is a symbolic link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stale_part_files_without_a_pending_row_are_removed_from_tmp()
    {
        var host = await NewHostAsync();
        var tmp = Directory.CreateDirectory(Path.Combine(PrimaryRoot, ".tmp"));
        var stale = Path.Combine(tmp.FullName, $"{Guid.NewGuid():N}.part");
        var fresh = Path.Combine(tmp.FullName, $"{Guid.NewGuid():N}.part");
        await File.WriteAllTextAsync(stale, "x");
        await File.WriteAllTextAsync(fresh, "x");
        File.SetLastWriteTimeUtc(stale, Clock.GetUtcNow().UtcDateTime.AddHours(-25));
        File.SetLastWriteTimeUtc(fresh, Clock.GetUtcNow().UtcDateTime.AddHours(-1));

        await ReconcileAsync(host);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    private string PlantOrphan(string relative, string content = "orphan")
    {
        var path = Path.Combine([PrimaryRoot, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Task ReconcileAsync(FileStorageTestHost host) =>
        InScopeAsync(host, sp => sp.GetRequiredService<FileStorageSweeper>().ReconcileAsync(default));

    /// <summary>Ticks until the pass in progress completes.</summary>
    private static async Task RunPassAsync(FileStorageTestHost host)
    {
        var pass = (await VolumeRowAsync(host)).ReconciliationPass;
        for (var tick = 0; tick < 20; tick++)
        {
            await ReconcileAsync(host);
            if ((await VolumeRowAsync(host)).ReconciliationPass > Math.Max(pass, 1))
            {
                return;
            }
        }

        throw new InvalidOperationException("The pass did not complete. " + string.Join(" | ", host.Logs.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)));
    }

    private static async Task SeedLiveRowsAsync(FileStorageTestHost host, int count)
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var volumeId = host.Registry.Find("primary")!.Id;
        context.StoredObjects.AddRange(Enumerable.Range(0, count).Select(i => Live(volumeId, $"test-secrets/zz/zz/{i}.jpg")));
        await context.SaveChangesAsync();
    }

    private static StoredObject Live(Guid volumeId, string path) => new()
    {
        Id = Guid.NewGuid(), VolumeId = volumeId, FeatureKey = "TestSecrets", RelativePath = path,
        State = StoredObjectState.Committed, Sha256Hash = new string('0', 64), CreatedAtUtc = DateTime.UtcNow,
    };

    private static async Task<List<FileStorageOrphanSighting>> SightingsAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageOrphanSightings.AsNoTracking().ToListAsync();
    }

    private static async Task<FileStorageVolume> VolumeRowAsync(FileStorageTestHost host)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileStorageVolumes.AsNoTracking().SingleAsync();
    }
}
