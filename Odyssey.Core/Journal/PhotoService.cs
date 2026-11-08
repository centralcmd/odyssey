using Odyssey.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal.Originals;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Core.Pagination;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// CRUD, archival, and server-side listing for library photos (issue #321). Since issue #428 a photo owns its
/// original: an upload writes it to the object store (<c>PhotoOriginals</c>) and creates the photo in one request,
/// and no photo is ever made from an existing file. Reads return link ids only (§10.5), and unresolved person links
/// are dropped at read time (§10.4). Metadata extraction runs on add only and never overrides a caller-supplied value.
/// </summary>
public class PhotoService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contacts;
    private readonly IFileReferenceGuard fileReferences;
    private readonly PhotoMetadataService metadataService;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly ILogger<PhotoService> logger;
    private readonly PhotoThumbnailService thumbnails;
    private readonly PhotoOriginalWriter originals;
    private readonly IObjectStore store;
    private readonly IUploadLimitsLookup uploadLimits;
    private readonly TimeProvider timeProvider;

    public PhotoService(
        OdysseyContext context,
        IContactLookup contacts,
        IFileReferenceGuard fileReferences,
        PhotoMetadataService metadataService,
        IJournalLimitsLookup journalLimits,
        ILogger<PhotoService> logger,
        PhotoThumbnailService thumbnails,
        PhotoOriginalWriter originals,
        IObjectStore store,
        IUploadLimitsLookup uploadLimits,
        TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contacts = contacts;
        this.fileReferences = fileReferences;
        this.metadataService = metadataService;
        this.journalLimits = journalLimits;
        this.logger = logger;
        this.thumbnails = thumbnails;
        this.originals = originals;
        this.store = store;
        this.uploadLimits = uploadLimits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-side paged list (issue #277): free-text search + tag/person/album/taken-date/archival filters + allowlisted sort.</summary>
    public async Task<PagedResult<PhotoSummary>> ListAsync(
        PhotosQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.Photos.AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(p =>
                (p.Title != null && EF.Functions.Like(p.Title, pattern)) ||
                (p.Caption != null && EF.Functions.Like(p.Caption, pattern)) ||
                (p.LocationName != null && EF.Functions.Like(p.LocationName, pattern)));
        }

        if (query.TagIds is { Length: > 0 } tagIds)
        {
            var ids = tagIds.Distinct().ToList();
            q = q.Where(p => p.Tags.Any(t => ids.Contains(t.PhotoTagId)));
        }

        if (query.PersonIds is { Length: > 0 } personIds)
        {
            var ids = personIds.Distinct().ToList();
            q = q.Where(p => p.People.Any(pp => ids.Contains(pp.ContactId)));
        }

        if (query.AlbumIds is { Length: > 0 } albumIds)
        {
            var ids = albumIds.Distinct().ToList();
            q = q.Where(p => p.Albums.Any(a => ids.Contains(a.PhotoAlbumId)));
        }

        if (query.From is { } from)
        {
            q = q.Where(p => p.TakenAt != null && p.TakenAt >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(p => p.TakenAt != null && p.TakenAt <= to);
        }

        q = query.Status == ArchivalStatus.Archived
            ? q.Where(p => p.Archived != null)
            : q.Where(p => p.Archived == null);

        if (query.FavouritesOnly == true)
        {
            q = q.Where(p => p.Favourited != null);
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is PhotoSortBy.Title);
        IOrderedQueryable<Photo> sorted = query.SortBy switch
        {
            PhotoSortBy.Title => ascending ? q.OrderBy(p => p.Title) : q.OrderByDescending(p => p.Title),
            PhotoSortBy.CreatedAt => ascending ? q.OrderBy(p => p.CreatedAt) : q.OrderByDescending(p => p.CreatedAt),
            // TakenAt is nullable: pin nulls last regardless of direction (MariaDB sorts NULLs first on
            // ASC by default; #277's tiebreak only adds the PK), so undated photos never lead the grid.
            _ => ascending
                ? q.OrderBy(p => p.TakenAt == null).ThenBy(p => p.TakenAt)
                : q.OrderBy(p => p.TakenAt == null).ThenByDescending(p => p.TakenAt),
        };

        var rows = sorted
            .ThenBy(p => p.PhotoId)
            .Select(p => new
            {
                Summary = new PhotoSummary
                {
                    PhotoId = p.PhotoId,
                    Title = p.Title,
                    TakenAt = p.TakenAt,
                    LocationName = p.LocationName,
                    PixelWidth = p.PixelWidth,
                    PixelHeight = p.PixelHeight,
                    Archived = p.Archived,
                    Favourited = p.Favourited,
                    CreatedByUserId = p.CreatedByUserId,
                    PersonContactIds = p.People.Select(pp => pp.ContactId).ToList(),
                    PersonCount = p.People.Count,
                    AlbumCount = p.Albums.Count,
                    ThumbnailsReady = context.PhotoThumbnails.Count(t => t.PhotoId == p.PhotoId) == 2,
                },
                Tags = p.Tags
                    .Select(t => new TagIconCandidate(
                        t.PhotoTagId,
                        t.PhotoTag == null ? null : t.PhotoTag.Name,
                        t.PhotoTag == null ? null : t.PhotoTag.Icon,
                        t.PhotoTag == null ? null : t.PhotoTag.Color))
                    .ToList(),
            });

        return await rows.ToPagedResultAsync(
            query.Offset, query.Limit, row => TagIconProjection.Apply(row.Summary, row.Tags), cancellationToken);
    }

    /// <summary>Aggregate counters for the Overview panel, computed over the active (non-archived) library
    /// so the client needn't pull every photo. Per-tag / per-person counts are keyed by id.</summary>
    public async Task<PhotoLibraryStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var active = context.Photos.Where(p => p.Archived == null);

        var personCounts = await context.PhotoPeople
            .Where(l => l.Photo!.Archived == null)
            .GroupBy(l => l.ContactId)
            .Select(g => new PhotoCountByKey { Key = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Drop buckets whose contact is no longer a resolvable Person (§10.4), matching the
        // single-photo read path so the Overview can't show a phantom person bucket.
        var existingPersons = await contacts.ExistingPersonIdsAsync(
            [.. personCounts.Select(c => c.Key)], cancellationToken);
        var thumbnailStats = await thumbnails.GetStatsAsync(cancellationToken);

        return new PhotoLibraryStats
        {
            TotalCount = await active.CountAsync(cancellationToken),
            FavouriteCount = await active.CountAsync(p => p.Favourited != null, cancellationToken),
            TagCounts = await context.PhotoTagLinks
                .Where(l => l.Photo!.Archived == null)
                .GroupBy(l => l.PhotoTagId)
                .Select(g => new PhotoCountByKey { Key = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken),
            PersonCounts = [.. personCounts.Where(c => existingPersons.Contains(c.Key))],
            ThumbnailsPending = thumbnailStats.Pending,
            ThumbnailsFailed = thumbnailStats.Failed,
            ThumbnailsUnsupported = thumbnailStats.Unsupported,
            // The controller nulls it for a caller without system-settings.read (§3.6).
            ThumbnailWorkerState = thumbnailStats.WorkerState,
            ThumbnailWorkerResumesAtUtc = thumbnailStats.ResumesAtUtc,
        };
    }

    public async Task<ExistingPhoto?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var photo = await LoadWithDetails(id, cancellationToken);
        if (photo is null)
        {
            return null;
        }

        var dto = await ToDtoAsync(photo, cancellationToken);
        dto.PersonContactIds = await ResolveExistingPersonsAsync(dto.PersonContactIds, cancellationToken);
        return dto;
    }

    /// <summary>
    /// Uploads a photo (issue #428 §3.2): validates the image, refuses a duplicate, writes the original to the object
    /// store and creates the photo — with its tags, people, album items and thumbnail job — in one save.
    /// </summary>
    public async Task<ExistingPhoto> UploadAsync(
        FileUpload upload, NewPhotoUpload request, string userId, bool canAutoCreateTags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var contentType = await ValidateUploadAsync(upload, cancellationToken);
        ValidateCoordinates(request.CapturedLatitude, request.CapturedLongitude);
        var callerTakenAt = NormalizeTakenAt(request.TakenAt, fromCaller: true);
        var links = await ValidateLinks(request.TagIds, request.PersonContactIds, request.AlbumIds, cancellationToken);

        // The form file is spooled to disk by the framework, so it is hashed and read here without a second copy in
        // memory (§3.2 step 3); the stream is re-opened for the write.
        await using var content = upload.OpenReadStream();
        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(content, cancellationToken)).ToLowerInvariant();
        await EnsureNotDuplicateAsync(hash, upload.Length, cancellationToken);

        var extracted = await metadataService.ExtractFromStreamAsync(content, cancellationToken);
        var tagIds = await ResolveTagIdsAsync(links.TagIds, extracted.Keywords, canAutoCreateTags, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var fileName = SanitizeUploadName(upload.FileName);
        content.Position = 0;
        var maxBytes = (await uploadLimits.GetAsync(cancellationToken)).MaxUploadBytes;
        var stored = await originals.PutAsync(content, fileName, contentType, now, maxBytes, cancellationToken);

        var photo = new Photo
        {
            PhotoId = Guid.NewGuid(),
            StoredObjectId = stored.Id,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = stored.SizeBytes,
            Sha256Hash = stored.Sha256Hash,
            Title = Clamp(request.Title ?? extracted.Title, PhotoLimits.MaxTitleLength),
            Caption = Clamp(request.Caption ?? extracted.Caption, PhotoLimits.MaxCaptionLength),
            TakenAt = callerTakenAt ?? NormalizeTakenAt(extracted.TakenAt, fromCaller: false),
            CapturedLatitude = request.CapturedLatitude ?? extracted.Latitude,
            CapturedLongitude = request.CapturedLongitude ?? extracted.Longitude,
            LocationName = request.LocationName,
            PixelWidth = request.PixelWidth ?? extracted.PixelWidth,
            PixelHeight = request.PixelHeight ?? extracted.PixelHeight,
            Favourited = request.Favourite ? now : null,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var tagId in tagIds)
        {
            photo.Tags.Add(new PhotoTagLink { PhotoTagId = tagId });
        }

        foreach (var contactId in links.PersonIds)
        {
            photo.People.Add(new PhotoPerson { ContactId = contactId });
        }

        context.Photos.Add(photo);
        // Album items share the photo's save (issue #428 §3.2 step 7), so a failed link cannot leave a half-made photo.
        await StageAlbumItemsAsync(photo.PhotoId, links.AlbumIds, now, cancellationToken);
        // The thumbnail job, in the same save as the photo (issue #362 §3.3 step 1). The worker sees it on its next tick.
        await thumbnails.StageEnqueueAsync(photo, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var created = await LoadWithDetails(photo.PhotoId, cancellationToken);
        return await ToDtoAsync(created!, cancellationToken);
    }

    /// <summary>
    /// The upload's checks (§6, §8): the effective cap (413), then an allowed image type whose leading bytes agree
    /// with it (415). Returns the normalised content type.
    /// </summary>
    private async Task<string> ValidateUploadAsync(FileUpload upload, CancellationToken cancellationToken)
    {
        if (upload.Length <= 0)
        {
            throw new DomainValidationException("The photo file is empty.", code: null, field: "file");
        }

        var limits = await uploadLimits.GetAsync(cancellationToken);
        if (upload.Length > limits.MaxUploadBytes)
        {
            throw new PhotoTooLargeException($"The photo is larger than the {limits.MaxUploadMegabytes} MB limit.");
        }

        if (string.IsNullOrWhiteSpace(upload.ContentType) || !PhotoOriginalLimits.ContentTypes.Contains(upload.ContentType))
        {
            throw new PhotoUnsupportedTypeException("A photo must be a JPEG, PNG, GIF or WebP image.");
        }

        await using (var header = upload.OpenReadStream())
        {
            var bytes = new byte[16];
            var read = await header.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken);
            if (!FileValidationService.HeaderMatchesContentType(upload.ContentType, bytes.AsSpan(0, read)))
            {
                throw new PhotoUnsupportedTypeException("The file's content does not match its declared image type.");
            }
        }

        return PhotoOriginalLimits.Normalize(upload.ContentType);
    }

    /// <summary>
    /// Refuses bytes equal to an existing photo's (§3.2 step 3): a disk-backed photo by its own hash, a legacy one by
    /// its file's — only files wrapped by a photo, so this is never an oracle for finance documents. Archived photos
    /// count. Holds no lock: two identical uploads at the same instant can both pass (accepted, Non-Goal 9).
    /// </summary>
    private async Task EnsureNotDuplicateAsync(string hash, long size, CancellationToken cancellationToken)
    {
        var match = await context.Photos
            .Where(p => p.Sha256Hash == hash && p.SizeBytes == size)
            .Select(p => new { p.PhotoId, p.Archived })
            .FirstOrDefaultAsync(cancellationToken)
            ?? await context.Photos
                .Where(p => p.FileId != null)
                .Join(context.FileMetadata, p => p.FileId, f => (Guid?)f.Id, (p, f) => new { p.PhotoId, p.Archived, f.Sha256Hash, f.SizeBytes })
                .Where(x => x.Sha256Hash == hash && x.SizeBytes == size)
                .Select(x => new { x.PhotoId, x.Archived })
                .FirstOrDefaultAsync(cancellationToken);
        if (match is not null)
        {
            throw new DuplicatePhotoException(match.PhotoId, match.Archived is not null);
        }
    }

    private static string SanitizeUploadName(string? fileName)
    {
        var sanitized = new FileValidationService().SanitizeFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "photo";
        }

        return sanitized.Length > FileValidationService.MaxFileNameLength
            ? sanitized[..FileValidationService.MaxFileNameLength].TrimEnd()
            : sanitized;
    }

    public async Task<ExistingPhoto?> Update(
        Guid id, UpdatePhoto request, string userId, CancellationToken cancellationToken = default)
    {
        var photo = await LoadWithDetailsForUpdate(id, cancellationToken);
        if (photo is null)
        {
            return null;
        }

        ValidateCoordinates(request.CapturedLatitude, request.CapturedLongitude);
        var takenAt = NormalizeTakenAt(request.TakenAt, fromCaller: true);
        // A rename is photo data (issue #428 §5.4): it writes Photo.FileName under photos.update and never touches a
        // Files-store row. A blank name leaves the name as it is. Sanitized before anything is applied.
        var newFileName = string.IsNullOrWhiteSpace(request.FileName)
            ? null
            : new FileValidationService().SanitizeRenamedFileName(request.FileName);
        // Person links are validated change-only (issue #335 §7.6): a tag the photo already holds is
        // never re-checked, so one left behind by a contact converted to an Organization concurrently
        // with its creation never makes the photo un-editable. A newly added id must still be a Person.
        var keptPersonIds = photo.People.Select(p => p.ContactId).ToHashSet();
        var links = await ValidateLinks(
            request.TagIds, request.PersonContactIds, request.AlbumIds, cancellationToken, keptPersonIds);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // PUT is authoritative: the edited values win and extraction never re-runs on update (§9).
        photo.Title = Clamp(request.Title, PhotoLimits.MaxTitleLength);
        photo.Caption = Clamp(request.Caption, PhotoLimits.MaxCaptionLength);
        photo.TakenAt = takenAt;
        photo.CapturedLatitude = request.CapturedLatitude;
        photo.CapturedLongitude = request.CapturedLongitude;
        photo.LocationName = request.LocationName;
        photo.PixelWidth = request.PixelWidth;
        photo.PixelHeight = request.PixelHeight;
        photo.UpdatedByUserId = userId;
        photo.UpdatedAt = now;
        ApplyArchiveTransition(photo, request.Archived);
        ApplyFavouriteTransition(photo, request.Favourite);
        if (newFileName is not null)
        {
            var current = await PhotoOriginalSource.Describe(context, context.Photos.Where(p => p.PhotoId == id))
                .Select(i => i.FileName)
                .FirstOrDefaultAsync(cancellationToken);
            if (!string.Equals(current, newFileName, StringComparison.Ordinal))
            {
                photo.FileName = newFileName;
                // The thumbnails are named from the photo's name, so they follow it (the #362 rename rule).
                await thumbnails.StageRenameForPhotoAsync(photo.PhotoId, cancellationToken);
            }
        }

        DiffTags(photo, links.TagIds);
        DiffPeople(photo, links.PersonIds);
        await DiffAlbumsAsync(photo, links.AlbumIds, now, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadWithDetails(id, cancellationToken);
        var dto = await ToDtoAsync(reloaded!, cancellationToken);
        dto.PersonContactIds = await ResolveExistingPersonsAsync(dto.PersonContactIds, cancellationToken);
        return dto;
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var photo = await context.Photos.FirstOrDefaultAsync(p => p.PhotoId == id, cancellationToken);
        if (photo is null)
        {
            return false;
        }

        if (photo.StoredObjectId is { } objectId)
        {
            // A photo that owns its original (issue #428): the original and both thumbnails are released in the
            // delete's own save, graced by FileStorageDeletionGraceHours. No file is touched.
            await thumbnails.StageDeleteAsync(photo.PhotoId, cancellationToken);
            await store.StageReleaseAsync(objectId, immediate: false, cancellationToken);
            context.Photos.Remove(photo);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        // A legacy photo and the file it wraps are one thing, so they are deleted together in both directions:
        // deleting the file cascades the photo (FK on Photo.FileId), and deleting the photo deletes the file here.
        //
        // Refuse when something else still holds the file. Several tables carry a cascading FK to FileMetadata, so
        // deleting it would quietly strip the file off a transaction, a tax statement or a journal entry as a side
        // effect of a photo delete. A 409 naming the other holders is the honest answer.
        var fileId = photo.FileId!.Value;
        var otherHolders = await fileReferences.DescribeNonPhotoReferencesAsync(fileId, cancellationToken);
        if (otherHolders.Count > 0)
        {
            throw new DomainConflictException(
                "This photo's file is also used as " + string.Join(", ", otherHolders) +
                ". Deleting the photo would remove it there too, so the photo was not deleted.");
        }

        // Owned tag/person/album-item rows cascade; an album whose cover was this photo nulls its
        // CoverPhotoId (FK SET NULL). Both thumbnails are released in the same save (issue #362 §4.1).
        await thumbnails.StageDeleteAsync(photo.PhotoId, cancellationToken);
        context.Photos.Remove(photo);
        await context.SaveChangesAsync(cancellationToken);

        await fileReferences.DeleteFileAndBlobAsync(fileId, cancellationToken);
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ApplyArchiveTransition(Photo photo, bool requestedArchived)
    {
        var currentArchived = photo.Archived is not null;
        if (!currentArchived && requestedArchived)
        {
            photo.Archived = timeProvider.GetUtcNow().UtcDateTime;
        }
        else if (currentArchived && !requestedArchived)
        {
            photo.Archived = null;
        }
    }

    private void ApplyFavouriteTransition(Photo photo, bool requestedFavourite)
    {
        var currentFavourite = photo.Favourited is not null;
        if (!currentFavourite && requestedFavourite)
        {
            photo.Favourited = timeProvider.GetUtcNow().UtcDateTime;
        }
        else if (currentFavourite && !requestedFavourite)
        {
            photo.Favourited = null;
        }
    }

    // Update is the only caller that writes to what it loads; every other one turns the row straight
    // into a DTO, so it reads through the untracked overload.
    private async Task<Photo?> LoadWithDetails(Guid id, CancellationToken cancellationToken) =>
        await WithDetails(context.Photos.AsNoTracking())
            .FirstOrDefaultAsync(p => p.PhotoId == id, cancellationToken);

    private async Task<Photo?> LoadWithDetailsForUpdate(Guid id, CancellationToken cancellationToken) =>
        await WithDetails(context.Photos)
            .FirstOrDefaultAsync(p => p.PhotoId == id, cancellationToken);

    private static IQueryable<Photo> WithDetails(IQueryable<Photo> photos) => photos
        .Include(p => p.Tags)
        .Include(p => p.People)
        .Include(p => p.Albums);

    private static void ValidateCoordinates(double? latitude, double? longitude)
    {
        if (latitude is < -90d or > 90d)
        {
            throw new DomainValidationException("CapturedLatitude must be between -90 and 90.");
        }

        if (longitude is < -180d or > 180d)
        {
            throw new DomainValidationException("CapturedLongitude must be between -180 and 180.");
        }
    }

    // Caller-supplied dates out of the 1900–2100 window are rejected; extracted dates out of range are
    // silently dropped (extraction is best-effort). Stored as Unspecified wall-clock (EXIF has no tz).
    private static DateTime? NormalizeTakenAt(DateTime? value, bool fromCaller)
    {
        if (value is not { } takenAt)
        {
            return null;
        }

        if (takenAt.Year is < 1900 or > 2100)
        {
            if (fromCaller)
            {
                throw new DomainValidationException("TakenAt must fall within the years 1900–2100.");
            }

            return null;
        }

        return DateTime.SpecifyKind(takenAt, DateTimeKind.Unspecified);
    }

    private async Task<ValidatedLinks> ValidateLinks(
        Guid[] tagIds, Guid[] personIds, Guid[] albumIds, CancellationToken cancellationToken,
        IReadOnlySet<Guid>? keptPersonIds = null)
    {
        var limits = await journalLimits.GetAsync(cancellationToken);
        var tags = DistinctCapped(tagIds, "tag", limits.PhotoMaxLinksPerKind);
        var persons = DistinctCapped(personIds, "person", limits.PhotoMaxLinksPerKind);
        var albums = DistinctCapped(albumIds, "album", limits.PhotoMaxLinksPerKind);

        await EnsureTagsExist(tags, cancellationToken);
        await EnsurePersonsExist(
            keptPersonIds is null ? persons : [.. persons.Where(id => !keptPersonIds.Contains(id))],
            cancellationToken);
        await EnsureAlbumsExist(albums, cancellationToken);

        return new ValidatedLinks(tags, persons, albums);
    }

    // The cap is a parameter, not a field read: it lives in the settings store now and this helper is
    // synchronous, so the async caller reads it once and passes it down.
    private static List<Guid> DistinctCapped(Guid[] ids, string kind, int maxLinksPerKind)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count > maxLinksPerKind)
        {
            throw new DomainUnprocessableException(
                $"A photo cannot have more than {maxLinksPerKind} {kind} links.");
        }

        return distinct;
    }

    private async Task EnsureTagsExist(IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return;
        }

        var found = await context.PhotoTags
            .Where(t => tagIds.Contains(t.PhotoTagId) && t.Archived == null)
            .Select(t => t.PhotoTagId)
            .ToListAsync(cancellationToken);

        var missing = tagIds.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException($"Unknown or archived photo tag(s): {string.Join(", ", missing)}.");
        }
    }

    private async Task EnsurePersonsExist(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var found = await contacts.ExistingPersonIdsAsync(ids, cancellationToken);
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException(
                $"Person link(s) are unknown or not a Person contact: {string.Join(", ", missing)}.");
        }
    }

    private async Task EnsureAlbumsExist(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var found = await context.PhotoAlbums
            .Where(a => ids.Contains(a.PhotoAlbumId))
            .Select(a => a.PhotoAlbumId)
            .ToListAsync(cancellationToken);

        var missing = ids.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException($"Unknown album(s): {string.Join(", ", missing)}.");
        }
    }

    // Merge caller-supplied tag ids with tags resolved from extracted keywords: match existing tags by
    // (DB-collation) case-insensitive name, auto-create the rest only when the caller holds
    // photos.tags.create, then union caller-first and cap (caller ids win) — never fatal (§5, §10.6).
    private async Task<List<Guid>> ResolveTagIdsAsync(
        IReadOnlyList<Guid> callerTagIds, IReadOnlyList<string> keywords, bool canAutoCreateTags, CancellationToken cancellationToken)
    {
        var maxLinksPerKind = (await journalLimits.GetAsync(cancellationToken)).PhotoMaxLinksPerKind;

        var cleaned = keywords
            .Select(k => k?.Trim() ?? string.Empty)
            .Where(k => k.Length is > 0 and <= PhotoLimits.MaxTagNameLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var final = callerTagIds.ToList();
        if (cleaned.Count == 0)
        {
            return final;
        }

        var existing = await context.PhotoTags
            .Where(t => cleaned.Contains(t.Name))
            .Select(t => new { t.PhotoTagId, t.Name })
            .ToListAsync(cancellationToken);
        var byName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in existing)
        {
            byName.TryAdd(tag.Name, tag.PhotoTagId);
        }

        var keywordIds = new List<Guid>();
        var skipped = 0;
        foreach (var keyword in cleaned)
        {
            if (byName.TryGetValue(keyword, out var matchedId))
            {
                keywordIds.Add(matchedId);
            }
            else if (canAutoCreateTags)
            {
                keywordIds.Add(await FindOrCreateTagAsync(keyword, cancellationToken));
            }
            else
            {
                skipped++;
            }
        }

        if (skipped > 0)
        {
            // Log the COUNT only — never the raw (file-influenced) keyword values (§11, log-injection / PII).
            logger.LogInformation(
                "Skipped auto-creating {SkippedCount} extracted keyword tag(s): caller lacks photos.tags.create.", skipped);
        }

        var existingSet = final.ToHashSet();
        var dropped = 0;
        foreach (var id in keywordIds)
        {
            if (existingSet.Contains(id))
            {
                continue;
            }

            if (final.Count >= maxLinksPerKind)
            {
                dropped++;
                continue;
            }

            final.Add(id);
            existingSet.Add(id);
        }

        if (dropped > 0)
        {
            logger.LogInformation("Dropped {DroppedCount} extracted keyword tag(s) over the per-photo cap.", dropped);
        }

        return final;
    }

    private async Task<Guid> FindOrCreateTagAsync(string name, CancellationToken cancellationToken)
    {
        var tag = new PhotoTag { Name = name };
        context.PhotoTags.Add(tag);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return tag.PhotoTagId;
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // A concurrent add created the same new keyword tag first — re-fetch the winner (case-insensitive
            // on MariaDB) instead of letting the DbUpdateException become a 409 (§16.3e).
            context.Entry(tag).State = EntityState.Detached;
            var winner = await context.PhotoTags.FirstAsync(t => t.Name == name, cancellationToken);
            return winner.PhotoTagId;
        }
    }

    private async Task StageAlbumItemsAsync(Guid photoId, IReadOnlyCollection<Guid> albumIds, DateTime now, CancellationToken cancellationToken)
    {
        if (albumIds.Count == 0)
        {
            return;
        }

        var nextPositions = await NextAlbumPositionsAsync(albumIds, cancellationToken);
        foreach (var albumId in albumIds)
        {
            context.PhotoAlbumItems.Add(new PhotoAlbumItem
            {
                PhotoAlbumId = albumId,
                PhotoId = photoId,
                Position = nextPositions.GetValueOrDefault(albumId),
                CreatedAt = now,
            });
        }
    }

    private async Task<Dictionary<Guid, int>> NextAlbumPositionsAsync(IReadOnlyCollection<Guid> albumIds, CancellationToken cancellationToken)
    {
        var counts = await context.PhotoAlbumItems
            .Where(i => albumIds.Contains(i.PhotoAlbumId))
            .GroupBy(i => i.PhotoAlbumId)
            .Select(g => new { AlbumId = g.Key, Next = g.Max(i => i.Position) + 1 })
            .ToDictionaryAsync(x => x.AlbumId, x => x.Next, cancellationToken);
        return counts;
    }

    private void DiffTags(Photo photo, IReadOnlyCollection<Guid> desiredTagIds)
    {
        var desired = desiredTagIds.ToHashSet();

        foreach (var link in photo.Tags.Where(t => !desired.Contains(t.PhotoTagId)).ToList())
        {
            photo.Tags.Remove(link);
            context.PhotoTagLinks.Remove(link);
        }

        var existing = photo.Tags.Select(t => t.PhotoTagId).ToHashSet();
        foreach (var tagId in desiredTagIds.Where(id => !existing.Contains(id)))
        {
            photo.Tags.Add(new PhotoTagLink { PhotoId = photo.PhotoId, PhotoTagId = tagId });
        }
    }

    private void DiffPeople(Photo photo, IReadOnlyCollection<Guid> desiredPersonIds)
    {
        var desired = desiredPersonIds.ToHashSet();

        foreach (var link in photo.People.Where(p => !desired.Contains(p.ContactId)).ToList())
        {
            photo.People.Remove(link);
            context.PhotoPeople.Remove(link);
        }

        var existing = photo.People.Select(p => p.ContactId).ToHashSet();
        foreach (var contactId in desiredPersonIds.Where(id => !existing.Contains(id)))
        {
            photo.People.Add(new PhotoPerson { PhotoId = photo.PhotoId, ContactId = contactId });
        }
    }

    private async Task DiffAlbumsAsync(Photo photo, IReadOnlyCollection<Guid> desiredAlbumIds, DateTime now, CancellationToken cancellationToken)
    {
        var desired = desiredAlbumIds.ToHashSet();

        foreach (var item in photo.Albums.Where(a => !desired.Contains(a.PhotoAlbumId)).ToList())
        {
            photo.Albums.Remove(item);
            context.PhotoAlbumItems.Remove(item);
        }

        var existing = photo.Albums.Select(a => a.PhotoAlbumId).ToHashSet();
        var toAdd = desiredAlbumIds.Where(id => !existing.Contains(id)).ToList();
        if (toAdd.Count == 0)
        {
            return;
        }

        var nextPositions = await NextAlbumPositionsAsync(toAdd, cancellationToken);
        foreach (var albumId in toAdd)
        {
            photo.Albums.Add(new PhotoAlbumItem
            {
                PhotoAlbumId = albumId,
                PhotoId = photo.PhotoId,
                Position = nextPositions.GetValueOrDefault(albumId),
                CreatedAt = now,
            });
        }
    }

    private async Task<List<Guid>> ResolveExistingPersonsAsync(List<Guid> personIds, CancellationToken cancellationToken)
    {
        if (personIds.Count == 0)
        {
            return personIds;
        }

        // Drop links whose contact no longer exists so an erased person is not surfaced (§10.4/§16.15).
        var stillExist = await contacts.ExistingIdsAsync(personIds, cancellationToken);
        return personIds.Where(stillExist.Contains).ToList();
    }

    // Truncate an extracted/caller value to the column's max (extracted title/caption may be longer);
    // null and already-short values pass through unchanged.
    private static string? Clamp(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private async Task<ExistingPhoto> ToDtoAsync(Photo photo, CancellationToken cancellationToken)
    {
        var original = await PhotoOriginalSource.Describe(context, context.Photos.Where(p => p.PhotoId == photo.PhotoId))
            .FirstOrDefaultAsync(cancellationToken);
        var dto = new ExistingPhoto
        {
            PhotoId = photo.PhotoId,
            FileName = original?.FileName,
            ContentType = original?.ContentType,
            SizeBytes = original?.SizeBytes,
            Title = photo.Title,
            Caption = photo.Caption,
            TakenAt = photo.TakenAt,
            CapturedLatitude = photo.CapturedLatitude,
            CapturedLongitude = photo.CapturedLongitude,
            LocationName = photo.LocationName,
            PixelWidth = photo.PixelWidth,
            PixelHeight = photo.PixelHeight,
            Archived = photo.Archived,
            Favourited = photo.Favourited,
            CreatedByUserId = photo.CreatedByUserId,
            UpdatedByUserId = photo.UpdatedByUserId,
            CreatedAt = photo.CreatedAt,
            UpdatedAt = photo.UpdatedAt,
            PersonContactIds = photo.People.Select(p => p.ContactId).ToList(),
            AlbumIds = photo.Albums.Select(a => a.PhotoAlbumId).ToList(),
            ThumbnailsReady = (await thumbnails.ReadyAsync([photo.PhotoId], cancellationToken)).Count == 1,
        };

        var tagIds = photo.Tags.Select(t => t.PhotoTagId).ToList();
        return TagIconProjection.Apply(dto, tagIds, await TagIconProjection.PhotoTagsAsync(context, tagIds, cancellationToken));
    }

    private sealed record ValidatedLinks(List<Guid> TagIds, List<Guid> PersonIds, List<Guid> AlbumIds);
}
