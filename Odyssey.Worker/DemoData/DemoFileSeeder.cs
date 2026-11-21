using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal.Originals;
using Odyssey.TestData;

namespace Odyssey.Worker.DemoData;

/// <summary>
/// Writes the demo dataset's files and photos onto the object store and inserts the rows that name them (the half of
/// the demo seed with bytes). The migrations job seeds everything else, but it has no file volumes, and only a host with
/// them can put a file on the store — so this runs in the worker, once the API has initialised the volumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent per record, and safe on every replica.</b> Every row has a deterministic id, so a pass skips what is
/// already there and inserts only what is missing — the shape the photo library seed always had. Two replicas racing
/// on one file both write the bytes, and the loser's save fails on the primary key; it then finds the winner's row and
/// moves on, and its orphaned reservation is removed by the abandoned-write sweep. No process state coordinates them.
/// </para>
/// <para>
/// A file is written and saved together with its links (a tax-statement document, a journal-entry or task attachment, a
/// contact's image), and only when the record it is attached to exists: every link is a real foreign key, and a
/// record the migrations job did not seed (its seed turned off, or skipped by a sentinel) gets no file.
/// </para>
/// </remarks>
public sealed class DemoFileSeeder(IServiceScopeFactory scopes, ILogger<DemoFileSeeder> logger)
{
    /// <summary>
    /// One pass. <paramref name="data"/> must be freshly built (<see cref="DemoDataSet.Build"/>): its entities are added
    /// to the pass's contexts as they are, so a set must never be handed to a second pass.
    /// </summary>
    public async Task SeedAsync(DemoDataSet data, CancellationToken cancellationToken)
    {
        var files = 0;
        foreach (var file in data.Files)
        {
            if (await SeedFileAsync(data, file, cancellationToken))
            {
                files++;
            }
        }

        var photos = await SeedPhotosAsync(data, cancellationToken);
        logger.LogInformation("Seeded {Files} demo files and {Photos} demo photos onto the file store.", files, photos);
    }

