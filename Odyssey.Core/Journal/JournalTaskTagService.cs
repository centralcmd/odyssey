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
/// CRUD for the module-local task tags: name search + archival-status filter + allowlisted sort,
/// case-insensitive uniqueness among non-archived tags, and a delete-if-unused guard (in-use → 409).
/// </summary>
public class JournalTaskTagService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public JournalTaskTagService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-side paged list (issue #277): name/description search + status filter + allowlisted sort.</summary>
    public async Task<PagedResult<ExistingJournalTaskTag>> ListAsync(
        JournalTaskTagsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.JournalTaskTags.AsNoTracking();

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
        IOrderedQueryable<JournalTaskTag> sorted = query.SortBy switch
        {
            JournalTaskTagSortBy.Status => ascending ? q.OrderBy(t => t.Archived != null) : q.OrderByDescending(t => t.Archived != null),
            _ => ascending ? q.OrderBy(t => t.Name) : q.OrderByDescending(t => t.Name),
        };
        q = sorted.ThenBy(t => t.JournalTaskTagId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, t => t.Adapt<ExistingJournalTaskTag>(), cancellationToken);
    }

    public async Task<ExistingJournalTaskTag?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await context.JournalTaskTags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.JournalTaskTagId == id, cancellationToken);
        return tag?.Adapt<ExistingJournalTaskTag>();
    }

    public async Task<ExistingJournalTaskTag> Create(NewJournalTaskTag request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        await EnsureNameAvailable(name, null, cancellationToken);

        var tag = new JournalTaskTag
        {
            Name = name,
            Description = request.Description,
            Archived = null,
        };

        context.JournalTaskTags.Add(tag);
        await context.SaveChangesAsync(cancellationToken);

        return tag.Adapt<ExistingJournalTaskTag>();
    }

    public async Task<ExistingJournalTaskTag?> Update(Guid id, UpdateJournalTaskTag request, CancellationToken cancellationToken = default)
    {
        var tag = await context.JournalTaskTags.FirstOrDefaultAsync(t => t.JournalTaskTagId == id, cancellationToken);
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

        return tag.Adapt<ExistingJournalTaskTag>();
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await context.JournalTaskTags.FirstOrDefaultAsync(t => t.JournalTaskTagId == id, cancellationToken);
        if (tag is null)
        {
            return false;
        }

        var inUse = await context.JournalTaskTagLinks.AnyAsync(t => t.JournalTaskTagId == id, cancellationToken);
        if (inUse)
        {
            throw new DomainConflictException("Tag is in use by one or more tasks; archive it instead of deleting.");
        }

        context.JournalTaskTags.Remove(tag);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Case-insensitive uniqueness among non-archived tags, without a stored normalized column (see
    // JournalTagService for the rationale): OrdinalIgnoreCase matches the *_ci relational collation.
    private async Task EnsureNameAvailable(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var existingNames = await context.JournalTaskTags
            .Where(t => t.Archived == null && (excludeId == null || t.JournalTaskTagId != excludeId))
            .Select(t => t.Name)
            .ToListAsync(cancellationToken);

        if (existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainConflictException($"A task tag named '{name}' already exists.");
        }
    }

    private void ApplyArchiveTransition(JournalTaskTag tag, bool requestedArchived)
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
