using Odyssey.Core.Recipes;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>Read-time servings scaling (issue #525 §8.3, AC 7 and AC 25).</summary>
public class RecipeServingsScalerTests
{
    [Fact]
    public void Doubling_ScalesScalableAmounts_AndLeavesTheRestAlone()
    {
        var recipe = Recipe(4,
            Line(IngredientUnit.Gram, 250),
            Line(IngredientUnit.Clove, 2),
            Line(IngredientUnit.Pinch, 1),
            Line(IngredientUnit.ToTaste, null),
            Line(IngredientUnit.None, null));

        RecipeServingsScaler.Apply(recipe, 8);

        Assert.True(recipe.Scaled);
        Assert.Equal(8, recipe.DisplayedServings);
        Assert.Equal(4, recipe.Servings);
        Assert.Equal([500m, 4m, 1m, null, null], recipe.Ingredients.Select(i => i.Amount));
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
    public void ARangeNotedInTheName_IsPlainText_AndDoesNotScale()
    {
        // What the issue #595 migration leaves of a range: the lower amount, and the range as text in the name.
        var line = Line(IngredientUnit.Clove, 4);
        line.Name = "garlic, unpeeled (4–6)";
        var recipe = Recipe(4, line);

        RecipeServingsScaler.Apply(recipe, 8);

        Assert.Equal(8m, recipe.Ingredients[0].Amount);
        Assert.Equal("garlic, unpeeled (4–6)", recipe.Ingredients[0].Name);
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

    /// <summary>
    /// Every member's ordinal and whether it scales (§8.2). The ordinals are a wire and persistence contract, so a
    /// renumbering, a reuse or a new member fails here and has to be decided deliberately.
    /// </summary>
    [Fact]
    public void Every_unit_has_its_contracted_ordinal_and_scaling()
    {
        (IngredientUnit Unit, int Ordinal, bool Scalable)[] expected =
        [
            (IngredientUnit.None, 0, true), (IngredientUnit.Milligram, 1, true), (IngredientUnit.Gram, 2, true),
            (IngredientUnit.Kilogram, 3, true), (IngredientUnit.Millilitre, 4, true), (IngredientUnit.Centilitre, 5, true),
            (IngredientUnit.Decilitre, 6, true), (IngredientUnit.Litre, 7, true), (IngredientUnit.Teaspoon, 8, true),
            (IngredientUnit.Tablespoon, 9, true), (IngredientUnit.Cup, 10, true), (IngredientUnit.Piece, 11, true),
            (IngredientUnit.Clove, 12, true), (IngredientUnit.Slice, 13, true), (IngredientUnit.Can, 14, true),
            (IngredientUnit.Package, 15, true), (IngredientUnit.Bunch, 16, true), (IngredientUnit.Pinch, 17, false),
            (IngredientUnit.ToTaste, 18, false),
        ];

        Assert.Equal(expected.Select(e => e.Unit), Enum.GetValues<IngredientUnit>());
        Assert.All(expected, e =>
        {
            Assert.Equal(e.Ordinal, (int)e.Unit);
            Assert.Equal(e.Scalable, IngredientUnitScaling.IsScalable(e.Unit));
        });
        Assert.False(IngredientUnitScaling.IsScalable((IngredientUnit)99));
    }

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

    private static RecipeIngredientDto Line(IngredientUnit unit, decimal? amount) => new()
    {
        RecipeIngredientId = Guid.NewGuid(),
        Position = 0,
        Unit = unit,
        Name = "x",
        Amount = amount,
    };
}
