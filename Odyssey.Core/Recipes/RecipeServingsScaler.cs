using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes;

/// <summary>
/// Read-time servings scaling (issue #525 §8.3). Pure: it rewrites a read DTO's amounts and never touches a row, so a
/// scaled read changes neither <c>UpdatedAt</c> nor <c>Version</c>.
/// </summary>
public static class RecipeServingsScaler
{
    private static readonly decimal Floor = decimal.Parse(RecipeLimits.MinAmount, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Scales <paramref name="recipe"/> to <paramref name="servings"/>. Equal to the stored servings, it only stamps
    /// <see cref="ExistingRecipe.DisplayedServings"/>; otherwise each scalable, non-null amount is multiplied by
    /// <c>servings / recipe.Servings</c>, rounded half away from zero to <see cref="RecipeLimits.AmountScale"/> places
    /// and floored at <see cref="RecipeLimits.MinAmount"/>. Units never change, and text in a name (such as a range a
    /// migration noted there, "garlic (4–6)") is never scaled.
    /// </summary>
    public static ExistingRecipe Apply(ExistingRecipe recipe, int servings)
    {
        recipe.DisplayedServings = servings;
        if (servings == recipe.Servings || recipe.Servings <= 0)
        {
            recipe.Scaled = false;
            recipe.DisplayedServings = recipe.Servings;
            return recipe;
        }

        var factor = (decimal)servings / recipe.Servings;
        foreach (var ingredient in recipe.Ingredients)
        {
            if (!IngredientUnitScaling.IsScalable(ingredient.Unit) || ingredient.Amount is not { } amount)
            {
                continue;
            }

            ingredient.Amount = Scale(amount, factor);
        }

        recipe.Scaled = true;
        return recipe;
    }

    private static decimal Scale(decimal value, decimal factor) =>
        RecipeAmounts.Trim(Math.Max(Floor, Math.Round(value * factor, RecipeLimits.AmountScale, MidpointRounding.AwayFromZero)));
}

/// <summary>Amount helpers shared by validation, mapping and scaling.</summary>
public static class RecipeAmounts
{
    /// <summary>Whether <paramref name="value"/> fits <see cref="RecipeLimits.AmountScale"/> decimal places.</summary>
    public static bool FitsScale(decimal value) =>
        decimal.Round(value, RecipeLimits.AmountScale) == value;

    /// <summary>Drops trailing zeros (<c>500.000</c> → <c>500</c>) so the wire carries the value, not the column scale.</summary>
    public static decimal Trim(decimal value) => value / 1.0000000000000000000000000000m;

    /// <inheritdoc cref="Trim(decimal)"/>
    public static decimal? Trim(decimal? value) => value is { } v ? Trim(v) : null;
}
