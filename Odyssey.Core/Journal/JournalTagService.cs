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
/// CRUD for the module-local journal tags: name search + archival-status filter + allowlisted sort,
/// case-insensitive name uniqueness across active and archived tags, and a delete-if-unused guard (in-use → 409).
/// </summary>
public class JournalTagService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public JournalTagService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-side paged list (issue #277): name/description search + status filter + allowlisted sort.</summary>
    public async Task<PagedResult<ExistingJournalTag>> ListAsync(
        JournalTagsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.JournalTags.AsNoTracking();

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
        IOrderedQueryable<JournalTag> sorted = query.SortBy switch
        {
            JournalTagSortBy.Status => ascending ? q.OrderBy(t => t.Archived != null) : q.OrderByDescending(t => t.Archived != null),
            _ => ascending ? q.OrderBy(t => t.Name) : q.OrderByDescending(t => t.Name),
        };
        q = sorted.ThenBy(t => t.JournalTagId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, t => t.Adapt<ExistingJournalTag>(), cancellationToken);
    }

    public async Task<ExistingJournalTag?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await context.JournalTags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.JournalTagId == id, cancellationToken);
        return tag?.Adapt<ExistingJournalTag>();
    }

    public async Task<ExistingJournalTag> Create(NewJournalTag request, CancellationToken cancellationToken = default)
    {
        TagIconGuard.EnsureKnown(request.Icon);
        TagColorGuard.EnsureValid(request.Color);
        var name = request.Name.Trim();
        await EnsureNameAvailable(name, null, cancellationToken);

        var tag = new JournalTag
        {
            Name = name,
            Description = request.Description,
            Archived = null,
            Icon = request.Icon,
            Color = HexColor.Normalize(request.Color),
        };

        context.JournalTags.Add(tag);
        await SaveGuardingDuplicateName(name, cancellationToken);

        return tag.Adapt<ExistingJournalTag>();
    }

    public async Task<ExistingJournalTag?> Update(Guid id, UpdateJournalTag request, CancellationToken cancellationToken = default)
    {
        TagIconGuard.EnsureKnown(request.Icon);
        TagColorGuard.EnsureValid(request.Color);

        var tag = await context.JournalTags.FirstOrDefaultAsync(t => t.JournalTagId == id, cancellationToken);
        if (tag is null)
        {
            return null;
        }

        var name = request.Name.Trim();
        await EnsureNameAvailable(name, id, cancellationToken);

        tag.Name = name;
        tag.Description = request.Description;
        // Full replacement: an omitted or null icon resets the tag to the default.
        tag.Icon = request.Icon;
        tag.Color = HexColor.Normalize(request.Color);
        ApplyArchiveTransition(tag, request.Archived);

        await SaveGuardingDuplicateName(name, cancellationToken);

        return tag.Adapt<ExistingJournalTag>();
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await context.JournalTags.FirstOrDefaultAsync(t => t.JournalTagId == id, cancellationToken);
        if (tag is null)
        {
            return false;
        }

        var inUse = await context.JournalEntryTags.AnyAsync(t => t.JournalTagId == id, cancellationToken);
        if (inUse)
        {
            throw new DomainConflictException("Tag is in use by one or more entries; archive it instead of deleting.");
        }

        context.JournalTags.Remove(tag);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// A journal tag name is unique case-insensitively across ALL journal tags, archived ones included: an archived tag
    /// is not offered by the pickers, so reusing its name would put two tags with one name in the list the moment
    /// the archived one is restored. The design system's picker refuses such a name with "“X” is an archived tag.
    /// Restore it on the Journal tags page to use it."; this is the server half.
    /// </summary>
    /// <remarks>
    /// The comparison is <see cref="StringComparison.OrdinalIgnoreCase"/> rather than a database collation, so it
    /// holds on the EF InMemory tiers, which have no collation at all. The unique index on <c>Name</c>
    /// (<c>UniqueJournalAndTaskTagNames</c>) is the other half; this is what turns the violation into an explaining,
    /// field-keyed <c>409</c>.
    /// </remarks>
    private async Task EnsureNameAvailable(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        // Materialised rather than compared in SQL: EF cannot translate OrdinalIgnoreCase, and the tag table is
        // small reference data the pickers already load whole.
        var clash = (await context.JournalTags
                .AsNoTracking()
                .Where(t => excludeId == null || t.JournalTagId != excludeId)
                .Select(t => new { t.Name, t.Archived })
                .ToListAsync(cancellationToken))
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        if (clash is not null)
        {
            throw DuplicateName(name, clash.Archived is not null);
        }
    }

    /// <summary>
    /// Saves, translating the unique index's duplicate-key error into the <b>same</b> field-keyed <c>409</c> the
    /// pre-check throws, so the loser of a concurrent create or rename gets an answer the form can show.
    /// </summary>
    private async Task SaveGuardingDuplicateName(string name, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKeyOn(ex, "IX_JournalTags_Name"))
        {
            // The failed write is still tracked; leaving it would flush on the next save. The winner's archived
            // state is not known here, so the message takes the plain form.
            context.ChangeTracker.Clear();
            throw DuplicateName(name, archived: false);
        }
    }

    // Keyed to `name` so the tag dialogs render it at the field rather than as an unattributed toast.
    private static DomainConflictException DuplicateName(string name, bool archived) =>
        new(archived
                ? $"A journal tag named '{name}' already exists but is archived. Restore it, or rename it, to reuse the name."
                : $"A journal tag named '{name}' already exists.",
            nameof(NewJournalTag.Name));

    private void ApplyArchiveTransition(JournalTag tag, bool requestedArchived)
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
