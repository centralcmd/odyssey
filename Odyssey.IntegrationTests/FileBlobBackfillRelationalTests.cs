using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The file move (issue #448) on real MariaDB, where EF InMemory cannot reach: the four-source CHECK, the failure table's
/// lack of a key, the commit's row lock against a racing attach, the blob removal not cascading the file away, and two
/// runners moving each file once.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class FileBlobBackfillRelationalTests(MariaDbFixture fixture)
{
    private static readonly byte[] Pdf = "%PDF-1.7 relational move"u8.ToArray();

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString)).Options);

    /// <summary>AC 23: exactly one of FileBlobId and the three stored-object columns is set.</summary>
    [SkippableFact]
    public async Task The_check_refuses_a_file_with_no_source_or_two()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var context = NewContext();

        var none = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO `FileMetadata` (`Id`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `UploadedAtUtc`)
            VALUES ({Guid.NewGuid()}, 'a.pdf', 'application/pdf', 3, 'abc', {DateTime.UtcNow})
            """));
        Assert.Equal(4025, none.Number);

        var blobId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO `FileBlob` (`Id`, `Content`) VALUES ({blobId}, {Pdf})");
        var stored = await StoreOneAsync(photos);
        var both = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO `FileMetadata` (`Id`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `FileBlobId`, `FinanceStoredObjectId`, `UploadedAtUtc`)
            VALUES ({Guid.NewGuid()}, 'a.pdf', 'application/pdf', 3, 'abc', {blobId}, {stored}, {DateTime.UtcNow})
            """));
        Assert.Equal(4025, both.Number);
    }

    /// <summary>AC 31: the failure table carries no foreign key, so a parked file is never "used elsewhere".</summary>
    [SkippableFact]
    public async Task The_failure_table_has_no_foreign_key()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var context = NewContext();

        var keys = await context.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS `Value` FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'FileBlobBackfillFailures' AND REFERENCED_TABLE_NAME IS NOT NULL
            """).SingleAsync();

        Assert.Equal(0, keys);
    }

    /// <summary>AC 17/20: the move nulls the key and removes the blob in one save; the file and its link survive.</summary>
    [SkippableFact]
    public async Task A_move_removes_the_blob_without_cascading_the_file_away()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        var fileId = await StageLegacyAsync();
        var entryId = await NewEntryAsync();
        await AttachAsync(fileId, entryId);

        var outcome = await Job(photos).MoveOneAsync(fileId, StoredFileArea.Journal, default);

        Assert.Equal(FileBackfillOutcome.MovedJournal, outcome);
        await using var context = NewContext();
        var file = await context.FileMetadata.AsNoTracking().SingleAsync(f => f.Id == fileId);
        Assert.Null(file.FileBlobId);
        Assert.NotNull(file.JournalStoredObjectId);
        Assert.True(await context.JournalEntryAttachments.AnyAsync(a => a.FileId == fileId));
        await using var scope = photos.Host.Scope();
        var content = await scope.ServiceProvider.GetRequiredService<StoredFileContentSource>().ReadAllAsync(fileId, long.MaxValue, default);
        Assert.Equal(Pdf, content!.Value.Bytes);
    }

    /// <summary>
    /// AC 21, §3.5 step 7: an attach that starts after the commit has locked the file row waits for the commit, then lands on
    /// the moved file — never on a row the move is changing underneath it.
    /// </summary>
    [SkippableFact]
    public async Task An_attach_racing_the_commit_waits_for_it_and_lands_on_the_moved_file()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        Task? attach = null;
        var entryId = Guid.Empty;
        bool? attachWaitedForTheLock = null;
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString, configureServices: services =>
            services.Replace(ServiceDescriptor.Scoped<IFileReferenceCounter>(sp => new RacingCounter(
                new FileReferenceCounter(sp.GetRequiredService<OdysseyContext>()),
                id => attach = Task.Run(() => AttachAsync(id, entryId)),
                () => attachWaitedForTheLock = attach is { IsCompleted: false }))));
        var fileId = await StageLegacyAsync();
        entryId = await NewEntryAsync();
        await AttachAsync(fileId, await NewEntryAsync());

        var outcome = await Job(photos).MoveOneAsync(fileId, StoredFileArea.Journal, default);

        Assert.Equal(FileBackfillOutcome.MovedJournal, outcome);
        // Probed while the commit still held the row: an attach not blocked by the lock finishes well inside the pause, so a
        // slow runner can only make this pass for the wrong reason, never fail spuriously.
        Assert.True(attachWaitedForTheLock, "The attach completed while the commit held the file row.");
        await attach!;
        await using var context = NewContext();
        Assert.Equal(2, await context.JournalEntryAttachments.CountAsync(a => a.FileId == fileId));
        Assert.NotNull((await context.FileMetadata.AsNoTracking().SingleAsync(f => f.Id == fileId)).JournalStoredObjectId);
    }

    /// <summary>AC 21: two runners at once move each file exactly once.</summary>
    [SkippableFact]
    public async Task Two_runners_move_each_file_once()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        // The shared database can hold other classes' legacy files, so these are the oldest by far (they move first)
        // and carry unique bytes, so their hashes name only their own objects.
        var ids = new List<Guid>();
        var hashes = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var bytes = Pdf.Concat(Guid.NewGuid().ToByteArray()).ToArray();
            hashes.Add(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            ids.Add(await StageLegacyAsync(bytes, new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i)));
        }

        await Task.WhenAll(Job(photos).RunOnceAsync(default), Job(photos).RunOnceAsync(default));
        for (var pass = 0; pass < 5; pass++)
        {
            await using var check = NewContext();
            if (!await check.FileMetadata.AnyAsync(f => ids.Contains(f.Id) && f.FileBlobId != null))
            {
                break;
            }

            await Job(photos).RunOnceAsync(default);
        }

        await using var context = NewContext();
        var files = await context.FileMetadata.AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync();
        Assert.All(files, f => Assert.Null(f.FileBlobId));
        var objectIds = files.Select(f => f.FinanceStoredObjectId!.Value).ToList();
        Assert.Equal(4, objectIds.Distinct().Count());
        // Each file's bytes were committed exactly once: a losing runner's copy is never committed.
        Assert.Equal(4, await context.StoredObjects.CountAsync(o =>
            hashes.Contains(o.Sha256Hash!) && o.State == StoredObjectState.Committed));
    }

    private static FileBlobBackfillJob Job(PhotoStoreHost photos) =>
        ActivatorUtilities.CreateInstance<FileBlobBackfillJob>(photos.Host.Services);

    private async Task<Guid> StageLegacyAsync(byte[]? bytes = null, DateTime? uploadedAt = null)
    {
        bytes ??= Pdf;
        await using var context = NewContext();
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = bytes };
        var file = new FileMetadata
        {
            Id = Guid.NewGuid(), FileName = "Lønnsslipp.pdf", ContentType = "application/pdf", SizeBytes = bytes.LongLength,
            Sha256Hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), FileBlobId = blob.Id,
            UploadedAtUtc = uploadedAt ?? new DateTime(2021, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(file);
        await context.SaveChangesAsync();
        return file.Id;
    }

    private static async Task<Guid> StoreOneAsync(PhotoStoreHost photos)
    {
        await using var scope = photos.Host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var file = await scope.ServiceProvider.GetRequiredService<StoredFileWriter>().StageNewFileAsync(
            context, StoredFileArea.Finance, new MemoryStream(Pdf), "a.pdf", "application/pdf", null, null, 1024, default);
        await context.SaveChangesAsync();
        return file.FinanceStoredObjectId!.Value;
    }

    private async Task<Guid> NewEntryAsync()
    {
        await using var context = NewContext();
        var entry = new JournalEntry
        {
            JournalEntryId = Guid.NewGuid(), ExternalUid = Guid.NewGuid().ToString(), Title = "t", Content = "c",
            EntryDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        context.JournalEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry.JournalEntryId;
    }

    private async Task AttachAsync(Guid fileId, Guid entryId)
    {
        await using var context = NewContext();
        context.JournalEntryAttachments.Add(new JournalEntryAttachment { JournalEntryId = entryId, FileId = fileId, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
    }

    /// <summary>Starts a competing attach right after the commit's re-classification reads the links, then gives it a second to reach the lock.</summary>
    private sealed class RacingCounter(IFileReferenceCounter inner, Action<Guid> startRace, Action probe) : IFileReferenceCounter
    {
        private bool started;

        public IReadOnlyList<FileReference> References => inner.References;

        public Task<bool> HasOtherReferencesAsync(Guid fileId, CancellationToken cancellationToken) =>
            inner.HasOtherReferencesAsync(fileId, cancellationToken);

        public async Task<IReadOnlyList<FileReference>> ReferencesNamingAsync(Guid fileId, CancellationToken cancellationToken)
        {
            var result = await inner.ReferencesNamingAsync(fileId, cancellationToken);
            if (!started)
            {
                started = true;
                startRace(fileId);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                probe();
            }

            return result;
        }
    }
}
