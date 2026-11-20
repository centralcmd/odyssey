using Odyssey.Core;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Photos-module behaviour EF InMemory cannot verify (issue #321), now exercised through the merged
/// <see cref="OdysseyContext"/>: the album cover FK <c>SET NULL</c> + membership cascade when a photo is
/// deleted (§16.7), the concurrent keyword→tag insert race where the loser's duplicate-key must be caught
/// and re-fetched rather than surfacing as a 409 (§16.3e), and the finalizing migration dropping a
/// residual null journal-photo link.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class PhotoRelationalIntegrationTests(MariaDbFixture fixture)
{
    [SkippableFact]
    public async Task Deleting_a_cover_photo_nulls_the_cover_and_cascades_membership()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        var provider = photos.Host;

        Guid albumId;
        var photoId = (await photos.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()))).PhotoId;
        await using (var scope = provider.Scope())
        {
            var albums = scope.ServiceProvider.GetRequiredService<PhotoAlbumService>();
            var album = await albums.Create(new NewPhotoAlbum { Name = "Trip", PhotoIds = [photoId], CoverPhotoId = photoId }, "user");
            albumId = album.PhotoAlbumId;
        }

        await using (var scope = provider.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<PhotoService>().Delete(photoId);
        }

        await using (var scope = provider.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var album = await context.PhotoAlbums.FirstAsync(a => a.PhotoAlbumId == albumId);
            Assert.Null(album.CoverPhotoId); // FK SET NULL
            Assert.False(await context.PhotoAlbumItems.AnyAsync(i => i.PhotoAlbumId == albumId)); // cascaded
        }
    }

    [SkippableFact]
    public async Task Deleting_a_photo_cascades_its_journal_entry_links()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        var provider = photos.Host;

        // A library photo linked from a journal entry. With the merged context the link is a real FK
        // (Cascade), so deleting the photo must sweep the JournalEntryPhotos row.
        var photoId = (await photos.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()))).PhotoId;
        await using (var scope = provider.Scope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var entryId = Guid.NewGuid();
            var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            journal.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = entryId,
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                Title = "Linked",
                Content = "c",
                EntryDate = now,
                CreatedByUserId = "u",
                CreatedAt = now,
                UpdatedAt = now,
            });
            journal.JournalEntryPhotos.Add(new JournalEntryPhoto
            {
                JournalEntryPhotoId = Guid.NewGuid(),
                JournalEntryId = entryId,
                PhotoId = photoId,
                Position = 0,
                CreatedAt = now,
            });
            await journal.SaveChangesAsync();
        }

        await using (var scope = provider.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<PhotoService>().Delete(photoId);
        }

        await using (var scope = provider.Scope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.False(await journal.JournalEntryPhotos.AnyAsync(p => p.PhotoId == photoId)); // FK cascade
        }
    }

    [SkippableFact]
    public async Task Concurrent_adds_with_the_same_new_keyword_create_exactly_one_tag()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(
            fixture.OdysseyConnectionString, new FixedMetadataExtractor(new PhotoMetadata { Keywords = ["RaceTag"] }));

        // Different bytes, so the duplicate check does not refuse the second (issue #428).
        var results = await Task.WhenAll(
            photos.UploadAsync(PhotoTestData.Jpeg(1), canAutoCreateTags: true),
            photos.UploadAsync(PhotoTestData.Jpeg(2), canAutoCreateTags: true));

        await using var assertScope = photos.Host.Scope();
        var context = assertScope.ServiceProvider.GetRequiredService<OdysseyContext>();

        // Exactly one tag, no duplicate, no bubbled 409 — both photos linked to the single winner.
        var tags = await context.PhotoTags.Where(t => t.Name == "RaceTag").ToListAsync();
        Assert.Single(tags);
        Assert.All(results, photo => Assert.Contains(tags[0].PhotoTagId, photo.TagIds));
    }

    [SkippableFact]
    public async Task List_sorted_by_TakenAt_keeps_undated_photos_last_in_both_directions()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photoHost = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        var datedId = (await photoHost.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), new NewPhotoUpload { TakenAt = new DateTime(2020, 1, 1) })).PhotoId;
        var undatedId = (await photoHost.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()))).PhotoId;

        async Task<List<Guid>> ListByTakenAtAsync(SortDirection dir)
        {
            await using var scope = photoHost.Host.Scope();
            var photos = scope.ServiceProvider.GetRequiredService<PhotoService>();
            var result = await photos.ListAsync(new PhotosQueryParams { SortBy = PhotoSortBy.TakenAt, SortDir = dir, Limit = 1000 });
            return [.. result.Items.Select(i => i.PhotoId)];
        }

        // Every dated photo precedes every undated one, so the dated id always sorts ahead of the undated
        // id — in both directions (raw MariaDB would lead NULLs on ASC without the explicit nulls-last).
        var asc = await ListByTakenAtAsync(SortDirection.Asc);
        var desc = await ListByTakenAtAsync(SortDirection.Desc);
        Assert.True(asc.IndexOf(datedId) < asc.IndexOf(undatedId));
        Assert.True(desc.IndexOf(datedId) < desc.IndexOf(undatedId));
    }

    /// <summary>
    /// A LEGACY library photo (issue #428: created before photos owned their bytes) and the file it wraps are one
    /// thing, deleted together in both directions. The
    /// file-to-photo half is a foreign key (proved in <c>RelationalIntegrationTests</c>); this is the
    /// half that lives in <c>PhotoService</c>, and it reverses the original decision to leave the blob
    /// behind — which had left a photo delete and a file delete disagreeing about which object was the
    /// durable one.
    /// </summary>
    /// <summary>PR #441 security finding 1, on MariaDB: the Files store's visibility filter translates and holds.</summary>
    [SkippableFact]
    public async Task The_files_store_hides_a_former_file_only_a_photo_names_and_shows_a_shared_one_on_MariaDB()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        var stem = $"vis-{Guid.NewGuid():N}";
        var photoOnly = await StageWithFormerFileAsync(photos, PhotoTestData.Jpeg(PhotoTestData.NextSeed()), $"{stem}-a.jpg");
        var shared = await StageWithFormerFileAsync(photos, PhotoTestData.Jpeg(PhotoTestData.NextSeed()), $"{stem}-b.jpg");
        await AttachAsync(photos, shared);

        await using var scope = photos.Host.Scope();
        var files = new FileService(scope.ServiceProvider.GetRequiredService<OdysseyContext>(), new FileValidationService());
        var listed = await files.ListAsync(new Odyssey.Dtos.Finance.FilesQueryParams { Search = stem });

        Assert.Equal([shared], listed.Items.Select(f => f.Id));
        Assert.Null(await files.GetFileMetadataAsync(photoOnly));
        Assert.NotNull(await files.GetFileMetadataAsync(shared));
    }

    /// <summary>A photo on the store whose former file, kept in the Files store, it names as its <c>LegacyFileId</c>.</summary>
    private static async Task<Guid> StageWithFormerFileAsync(PhotoStoreHost photos, byte[] bytes, string fileName)
    {
        await using var scope = photos.Host.Scope();
        var file = await StoredFileFixtures.StageFileAsync(
            scope.ServiceProvider, bytes, fileName, "image/jpeg", Odyssey.Core.FileStorage.Files.StoredFileArea.Journal, uploadedByUserId: "user");
        var photo = await StoredFileFixtures.StagePhotoAsync(scope.ServiceProvider, bytes, fileName, userId: "user");
        photo.LegacyFileId = file.Id;
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().SaveChangesAsync();
        return file.Id;
    }

    private static async Task<Guid> NewEntryAsync(PhotoStoreHost photos)
    {
        await using var scope = photos.Host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var entry = new JournalEntry
        {
            JournalEntryId = Guid.NewGuid(), ExternalUid = Guid.NewGuid().ToString(), Title = "t", Content = "c",
            EntryDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        context.JournalEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry.JournalEntryId;
    }

    private static async Task AttachAsync(PhotoStoreHost photos, Guid fileId)
    {
        var entryId = await NewEntryAsync(photos);
        await using var scope = photos.Host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.JournalEntryAttachments.Add(new JournalEntryAttachment { JournalEntryId = entryId, FileId = fileId, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
    }
}
