using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
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
        DateTime? uploadedAt = null, string? sha = null) =>
        InContextAsync(host, async c =>
        {
            bytes ??= PdfBytes;
            var blob = new FileBlob { Id = Guid.NewGuid(), Content = bytes };
            var file = new FileMetadata
            {
                Id = Guid.NewGuid(), FileName = name, ContentType = contentType, SizeBytes = bytes.LongLength,
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
    [InlineData("journal", "application/pdf", StoredFileArea.Journal)]
    [InlineData("journal+avatar", "image/jpeg", StoredFileArea.Journal)]
    [InlineData("journal+finance", "application/pdf", StoredFileArea.Finance)]
    [InlineData("none", "application/pdf", StoredFileArea.Finance)]
    [InlineData("avatar", "image/jpeg", StoredFileArea.ContactAvatars)]
    [InlineData("avatar", "application/pdf", StoredFileArea.Finance)]
    public async Task Each_file_is_filed_by_its_links(string links, string contentType, StoredFileArea expected)
    {
        var host = await NewFilesHostAsync();
        var bytes = contentType == "image/jpeg" ? PhotoTestData.Jpeg(3) : PdfBytes;
        var fileId = await AddLegacyAsync(host, bytes, contentType, contentType == "image/jpeg" ? "a.jpg" : "a.pdf");
        if (links.Contains("journal")) await LinkJournalAsync(host, fileId);
        if (links.Contains("finance")) await LinkTransactionAsync(host, fileId);
        if (links.Contains("avatar")) await LinkAvatarAsync(host, fileId);

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

    [Fact]
    public async Task A_full_finance_volume_never_hides_younger_journal_files()
    {
        var host = await NewFilesHostAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IFileStorageSpaceProbe>(new FullFor("finance-full"))));
        // FinanceFiles on its own volume, which reports no free space.
        await InContextAsync(host, _ => Task.CompletedTask);
        var volumes = host.Services.GetRequiredService<FileStorageVolumeRegistry>();
        var primary = volumes.Find("primary")!;
        Directory.CreateDirectory(Path.Combine(Base.FullName, "volumes", "finance-full"));
        volumes.Activate(
            [(Guid.NewGuid(), "finance-full", Path.Combine(Base.FullName, "volumes", "finance-full"))],
            new Dictionary<string, string>(volumes.Bindings) { [StoredFileAreas.FinanceFeatureKey] = "finance-full" });
        _ = primary;

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
