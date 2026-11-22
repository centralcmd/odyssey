using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Books;

namespace Odyssey.Core.Books;

/// <summary>
/// CRUD for the book tags (issue #527 §5), the photo-tag contract: name search, archival filter, global
/// case-insensitive name uniqueness across active and archived, and delete-if-unused (in use → 409, archive instead).
/// List rows carry <see cref="ExistingBookTag.BookCount"/>.
/// </summary>
public sealed class BookTagService(OdysseyContext context, TimeProvider? timeProvider = null)
{
    private const string InUseMessage = "Tag is in use by one or more books; archive it instead of deleting.";
    private const string NameTakenMessage = "A book tag with that name already exists.";

    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<PagedResult<ExistingBookTag>> ListAsync(BookTagsQueryParams query, CancellationToken cancellationToken = default)
    {
        var q = context.BookTags.AsNoTracking();

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

        var sortBy = query.SortBy ?? BookTagSortBy.Name;
        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: sortBy != BookTagSortBy.BookCount);
        IOrderedQueryable<BookTag> sorted = sortBy switch
        {
            BookTagSortBy.Status => ascending ? q.OrderBy(t => t.Archived != null) : q.OrderByDescending(t => t.Archived != null),
            BookTagSortBy.BookCount => ascending ? q.OrderBy(t => t.Links.Count) : q.OrderByDescending(t => t.Links.Count),
            _ => ascending ? q.OrderBy(t => t.Name) : q.OrderByDescending(t => t.Name),
        };

        return await sorted.ThenBy(t => t.BookTagId)
            .Select(t => new { Tag = t, Count = t.Links.Count })
            .ToPagedResultAsync(query.Offset, query.Limit, row => Map(row.Tag, row.Count), cancellationToken);
    }

    public async Task<ExistingBookTag?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await context.BookTags.AsNoTracking()
            .Where(t => t.BookTagId == id)
            .Select(t => new { Tag = t, Count = t.Links.Count })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Map(row.Tag, row.Count);
    }

    public async Task<ExistingBookTag> CreateAsync(NewBookTag request, CancellationToken cancellationToken = default)
    {
        TagIconGuard.EnsureKnown(request.Icon);
        TagColorGuard.EnsureValid(request.Color);
        var name = request.Name.Trim();
        await EnsureNameAvailableAsync(name, null, cancellationToken);

        var tag = new BookTag
        {
            Name = name,
            Description = request.Description,
            Icon = request.Icon,
            Color = HexColor.Normalize(request.Color),
        };
        context.BookTags.Add(tag);
        await SaveAsync(cancellationToken);
        return Map(tag, 0);
    }

    public async Task<ExistingBookTag?> UpdateAsync(Guid id, UpdateBookTag request, CancellationToken cancellationToken = default)
    {
        TagIconGuard.EnsureKnown(request.Icon);
        TagColorGuard.EnsureValid(request.Color);

        var tag = await context.BookTags.FirstOrDefaultAsync(t => t.BookTagId == id, cancellationToken);
        if (tag is null)
        {
            return null;
        }

        var name = request.Name.Trim();
        await EnsureNameAvailableAsync(name, id, cancellationToken);

        tag.Name = name;
        tag.Description = request.Description;
        tag.Icon = request.Icon;
        tag.Color = HexColor.Normalize(request.Color);
        if (tag.Archived is null && request.Archived)
        {
            tag.Archived = timeProvider.GetUtcNow().UtcDateTime;
        }
        else if (tag.Archived is not null && !request.Archived)
        {
            tag.Archived = null;
        }

        await SaveAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await context.BookTags.FirstOrDefaultAsync(t => t.BookTagId == id, cancellationToken);
        if (tag is null)
        {
            return false;
        }

        if (await context.BookTagLinks.AnyAsync(l => l.BookTagId == id, cancellationToken))
        {
            throw new DomainConflictException(InUseMessage);
        }

        context.BookTags.Remove(tag);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsReferencedRow(exception))
        {
            // A link added between the check and the delete: the RESTRICT key is the backstop.
            throw new DomainConflictException(InUseMessage);
        }

        return true;
    }

    private async Task EnsureNameAvailableAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var existingNames = await context.BookTags
            .Where(t => excludeId == null || t.BookTagId != excludeId)
            .Select(t => t.Name)
            .ToListAsync(cancellationToken);

        if (existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainConflictException(NameTakenMessage, "name");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsDuplicateKey(exception))
        {
            throw new DomainConflictException(NameTakenMessage, "name");
        }
    }

    private static ExistingBookTag Map(BookTag tag, int count) => new()
    {
        BookTagId = tag.BookTagId,
        Name = tag.Name,
        Description = tag.Description,
        Archived = tag.Archived,
        Icon = TagIcons.Normalize(tag.Icon),
        Color = HexColor.Normalize(tag.Color),
        BookCount = count,
    };
}
