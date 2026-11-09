using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Application;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>
/// Issue #448 §3.5: the worker job that moves every remaining <c>FileBlob</c> onto the file store. It classifies each file
/// from its links, verifies before it changes the database, keeps the file's id, parks a file it cannot move for a reason
/// of its own, leaves a photo-wrapped file to the photo move, and never lets an unhealthy area stall the others.
/// </summary>
public sealed class FileBlobBackfillJobTests : FileStorageTestBase
{
    private static readonly byte[] PdfBytes = "%PDF-1.7 statement"u8.ToArray();

    private static FileBlobBackfillJob Job(FileStorageTestHost host) => ActivatorUtilities.CreateInstance<FileBlobBackfillJob>(host.Services);

    private static async Task<T> InContextAsync<T>(FileStorageTestHost host, Func<OdysseyContext, Task<T>> action)
    {
        await using var scope = host.Scope();
        return await action(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    private static async Task InContextAsync(FileStorageTestHost host, Func<OdysseyContext, Task> action)
    {
        await using var scope = host.Scope();
        await action(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    /// <summary>A legacy file: bytes in FileBlob, as every file was before the move.</summary>
    private static Task<Guid> AddLegacyAsync(
        FileStorageTestHost host, byte[]? bytes = null, string contentType = "application/pdf", string name = "Lønnsslipp.pdf",
        DateTime? uploadedAt = null, string? sha = null, long? sizeBytes = null) =>
        InContextAsync(host, async c =>
        {
            bytes ??= PdfBytes;
            var blob = new FileBlob { Id = Guid.NewGuid(), Content = bytes };
            var file = new FileMetadata
            {
                Id = Guid.NewGuid(), FileName = name, ContentType = contentType, SizeBytes = sizeBytes ?? bytes.LongLength,
                Sha256Hash = sha ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), FileBlobId = blob.Id,
                UploadedAtUtc = uploadedAt ?? new DateTime(2021, 5, 1, 9, 0, 0, DateTimeKind.Utc),
            };
            c.FileBlob.Add(blob);
            c.FileMetadata.Add(file);
            await c.SaveChangesAsync();
            return file.Id;
        });

    private static Task LinkJournalAsync(FileStorageTestHost host, Guid fileId) => InContextAsync(host, async c =>
    {
        c.JournalEntryAttachments.Add(new JournalEntryAttachment { JournalEntryId = Guid.NewGuid(), FileId = fileId, CreatedAt = DateTime.UtcNow });
        await c.SaveChangesAsync();
    });

    private static Task LinkTransactionAsync(FileStorageTestHost host, Guid fileId) => InContextAsync(host, async c =>
    {
        c.TransactionFiles.Add(new TransactionFile { TransactionId = Guid.NewGuid(), FileMetadataId = fileId, AttachedAtUtc = DateTime.UtcNow });
        await c.SaveChangesAsync();
    });

    private static Task LinkAvatarAsync(FileStorageTestHost host, Guid fileId) => InContextAsync(host, async c =>
    {
        c.Contacts.Add(new Contact { ContactId = Guid.NewGuid(), ExternalUid = Guid.NewGuid().ToString(), NormalizedName = "ada", AvatarFileId = fileId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await c.SaveChangesAsync();
    });

    private static Task<FileMetadata> FileAsync(FileStorageTestHost host, Guid id) =>
        InContextAsync(host, c => c.FileMetadata.AsNoTracking().SingleAsync(f => f.Id == id));

    private static Task<StoredObject> ObjectAsync(FileStorageTestHost host, Guid id) =>
        InContextAsync(host, c => c.StoredObjects.AsNoTracking().SingleAsync(o => o.Id == id));

    private static Task<FileBlobBackfillLease> LeaseAsync(FileStorageTestHost host) =>
        InContextAsync(host, c => c.FileBlobBackfillLeases.AsNoTracking().SingleAsync());

    private static Task<string> ParkedCodeAsync(FileStorageTestHost host, Guid fileId) =>
        InContextAsync(host, async c => (await c.FileBlobBackfillFailures.AsNoTracking().SingleAsync(f => f.FileId == fileId)).Code);

    private static Task<List<FileBlobBackfillJobRun>> RunsAsync(FileStorageTestHost host) =>
        InContextAsync(host, c => c.Set<FileBlobBackfillJobRun>().AsNoTracking().OrderBy(r => r.StartedAtUtc).ToListAsync());

    /// <summary>Wraps the real store so a test can make a write fail with a chosen exception.</summary>
    private sealed class FaultyStore
    {
        public Func<Exception?>? PutFailure { get; set; }

        public void Install(IServiceCollection services)
        {
            services.AddScoped<ObjectStore>();
            services.Replace(ServiceDescriptor.Scoped<IObjectStore>(sp => new Wrapper(sp.GetRequiredService<ObjectStore>(), this)));
        }

        private sealed class Wrapper(IObjectStore inner, FaultyStore faults) : IObjectStore
        {
            public Task<StoredObject> PutAsync(
                string featureKey, object? layoutArgs, string? originalFileName, string? suffix, string contentType,
                Stream content, long maxBytes, CancellationToken cancellationToken) =>
                faults.PutFailure?.Invoke() is { } failure
                    ? Task.FromException<StoredObject>(failure)
                    : inner.PutAsync(featureKey, layoutArgs, originalFileName, suffix, contentType, content, maxBytes, cancellationToken);

            public Task<StoredObjectContent> OpenReadAsync(string featureKey, Guid storedObjectId, CancellationToken cancellationToken) =>
                inner.OpenReadAsync(featureKey, storedObjectId, cancellationToken);

            public void StageRelease(StoredObject storedObject, bool immediate = false) => inner.StageRelease(storedObject, immediate);

            public Task StageReleaseAsync(Guid storedObjectId, bool immediate, CancellationToken cancellationToken) =>
                inner.StageReleaseAsync(storedObjectId, immediate, cancellationToken);

            public Task<string> ReadBackHashAsync(StoredObject storedObject, CancellationToken cancellationToken) =>
                inner.ReadBackHashAsync(storedObject, cancellationToken);
        }
    }

    [Fact]
    public async Task A_finance_file_moves_to_finance_files_under_its_upload_year_and_keeps_its_id()
    {
        var host = await NewFilesHostAsync();
        var fileId = await AddLegacyAsync(host);
        await LinkTransactionAsync(host, fileId);

        await Job(host).RunOnceAsync(default);

        var file = await FileAsync(host, fileId);
        Assert.Null(file.FileBlobId);
        Assert.Null(file.JournalStoredObjectId);
        var stored = await ObjectAsync(host, file.FinanceStoredObjectId!.Value);
        Assert.Equal((StoredObjectState.Committed, StoredFileAreas.FinanceFeatureKey), (stored.State, stored.FeatureKey));
        Assert.StartsWith("finance-files/2021/1/", stored.RelativePath, StringComparison.Ordinal);
        Assert.Equal(PdfBytes, await File.ReadAllBytesAsync(Path.Combine(PrimaryRoot, stored.RelativePath)));
        Assert.False(await InContextAsync(host, c => c.FileBlob.AnyAsync()));
        // The link is untouched: the file's id never changes.
        Assert.True(await InContextAsync(host, c => c.TransactionFiles.AnyAsync(t => t.FileMetadataId == fileId)));
    }

    [Theory]
    [InlineData(true, false, false, "application/pdf", StoredFileArea.Journal)]
    [InlineData(true, false, true, "image/jpeg", StoredFileArea.Journal)]
    [InlineData(true, true, false, "application/pdf", StoredFileArea.Finance)]
    [InlineData(false, false, false, "application/pdf", StoredFileArea.Finance)]
    [InlineData(false, false, true, "image/jpeg", StoredFileArea.ContactAvatars)]
    [InlineData(false, false, true, "application/pdf", StoredFileArea.Finance)]
    public async Task Each_file_is_filed_by_its_links(bool journal, bool finance, bool avatar, string contentType, StoredFileArea expected)
    {
        var host = await NewFilesHostAsync();
        var bytes = contentType == "image/jpeg" ? PhotoTestData.Jpeg(3) : PdfBytes;
        var fileId = await AddLegacyAsync(host, bytes, contentType, contentType == "image/jpeg" ? "a.jpg" : "a.pdf");
        if (journal) await LinkJournalAsync(host, fileId);
        if (finance) await LinkTransactionAsync(host, fileId);
        if (avatar) await LinkAvatarAsync(host, fileId);

        await Job(host).RunOnceAsync(default);

        Assert.Equal(expected, StoredFileAreas.AreaOf(await FileAsync(host, fileId)));
    }

    [Fact]
    public async Task A_file_a_legacy_photo_wraps_waits_for_the_photo_move_and_counts_once()
    {
        var host = await NewFilesHostAsync();
        Guid fileId = default;
        await InContextAsync(host, async c =>
        {
            var (_, file) = PhotoTestData.StageLegacyPhoto(c, PhotoTestData.Jpeg(5), "IMG.jpg");
            fileId = file.Id;
            await c.SaveChangesAsync();
        });

        await Job(host).RunOnceAsync(default);

        Assert.NotNull((await FileAsync(host, fileId)).FileBlobId);
        var status = await InContextAsync(host, c => new FileBackfillStatusService(c).GetAsync(default));
        Assert.Equal((1, 0, 1, 0), (status.Remaining, status.ToMove, status.WaitingOnPhotos, status.Failed));
    }

    [Fact]
    public async Task A_hash_mismatch_parks_the_file_which_stays_readable_and_is_not_retried()
    {
        var host = await NewFilesHostAsync();
        var fileId = await AddLegacyAsync(host, sha: new string('0', 64));

        await Job(host).RunOnceAsync(default);
        await Job(host).RunOnceAsync(default);

        Assert.Equal("hash", (await InContextAsync(host, c => c.FileBlobBackfillFailures.AsNoTracking().SingleAsync(f => f.FileId == fileId))).Code);
        Assert.NotNull((await FileAsync(host, fileId)).FileBlobId);
        Assert.False(await InContextAsync(host, c => c.StoredObjects.AnyAsync(o => o.State == StoredObjectState.Committed)));
        var status = await InContextAsync(host, c => new FileBackfillStatusService(c).GetAsync(default));
        Assert.Equal((1, 0, 1), (status.Remaining, status.ToMove, status.Failed));
        Assert.Equal(1, status.FailedByCode["hash"]);
        Assert.Equal(FileBlobBackfillFailureCodes.All.Count, status.FailedByCode.Count);
    }

    [Fact]
    public async Task A_content_type_with_no_extension_is_parked_as_type()
    {
        var host = await NewFilesHostAsync();
        var fileId = await AddLegacyAsync(host, contentType: "application/x-legacy-format", name: "old.bin");

        await Job(host).RunOnceAsync(default);

        Assert.Equal("type", (await InContextAsync(host, c => c.FileBlobBackfillFailures.SingleAsync(f => f.FileId == fileId))).Code);
    }

    [Fact]
    public async Task A_failure_row_whose_file_is_gone_is_pruned()
    {
        var host = await NewFilesHostAsync();
        await InContextAsync(host, async c =>
        {
            c.FileBlobBackfillFailures.Add(new FileBlobBackfillFailure { FileId = Guid.NewGuid(), Code = "read", RecordedAtUtc = DateTime.UtcNow });
            await c.SaveChangesAsync();
        });

        await Job(host).RunOnceAsync(default);

        Assert.False(await InContextAsync(host, c => c.FileBlobBackfillFailures.AnyAsync()));
    }

    /// <summary>
    /// AC 35: with FinanceFiles on a full volume and 30 older finance files ahead of one journal file, the journal file moves
    /// in the first tick and the tick is not held; once only finance files are left, the job is held for storage.
    /// </summary>
    [Fact]
    public async Task A_full_finance_volume_never_hides_younger_journal_files()
    {
        var host = await NewFilesHostAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IFileStorageSpaceProbe>(new FullFor("finance-full"))));
        BindFinanceTo(host, "finance-full");

        for (var i = 0; i < 30; i++)
        {
            await AddLegacyAsync(host, uploadedAt: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }

        var journalId = await AddLegacyAsync(host, uploadedAt: new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await LinkJournalAsync(host, journalId);

        var scan = await Job(host).NextCandidatesAsync(default);
        Assert.Equal([journalId], scan.Eligible.Select(e => e.FileId));
        Assert.Equal(30, scan.SkippedForHealth);

        await Job(host).RunOnceAsync(default);
        Assert.Equal(StoredFileArea.Journal, StoredFileAreas.AreaOf(await FileAsync(host, journalId)));
        Assert.DoesNotContain(await RunsAsync(host), r => r.Status == JobRunStatus.Held);

        await Job(host).RunOnceAsync(default);
        Assert.Contains(await RunsAsync(host), r => (r.Status, r.HeldReason) == (JobRunStatus.Held, JobHeldReason.StorageUnavailable));
    }

    /// <summary>AC 35: one tick never examines more than <see cref="FileBlobBackfillJob.ScanLimit"/> rows.</summary>
    [Fact]
    public async Task A_scan_examines_at_most_the_scan_limit()
    {
        var host = await NewFilesHostAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IFileStorageSpaceProbe>(new FullFor("finance-full"))));
        BindFinanceTo(host, "finance-full");
        await InContextAsync(host, async c =>
        {
            for (var i = 0; i < FileBlobBackfillJob.ScanLimit + 50; i++)
            {
                var blob = new FileBlob { Id = Guid.NewGuid(), Content = PdfBytes };
                c.FileBlob.Add(blob);
                c.FileMetadata.Add(new FileMetadata
                {
                    Id = Guid.NewGuid(), FileName = "a.pdf", ContentType = "application/pdf", SizeBytes = PdfBytes.LongLength,
                    Sha256Hash = Convert.ToHexString(SHA256.HashData(PdfBytes)).ToLowerInvariant(), FileBlobId = blob.Id,
                    UploadedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                });
            }

            await c.SaveChangesAsync();
        });

        var scan = await Job(host).NextCandidatesAsync(default);

        Assert.Empty(scan.Eligible);
        Assert.Equal(FileBlobBackfillJob.ScanLimit, scan.Examined);
    }

    /// <summary>With every area unhealthy, the scan reports the hold without classifying a single row.</summary>
    [Fact]
    public async Task With_every_area_below_the_reserve_nothing_is_examined_and_the_job_is_held()
    {
        var host = await NewFilesHostAsync();
        await AddLegacyAsync(host);
        host.Space.Override = (1024L * 1024 * 1024, 1024L * 1024 * 1024 * 1024);

        var scan = await Job(host).NextCandidatesAsync(default);
        Assert.Equal((0, 0), (scan.Eligible.Count, scan.Examined));
        Assert.True(scan.SkippedForHealth > 0);

        await Job(host).RunOnceAsync(default);
        var held = Assert.Single(await RunsAsync(host));
        Assert.Equal((JobRunStatus.Held, JobHeldReason.StorageUnavailable), (held.Status, held.HeldReason));
    }

    [Fact]
    public async Task A_storage_outage_parks_nothing_and_backs_off_until_the_delay_has_passed()
    {
        var store = new FaultyStore { PutFailure = () => new IOException("disk gone") };
        var host = await NewFilesHostAsync(configure: store.Install);
        var fileId = await AddLegacyAsync(host);

        await Job(host).RunOnceAsync(default);

        Assert.False(await InContextAsync(host, c => c.FileBlobBackfillFailures.AnyAsync()));
        var lease = await LeaseAsync(host);
        Assert.Equal(1, lease.ConsecutiveEnvironmentalFailures);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime + FileBlobBackfillJob.Backoff(1), lease.NextAttemptAtUtc);
        Assert.Null(lease.HolderId);

        store.PutFailure = null;
        await Job(host).RunOnceAsync(default);
        Assert.NotNull((await FileAsync(host, fileId)).FileBlobId);

        Clock.Advance(FileBlobBackfillJob.Backoff(1));
        await Job(host).RunOnceAsync(default);
        Assert.Null((await FileAsync(host, fileId)).FileBlobId);
        lease = await LeaseAsync(host);
        Assert.Equal((0, (DateTime?)null), (lease.ConsecutiveEnvironmentalFailures, lease.NextAttemptAtUtc));
    }

    [Fact]
    public async Task A_back_off_holds_the_process_and_a_manual_run_ends_it_and_records_the_move()
    {
        var store = new FaultyStore { PutFailure = () => new IOException("disk gone") };
        var host = await NewFilesHostAsync(configure: store.Install);
        var fileId = await AddLegacyAsync(host);
        await LinkJournalAsync(host, fileId);

        await Job(host).RunOnceAsync(default);
        store.PutFailure = null;
        await Job(host).RunOnceAsync(default);

        var runs = await RunsAsync(host);
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.Storage, 1), (runs[0].Status, runs[0].FailureCategory, runs[0].Interruptions));
        Assert.Equal((JobRunStatus.Held, JobHeldReason.BackingOff), (runs[1].Status, runs[1].HeldReason));

        var manualId = Guid.NewGuid();
        await InContextAsync(host, async c =>
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            c.JobRuns.Add(new FileBlobBackfillJobRun
            {
                JobRunId = manualId, Trigger = JobRunTrigger.Manual, Status = JobRunStatus.Running,
                StartedAtUtc = now, LastActivityAtUtc = now, Generation = Guid.NewGuid(),
            });
            await c.SaveChangesAsync();
        });

