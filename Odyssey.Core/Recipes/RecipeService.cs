using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes;

/// <summary>
/// The household's recipes (issue #525). Shared data gated by claims alone; the caller's <c>photos.read</c> and
/// <c>recipes.tags.read</c> arrive as arguments because the service decides what they unlock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim before existence.</b> Adding a photo or tag link is refused for a caller without the claim before any
/// lookup runs, so a missing claim and a missing id give the same <c>403</c> (§7.4). This deliberately differs from
/// <see cref="JournalEntryService"/>, which validates existence first.
/// </para>
/// <para>
/// <b>Photos are absent, not hidden.</b> Without <c>photos.read</c> no photo lookup runs and the photo fields are
/// null (§7.3).
/// </para>
/// <para>
/// <b>Writes.</b> Ingredients and steps are replaced wholesale (tracked <c>RemoveRange</c> + add, safe because their
/// position index is not unique); photo and tag links are diffed in place so their unique pair indexes never see a
/// delete and re-insert of one pair in one save.
/// </para>
/// </remarks>
public sealed class RecipeService(
    OdysseyContext context,
    IPhotoLookup photos,
    ILogger<RecipeService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // ── Reads ────────────────────────────────────────────────────────────────

    /// <summary>The paged list (§5 item 1).</summary>
    public async Task<PagedResult<RecipeSummary>> ListAsync(
        RecipesQueryParams query, bool canReadPhotos, CancellationToken cancellationToken = default)
    {
        if (query.HasPhotos is not null && !canReadPhotos)
        {
            throw new DomainForbiddenException("Filtering by photos requires the photos.read permission.");
        }

        var q = context.Recipes.AsNoTracking();

        q = query.Status == ArchivalStatus.Archived ? q.Where(r => r.Archived != null) : q.Where(r => r.Archived == null);

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(r => EF.Functions.Like(r.Title, pattern)
                             || r.Ingredients.Any(i => EF.Functions.Like(i.Name, pattern)));
        }

        foreach (var tagId in (query.TagIds ?? []).Distinct())
        {
            q = q.Where(r => r.Tags.Any(t => t.RecipeTagId == tagId));
        }

        if (query.MaxTotalMinutes is { } maxTotal)
        {
            q = q.Where(r => (r.PrepMinutes != null || r.CookMinutes != null)
                             && (r.PrepMinutes ?? 0) + (r.CookMinutes ?? 0) <= maxTotal);
        }

        if (query.HasPhotos is { } hasPhotos)
        {
            q = hasPhotos ? q.Where(r => r.Photos.Any()) : q.Where(r => !r.Photos.Any());
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or RecipeSortBy.Title);
        IOrderedQueryable<Recipe> sorted = query.SortBy switch
        {
            RecipeSortBy.UpdatedAt => ascending ? q.OrderBy(r => r.UpdatedAt) : q.OrderByDescending(r => r.UpdatedAt),
            RecipeSortBy.CreatedAt => ascending ? q.OrderBy(r => r.CreatedAt) : q.OrderByDescending(r => r.CreatedAt),
            // Nulls last in both directions: the null flag sorts first, ascending, whatever the direction asked.
            RecipeSortBy.TotalMinutes => ascending
                ? q.OrderBy(r => r.PrepMinutes == null && r.CookMinutes == null)
                    .ThenBy(r => (r.PrepMinutes ?? 0) + (r.CookMinutes ?? 0))
                : q.OrderBy(r => r.PrepMinutes == null && r.CookMinutes == null)
                    .ThenByDescending(r => (r.PrepMinutes ?? 0) + (r.CookMinutes ?? 0)),
            _ => ascending ? q.OrderBy(r => r.Title) : q.OrderByDescending(r => r.Title),
        };

        var rows = sorted
            .ThenBy(r => r.RecipeId)
            .Select(r => new SummaryRow
            {
                RecipeId = r.RecipeId,
                Title = r.Title,
                Description = r.Description,
                Servings = r.Servings,
                ServingsLabel = r.ServingsLabel,
                PrepMinutes = r.PrepMinutes,
                CookMinutes = r.CookMinutes,
                IngredientCount = r.Ingredients.Count,
                StepCount = r.Steps.Count,
                CoverPhotoId = r.Photos
                    .OrderBy(p => p.Position)
                    .ThenBy(p => p.RecipePhotoId)
                    .Select(p => (Guid?)p.PhotoId)
                    .FirstOrDefault(),
                Tags = r.Tags
                    .Select(t => new TagIconCandidate(
                        t.RecipeTagId,
                        t.RecipeTag == null ? null : t.RecipeTag.Name,
                        t.RecipeTag == null ? null : t.RecipeTag.Icon,
                        t.RecipeTag == null ? null : t.RecipeTag.Color))
                    .ToList(),
                UpdatedAt = r.UpdatedAt,
                Archived = r.Archived,
            });

        var page = await rows.ToPagedResultAsync(query.Offset, query.Limit, row => TagIconProjection.Apply(new RecipeSummary
        {
            RecipeId = row.RecipeId,
            Title = row.Title,
            Description = row.Description,
            Servings = row.Servings,
            ServingsLabel = row.ServingsLabel,
            PrepMinutes = row.PrepMinutes,
            CookMinutes = row.CookMinutes,
            TotalMinutes = Total(row.PrepMinutes, row.CookMinutes),
            IngredientCount = row.IngredientCount,
            StepCount = row.StepCount,
            CoverPhotoId = canReadPhotos ? row.CoverPhotoId : null,
            UpdatedAt = row.UpdatedAt,
            Archived = row.Archived,
        }, row.Tags), cancellationToken);

        if (canReadPhotos)
        {
            var covers = page.Items.Where(i => i.CoverPhotoId is not null).Select(i => i.CoverPhotoId!.Value).Distinct().ToList();
            if (covers.Count > 0)
            {
                var ready = await PhotoThumbnailService.ReadyAsync(context, covers, cancellationToken);
                foreach (var item in page.Items)
                {
                    item.CoverThumbnailsReady = item.CoverPhotoId is { } cover && ready.Contains(cover);
                }
            }
        }

        return page;
    }

    /// <summary>One recipe, optionally scaled to <paramref name="servings"/>; <c>null</c> when it does not exist.</summary>
    public async Task<RecipeResult?> GetAsync(
        Guid id, int? servings, bool canReadPhotos, CancellationToken cancellationToken = default)
    {
        var recipe = await WithDetails(context.Recipes.AsNoTracking())
            .FirstOrDefaultAsync(r => r.RecipeId == id, cancellationToken);
        if (recipe is null)
        {
            return null;
        }

        var result = await ToResultAsync(recipe, canReadPhotos, cancellationToken);
        if (servings is { } wanted)
        {
            RecipeServingsScaler.Apply(result.Recipe, wanted);
        }

        return result;
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    public async Task<RecipeResult> CreateAsync(
        NewRecipe request, string userId, bool canReadPhotos, bool canReadTags, CancellationToken cancellationToken = default)
    {
        var photoIds = request.PhotoIds ?? [];
        var tagIds = request.TagIds ?? [];

        // Claims before any lookup (§3 step 5).
        if (photoIds.Length > 0 && !canReadPhotos)
        {
            throw PhotoLinkForbidden();
        }

        if (tagIds.Length > 0 && !canReadTags)
        {
            throw TagLinkForbidden();
        }

        var fields = Normalize(request);
        var distinctPhotos = Distinct(photoIds, "photoIds", "photo");
        var distinctTags = Distinct(tagIds, "tagIds", "tag");

        await EnsureTagsLinkable(distinctTags, kept: new HashSet<Guid>(), cancellationToken);
        await EnsurePhotosExist(distinctPhotos, cancellationToken);

        var now = clock.GetUtcNow().UtcDateTime;
        var recipe = new Recipe
        {
            Title = fields.Title,
            Version = 1,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(recipe, fields);
        AddChildren(recipe, fields);

        for (var i = 0; i < distinctPhotos.Count; i++)
        {
            recipe.Photos.Add(new RecipePhoto { PhotoId = distinctPhotos[i], Position = i, CreatedAt = now });
        }

        foreach (var tagId in distinctTags)
        {
            recipe.Tags.Add(new RecipeTagLink { RecipeTagId = tagId });
        }

        context.Recipes.Add(recipe);
        await SaveAsync(cancellationToken);

        return await ToResultAsync(recipe, canReadPhotos, cancellationToken);
    }

    /// <summary>A full replacement guarded by <see cref="UpdateRecipe.Version"/>; <c>null</c> when the recipe does not exist.</summary>
    public async Task<RecipeResult?> UpdateAsync(
        Guid id, UpdateRecipe request, string userId, bool canReadPhotos, bool canReadTags,
        CancellationToken cancellationToken = default)
    {
        // §3 step 4: load first — the claim checks need the current link set — and 404 the same for every caller.
        var recipe = await WithDetails(context.Recipes).FirstOrDefaultAsync(r => r.RecipeId == id, cancellationToken);
        if (recipe is null)
        {
            return null;
        }

        var tagIds = request.TagIds ?? [];
        var keptPhotos = recipe.Photos.Select(p => p.PhotoId).ToHashSet();
        var keptTags = recipe.Tags.Select(t => t.RecipeTagId).ToHashSet();

        // §3 step 5: claims before any photo/tag lookup.
        // A caller without photos.read cannot see the current set, so it may not replace it (send null to leave it).
        if (request.PhotoIds is not null && !canReadPhotos)
        {
            throw PhotoLinkForbidden();
        }

        if (!canReadTags && tagIds.Any(tagId => !keptTags.Contains(tagId)))
        {
            throw TagLinkForbidden();
        }

        // §3 step 6.
        if (request.Version != recipe.Version)
        {
            throw new RecipeVersionConflictException();
        }

        var fields = Normalize(request);
        var distinctTags = Distinct(tagIds, "tagIds", "tag");
        var distinctPhotos = request.PhotoIds is null ? null : Distinct(request.PhotoIds, "photoIds", "photo");

        await EnsureTagsLinkable(distinctTags, keptTags, cancellationToken);
        if (distinctPhotos is not null)
        {
            await EnsurePhotosExist(distinctPhotos, cancellationToken);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        recipe.Title = fields.Title;
        Apply(recipe, fields);
        recipe.UpdatedByUserId = userId;
        recipe.UpdatedAt = now;
        recipe.Version++;
        ApplyArchiveTransition(recipe, request.Archived, now);

        context.RecipeIngredients.RemoveRange(recipe.Ingredients);
        context.RecipeSteps.RemoveRange(recipe.Steps);
        recipe.Ingredients.Clear();
        recipe.Steps.Clear();
        AddChildren(recipe, fields);

        if (distinctPhotos is not null)
        {
            DiffPhotos(recipe, distinctPhotos, now);
        }

        DiffTags(recipe, distinctTags);

        await SaveAsync(cancellationToken);

        return await ToResultAsync(recipe, canReadPhotos, cancellationToken);
    }

    /// <summary>Hard delete; children cascade, library photos and tags stay. Logs the id and the acting user.</summary>
    public async Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        // Children loaded so the EF InMemory tier, which enforces no keys, cascades them too.
        var recipe = await WithDetails(context.Recipes).FirstOrDefaultAsync(r => r.RecipeId == id, cancellationToken);
        if (recipe is null)
        {
            return false;
        }

        context.Recipes.Remove(recipe);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Version is a concurrency token, so a delete racing a PUT or another delete affects no row. A recipe a
            // concurrent delete removed is a 404; one a concurrent PUT changed is a revision the caller never saw,
            // which is the version conflict, not a silent delete.
            context.ChangeTracker.Clear();
            if (await context.Recipes.AsNoTracking().AnyAsync(r => r.RecipeId == id, cancellationToken))
            {
                throw new RecipeVersionConflictException();
            }

            return false;
        }

        // The only irreversible action gets a traceable line (§7.7): ids only, never a title.
        logger.LogInformation("Recipe {RecipeId} deleted by user {UserId}.", id, userId);
        return true;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Trims and cross-checks the request (§8.1), throwing a <c>422</c> keyed on the offending field. Over HTTP the
    /// annotations reject a blank title, name or step text first (<c>400</c>); these checks are the defence for a
    /// direct, non-HTTP caller.
    /// </summary>
    private static RecipeFields Normalize(RecipeRequest request)
    {
        var title = Required(request.Title, "title", "Enter a title.");
        if (request.Servings is not { } servings || servings < RecipeLimits.MinServings || servings > RecipeLimits.MaxServings)
        {
            throw new DomainUnprocessableException(
                $"Servings must be between {RecipeLimits.MinServings} and {RecipeLimits.MaxServings}.", "servings");
        }

        CheckMinutes(request.PrepMinutes, "prepMinutes");
        CheckMinutes(request.CookMinutes, "cookMinutes");

        var ingredients = request.Ingredients ?? [];
        var steps = request.Steps ?? [];
        if (ingredients.Length > RecipeLimits.MaxIngredients)
        {
            throw new DomainUnprocessableException(
                $"A recipe can have at most {RecipeLimits.MaxIngredients} ingredients.", "ingredients");
        }

        if (steps.Length > RecipeLimits.MaxSteps)
        {
            throw new DomainUnprocessableException(
                $"A recipe can have at most {RecipeLimits.MaxSteps} steps.", "steps");
        }

        var normalizedIngredients = new List<RecipeIngredient>(ingredients.Length);
        for (var i = 0; i < ingredients.Length; i++)
        {
            normalizedIngredients.Add(NormalizeIngredient(ingredients[i], i));
        }

        var normalizedSteps = new List<RecipeStep>(steps.Length);
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i] ?? throw new DomainUnprocessableException($"Step {i + 1} is missing.", $"steps[{i}]");
            normalizedSteps.Add(new RecipeStep
            {
                Position = i,
                Section = Optional(step.Section),
                Text = Required(step.Text, $"steps[{i}].text", $"Step {i + 1}: enter the instruction."),
            });
        }

        return new RecipeFields(
            title,
            Optional(request.Description),
            Optional(request.Notes),
            Optional(request.Source),
            servings,
            Optional(request.ServingsLabel),
            request.PrepMinutes,
            request.CookMinutes,
            normalizedIngredients,
            normalizedSteps);
    }

    private static RecipeIngredient NormalizeIngredient(RecipeIngredientRequest? line, int index)
    {
        if (line is null)
        {
            throw new DomainUnprocessableException($"Ingredient {index + 1} is missing.", $"ingredients[{index}]");
        }

        var key = $"ingredients[{index}]";
        if (line.Unit is not { } unit || !Enum.IsDefined(unit))
        {
            throw new DomainUnprocessableException($"Ingredient {index + 1}: choose a unit.", $"{key}.unit");
        }

        var name = Required(line.Name, $"{key}.name", $"Ingredient {index + 1}: enter a name.");

        if (unit == IngredientUnit.ToTaste && (line.Amount is not null || line.AmountMax is not null))
        {
            throw new DomainUnprocessableException(
                $"Ingredient {index + 1}: an ingredient added to taste has no amount.", $"{key}.amount");
        }

        CheckAmount(line.Amount, index, $"{key}.amount");
        CheckAmount(line.AmountMax, index, $"{key}.amountMax");

        if (line.AmountMax is { } max)
        {
            if (line.Amount is not { } amount)
            {
                throw new DomainUnprocessableException(
                    $"Ingredient {index + 1}: enter the amount before the range end.", $"{key}.amountMax");
            }

            if (max <= amount)
            {
                throw new DomainUnprocessableException(
                    $"Ingredient {index + 1}: the range end must be larger than the amount.", $"{key}.amountMax");
            }
        }

        return new RecipeIngredient
        {
            Position = index,
            Section = Optional(line.Section),
            Amount = line.Amount,
            AmountMax = line.AmountMax,
            Unit = unit,
            Name = name,
            Preparation = Optional(line.Preparation),
            Optional = line.Optional,
        };
    }

    private static void CheckAmount(decimal? value, int index, string key)
    {
        if (value is not { } amount)
        {
            return;
        }

        if (amount < MinAmount || amount > MaxAmount)
        {
            throw new DomainUnprocessableException(
                $"Ingredient {index + 1}: enter an amount from {RecipeLimits.MinAmount} to {RecipeLimits.MaxAmount}.", key);
        }

        if (!RecipeAmounts.FitsScale(amount))
        {
            throw new DomainUnprocessableException(
                $"Ingredient {index + 1}: use at most {RecipeLimits.AmountScale} decimals.", key);
        }
    }

    private static readonly decimal MinAmount = decimal.Parse(RecipeLimits.MinAmount, System.Globalization.CultureInfo.InvariantCulture);
    private static readonly decimal MaxAmount = decimal.Parse(RecipeLimits.MaxAmount, System.Globalization.CultureInfo.InvariantCulture);

    private static void CheckMinutes(int? minutes, string key)
    {
        if (minutes is < 0 or > RecipeLimits.MaxMinutes)
        {
            throw new DomainUnprocessableException(
                $"Enter minutes from 0 to {RecipeLimits.MaxMinutes}, or leave it empty.", key);
        }
    }

    private static string Required(string? value, string key, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainUnprocessableException(message, key);
        }

        return trimmed;
    }

    private static string? Optional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static List<Guid> Distinct(Guid[] ids, string key, string kind)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count != ids.Length)
        {
            throw new DomainUnprocessableException($"The same {kind} is listed more than once.", key);
        }

        return distinct;
    }

    /// <summary>Every id is a tag, and every newly added one is active; a kept archived tag stays (§8.1).</summary>
    private async Task EnsureTagsLinkable(IReadOnlyCollection<Guid> tagIds, IReadOnlySet<Guid> kept, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return;
        }

        var found = await context.RecipeTags.AsNoTracking()
            .Where(t => tagIds.Contains(t.RecipeTagId))
            .Select(t => new { t.RecipeTagId, t.Archived })
            .ToListAsync(cancellationToken);

        var unusable = tagIds
            .Where(id => found.FirstOrDefault(t => t.RecipeTagId == id) is not { } tag
                         || (tag.Archived is not null && !kept.Contains(id)))
            .ToList();
        if (unusable.Count > 0)
        {
            throw new DomainUnprocessableException(
                $"Unknown or archived recipe tag(s): {string.Join(", ", unusable)}.", "tagIds");
        }
    }

    private async Task EnsurePhotosExist(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken)
    {
        if (photoIds.Count == 0)
        {
            return;
        }

        var found = await photos.ExistingIdsAsync(photoIds, cancellationToken);
        var missing = photoIds.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException($"Unknown photo(s): {string.Join(", ", missing)}.", "photoIds");
        }
    }

    private static DomainForbiddenException PhotoLinkForbidden() =>
        new("Linking photos requires the photos.read permission.");

    private static DomainForbiddenException TagLinkForbidden() =>
        new("Linking tags requires the recipes.tags.read permission.");

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static void Apply(Recipe recipe, RecipeFields fields)
    {
        recipe.Title = fields.Title;
        recipe.Description = fields.Description;
        recipe.Notes = fields.Notes;
        recipe.Source = fields.Source;
        recipe.Servings = fields.Servings;
        recipe.ServingsLabel = fields.ServingsLabel;
        recipe.PrepMinutes = fields.PrepMinutes;
        recipe.CookMinutes = fields.CookMinutes;
    }

    private static void AddChildren(Recipe recipe, RecipeFields fields)
    {
        foreach (var ingredient in fields.Ingredients)
        {
            recipe.Ingredients.Add(ingredient);
        }

        foreach (var step in fields.Steps)
        {
            recipe.Steps.Add(step);
        }
    }

    private void DiffPhotos(Recipe recipe, IReadOnlyList<Guid> desired, DateTime now)
    {
        var wanted = desired.ToHashSet();
        foreach (var link in recipe.Photos.Where(p => !wanted.Contains(p.PhotoId)).ToList())
        {
            recipe.Photos.Remove(link);
            context.RecipePhotos.Remove(link);
        }

        var existing = recipe.Photos.ToDictionary(p => p.PhotoId);
        for (var i = 0; i < desired.Count; i++)
        {
            if (existing.TryGetValue(desired[i], out var link))
            {
                link.Position = i;
            }
            else
            {
                recipe.Photos.Add(new RecipePhoto { RecipeId = recipe.RecipeId, PhotoId = desired[i], Position = i, CreatedAt = now });
            }
        }
    }

    private void DiffTags(Recipe recipe, IReadOnlyCollection<Guid> desired)
    {
        var wanted = desired.ToHashSet();
        foreach (var link in recipe.Tags.Where(t => !wanted.Contains(t.RecipeTagId)).ToList())
        {
            recipe.Tags.Remove(link);
            context.RecipeTagLinks.Remove(link);
        }

        var existing = recipe.Tags.Select(t => t.RecipeTagId).ToHashSet();
        foreach (var tagId in desired.Where(id => !existing.Contains(id)))
        {
            recipe.Tags.Add(new RecipeTagLink { RecipeId = recipe.RecipeId, RecipeTagId = tagId });
        }
    }

    private void ApplyArchiveTransition(Recipe recipe, bool requested, DateTime now)
    {
        if (requested && recipe.Archived is null)
        {
            recipe.Archived = now;
        }
        else if (!requested && recipe.Archived is not null)
        {
            recipe.Archived = null;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new RecipeVersionConflictException();
        }
        catch (DbUpdateException ex) when (DbErrors.IsMissingReferenceFrom(ex, "RecipePhotos"))
        {
            throw new DomainUnprocessableException("A linked photo no longer exists.", "photoIds");
        }
        catch (DbUpdateException ex) when (DbErrors.IsMissingReferenceFrom(ex, "RecipeTagLinks"))
        {
            throw new DomainUnprocessableException("A linked recipe tag no longer exists.", "tagIds");
        }
    }

    private static IQueryable<Recipe> WithDetails(IQueryable<Recipe> recipes) => recipes
        .Include(r => r.Ingredients)
        .Include(r => r.Steps)
        .Include(r => r.Photos)
        .Include(r => r.Tags)
        .AsSplitQuery();

    private async Task<RecipeResult> ToResultAsync(Recipe recipe, bool canReadPhotos, CancellationToken cancellationToken)
    {
        var dto = new ExistingRecipe
        {
            RecipeId = recipe.RecipeId,
            Version = recipe.Version,
            Title = recipe.Title,
            Description = recipe.Description,
            Notes = recipe.Notes,
            Source = recipe.Source,
            Servings = recipe.Servings,
            ServingsLabel = recipe.ServingsLabel,
            DisplayedServings = recipe.Servings,
            PrepMinutes = recipe.PrepMinutes,
            CookMinutes = recipe.CookMinutes,
            TotalMinutes = Total(recipe.PrepMinutes, recipe.CookMinutes),
            CreatedAt = recipe.CreatedAt,
            UpdatedAt = recipe.UpdatedAt,
            Archived = recipe.Archived,
            Ingredients = recipe.Ingredients
                .OrderBy(i => i.Position).ThenBy(i => i.RecipeIngredientId)
                .Select(i => new RecipeIngredientDto
                {
                    RecipeIngredientId = i.RecipeIngredientId,
                    Position = i.Position,
                    Section = i.Section,
                    Amount = RecipeAmounts.Trim(i.Amount),
                    AmountMax = RecipeAmounts.Trim(i.AmountMax),
                    Unit = i.Unit,
                    Name = i.Name,
                    Preparation = i.Preparation,
                    Optional = i.Optional,
                })
                .ToList(),
            Steps = recipe.Steps
                .OrderBy(s => s.Position).ThenBy(s => s.RecipeStepId)
                .Select(s => new RecipeStepDto
                {
                    RecipeStepId = s.RecipeStepId,
                    Position = s.Position,
                    Section = s.Section,
                    Text = s.Text,
                })
                .ToList(),
        };

        var tagIds = recipe.Tags.Select(t => t.RecipeTagId).ToList();
        TagIconProjection.Apply(dto, tagIds, await TagIconProjection.RecipeTagsAsync(context, tagIds, cancellationToken));

        if (canReadPhotos)
        {
            var links = recipe.Photos.OrderBy(p => p.Position).ThenBy(p => p.RecipePhotoId).ToList();
            var linkedIds = links.Select(p => p.PhotoId).ToList();
            var info = await photos.ResolveAsync(linkedIds, cancellationToken);
            var ready = await PhotoThumbnailService.ReadyAsync(context, linkedIds, cancellationToken);
            dto.Photos = links
                .Where(p => info.ContainsKey(p.PhotoId))
                .Select(p => new RecipePhotoDto
                {
                    RecipePhotoId = p.RecipePhotoId,
                    PhotoId = p.PhotoId,
                    Position = p.Position,
                    Title = info[p.PhotoId].Title,
                    FileName = info[p.PhotoId].FileName,
                    ThumbnailsReady = ready.Contains(p.PhotoId),
                })
                .ToList();
        }

        return new RecipeResult(dto, recipe.CreatedByUserId, recipe.UpdatedByUserId);
    }

    private static int? Total(int? prep, int? cook) =>
        prep is null && cook is null ? null : (prep ?? 0) + (cook ?? 0);

    private sealed record RecipeFields(
        string Title,
        string? Description,
        string? Notes,
        string? Source,
        int Servings,
        string? ServingsLabel,
        int? PrepMinutes,
        int? CookMinutes,
        List<RecipeIngredient> Ingredients,
        List<RecipeStep> Steps);

    private sealed class SummaryRow
    {
        public Guid RecipeId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public int Servings { get; set; }

        public string? ServingsLabel { get; set; }

        public int? PrepMinutes { get; set; }

        public int? CookMinutes { get; set; }

        public int IngredientCount { get; set; }

        public int StepCount { get; set; }

        public Guid? CoverPhotoId { get; set; }

        public List<TagIconCandidate> Tags { get; set; } = [];

        public DateTime UpdatedAt { get; set; }

        public DateTime? Archived { get; set; }
    }
}

/// <summary>
/// A recipe read plus its author attribution. The ids stay on the server: the controller resolves them to display names
/// (the resolver needs the caller) and never puts them on the wire (§7.3).
/// </summary>
public sealed record RecipeResult(ExistingRecipe Recipe, string? CreatedByUserId, string? UpdatedByUserId);

/// <summary>A <c>PUT</c> whose <c>version</c> is stale, or a save that lost the race. <c>409 recipe-version-conflict</c>.</summary>
public sealed class RecipeVersionConflictException : DomainException
{
    public RecipeVersionConflictException()
        : base("The recipe was changed by someone else. Reload and try again.")
    {
    }

    public override int StatusCode => (int)System.Net.HttpStatusCode.Conflict;

    public override string? Code => RecipeErrorCodes.VersionConflict;
}
