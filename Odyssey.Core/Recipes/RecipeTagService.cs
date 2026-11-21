using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes;

/// <summary>
/// CRUD for recipe tags (issue #525), the <see cref="PhotoTagService"/> contract plus a recipe count: global
/// case-insensitive name uniqueness across active and archived tags, and delete-if-unused (in use → <c>409</c>).
/// An omitted status filter returns active and archived tags alike.
/// </summary>
public sealed class RecipeTagService(OdysseyContext context, TimeProvider? timeProvider = null)
{
    private const string InUseMessage = "Tag is in use by one or more recipes; archive it instead of deleting.";

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<PagedResult<ExistingRecipeTag>> ListAsync(
        RecipeTagsQueryParams query, CancellationToken cancellationToken = default)
    {
        var q = context.RecipeTags.AsNoTracking();

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

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is not RecipeTagSortBy.RecipeCount);
        IOrderedQueryable<RecipeTag> sorted = query.SortBy switch
        {
            RecipeTagSortBy.Status => ascending ? q.OrderBy(t => t.Archived != null) : q.OrderByDescending(t => t.Archived != null),
            RecipeTagSortBy.RecipeCount => ascending ? q.OrderBy(t => t.Recipes.Count) : q.OrderByDescending(t => t.Recipes.Count),
            _ => ascending ? q.OrderBy(t => t.Name) : q.OrderByDescending(t => t.Name),
        };

        var rows = sorted
            .ThenBy(t => t.Name)
            .ThenBy(t => t.RecipeTagId)
            .Select(t => new TagRow(t.RecipeTagId, t.Name, t.Description, t.Archived, t.Icon, t.Color, t.Recipes.Count));

        return await rows.ToPagedResultAsync(query.Offset, query.Limit, ToDto, cancellationToken);
    }

    public async Task<ExistingRecipeTag?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await context.RecipeTags.AsNoTracking()
            .Where(t => t.RecipeTagId == id)
            .Select(t => new TagRow(t.RecipeTagId, t.Name, t.Description, t.Archived, t.Icon, t.Color, t.Recipes.Count))
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToDto(row);
    }

    public async Task<ExistingRecipeTag> CreateAsync(NewRecipeTag request, CancellationToken cancellationToken = default)
    {
        TagIconGuard.EnsureKnown(request.Icon);
        TagColorGuard.EnsureValid(request.Color);
        var name = request.Name.Trim();
        await EnsureNameAvailable(name, null, cancellationToken);

        var tag = new RecipeTag
        {
            Name = name,
            Description = request.Description,
            Icon = request.Icon,
            Color = HexColor.Normalize(request.Color),
        };

        context.RecipeTags.Add(tag);
        await SaveAsync(name, cancellationToken);

        return ToDto(new TagRow(tag.RecipeTagId, tag.Name, tag.Description, tag.Archived, tag.Icon, tag.Color, 0));
    }

    public async Task<ExistingRecipeTag?> UpdateAsync(Guid id, UpdateRecipeTag request, CancellationToken cancellationToken = default)
    {
        TagIconGuard.EnsureKnown(request.Icon);
        TagColorGuard.EnsureValid(request.Color);

        var tag = await context.RecipeTags.FirstOrDefaultAsync(t => t.RecipeTagId == id, cancellationToken);
        if (tag is null)
        {
            return null;
        }

        var name = request.Name.Trim();
        await EnsureNameAvailable(name, id, cancellationToken);

        tag.Name = name;
        tag.Description = request.Description;
        // Full replacement: an omitted icon or colour resets it.
        tag.Icon = request.Icon;
        tag.Color = HexColor.Normalize(request.Color);
        if (request.Archived && tag.Archived is null)
        {
            tag.Archived = clock.GetUtcNow().UtcDateTime;
        }
        else if (!request.Archived && tag.Archived is not null)
        {
            tag.Archived = null;
        }

        await SaveAsync(name, cancellationToken);

        var count = await context.RecipeTagLinks.CountAsync(l => l.RecipeTagId == id, cancellationToken);
        return ToDto(new TagRow(tag.RecipeTagId, tag.Name, tag.Description, tag.Archived, tag.Icon, tag.Color, count));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await context.RecipeTags.FirstOrDefaultAsync(t => t.RecipeTagId == id, cancellationToken);
        if (tag is null)
        {
            return false;
        }

        if (await context.RecipeTagLinks.AnyAsync(l => l.RecipeTagId == id, cancellationToken))
        {
            throw new DomainConflictException(InUseMessage);
        }

        context.RecipeTags.Remove(tag);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsRowReferenced(ex))
        {
            // A recipe linked the tag between the check and the delete; the RESTRICT key is the backstop.
            throw new DomainConflictException(InUseMessage);
        }

        return true;
    }

    // Global case-insensitive uniqueness (active AND archived, as the unique index spans both). Compared in memory
    // with OrdinalIgnoreCase, which matches the *_ci collation and runs the same on the InMemory provider.
    private async Task EnsureNameAvailable(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var existingNames = await context.RecipeTags
            .Where(t => excludeId == null || t.RecipeTagId != excludeId)
            .Select(t => t.Name)
            .ToListAsync(cancellationToken);

        if (existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw NameTaken();
        }
    }

    private async Task SaveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // Lost a create/rename race to the same name; the unique index refused it.
            throw NameTaken();
        }
    }

    private static DomainConflictException NameTaken() => new("A recipe tag with that name already exists.", "name");

    private static ExistingRecipeTag ToDto(TagRow row) => new()
    {
        RecipeTagId = row.Id,
        Name = row.Name,
        Description = row.Description,
        Archived = row.Archived,
        Icon = TagIcons.Normalize(row.Icon),
        Color = HexColor.Normalize(row.Color),
        RecipeCount = row.RecipeCount,
    };

    private sealed record TagRow(
        Guid Id, string Name, string? Description, DateTime? Archived, string? Icon, string? Color, int RecipeCount);
}
