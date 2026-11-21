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
/// case-insensitive name uniqueness that a new or renamed tag checks against archived tags too, and a
/// delete-if-unused guard (in-use → 409).
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
        TagIconGuard.EnsureKnown(request.Icon);
        TagColorGuard.EnsureValid(request.Color);
        var name = request.Name.Trim();
        await EnsureNameAvailable(name, null, null, cancellationToken);

        var tag = new JournalTaskTag
        {
            Name = name,
            Description = request.Description,
            Archived = null,
            Icon = request.Icon,
            Color = HexColor.Normalize(request.Color),
        };

        context.JournalTaskTags.Add(tag);
        await context.SaveChangesAsync(cancellationToken);

        return tag.Adapt<ExistingJournalTaskTag>();
    }

    public async Task<ExistingJournalTaskTag?> Update(Guid id, UpdateJournalTaskTag request, CancellationToken cancellationToken = default)
    {
        TagIconGuard.EnsureKnown(request.Icon);
        TagColorGuard.EnsureValid(request.Color);

        var tag = await context.JournalTaskTags.FirstOrDefaultAsync(t => t.JournalTaskTagId == id, cancellationToken);
        if (tag is null)
        {
            return null;
        }

        var name = request.Name.Trim();
        await EnsureNameAvailable(name, id, tag.Name, cancellationToken);

        tag.Name = name;
        tag.Description = request.Description;
        // Full replacement: an omitted or null icon resets the tag to the default.
        tag.Icon = request.Icon;
        tag.Color = HexColor.Normalize(request.Color);
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

    /// <summary>
    /// A new name, or a rename, must not match any other task tag case-insensitively, archived ones included:
    /// an archived tag is not offered by the pickers, so reusing its name would put two tags with one name in
    /// the list the moment the archived one is restored. The design system's picker refuses such a name with
    /// "“X” is an archived tag. Restore it on the Task tags page to use it."; this is the server half.
    /// </summary>
    /// <remarks>
    /// A tag that keeps its name is checked against ACTIVE tags only. Databases from before this rule can
    /// hold an active and an archived tag with one name, and each must stay editable; restoring the archived
    /// one is still refused while the active one holds the name. Names are compared in memory with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>, which behaves identically on the relational store
    /// (utf8mb4 *_ci collation) and the InMemory provider used by tests.
    /// </remarks>
    private async Task EnsureNameAvailable(string name, Guid? excludeId, string? currentName, CancellationToken cancellationToken)
    {
        var renamed = currentName is null || !string.Equals(currentName.Trim(), name, StringComparison.OrdinalIgnoreCase);
        var others = await context.JournalTaskTags
            .AsNoTracking()
            .Where(t => excludeId == null || t.JournalTaskTagId != excludeId)
            .Select(t => new { t.Name, t.Archived })
            .ToListAsync(cancellationToken);

        var clash = others
            .Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Archived is null || renamed)
            .OrderBy(t => t.Archived is null ? 0 : 1)
            .FirstOrDefault();
        if (clash is null)
        {
            return;
        }

        // Keyed to `name` so the tag dialogs render it at the field rather than as an unattributed toast.
        throw new DomainConflictException(
            clash.Archived is null
                ? $"A task tag named '{name}' already exists."
                : $"A task tag named '{name}' already exists but is archived. Restore it, or rename it, to reuse the name.",
            nameof(NewJournalTaskTag.Name));
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
