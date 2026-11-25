using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>
/// The Recipes page's way into AI recipe import (issue #604; design system · Navigation and entry): Import with AI
/// before New recipe, the Draft recipes ready signal — one row per waiting draft recipe — and Recipe imports in the
/// header ⋯ menu. A draft recipe is never listed among the recipes: it lives on its import.
/// </summary>
public partial class RecipesPage
{
    /// <summary>The signal shows at most this many draft recipes; the rest are on Recipe imports.</summary>
    internal const int SignalRows = 10;

    [Inject] private IRecipeImportsApiClient RecipeImports { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private bool _canImport;
    private RecipeImportOptions? _importOptions;
    private List<RecipeImportSummary> _pendingImports = [];
    private Guid _importKey;
    private bool _importOpen;

    private bool ImportEnabled => _importOptions is { Enabled: true };

    private IReadOnlyCollection<PageHeaderProblem>? ImportSignal => _pendingImports.Count == 0
        ? null
        : [.. _pendingImports.Select(i => new PageHeaderProblem
        {
            Severity = PageHeaderSeverity.Information,
            Lead = i.DraftTitle ?? "Untitled draft recipe",
            Message = "A draft recipe from Recipe imports is ready to review — it isn’t in Recipes until you create it.",
            ViewLabel = "Review",
            ActionAriaLabel = $"Review {i.DraftTitle ?? "the untitled draft recipe"}",
            OnView = EventCallback.Factory.Create(this, () => Navigation.NavigateTo($"/recipe-imports?open={i.Id}&review=true")),
        })];

    private async Task LoadImportSignalAsync()
    {
        _canImport = (await AuthenticationStateProvider.GetUserAsync()).HasPermission(PermissionClaims.RecipesImport);
        if (!_canImport)
        {
            return;
        }

        var options = RecipeImports.GetOptionsAsync();
        var pending = RecipeImports.ListAsync(new RecipeImportListFilter { HasPendingDraft = true }, 1, SignalRows);
        var optionsResult = await options;
        _importOptions = optionsResult.IsSuccess ? optionsResult.Value : null;
        _pendingImports = [.. (await pending).ValueOr(new()).Items];
        StateHasChanged();
    }

    private void StartImport()
    {
        if (!ImportEnabled)
        {
            return;
        }

        _importKey = Guid.NewGuid();
        _importOpen = true;
    }

    private Task OnImportSentAsync(RecipeImportDetail created) =>
        Announce("Import queued. The draft recipe will be ready to review on Recipe imports — it isn’t in Recipes until you create it.");
}
