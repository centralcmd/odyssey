using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;

namespace Odyssey.ApiClient.Resources;

/// <summary>The list filters of <c>GET /api/recipes</c> (issue #525 §5 item 1); <c>null</c> leaves a filter off.</summary>
public sealed record RecipeListFilter
{
    public string? Search { get; init; }

    public IReadOnlyCollection<Guid>? TagIds { get; init; }

    public int? MaxTotalMinutes { get; init; }

    /// <summary>Only for a <c>photos.read</c> holder; the server refuses it otherwise.</summary>
    public bool? HasPhotos { get; init; }

    public ArchivalStatus? Status { get; init; }

    public RecipeSortBy? SortBy { get; init; }

    public SortDirection? SortDir { get; init; }
}

/// <summary>
/// Typed client for the recipe endpoints (issue #525). Writes return the saved recipe, because its <c>version</c> is
/// what the next <c>PUT</c> must carry. Results are returned, never presented.
/// </summary>
public interface IRecipesApiClient
{
    /// <summary>One window of the list; <paramref name="page"/> is 1-based.</summary>
    Task<ApiResult<PagedResult<RecipeSummary>>> ListAsync(
        RecipeListFilter? filter = null, int page = 1, int pageSize = 25, CancellationToken ct = default);

    /// <summary>One recipe, scaled to <paramref name="servings"/> when given. A <c>404</c> is a failed result.</summary>
    Task<ApiResult<ExistingRecipe>> GetAsync(Guid id, int? servings = null, CancellationToken ct = default);

    Task<ApiResult<ExistingRecipe>> CreateAsync(NewRecipe recipe, CancellationToken ct = default);

    Task<ApiResult<ExistingRecipe>> UpdateAsync(Guid id, UpdateRecipe recipe, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IRecipesApiClient" />
public sealed class RecipesApiClient(IOdysseyApi api) : IRecipesApiClient
{
    private const string Base = "api/recipes";

    public Task<ApiResult<PagedResult<RecipeSummary>>> ListAsync(
        RecipeListFilter? filter = null, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        filter ??= new RecipeListFilter();
        var query = PagedQuery.For(Base)
            .Window(page, pageSize)
            .Add("search", filter.Search)
            .AddMany("tagIds", filter.TagIds?.Select(id => id.ToString()))
            .Add("maxTotalMinutes", filter.MaxTotalMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AddBool("hasPhotos", filter.HasPhotos)
            .Add("status", filter.Status?.ToString())
            .Add("sortBy", filter.SortBy?.ToString())
            .Add("sortDir", filter.SortDir?.ToString())
            .Build();
        return api.GetPagedAsync<RecipeSummary>(query, ct);
    }

    public Task<ApiResult<ExistingRecipe>> GetAsync(Guid id, int? servings = null, CancellationToken ct = default) =>
        api.GetAsync<ExistingRecipe>(
            servings is { } n ? $"{Base}/{id}?servings={n.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : $"{Base}/{id}",
            ct);

    public Task<ApiResult<ExistingRecipe>> CreateAsync(NewRecipe recipe, CancellationToken ct = default) =>
        api.SendAsync<ExistingRecipe>(HttpMethod.Post, Base, recipe, ct);

    public Task<ApiResult<ExistingRecipe>> UpdateAsync(Guid id, UpdateRecipe recipe, CancellationToken ct = default) =>
        api.SendAsync<ExistingRecipe>(HttpMethod.Put, $"{Base}/{id}", recipe, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}
