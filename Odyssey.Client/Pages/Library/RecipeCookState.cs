using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>Where an open card's read stands (design system components-recipes.md · States).</summary>
public enum RecipeCardPhase { Loading, Ready, Error, Gone }

/// <summary>
/// One card's cooking state (issue #526): its read, the servings it is scaled to and the check-offs. Session-only —
/// nothing here is saved — and kept per recipe, so collapsing and reopening a card keeps its checks.
/// </summary>
public sealed class RecipeCookState
{
    public RecipeCardPhase Phase { get; set; } = RecipeCardPhase.Loading;

    /// <summary>The last successful read, scaled when <see cref="ExistingRecipe.Scaled"/>.</summary>
    public ExistingRecipe? Detail { get; set; }

    /// <summary>The servings asked for, or <c>null</c> for the written yield.</summary>
    public int? Servings { get; set; }

    /// <summary>A <c>?servings=</c> re-read is in flight: the amounts dim and the list is aria-busy.</summary>
    public bool Scaling { get; set; }

    /// <summary>The last scale failed; the amounts still show the earlier yield.</summary>
    public bool ScaleError { get; set; }

    /// <summary>The servings the failed scale asked for, so Try again repeats it.</summary>
    public int? FailedServings { get; set; }

    public HashSet<int> CheckedIngredients { get; } = [];

    public HashSet<int> CheckedSteps { get; } = [];

    public int CheckedCount => CheckedIngredients.Count + CheckedSteps.Count;

    /// <summary>The ⋯ menu's cooking actions are offered: the card is not known to be failed or gone.</summary>
    public bool Cookable => Phase is RecipeCardPhase.Loading or RecipeCardPhase.Ready;

    public void ClearChecks()
    {
        CheckedIngredients.Clear();
        CheckedSteps.Clear();
    }
}
