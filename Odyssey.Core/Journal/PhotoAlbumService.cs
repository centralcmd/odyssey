using Odyssey.Core;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// CRUD, archival, and server-side listing for albums (issue #321). Membership is set by scalar photo id
/// only; the ordered <c>PhotoIds</c> array defines each member's <c>Position</c>. The cover photo is
/// validated against the post-replace membership (§7 album PUT evaluation order).
/// </summary>
public class PhotoAlbumService
{
    private readonly OdysseyContext context;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;

    public PhotoAlbumService(
        OdysseyContext context,
        IJournalLimitsLookup journalLimits,
        TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.journalLimits = journalLimits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PagedResult<PhotoAlbumSummary>> ListAsync(
        AlbumsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.PhotoAlbums.AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(a => EF.Functions.Like(a.Name, pattern) ||
                             (a.Description != null && EF.Functions.Like(a.Description, pattern)));
        }

        q = query.Status switch
        {
            ArchivalStatus.Archived => q.Where(a => a.Archived != null),
            ArchivalStatus.Active => q.Where(a => a.Archived == null),
            _ => q,
        };

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is not PhotoAlbumSortBy.CreatedAt);
        IOrderedQueryable<PhotoAlbum> sorted = query.SortBy switch
        {
            PhotoAlbumSortBy.CreatedAt => ascending ? q.OrderBy(a => a.CreatedAt) : q.OrderByDescending(a => a.CreatedAt),
            PhotoAlbumSortBy.Status => ascending ? q.OrderBy(a => a.Archived != null) : q.OrderByDescending(a => a.Archived != null),
            _ => ascending ? q.OrderBy(a => a.Name) : q.OrderByDescending(a => a.Name),
        };

        var rows = sorted
            .ThenBy(a => a.PhotoAlbumId)
            .Select(a => new PhotoAlbumSummary
            {
                PhotoAlbumId = a.PhotoAlbumId,
                Name = a.Name,
                Description = a.Description,
                CoverPhotoId = a.CoverPhotoId,
                // Resolve the cover's backing file id in the same query so the list can show a thumbnail
                // without a per-photo round trip.
                CoverFileId = context.Photos
                    .Where(p => p.PhotoId == a.CoverPhotoId)
                    .Select(p => (Guid?)p.FileId)
                    .FirstOrDefault(),
                Archived = a.Archived,
                CreatedByUserId = a.CreatedByUserId,
                PhotoCount = a.Items.Count,
            });