    private async Task<bool> SeedFileAsync(DemoDataSet data, DemoFile file, CancellationToken cancellationToken)
    {
        var id = file.Metadata.Id;
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        if (await context.FileMetadata.AnyAsync(f => f.Id == id, cancellationToken))
        {
            return false;
        }

        var taxLinks = await ExistingAsync(data.TaxStatementFiles.Where(l => l.FileMetadataId == id),
            l => context.TaxStatements.AnyAsync(t => t.TaxStatementId == l.TaxStatementId, cancellationToken));
        var entryLinks = await ExistingAsync(data.JournalEntryAttachments.Where(l => l.FileId == id),
            l => context.JournalEntries.AnyAsync(e => e.JournalEntryId == l.JournalEntryId, cancellationToken));
        var taskLinks = await ExistingAsync(data.JournalTaskAttachments.Where(l => l.FileId == id),
            l => context.JournalTasks.AnyAsync(t => t.JournalTaskId == l.JournalTaskId, cancellationToken));
        var avatarOwners = data.ContactAvatars.Where(a => a.File.Metadata.Id == id).Select(a => a.ContactId).ToList();
        var contacts = await context.Contacts
            .Where(c => avatarOwners.Contains(c.ContactId) && c.AvatarFileId == null)
            .ToListAsync(cancellationToken);
        if (taxLinks.Count + entryLinks.Count + taskLinks.Count + contacts.Count == 0)
        {
            // Nothing to attach it to: the record it belongs to was never seeded.
            return false;
        }

        var writer = scope.ServiceProvider.GetRequiredService<StoredFileWriter>();
        var row = file.Metadata;
        using (var content = new MemoryStream(file.Content, writable: false))
        {
            var stored = await writer.PutAsync(
                file.Area, content, row.FileName, row.ContentType, row.UploadedAtUtc, file.Content.LongLength, cancellationToken);
            StoredFileAreas.Assign(row, file.Area, stored.Id);
        }

        context.FileMetadata.Add(row);
        context.TaxStatementFiles.AddRange(taxLinks);
        context.JournalEntryAttachments.AddRange(entryLinks);
        context.JournalTaskAttachments.AddRange(taskLinks);
        foreach (var contact in contacts)
        {
            contact.AvatarFileId = id;
        }

        return await SaveOrLoseRaceAsync(context, () => context.FileMetadata.AnyAsync(f => f.Id == id, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The photo library: each missing photo written with its original, then the tags, albums, tag and person links,
    /// album items and journal-entry placements that are missing (the record-level shape the migrations job's photo
    /// seed had). A demo photo is recognised by its id or, on a database seeded while photos still wrapped a file, by
    /// that file's id, now its <see cref="Photo.LegacyFileId"/>.
    /// </summary>
    private async Task<int> SeedPhotosAsync(DemoDataSet data, CancellationToken cancellationToken)
    {
        var written = 0;
        foreach (var demo in data.Photos)
        {
            if (await SeedPhotoAsync(demo, cancellationToken))
            {
                written++;
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var photoIds = (await context.Photos.Select(p => p.PhotoId).ToListAsync(cancellationToken)).ToHashSet();
        var tagNames = (await context.PhotoTags.Select(t => t.Name).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagIds = (await context.PhotoTags.Select(t => t.PhotoTagId).ToListAsync(cancellationToken)).ToHashSet();
        var albumIds = (await context.PhotoAlbums.Select(a => a.PhotoAlbumId).ToListAsync(cancellationToken)).ToHashSet();
        var contactIds = (await context.Contacts.Select(c => c.ContactId).ToListAsync(cancellationToken)).ToHashSet();
        var entryIds = (await context.JournalEntries.Select(e => e.JournalEntryId).ToListAsync(cancellationToken)).ToHashSet();
        var links = (await context.PhotoTagLinks.Select(l => new { l.PhotoTagLinkId, l.PhotoId, l.PhotoTagId }).ToListAsync(cancellationToken));
        var people = (await context.PhotoPeople.Select(p => new { p.PhotoPersonId, p.PhotoId, p.ContactId }).ToListAsync(cancellationToken));
        var items = (await context.PhotoAlbumItems.Select(i => new { i.PhotoAlbumItemId, i.PhotoAlbumId, i.PhotoId }).ToListAsync(cancellationToken));
        var placements = (await context.JournalEntryPhotos.Select(p => new { p.JournalEntryPhotoId, p.JournalEntryId, p.PhotoId }).ToListAsync(cancellationToken));

        // The surrogate key is what the database enforces, and on a database seeded by an older generator the pair and
        // the key can disagree (a contact id changed): skip anything whose key or pair is already taken.
        var newTags = data.PhotoTags.Where(t => !tagNames.Contains(t.Name) && !tagIds.Contains(t.PhotoTagId)).ToList();
        var newAlbums = data.PhotoAlbums.Where(a => !albumIds.Contains(a.PhotoAlbumId)).ToList();
        var presentTags = tagIds.Concat(newTags.Select(t => t.PhotoTagId)).ToHashSet();
        var presentAlbums = albumIds.Concat(newAlbums.Select(a => a.PhotoAlbumId)).ToHashSet();
        foreach (var album in newAlbums.Where(a => a.CoverPhotoId is { } cover && !photoIds.Contains(cover)))
        {
            album.CoverPhotoId = null;
        }

        var newLinks = data.PhotoTagLinks
            .Where(l => photoIds.Contains(l.PhotoId) && presentTags.Contains(l.PhotoTagId)
                        && !links.Any(x => x.PhotoTagLinkId == l.PhotoTagLinkId || (x.PhotoId == l.PhotoId && x.PhotoTagId == l.PhotoTagId)))
            .ToList();
        var newPeople = data.PhotoPeople
            .Where(p => photoIds.Contains(p.PhotoId) && contactIds.Contains(p.ContactId)
                        && !people.Any(x => x.PhotoPersonId == p.PhotoPersonId || (x.PhotoId == p.PhotoId && x.ContactId == p.ContactId)))
            .ToList();
        var newItems = data.PhotoAlbumItems
            .Where(i => presentAlbums.Contains(i.PhotoAlbumId) && photoIds.Contains(i.PhotoId)
                        && !items.Any(x => x.PhotoAlbumItemId == i.PhotoAlbumItemId || (x.PhotoAlbumId == i.PhotoAlbumId && x.PhotoId == i.PhotoId)))
            .ToList();
        var newPlacements = data.JournalEntryPhotos
            .Where(p => entryIds.Contains(p.JournalEntryId) && photoIds.Contains(p.PhotoId)
                        && !placements.Any(x => x.JournalEntryPhotoId == p.JournalEntryPhotoId || (x.JournalEntryId == p.JournalEntryId && x.PhotoId == p.PhotoId)))
            .ToList();

        if (newTags.Count + newAlbums.Count + newLinks.Count + newPeople.Count + newItems.Count + newPlacements.Count > 0)
        {
            context.PhotoTags.AddRange(newTags);
            context.PhotoAlbums.AddRange(newAlbums);
            context.PhotoTagLinks.AddRange(newLinks);
            context.PhotoPeople.AddRange(newPeople);
            context.PhotoAlbumItems.AddRange(newItems);
            context.JournalEntryPhotos.AddRange(newPlacements);
            // A replica that raced this save to the same rows fails it; the next pass finds them present.
            await context.SaveChangesAsync(cancellationToken);
        }

        return written;
    }

    private async Task<bool> SeedPhotoAsync(DemoPhoto demo, CancellationToken cancellationToken)
    {
        var id = demo.Photo.PhotoId;
        var formerFileId = demo.FormerFileId;
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Task<bool> Present() => context.Photos.AnyAsync(p => p.PhotoId == id || p.LegacyFileId == formerFileId, cancellationToken);
        if (await Present())
        {
            return false;
        }

        var photo = demo.Photo;
        var writer = scope.ServiceProvider.GetRequiredService<PhotoOriginalWriter>();
        using (var content = new MemoryStream(demo.Content, writable: false))
        {
            var stored = await writer.PutAsync(
                content, photo.FileName, photo.ContentType, photo.CreatedAt, demo.Content.LongLength, cancellationToken);
            photo.StoredObjectId = stored.Id;
        }

        context.Photos.Add(photo);
        return await SaveOrLoseRaceAsync(context, Present, cancellationToken);
    }

    /// <summary>Saves; a failure after which the row exists is a race another replica won, and reads as nothing written.</summary>
    private static async Task<bool> SaveOrLoseRaceAsync(OdysseyContext context, Func<Task<bool>> present, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (await present())
            {
                return false;
            }

            throw;
        }
    }

    private static async Task<List<T>> ExistingAsync<T>(IEnumerable<T> links, Func<T, Task<bool>> principalExists)
    {
        var present = new List<T>();
        foreach (var link in links)
        {
            if (await principalExists(link))
            {
                present.Add(link);
            }
        }

        return present;
    }
}
