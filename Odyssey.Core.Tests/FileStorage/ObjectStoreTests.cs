using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>Put, read and release (issue #346 §3.5; AC 1, 3, 7, 9, 10, 17, 18, 26, 27).</summary>
public class ObjectStoreTests : FileStorageTestBase
{
    private static readonly Guid Owner = Guid.Parse("3fa29c0e-8b7d-4e51-a6c2-d9f01b4e7a33");

    [Fact]
    public async Task A_put_and_the_callers_save_commit_the_row_and_the_file()
    {
        var host = await NewHostAsync();
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var content = Bytes(5 * 1024 * 1024);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(content.ToArray()));

        var row = await store.PutAsync("TestFiles", Owner, "Holiday photo.HTML", "_x", "image/jpeg", content, 64L * 1024 * 1024, default);
        context.TestFileAttachments.Add(new TestFileAttachment { Id = Guid.NewGuid(), StoredObjectId = row.Id });
        await context.SaveChangesAsync();

        var saved = await RowAsync(host, row.Id);
        Assert.Equal(StoredObjectState.Committed, saved!.State);
        Assert.Equal($"test-files/3f/a2/{Owner:N}/Holiday photo_x.jpg", saved.RelativePath);
        Assert.Equal(expectedHash, saved.Sha256Hash);
        Assert.Equal(5 * 1024 * 1024, saved.SizeBytes);

        var file = new FileInfo(Path.Combine(PrimaryRoot, saved.RelativePath));
        Assert.True(file.Exists);
        Assert.Equal(5 * 1024 * 1024, file.Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(VolumeFileSystem.FileMode, file.UnixFileMode);
            Assert.Equal(VolumeFileSystem.DirectoryMode, file.Directory!.UnixFileMode);
            Assert.Equal(VolumeFileSystem.DirectoryMode, new DirectoryInfo(Path.Combine(PrimaryRoot, "test-files")).UnixFileMode);
        }

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(PrimaryRoot, ".tmp")));
    }

    [Fact]
    public async Task Collisions_number_from_two_with_the_marker_before_the_suffix()
    {
        var host = await NewHostAsync();

        var first = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "IMG_2041.jpg");
        var second = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "IMG_2041.jpg");
        var third = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "img_2041.JPG");
        var suffixed = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "IMG_2041.jpg", "_256", contentType: "image/webp");
        var suffixed2 = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "IMG_2041.jpg", "_256", contentType: "image/webp");

        Assert.EndsWith("/IMG_2041.jpg", first.RelativePath);
        Assert.EndsWith("/IMG_2041 (2).jpg", second.RelativePath);
        Assert.EndsWith("/img_2041 (3).jpg", third.RelativePath);
        Assert.EndsWith("/IMG_2041_256.webp", suffixed.RelativePath);
        Assert.EndsWith("/IMG_2041 (2)_256.webp", suffixed2.RelativePath);
    }

    [Fact]
    public async Task Underscore_and_percent_in_a_stem_collide_only_with_the_exact_name()
    {
        var host = await NewHostAsync();

        var underscore = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "a_b.jpg");
        var other = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "aXb.jpg");
        var percent = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "a%b.jpg");

        Assert.EndsWith("/a_b.jpg", underscore.RelativePath);
        Assert.EndsWith("/aXb.jpg", other.RelativePath);
        Assert.EndsWith("/a_b (2).jpg", percent.RelativePath); // `%` is not on the allow-list: it becomes `_`.
    }

    [Fact]
    public async Task Past_ninety_nine_the_marker_is_eight_hex_characters()
    {
        var host = await NewHostAsync();
        await using (var scope = host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var volumeId = host.Registry.Find("primary")!.Id;
            var folder = $"test-files/3f/a2/{Owner:N}";
            context.StoredObjects.Add(Committed(volumeId, $"{folder}/x.jpg"));
            for (var n = 2; n <= 99; n++)
            {
                context.StoredObjects.Add(Committed(volumeId, $"{folder}/x ({n}).jpg"));
            }

            await context.SaveChangesAsync();
        }

        var next = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "x.jpg");

        Assert.Matches(@"/x \([0-9a-f]{8}\)\.jpg$", next.RelativePath);
    }

    [Fact]
    public async Task A_stray_file_the_index_cannot_see_is_skipped_rather_than_overwritten()
    {
        var host = await NewHostAsync();
        var folder = Path.Combine(PrimaryRoot, "test-files", "3f", "a2", Owner.ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "IMG.jpg"), "stray");

        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, "IMG.jpg", length: 1024);

        Assert.EndsWith("/IMG (2).jpg", row.RelativePath);
        Assert.Equal("stray", await File.ReadAllTextAsync(Path.Combine(folder, "IMG.jpg")));
        // The retry stores the content, not an empty file: the input stream was drained by the first attempt.
        var stored = await File.ReadAllBytesAsync(Path.Combine(PrimaryRoot, row.RelativePath));
        Assert.Equal(Bytes(1024).ToArray(), stored);
        Assert.Equal(1024, row.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stored)), row.Sha256Hash);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(PrimaryRoot, ".tmp")));
        await using var scope = host.Scope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OdysseyContext>().StoredObjects.CountAsync());
    }

    [Fact]
    public async Task A_stray_collision_with_a_non_seekable_stream_still_stores_every_byte()
    {
        var host = await NewHostAsync();
        var folder = Path.Combine(PrimaryRoot, "test-files", "3f", "a2", Owner.ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "IMG.jpg"), "stray");
        await File.WriteAllTextAsync(Path.Combine(folder, "IMG (2).jpg"), "stray");

        await using var scope = host.Scope();
        var payload = Bytes(70_000).ToArray();
        var row = await scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync("TestFiles", Owner, "IMG.jpg", null, "image/jpeg", new ForwardOnlyStream(payload), 100_000, default);

        Assert.EndsWith("/IMG (3).jpg", row.RelativePath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(PrimaryRoot, row.RelativePath)));
        Assert.Equal(70_000, row.SizeBytes);
        await using var check = host.Scope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<OdysseyContext>().StoredObjects.CountAsync());
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - position);
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// No log line in the store carries an exception object: an I/O message embeds the full path and a duplicate-key
    /// message the key, and both carry a user's file name. Failures are named by <c>FileStorageLog.Describe</c>.
    /// </summary>
    [Fact]
    public void No_file_storage_log_line_carries_an_exception_object()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Odyssey.sln")))
        {
            root = root.Parent!;
        }

        var offenders = Directory.EnumerateFiles(Path.Combine(root.FullName, "Odyssey.Core", "FileStorage"), "*.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, i)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, @"logger\.Log\w*\(\s*(ex|cause|exception|failure|probeFailure)\s*,"))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.i + 1}")
            .ToList();

        Assert.True(offenders.Count == 0, "Log FileStorageLog.Describe(ex), not the exception: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task Opaque_naming_puts_no_user_text_on_disk()
    {
        var host = await NewHostAsync();

        var row = await PutCommittedAsync(host, TestStorageFeatures.Secrets, Owner, "Passport_Ola_Nordmann.jpg", "_full");

        Assert.Equal($"test-secrets/3f/a2/{row.Id:N}_full.jpg", row.RelativePath);
        Assert.DoesNotContain("Passport", string.Join("|", Directory.EnumerateFiles(PrimaryRoot, "*", SearchOption.AllDirectories)));
    }

    [Fact]
    public async Task A_read_returns_the_bytes_and_another_features_key_is_a_404()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner, length: 4096);

        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var content = await store.OpenReadAsync("TestFiles", row.Id, default);
        await using (content.Content)
        {
            var read = new MemoryStream();
            await content.Content.CopyToAsync(read);
            Assert.Equal(Bytes(4096).ToArray(), read.ToArray());
            Assert.Equal($"\"{row.Sha256Hash}\"", content.ETag);
        }

        await Assert.ThrowsAsync<DomainNotFoundException>(() => store.OpenReadAsync("TestSecrets", row.Id, default));
        await Assert.ThrowsAsync<DomainNotFoundException>(() => store.OpenReadAsync("TestFiles", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task A_pending_reservation_is_not_readable()
    {
        var host = await NewHostAsync();
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var row = await store.PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default);

        // The caller has not saved: the row in the database is still Pending.
        await using var reader = host.Scope();
        await Assert.ThrowsAsync<DomainNotFoundException>(
            () => reader.ServiceProvider.GetRequiredService<IObjectStore>().OpenReadAsync("TestFiles", row.Id, default));
    }

    [Fact]
    public async Task Exceeding_max_bytes_is_a_413_that_leaves_no_file_and_the_reservation_is_swept_later()
    {
        var host = await NewHostAsync();
        Guid reservationId;
        await using (var scope = host.Scope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
            var error = await Assert.ThrowsAsync<StoredObjectTooLargeException>(
                () => store.PutAsync("TestFiles", Owner, "big.jpg", null, "image/jpeg", Bytes(2000), 1000, default));
            Assert.Equal(413, error.StatusCode);
            Assert.Equal("file-storage-too-large", error.Code);
        }

        Assert.DoesNotContain(Directory.EnumerateFiles(PrimaryRoot, "*", SearchOption.AllDirectories), f => !f.EndsWith(".odyssey-volume.json"));
        await using (var scope = host.Scope())
        {
            var pending = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().StoredObjects.SingleAsync();
            Assert.Equal(StoredObjectState.Pending, pending.State);
            reservationId = pending.Id;
        }

        Assert.Equal(0, await SweepAsync(host, s => s.SweepAbandonedWritesAsync(default)));
        Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, await SweepAsync(host, s => s.SweepAbandonedWritesAsync(default)));
        Assert.Null(await RowAsync(host, reservationId));
    }

    [Fact]
    public async Task A_volume_below_the_free_space_floor_is_a_507_before_any_byte_is_written()
    {
        var host = await NewHostAsync();
        host.Space.Override = (ObjectStore.FreeSpaceFloorBytes - 1, 10L * 1024 * 1024 * 1024);

        await using var scope = host.Scope();
        var error = await Assert.ThrowsAsync<FileStorageFullException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default));

        Assert.Equal(507, error.StatusCode);
        Assert.DoesNotContain(Directory.EnumerateFiles(PrimaryRoot, "*", SearchOption.AllDirectories), f => !f.EndsWith(".odyssey-volume.json"));
    }

    public static TheoryData<string[]> BadLayouts => new()
    {
        new[] { "A B" },
        new[] { "a", "b", "c", "d", "e", "f" },
        new[] { "abcdefghijabcdefghijabcdefghijabc" },
        Array.Empty<string>(),
    };

    [Theory]
    [MemberData(nameof(BadLayouts))]
    public async Task A_layout_that_breaks_the_segment_rules_is_a_contract_violation_and_writes_nothing(string[] segments)
    {
        var bad = StorageFeature.Create<string[]>("TestBad", "test-bad", s => s, StorageReleaseMode.Graced,
            StorageFeatureReference.To<TestFileAttachment>(a => a.StoredObjectId));
        var host = await NewHostAsync(new Dictionary<string, string> { ["primary"] = Root("primary") },
            new Dictionary<string, string> { ["TestBad"] = "primary" }, [bad]);

        await using var scope = host.Scope();
        await Assert.ThrowsAsync<StoreContractViolationException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync("TestBad", segments, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default));

        Assert.False(Directory.Exists(Path.Combine(PrimaryRoot, "test-bad")));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<OdysseyContext>().StoredObjects.CountAsync());
    }

    [Theory]
    [InlineData("application/x-unknown", null)]
    [InlineData("image/jpeg", "a/b")]
    public async Task An_unmapped_content_type_or_an_invalid_suffix_is_a_contract_violation(string contentType, string? suffix)
    {
        var host = await NewHostAsync();
        await using var scope = host.Scope();

        await Assert.ThrowsAsync<StoreContractViolationException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync("TestFiles", Owner, "x.jpg", suffix, contentType, Bytes(10), 1000, default));
    }

    [Fact]
    public async Task Max_bytes_above_the_configured_ceiling_is_a_contract_violation()
    {
        var host = await NewHostAsync();
        await using var scope = host.Scope();

        await Assert.ThrowsAsync<StoreContractViolationException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 65L * 1024 * 1024, default));
    }

    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData(".odyssey-volume.json")]
    [InlineData("test-files/../../outside.jpg")]
    public async Task A_relative_path_edited_out_of_its_feature_root_is_refused(string tampered)
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await File.WriteAllTextAsync(Path.Combine(Base.FullName, "outside.jpg"), "secret");
        await SetPathAsync(host, row.Id, tampered);

        await using var scope = host.Scope();
        var error = await Assert.ThrowsAsync<FileStorageIntegrityException>(
            () => scope.ServiceProvider.GetRequiredService<IObjectStore>().OpenReadAsync("TestFiles", row.Id, default));
        Assert.Equal(500, error.StatusCode);
        Assert.DoesNotContain("outside", error.Message);
    }

    [Fact]
    public async Task A_symlinked_layout_folder_is_refused_on_read_and_on_put()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        var shard = Path.Combine(PrimaryRoot, "test-files", "3f");
        var elsewhere = Path.Combine(Base.FullName, "elsewhere");
        Directory.Move(shard, elsewhere);
        Directory.CreateSymbolicLink(shard, elsewhere);

        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        await Assert.ThrowsAsync<FileStorageIntegrityException>(() => store.OpenReadAsync("TestFiles", row.Id, default));
        await Assert.ThrowsAsync<FileStorageIntegrityException>(
            () => store.PutAsync("TestFiles", Owner, "new.jpg", null, "image/jpeg", Bytes(10), 1000, default));
        Assert.Single(Directory.EnumerateFiles(elsewhere, "*", SearchOption.AllDirectories));
        Assert.Contains(host.Logs.At(Microsoft.Extensions.Logging.LogLevel.Error), m => m.StartsWith("Security:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_symlinked_tmp_folder_is_refused_on_put()
    {
        var host = await NewHostAsync();
        var elsewhere = Directory.CreateDirectory(Path.Combine(Base.FullName, "tmp-elsewhere"));
        var tmp = Path.Combine(PrimaryRoot, ".tmp");
        if (Directory.Exists(tmp))
        {
            Directory.Delete(tmp, recursive: true);
        }

        Directory.CreateSymbolicLink(tmp, elsewhere.FullName);

        await using var scope = host.Scope();
        await Assert.ThrowsAsync<FileStorageIntegrityException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default));
        Assert.Empty(elsewhere.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task A_rolled_back_release_leaves_the_row_committed_and_the_file_present()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);

        await using (var scope = host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<IObjectStore>().StageReleaseAsync(row.Id, immediate: false, default);
            // Never saved: the caller's transaction rolled back.
        }

        Assert.Equal(StoredObjectState.Committed, (await RowAsync(host, row.Id))!.State);
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, row.RelativePath)));
    }

    [Fact]
    public async Task A_released_object_is_a_404_at_once_and_its_file_stays_until_the_sweep()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        await ReleaseAsync(host, row.Id);

        await using var scope = host.Scope();
        await Assert.ThrowsAsync<DomainNotFoundException>(
            () => scope.ServiceProvider.GetRequiredService<IObjectStore>().OpenReadAsync("TestFiles", row.Id, default));
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, row.RelativePath)));
    }

    [Fact]
    public async Task A_missing_file_is_an_integrity_fault_and_does_not_degrade_the_volume()
    {
        var host = await NewHostAsync();
        var row = await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        File.Delete(Path.Combine(PrimaryRoot, row.RelativePath));

        await using var scope = host.Scope();
        await Assert.ThrowsAsync<FileStorageIntegrityException>(
            () => scope.ServiceProvider.GetRequiredService<IObjectStore>().OpenReadAsync("TestFiles", row.Id, default));
        Assert.False(host.Registry.Find("primary")!.Degraded);
        Assert.Equal(StoredObjectState.Committed, (await RowAsync(host, row.Id))!.State);
    }

    [Fact]
    public async Task A_volume_that_loses_its_disk_is_degraded_with_a_503_while_another_keeps_working_and_a_probe_recovers_it()
    {
        var host = await NewHostAsync(
            new Dictionary<string, string> { ["a"] = Root("a"), ["b"] = Root("b") },
            new Dictionary<string, string> { ["TestFiles"] = "a", ["TestSecrets"] = "b", ["TestDated"] = "b" });
        var marker = Path.Combine(Root("a"), StorageNames.MarkerFileName);
        var saved = await File.ReadAllBytesAsync(marker);
        File.Delete(marker); // what an unmounted disk looks like from inside the container

        await using (var scope = host.Scope())
        {
            var error = await Assert.ThrowsAsync<FileStorageUnavailableException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
                .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default));
            Assert.Equal(503, error.StatusCode);
        }

        Assert.True(host.Registry.Find("a")!.Degraded);
        Assert.False(host.Registry.Find("b")!.Degraded);
        await PutCommittedAsync(host, TestStorageFeatures.Secrets, Owner);

        await using (var scope = host.Scope())
        {
            await Assert.ThrowsAsync<FileStorageUnavailableException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
                .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default));
        }

        await File.WriteAllBytesAsync(marker, saved);
        Assert.True(await host.Registry.ProbeAsync(host.Registry.Find("a")!, default));
        Assert.False(host.Registry.Find("a")!.Degraded);
        await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
    }

    [Fact]
    public async Task A_degraded_volume_is_rechecked_by_requests_at_most_every_fifteen_seconds_and_recovers_on_its_own()
    {
        var host = await NewHostAsync();
        var marker = Path.Combine(PrimaryRoot, StorageNames.MarkerFileName);
        var saved = await File.ReadAllBytesAsync(marker);
        File.Delete(marker);
        await using (var scope = host.Scope())
        {
            await Assert.ThrowsAsync<FileStorageUnavailableException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
                .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default));
        }

        await File.WriteAllBytesAsync(marker, saved);
        Clock.Advance(TimeSpan.FromSeconds(5));
        await using (var scope = host.Scope())
        {
            // Within the interval: still refused, without a probe.
            await Assert.ThrowsAsync<FileStorageUnavailableException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
                .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default));
        }

        Clock.Advance(FileStorageVolumeRegistry.RecheckInterval);
        await PutCommittedAsync(host, TestStorageFeatures.Files, Owner);
        Assert.False(host.Registry.Find("primary")!.Degraded);
    }

    [Fact]
    public async Task The_store_is_unavailable_when_nothing_is_configured()
    {
        var host = await NewHostAsync(new Dictionary<string, string>(), new Dictionary<string, string>(), features: []);

        await using var scope = host.Scope();
        Assert.False(host.Registry.IsActive);
        await Assert.ThrowsAsync<StoreContractViolationException>(() => scope.ServiceProvider.GetRequiredService<IObjectStore>()
            .PutAsync("TestFiles", Owner, "x.jpg", null, "image/jpeg", Bytes(10), 1000, default));
    }

    internal static async Task ReleaseAsync(FileStorageTestHost host, Guid id, bool immediate = false, bool dropReference = true)
    {
        await using var scope = host.Scope();
        var context = (TestStorageContext)scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await scope.ServiceProvider.GetRequiredService<IObjectStore>().StageReleaseAsync(id, immediate, default);
        if (dropReference)
        {
            context.TestFileAttachments.RemoveRange(context.TestFileAttachments.Where(a => a.StoredObjectId == id));
            context.TestSecretAttachments.RemoveRange(context.TestSecretAttachments.Where(a => a.StoredObjectId == id));
            context.TestDatedAttachments.RemoveRange(context.TestDatedAttachments.Where(a => a.StoredObjectId == id));
        }

        await context.SaveChangesAsync();
    }

    internal static async Task SetPathAsync(FileStorageTestHost host, Guid id, string relativePath)
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.StoredObjects.SingleAsync(o => o.Id == id);
        row.RelativePath = relativePath;
        await context.SaveChangesAsync();
    }

    private static StoredObject Committed(Guid volumeId, string path) => new()
    {
        Id = Guid.NewGuid(), VolumeId = volumeId, FeatureKey = "TestFiles", RelativePath = path, State = StoredObjectState.Committed,
        Sha256Hash = new string('0', 64), CreatedAtUtc = DateTime.UtcNow,
    };
}
