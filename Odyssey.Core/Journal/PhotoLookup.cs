using Odyssey.Core;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Context;

namespace Odyssey.Core.Journal;

/// <summary>
/// The <see cref="IPhotoLookup"/> implementation. Owns <see cref="OdysseyContext"/>; validates image ids
/// through the Finance <see cref="IFileLookup"/> so the journal path cannot mint a <c>Photo</c> over a
/// non-image file. The find-or-create runs <b>scalar</b> metadata extraction only — it never
/// auto-creates keyword tags (that is gated on <c>photos.tags.create</c> and only happens on the
/// explicit <c>POST /api/photos</c> path, §5/§10.6).
/// </summary>
public sealed class PhotoLookup(
    OdysseyContext context,
    IFileLookup files,
    PhotoMetadataService metadata,
    TimeProvider? timeProvider = null) : IPhotoLookup
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default)
    {
        if (photoIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = photoIds.Distinct().ToList();
        var found = await context.Photos
            .Where(p => ids.Contains(p.PhotoId))
            .Select(p => p.PhotoId)
            .ToListAsync(cancellationToken);

        return found.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> ResolveFileIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default)
    {
        if (photoIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        var ids = photoIds.Distinct().ToList();
        var rows = await context.Photos
            .Where(p => ids.Contains(p.PhotoId))
            .Select(p => new { p.PhotoId, p.FileId })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.PhotoId, r => r.FileId);
    }

    public async Task<Guid> FindOrCreatePhotoIdForFileAsync(Guid fileId, string userId, CancellationToken cancellationToken = default)
    {
        var images = await files.ExistingImageIdsAsync([fileId], cancellationToken);
        if (!images.Contains(fileId))
        {
            throw new DomainUnprocessableException($"File is unknown or not an image type: {fileId}.");
        }

        var existing = await context.Photos
            .Where(p => p.FileId == fileId)
            .Select(p => p.PhotoId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing != Guid.Empty)
        {
            return existing;
        }

        var extracted = await metadata.ExtractAsync(fileId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var photo = BuildPhoto(fileId, extracted, userId, now);

        context.Photos.Add(photo);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return photo.PhotoId;
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // Concurrent create raced us on the Photo.FileId unique index — re-fetch the winner so a
            // routine journal save never turns into a 409 (§5 cross-context write atomicity).
            context.Entry(photo).State = EntityState.Detached;
            return await context.Photos
                .Where(p => p.FileId == fileId)
                .Select(p => p.PhotoId)
                .FirstAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> FindOrCreatePhotoIdsForFilesAsync(
        IReadOnlyCollection<Guid> fileIds, string userId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, Guid>();
        if (fileIds.Count == 0)
        {
            return result;
        }

        var ids = fileIds.Distinct().ToList();

        // One query for the file ids already linked to a Photo row.
        var existing = await context.Photos
            .Where(p => ids.Contains(p.FileId))
            .Select(p => new { p.FileId, p.PhotoId })
            .ToListAsync(cancellationToken);
        foreach (var row in existing)
        {
            result[row.FileId] = row.PhotoId;
        }

        var missing = ids.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return result;
        }

        // One batched insert for the rest. Metadata extraction is per-file (it reads the blob), but the
        // Photo rows persist in a single SaveChanges rather than one per file (§5 step 3.7 / AC 34).
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var added = new List<(Guid FileId, Photo Photo)>(missing.Count);
        foreach (var fileId in missing)
        {
            var extracted = await metadata.ExtractAsync(fileId, cancellationToken);
            var photo = BuildPhoto(fileId, extracted, userId, now);
            context.Photos.Add(photo);
            added.Add((fileId, photo));
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            foreach (var (fileId, photo) in added)
            {
                result[fileId] = photo.PhotoId;
            }

            return result;
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // A concurrent import raced us on Photo.FileId for one or more of these. Detach our attempts,
            // re-fetch the winners, and resolve any residual individually — a routine import never 409s.
            foreach (var (_, photo) in added)
            {
                context.Entry(photo).State = EntityState.Detached;
            }

            var winners = await context.Photos
                .Where(p => missing.Contains(p.FileId))
                .Select(p => new { p.FileId, p.PhotoId })
                .ToListAsync(cancellationToken);
            foreach (var row in winners)
            {
                result[row.FileId] = row.PhotoId;
            }

            foreach (var fileId in missing.Where(id => !result.ContainsKey(id)))
            {
                result[fileId] = await FindOrCreatePhotoIdForFileAsync(fileId, userId, cancellationToken);
            }

            return result;
        }
    }

    private Photo BuildPhoto(Guid fileId, PhotoMetadata extracted, string userId, DateTime now) => new()
    {
        FileId = fileId,
        Title = extracted.Title,
        Caption = extracted.Caption,
        TakenAt = extracted.TakenAt,
        CapturedLatitude = extracted.Latitude,
        CapturedLongitude = extracted.Longitude,
        LocationName = null,
        PixelWidth = extracted.PixelWidth,
        PixelHeight = extracted.PixelHeight,
        CreatedByUserId = userId,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
