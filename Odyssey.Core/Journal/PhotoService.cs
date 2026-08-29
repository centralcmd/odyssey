using Odyssey.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// CRUD, archival, and server-side listing for library photos (issue #321). Cross-context references
/// (people, files) are validated via narrow read-only Finance lookups in front of the DB foreign keys
/// boundary (§5). Reads return link ids only (§10.5), and unresolved person links are dropped at read
/// time (§10.4). Metadata extraction runs on add only and never overrides a caller-supplied value.
/// </summary>
public class PhotoService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contacts;
    private readonly IFileLookup files;
    private readonly IFileReferenceGuard fileReferences;
    private readonly PhotoMetadataService metadataService;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly ILogger<PhotoService> logger;
    private readonly TimeProvider timeProvider;

    public PhotoService(
        OdysseyContext context,
        IContactLookup contacts,
        IFileLookup files,
        IFileReferenceGuard fileReferences,
        PhotoMetadataService metadataService,
        IJournalLimitsLookup journalLimits,
        ILogger<PhotoService> logger,
        TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contacts = contacts;
        this.files = files;
        this.fileReferences = fileReferences;
        this.metadataService = metadataService;
        this.journalLimits = journalLimits;
        this.logger = logger;
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
            .Select(p => new PhotoSummary
            {
                PhotoId = p.PhotoId,
                FileId = p.FileId,
                Title = p.Title,
                TakenAt = p.TakenAt,
                LocationName = p.LocationName,
                PixelWidth = p.PixelWidth,
                PixelHeight = p.PixelHeight,
                Archived = p.Archived,
                Favourited = p.Favourited,
                CreatedByUserId = p.CreatedByUserId,
                TagIds = p.Tags.Select(t => t.PhotoTagId).ToList(),
                PersonContactIds = p.People.Select(pp => pp.ContactId).ToList(),
                PersonCount = p.People.Count,
                AlbumCount = p.Albums.Count,
            });

        return await rows.ToPagedResultAsync(query.Offset, query.Limit, cancellationToken);
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
        };
    }

    public async Task<ExistingPhoto?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var photo = await LoadWithDetails(id, cancellationToken);
        if (photo is null)
        {
            return null;
        }

        var dto = ToDto(photo);
        dto.PersonContactIds = await ResolveExistingPersonsAsync(dto.PersonContactIds, cancellationToken);
        return dto;
    }

    public async Task<ExistingPhoto> Create(
        NewPhoto request, string userId, bool canAutoCreateTags, CancellationToken cancellationToken = default)
    {
        ValidateCoordinates(request.CapturedLatitude, request.CapturedLongitude);
        var callerTakenAt = NormalizeTakenAt(request.TakenAt, fromCaller: true);

        await EnsureFileIsImage(request.FileId, cancellationToken);
        var links = await ValidateLinks(request.TagIds, request.PersonContactIds, request.AlbumIds, cancellationToken);

        var extracted = await metadataService.ExtractAsync(request.FileId, cancellationToken);
        var tagIds = await ResolveTagIdsAsync(links.TagIds, extracted.Keywords, canAutoCreateTags, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var photo = new Photo
        {
            FileId = request.FileId,
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
        await context.SaveChangesAsync(cancellationToken);

        await AddToAlbumsAsync(photo.PhotoId, links.AlbumIds, now, cancellationToken);

        var created = await LoadWithDetails(photo.PhotoId, cancellationToken);
        return ToDto(created!);
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
        var links = await ValidateLinks(request.TagIds, request.PersonContactIds, request.AlbumIds, cancellationToken);

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

        DiffTags(photo, links.TagIds);
        DiffPeople(photo, links.PersonIds);
        await DiffAlbumsAsync(photo, links.AlbumIds, now, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadWithDetails(id, cancellationToken);
        var dto = ToDto(reloaded!);
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

        // A library photo and the file it wraps are one thing, so they are deleted together in both
        // directions: deleting the file cascades the photo (FK on Photo.FileId), and deleting the photo
        // deletes the file here. This reverses the original §7 decision to leave the blob intact, which
        // had left the two directions disagreeing about which object was the durable one.
        //
        // Refuse when something else still holds the file. Nine tables carry a cascading FK to
        // FileMetadata, so deleting it would quietly strip the file off a transaction, a tax statement or
        // a journal entry as a side effect of a photo delete — the database would do it without
        // complaint. A 409 naming the other holders is the honest answer; the caller can detach it there
        // first, or delete the file directly if that is really what they meant.
        var otherHolders = await fileReferences.DescribeNonPhotoReferencesAsync(photo.FileId, cancellationToken);
        if (otherHolders.Count > 0)
        {
            throw new DomainConflictException(
                "This photo's file is also used as " + string.Join(", ", otherHolders) +
                ". Deleting the photo would remove it there too, so the photo was not deleted.");
        }

        // Owned tag/person/album-item rows cascade; an album whose cover was this photo nulls its
        // CoverPhotoId (FK SET NULL).
        context.Photos.Remove(photo);
        await context.SaveChangesAsync(cancellationToken);

        await fileReferences.DeleteFileAndBlobAsync(photo.FileId, cancellationToken);
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

    private async Task EnsureFileIsImage(Guid fileId, CancellationToken cancellationToken)
    {
        var found = await files.ExistingImageIdsAsync([fileId], cancellationToken);
        if (!found.Contains(fileId))
        {
            throw new DomainUnprocessableException($"File is unknown or not an image type: {fileId}.");
        }
    }

    private async Task<ValidatedLinks> ValidateLinks(
        Guid[] tagIds, Guid[] personIds, Guid[] albumIds, CancellationToken cancellationToken)
    {
        var limits = await journalLimits.GetAsync(cancellationToken);
        var tags = DistinctCapped(tagIds, "tag", limits.PhotoMaxLinksPerKind);
        var persons = DistinctCapped(personIds, "person", limits.PhotoMaxLinksPerKind);
        var albums = DistinctCapped(albumIds, "album", limits.PhotoMaxLinksPerKind);

        await EnsureTagsExist(tags, cancellationToken);
        await EnsurePersonsExist(persons, cancellationToken);
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

    private async Task AddToAlbumsAsync(Guid photoId, IReadOnlyCollection<Guid> albumIds, DateTime now, CancellationToken cancellationToken)
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

        await context.SaveChangesAsync(cancellationToken);
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

    private static ExistingPhoto ToDto(Photo photo) => new()
    {
        PhotoId = photo.PhotoId,
        FileId = photo.FileId,
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
        TagIds = photo.Tags.Select(t => t.PhotoTagId).ToList(),
        PersonContactIds = photo.People.Select(p => p.ContactId).ToList(),
        AlbumIds = photo.Albums.Select(a => a.PhotoAlbumId).ToList(),
    };

    private sealed record ValidatedLinks(List<Guid> TagIds, List<Guid> PersonIds, List<Guid> AlbumIds);
}