        await Job(host).RunOnceAsync(default);

        Assert.Null((await FileAsync(host, fileId)).FileBlobId);
        Assert.Null((await LeaseAsync(host)).NextAttemptAtUtc);
        var manual = (await RunsAsync(host)).Single(r => r.JobRunId == manualId);
        Assert.Equal((JobRunStatus.Succeeded, 1), (manual.Status, manual.JournalFilesMoved));
    }

    [Fact]
    public async Task An_unexpected_failure_parks_that_file_as_error_and_the_run_moves_on()
    {
        var store = new FaultyStore();
        var host = await NewFilesHostAsync(configure: store.Install);
        var older = await AddLegacyAsync(host, uploadedAt: new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var younger = await AddLegacyAsync(host, uploadedAt: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var calls = 0;
        store.PutFailure = () => calls++ == 0 ? new InvalidOperationException("store contract") : null;

        await Job(host).RunOnceAsync(default);

        Assert.Equal(FileBlobBackfillFailureCodes.Error, await ParkedCodeAsync(host, older));
        Assert.Null((await FileAsync(host, younger)).FileBlobId);
        Assert.Null((await LeaseAsync(host)).NextAttemptAtUtc);
    }

    [Fact]
    public async Task A_size_that_disagrees_with_the_bytes_is_parked_as_read()
    {
        var host = await NewFilesHostAsync();
        var fileId = await AddLegacyAsync(host, sizeBytes: PdfBytes.LongLength + 1);

        await Job(host).RunOnceAsync(default);

        Assert.Equal(FileBlobBackfillFailureCodes.Read, await ParkedCodeAsync(host, fileId));
        Assert.NotNull((await FileAsync(host, fileId)).FileBlobId);
    }

    [Fact]
    public async Task A_file_over_the_deploy_time_ceiling_is_parked_as_too_large()
    {
        var host = await NewFilesHostAsync();
        var fileId = await AddLegacyAsync(host, sizeBytes: 64L * 1024 * 1024 + 1);

        await Job(host).RunOnceAsync(default);

        Assert.Equal(FileBlobBackfillFailureCodes.TooLarge, await ParkedCodeAsync(host, fileId));
    }

    /// <summary>AC 27: what the move logs names files by id, never by name.</summary>
    [Fact]
    public async Task The_move_never_logs_a_file_name_or_path()
    {
        var host = await NewFilesHostAsync();
        var moved = await AddLegacyAsync(host, name: "Lønnsslipp-mars.pdf");
        await AddLegacyAsync(host, name: "Kontoutskrift-april.pdf", sha: new string('0', 64));

        await Job(host).RunOnceAsync(default);

        Assert.Null((await FileAsync(host, moved)).FileBlobId);
        var stored = await ObjectAsync(host, (await FileAsync(host, moved)).FinanceStoredObjectId!.Value);
        Assert.NotEmpty(host.Logs.Entries);
        Assert.All(host.Logs.Entries, e =>
        {
            Assert.DoesNotContain("Lønnsslipp", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Kontoutskrift", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(stored.RelativePath, e.Message, StringComparison.Ordinal);
        });
    }

    private void BindFinanceTo(FileStorageTestHost host, string volume)
    {
        var volumes = host.Services.GetRequiredService<FileStorageVolumeRegistry>();
        var root = Path.Combine(Base.FullName, "volumes", volume);
        Directory.CreateDirectory(root);
        volumes.Activate(
            [(Guid.NewGuid(), volume, root)],
            new Dictionary<string, string>(volumes.Bindings) { [StoredFileAreas.FinanceFeatureKey] = volume });
    }

    [Fact]
    public async Task Every_key_to_file_metadata_is_mapped_to_an_area()
    {
        await using var context = TestContextFactory.Create();
        var references = FileReferenceCounter.Discover(context);
        Assert.NotEmpty(references);
        Assert.All(references, r => Assert.True(
            FileAreaClassifier.AreaOfReference.ContainsKey(r.EntityType),
            $"{r.EntityType.Name}.{r.PropertyName} references FileMetadata but has no area in FileAreaClassifier.AreaOfReference."));
        // And no mapping outlives its key.
        Assert.All(FileAreaClassifier.AreaOfReference.Keys, t => Assert.Contains(references, r => r.EntityType == t));
        // The failure table carries no key (issue #448 §4.4), so a parked file never counts as "used elsewhere".
        Assert.DoesNotContain(references, r => r.EntityType == typeof(FileBlobBackfillFailure));
    }

    [Fact]
    public void Every_allowed_upload_type_has_an_extension_on_disk()
    {
        Assert.All(FileValidationService.GetDefaultAllowedMimeTypes(), type =>
            Assert.True(StorageNames.IsValidExtension(ContentTypeExtensions.For(type)), $"{type} has no extension mapping."));
    }

    [Theory]
    [InlineData(new StoredFileArea[0], "application/pdf", StoredFileArea.Finance)]
    [InlineData(new[] { StoredFileArea.ContactAvatars }, "image/png", StoredFileArea.ContactAvatars)]
    [InlineData(new[] { StoredFileArea.ContactAvatars }, "application/pdf", StoredFileArea.Finance)]
    [InlineData(new[] { StoredFileArea.ContactAvatars, StoredFileArea.Journal }, "image/png", StoredFileArea.Journal)]
    [InlineData(new[] { StoredFileArea.Journal, StoredFileArea.Finance }, "application/pdf", StoredFileArea.Finance)]
    public void The_classification_rule(StoredFileArea[] links, string contentType, StoredFileArea expected) =>
        Assert.Equal(expected, FileAreaClassifier.Classify(links, contentType));

    /// <summary>Reports a full disk for one volume root, plenty for every other.</summary>
    private sealed class FullFor(string name) : IFileStorageSpaceProbe
    {
        public (long Free, long Total)? Measure(string rootPath) =>
            rootPath.EndsWith(name, StringComparison.Ordinal) ? (0, 1L << 40) : (500L << 30, 1L << 40);
    }
}
