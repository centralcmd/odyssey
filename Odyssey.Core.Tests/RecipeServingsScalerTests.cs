using Odyssey.Core.Recipes;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>Read-time servings scaling (issue #525 §8.3, AC 7 and AC 25).</summary>
public class RecipeServingsScalerTests
{
    [Fact]
    public void Doubling_ScalesScalableAmountsAndRanges_AndLeavesTheRestAlone()
    {
        var recipe = Recipe(4,
            Line(IngredientUnit.Gram, 250),
            Line(IngredientUnit.Clove, 2, 3),
            Line(IngredientUnit.Pinch, 1),
            Line(IngredientUnit.ToTaste, null),
            Line(IngredientUnit.None, null));

        RecipeServingsScaler.Apply(recipe, 8);

        Assert.True(recipe.Scaled);
        Assert.Equal(8, recipe.DisplayedServings);
        Assert.Equal(4, recipe.Servings);
        Assert.Equal([500m, 4m, 1m, null, null], recipe.Ingredients.Select(i => i.Amount));
        Assert.Equal(6m, recipe.Ingredients[1].AmountMax);
        Assert.Equal(IngredientUnit.Gram, recipe.Ingredients[0].Unit);
    }

    [Fact]
    public void TheStoredServings_IsNotAScale()
    {
        var recipe = Recipe(4, Line(IngredientUnit.Gram, 250));

        RecipeServingsScaler.Apply(recipe, 4);

        Assert.False(recipe.Scaled);
        Assert.Equal(4, recipe.DisplayedServings);
        Assert.Equal(250m, recipe.Ingredients[0].Amount);
    }

    [Fact]
    public void ATinyAmount_IsFlooredAtTheMinimum_NeverZero()
    {
        var recipe = Recipe(100, Line(IngredientUnit.Gram, 0.001m));

        RecipeServingsScaler.Apply(recipe, 1);

        Assert.Equal(0.001m, recipe.Ingredients[0].Amount);
    }

    [Fact]
    public void ARangeThatRoundsToOneValue_Collapses()
    {
        var recipe = Recipe(100, Line(IngredientUnit.Gram, 0.002m, 0.003m));

        RecipeServingsScaler.Apply(recipe, 1);

        Assert.Equal(0.001m, recipe.Ingredients[0].Amount);
        Assert.Null(recipe.Ingredients[0].AmountMax);
    }

    [Fact]
    public void Rounding_IsHalfAwayFromZero_ToThreePlaces()
    {
        var recipe = Recipe(3, Line(IngredientUnit.Gram, 1));

        RecipeServingsScaler.Apply(recipe, 1);

        Assert.Equal(0.333m, recipe.Ingredients[0].Amount);
    }

    [Theory]
    [InlineData("500.000", "500")]
    [InlineData("0.500", "0.5")]
    [InlineData("12.345", "12.345")]
    public void Trim_DropsTheColumnsTrailingZeros(string stored, string expected)
    {
        var value = decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, RecipeAmounts.Trim(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1.234", true)]
    [InlineData("1.2345", false)]
    public void FitsScale_AllowsThreeDecimals(string value, bool fits) =>
        Assert.Equal(fits, RecipeAmounts.FitsScale(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    private static ExistingRecipe Recipe(int servings, params RecipeIngredientDto[] lines) => new()
    {
        RecipeId = Guid.NewGuid(),
        Version = 1,
        Title = "t",
        Servings = servings,
        DisplayedServings = servings,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Ingredients = [.. lines],
    };

    private static RecipeIngredientDto Line(IngredientUnit unit, decimal? amount, decimal? max = null) => new()
    {
        RecipeIngredientId = Guid.NewGuid(),
        Position = 0,
        Unit = unit,
        Name = "x",
        Amount = amount,
        AmountMax = max,
    };
}
