using Odyssey.Core;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
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
        var photoId = (await photos.UploadAsync(PhotoTestData.Jpeg(Random.Shared.Next()))).PhotoId;
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
        var photoId = (await photos.UploadAsync(PhotoTestData.Jpeg(Random.Shared.Next()))).PhotoId;
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
        var datedId = (await photoHost.UploadAsync(PhotoTestData.Jpeg(Random.Shared.Next()), new NewPhotoUpload { TakenAt = new DateTime(2020, 1, 1) })).PhotoId;
        var undatedId = (await photoHost.UploadAsync(PhotoTestData.Jpeg(Random.Shared.Next()))).PhotoId;

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
    [SkippableFact]
    public async Task Deleting_a_photo_also_deletes_the_file_it_wraps()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        var provider = photos.Host;

        Guid photoId, fileId, blobId;
        await using (var scope = provider.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var (photo, file) = PhotoTestData.StageLegacyPhoto(context, userId: "user");
            await context.SaveChangesAsync();
            (photoId, fileId, blobId) = (photo.PhotoId, file.Id, file.FileBlobId);
        }

        await using (var scope = provider.Scope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<PhotoService>().Delete(photoId));
        }

        await using (var scope = provider.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.False(await context.Photos.AnyAsync(p => p.PhotoId == photoId));

            // The blob too, not just the metadata row: the FK runs metadata -> blob, so removing the
            // metadata alone would leave the bytes orphaned and unreachable rather than reclaimed.
            Assert.False(await context.FileMetadata.AnyAsync(f => f.Id == fileId));
            Assert.Equal(0, await context.FileBlob.CountAsync(b => b.Id == blobId));
        }
    }

    /// <summary>
    /// The case that makes the symmetric delete safe to have. Nine tables carry a cascading foreign key
    /// to <c>FileMetadata</c>, so deleting the file as a side effect of deleting a photo would silently
    /// strip that file off a transaction or a journal entry that also holds it — the database would
    /// oblige without complaint. The guard turns that into a 409 naming the other holder, and nothing is
    /// deleted: not the file, and not the photo either.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_photo_whose_file_is_attached_elsewhere_is_refused_and_deletes_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var photos = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        var provider = photos.Host;

        Guid photoId, fileId;
        var entryId = Guid.NewGuid();
        await using (var scope = provider.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var (photo, file) = PhotoTestData.StageLegacyPhoto(context, userId: "user");
            await context.SaveChangesAsync();
            (photoId, fileId) = (photo.PhotoId, file.Id);

            // The same file, also attached to a journal entry.
            context.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = entryId,
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                Title = "Holds the same file",
                Content = "holds",
                EntryDate = DateTime.UtcNow,
                CreatedByUserId = "user",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();

            context.JournalEntryAttachments.Add(new JournalEntryAttachment
            {
                JournalEntryAttachmentId = Guid.NewGuid(),
                JournalEntryId = entryId,
                FileId = fileId,
                CreatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using (var scope = provider.Scope())
        {
            var error = await Assert.ThrowsAsync<DomainConflictException>(
                () => scope.ServiceProvider.GetRequiredService<PhotoService>().Delete(photoId));

            // The message has to name the other holder, or the caller cannot act on the refusal.
            Assert.Contains("journal-entry attachment", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using (var scope = provider.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.True(await context.Photos.AnyAsync(p => p.PhotoId == photoId));
            Assert.True(await context.FileMetadata.AnyAsync(f => f.Id == fileId));
            Assert.True(await context.JournalEntryAttachments.AnyAsync(a => a.FileId == fileId));
        }
    }
}
