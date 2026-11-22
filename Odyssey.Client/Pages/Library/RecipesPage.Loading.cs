using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

public partial class RecipesPage
{
    // ── The list: server-paged, appended page by page (OdsInfiniteList remote mode) ──
    private List<RecipeSummary> _rows = [];
    private int? _totalCount;
    private int _page = 1;
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private bool _loadingMore;
    private bool _loadMoreFailed;
    private object _resetKey = 0;
    private int _generation;
    private CancellationTokenSource? _moreCts;
    // Latest-request-wins for the first page (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();

    // Whole-file tallies for the sub-line, independent of the filters.
    private int? _activeCount;
    private int? _withPhotosCount;

    private bool HasMore => _totalCount is { } total && _rows.Count < total;

    /// <summary>A fresh first page on any filter, sort, search or batch change.</summary>
    private async Task ResetAsync()
    {
        _moreCts?.Cancel();
        var generation = ++_generation;
        _refetching = !_isLoading;
        _loadingMore = false;
        _loadMoreFailed = false;
        if (_isLoading)
        {
            _ = Announce("Loading recipes…");
        }

        StateHasChanged();
        var filter = CurrentFilter();
        var response = await _listLoader.RunAsync(ct => Recipes.ListAsync(filter, 1, _batch, ct));
        if (response.IsSuperseded)
        {
            return;
        }

        var load = response.Value.PagedOrToast(Snackbar, "recipes");
        _loadError = !load.IsSuccess;
        _rows = [.. load.Items];
        _totalCount = load.IsSuccess ? load.TotalCount : null;
        _page = 1;
        _resetKey = generation;
        _isLoading = false;
        _refetching = false;
        // A recipe that left the list keeps no state behind it.
        if (_expandedId is { } open && _rows.All(r => r.RecipeId != open))
        {
            await CollapseAsync();
        }

        await Announce(_loadError ? "Recipes couldn’t be loaded."
            : _rows.Count == 0 ? (HasFilters ? "No recipes match." : "No recipes yet.")
            : $"Showing {_rows.Count} of {RecipeFormat.Count(_totalCount ?? _rows.Count, "recipe")}.");
        StateHasChanged();
    }

    /// <summary>The next page, appended. Never concurrent: the list asks only when no append is in flight.</summary>
    private async Task LoadMoreAsync(bool pressed)
    {
        if (_loadingMore || !HasMore)
        {
            return;
        }

        var generation = _generation;
        _moreCts?.Cancel();
        var cts = _moreCts = new CancellationTokenSource();
        _loadingMore = true;
        _loadMoreFailed = false;
        StateHasChanged();
        var result = await Recipes.ListAsync(CurrentFilter(), _page + 1, _batch, cts.Token);
        if (generation != _generation || cts.IsCancellationRequested)
        {
            return;
        }

        _loadingMore = false;
        if (result.IsSuccess && result.Value is { } page)
        {
            var known = _rows.Select(r => r.RecipeId).ToHashSet();
            var added = page.Items.Where(r => known.Add(r.RecipeId)).ToList();
            _rows.AddRange(added);
            _totalCount = page.TotalCount;
            _page++;
            await Announce($"{RecipeFormat.Count(added.Count, "more recipe")} loaded.");
        }
        else
        {
            _loadMoreFailed = true;
        }

        StateHasChanged();
    }

    private async Task LoadCountsAsync()
    {
        var active = Recipes.ListAsync(new RecipeListFilter { Status = ArchivalStatus.Active }, 1, 1);
        var photos = _canPhotos ? Recipes.ListAsync(new RecipeListFilter { Status = ArchivalStatus.Active, HasPhotos = true }, 1, 1) : null;
        var a = await active;
        _activeCount = a.IsSuccess ? a.Value?.TotalCount : null;
        if (photos is not null)
        {
            var p = await photos;
            _withPhotosCount = p.IsSuccess ? p.Value?.TotalCount : null;
        }

        StateHasChanged();
    }

    private async Task LoadTagsAsync()
    {
        if (!_canTags)
        {
            return;
        }

        var result = await RecipeTags.ListAllAsync();
        _tags = result.IsSuccess ? result.Value ?? [] : [];
        _tagsById = _tags.GroupBy(t => t.RecipeTagId).ToDictionary(g => g.Key, g => g.First());
        StateHasChanged();
    }

    // ── One card's read ──
    private readonly Dictionary<Guid, RecipeCookState> _cook = [];

    private RecipeCookState Cook(Guid id)
    {
        if (!_cook.TryGetValue(id, out var state))
        {
            _cook[id] = state = new RecipeCookState();
        }

        return state;
    }

    /// <summary>The card's read, at the servings it is scaled to. A 404 is the "no longer exists" line.</summary>
    private async Task LoadDetailAsync(Guid id)
    {
        var cook = Cook(id);
        cook.Phase = RecipeCardPhase.Loading;
        StateHasChanged();
        var result = await Recipes.GetAsync(id, cook.Servings);
        if (result.IsSuccess && result.Value is { } detail)
        {
            cook.Detail = detail;
            cook.Phase = RecipeCardPhase.Ready;
        }
        else
        {
            cook.Phase = result.Status == HttpStatusCode.NotFound ? RecipeCardPhase.Gone : RecipeCardPhase.Error;
        }

        StateHasChanged();
    }

    /// <summary>
    /// Change servings… → <c>GET /api/recipes/{id}?servings=n</c>. The server scales; while the read runs the amounts
    /// stay and dim. A failure keeps the earlier amounts under a warning with Try again.
    /// </summary>
    private async Task ScaleAsync(Guid id, int servings)
    {
        var cook = Cook(id);
        if (cook.Detail is not { } before)
        {
            return;
        }

        if (servings == before.DisplayedServings && !cook.ScaleError)
        {
            await Announce($"Amounts already show {servings}.");
            return;
        }

        cook.Scaling = true;
        cook.ScaleError = false;
        StateHasChanged();
        var written = servings == before.Servings;
        var result = await Recipes.GetAsync(id, written ? null : servings);
        cook.Scaling = false;
        if (result.IsSuccess && result.Value is { } scaled)
        {
            cook.Detail = scaled;
            cook.Servings = written ? null : servings;
            cook.FailedServings = null;
            await Announce(written ? $"Amounts reset to {before.Servings}." : $"Amounts scaled to {servings}.");
        }
        else
        {
            cook.ScaleError = true;
            cook.FailedServings = servings;
        }

        StateHasChanged();
    }

    private Task RetryScaleAsync(Guid id) =>
        Cook(id).FailedServings is { } n ? ScaleAsync(id, n) : Task.CompletedTask;

    /// <summary>The unscaled current read a write is built from — never a scaled one, whose amounts would be saved.</summary>
    private async Task<ExistingRecipe?> FreshAsync(Guid id)
    {
        var result = await Recipes.GetAsync(id);
        if (result.IsSuccess && result.Value is { } fresh)
        {
            return fresh;
        }

        if (result.Status == HttpStatusCode.NotFound)
        {
            Cook(id).Phase = RecipeCardPhase.Gone;
            await Announce("This recipe no longer exists.");
            StateHasChanged();
            return null;
        }

        Snackbar.Add($"Couldn’t read the recipe: {result.Error}", MudBlazor.Severity.Error);
        return null;
    }
}
