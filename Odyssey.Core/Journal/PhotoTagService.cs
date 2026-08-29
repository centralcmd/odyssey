using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// CRUD for the module-local photo tags: name search + archival-status filter + allowlisted sort,
/// <b>global</b> case-insensitive name uniqueness (across active and archived, so keyword find-or-create
/// cannot create a case/archival-variant duplicate — §6/§7), and a delete-if-unused guard (in-use → 409).
/// </summary>
public class PhotoTagService(OdysseyContext context, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<PagedResult<ExistingPhotoTag>> ListAsync(
        PhotoTagsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.PhotoTags.AsNoTracking();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(t => EF.Functions.Like(t.Name, pattern) ||
                             (t.Description != null && EF.Functions.Like(t.Description, pattern)));
        }

        q = query.Status switch
        {
            ArchivalStatus.Archived => q.Where(t => t.Archived != null),
            ArchivalStatus.Active => q.Where(t => t.Archived == null),
            _ => q,
        };

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: true);
        IOrderedQueryable<PhotoTag> sorted = query.SortBy switch
        {
            PhotoTagSortBy.Status => ascending ? q.OrderBy(t => t.Archived != null) : q.OrderByDescending(t => t.Archived != null),
            _ => ascending ? q.OrderBy(t => t.Name) : q.OrderByDescending(t => t.Name),
        };
        q = sorted.ThenBy(t => t.PhotoTagId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, t => t.Adapt<ExistingPhotoTag>(), cancellationToken);
    }

    public async Task<ExistingPhotoTag?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await context.PhotoTags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.PhotoTagId == id, cancellationToken);
        return tag?.Adapt<ExistingPhotoTag>();
    }

    public async Task<ExistingPhotoTag> Create(NewPhotoTag request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        await EnsureNameAvailable(name, null, cancellationToken);

        var tag = new PhotoTag
        {
            Name = name,
            Description = request.Description,
            Archived = null,
        };

        context.PhotoTags.Add(tag);
        await context.SaveChangesAsync(cancellationToken);

        return tag.Adapt<ExistingPhotoTag>();
    }

    public async Task<ExistingPhotoTag?> Update(Guid id, UpdatePhotoTag request, CancellationToken cancellationToken = default)
    {
        var tag = await context.PhotoTags.FirstOrDefaultAsync(t => t.PhotoTagId == id, cancellationToken);
        if (tag is null)
        {
            return null;
        }

        var name = request.Name.Trim();
        await EnsureNameAvailable(name, id, cancellationToken);

        tag.Name = name;
        tag.Description = request.Description;
        ApplyArchiveTransition(tag, request.Archived);

        await context.SaveChangesAsync(cancellationToken);

        return tag.Adapt<ExistingPhotoTag>();
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await context.PhotoTags.FirstOrDefaultAsync(t => t.PhotoTagId == id, cancellationToken);
        if (tag is null)
        {
            return false;
        }

        var inUse = await context.PhotoTagLinks.AnyAsync(l => l.PhotoTagId == id, cancellationToken);
        if (inUse)
        {
            throw new DomainConflictException("Tag is in use by one or more photos; archive it instead of deleting.");
        }

        context.PhotoTags.Remove(tag);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Global case-insensitive uniqueness (active AND archived — the Name unique index spans both, §7):
    // candidate names are compared in memory with OrdinalIgnoreCase, which behaves identically on the
    // relational store (utf8mb4 *_ci collation) and the InMemory provider used by tests.
    private async Task EnsureNameAvailable(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var existingNames = await context.PhotoTags
            .Where(t => excludeId == null || t.PhotoTagId != excludeId)
            .Select(t => t.Name)
            .ToListAsync(cancellationToken);

        if (existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainConflictException($"A photo tag named '{name}' already exists.");
        }
    }

    private void ApplyArchiveTransition(PhotoTag tag, bool requestedArchived)
    {
        var currentArchived = tag.Archived is not null;
        if (!currentArchived && requestedArchived)
        {
            tag.Archived = timeProvider.GetUtcNow().UtcDateTime;
        }
        else if (currentArchived && !requestedArchived)
        {
            tag.Archived = null;
        }
    }
}