        return await rows.ToPagedResultAsync(query.Offset, query.Limit, cancellationToken);
    }

    public async Task<ExistingPhotoAlbum?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var album = await LoadWithItems(id, cancellationToken);
        return album is null ? null : ToDto(album);
    }

    public async Task<ExistingPhotoAlbum> Create(NewPhotoAlbum request, string userId, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException("Name is required.");
        }

        var photoIds = DistinctCappedMembers(
            request.PhotoIds, (await journalLimits.GetAsync(cancellationToken)).PhotoMaxAlbumMembers);
        await EnsurePhotosExist(photoIds, cancellationToken);

        // On create there is no prior membership, so a cover must be one of the incoming members.
        if (request.CoverPhotoId is { } cover && !photoIds.Contains(cover))
        {
            throw new DomainUnprocessableException("The cover photo must be a member of the album.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var album = new PhotoAlbum
        {
            Name = name,
            Description = request.Description,
            CoverPhotoId = request.CoverPhotoId,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var i = 0; i < photoIds.Count; i++)
        {
            album.Items.Add(new PhotoAlbumItem { PhotoId = photoIds[i], Position = i, CreatedAt = now });
        }

        context.PhotoAlbums.Add(album);
        await context.SaveChangesAsync(cancellationToken);

        var created = await LoadWithItems(album.PhotoAlbumId, cancellationToken);
        return ToDto(created!);
    }

    public async Task<ExistingPhotoAlbum?> Update(Guid id, UpdatePhotoAlbum request, string userId, CancellationToken cancellationToken = default)
    {
        var album = await LoadWithItemsForUpdate(id, cancellationToken);
        if (album is null)
        {
            return null;
        }

        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException("Name is required.");
        }

        // (a) validate incoming photo ids exist.
        var photoIds = DistinctCappedMembers(
            request.PhotoIds, (await journalLimits.GetAsync(cancellationToken)).PhotoMaxAlbumMembers);
        await EnsurePhotosExist(photoIds, cancellationToken);

        var previousMembers = album.Items.Select(i => i.PhotoId).ToHashSet();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        album.Name = name;
        album.Description = request.Description;
        album.UpdatedByUserId = userId;
        album.UpdatedAt = now;
        ApplyArchiveTransition(album, request.Archived);

        // (b) replace membership + positions.
        ReplaceMembership(album, photoIds, now);

        // (c) resolve the cover against the POST-replace membership.
        album.CoverPhotoId = ResolveCover(request.CoverPhotoId, photoIds, previousMembers);

        await context.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadWithItems(id, cancellationToken);
        return ToDto(reloaded!);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var album = await context.PhotoAlbums.FirstOrDefaultAsync(a => a.PhotoAlbumId == id, cancellationToken);
        if (album is null)
        {
            return false;
        }

        // The album's membership rows cascade; the member photos (and their files) are untouched (§7).
        context.PhotoAlbums.Remove(album);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<Guid> DistinctCappedMembers(Guid[] photoIds, int maxAlbumMembers)
    {
        var distinct = photoIds.Distinct().ToList();
        if (distinct.Count > maxAlbumMembers)
        {
            throw new DomainUnprocessableException(
                $"An album cannot contain more than {maxAlbumMembers} photos.");
        }

        return distinct;
    }

    private async Task EnsurePhotosExist(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken)
    {
        if (photoIds.Count == 0)
        {
            return;
        }

        var found = await context.Photos
            .Where(p => photoIds.Contains(p.PhotoId))
            .Select(p => p.PhotoId)
            .ToListAsync(cancellationToken);

        var missing = photoIds.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException($"Unknown photo(s): {string.Join(", ", missing)}.");
        }
    }

    // Set the cover against the post-replace membership: a member is honoured; a photo that this same
    // request removed from the album is dropped to null (§7 13c); a photo that was never a member is
    // rejected (§16.7).
    private static Guid? ResolveCover(Guid? cover, IReadOnlyCollection<Guid> membership, IReadOnlyCollection<Guid> previousMembers)
    {
        if (cover is not { } coverId)
        {
            return null;
        }

        if (membership.Contains(coverId))
        {
            return coverId;
        }

        if (previousMembers.Contains(coverId))
        {
            return null;
        }

        throw new DomainUnprocessableException("The cover photo must be a member of the album.");
    }

    private void ReplaceMembership(PhotoAlbum album, IReadOnlyList<Guid> desiredPhotoIds, DateTime now)
    {
        var desired = desiredPhotoIds.ToHashSet();

        foreach (var item in album.Items.Where(i => !desired.Contains(i.PhotoId)).ToList())
        {
            album.Items.Remove(item);
            context.PhotoAlbumItems.Remove(item);
        }

        var existing = album.Items.ToDictionary(i => i.PhotoId);
        for (var i = 0; i < desiredPhotoIds.Count; i++)
        {
            var photoId = desiredPhotoIds[i];
            if (existing.TryGetValue(photoId, out var item))
            {
                item.Position = i;
            }
            else
            {
                album.Items.Add(new PhotoAlbumItem { PhotoAlbumId = album.PhotoAlbumId, PhotoId = photoId, Position = i, CreatedAt = now });
            }
        }
    }

    private void ApplyArchiveTransition(PhotoAlbum album, bool requestedArchived)
    {
        var currentArchived = album.Archived is not null;
        if (!currentArchived && requestedArchived)
        {
            album.Archived = timeProvider.GetUtcNow().UtcDateTime;
        }
        else if (currentArchived && !requestedArchived)
        {
            album.Archived = null;
        }
    }

    // Update is the only caller that writes to what it loads; every other one turns the row straight
    // into a DTO, so it reads through the untracked overload.
    private async Task<PhotoAlbum?> LoadWithItems(Guid id, CancellationToken cancellationToken) =>
        await WithItems(context.PhotoAlbums.AsNoTracking())
            .FirstOrDefaultAsync(a => a.PhotoAlbumId == id, cancellationToken);

    private async Task<PhotoAlbum?> LoadWithItemsForUpdate(Guid id, CancellationToken cancellationToken) =>
        await WithItems(context.PhotoAlbums)
            .FirstOrDefaultAsync(a => a.PhotoAlbumId == id, cancellationToken);

    private static IQueryable<PhotoAlbum> WithItems(IQueryable<PhotoAlbum> albums) =>
        albums.Include(a => a.Items);

    private static ExistingPhotoAlbum ToDto(PhotoAlbum album) => new()
    {
        PhotoAlbumId = album.PhotoAlbumId,
        Name = album.Name,
        Description = album.Description,
        CoverPhotoId = album.CoverPhotoId,
        Archived = album.Archived,
        CreatedByUserId = album.CreatedByUserId,
        UpdatedByUserId = album.UpdatedByUserId,
        CreatedAt = album.CreatedAt,
        UpdatedAt = album.UpdatedAt,
        PhotoIds = album.Items.OrderBy(i => i.Position).ThenBy(i => i.PhotoAlbumItemId).Select(i => i.PhotoId).ToList(),
    };
}
