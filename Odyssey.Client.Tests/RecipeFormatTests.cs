using Odyssey.Client.Pages.Library;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Recipes page's text rules (issue #526; design system components-recipes.md · The card head, The open card):
/// ingredient lines with their spoken units, word-unit plurals, ranges, "to taste" and "(optional)", and the
/// durations and yields of the meta line.
/// </summary>
public class RecipeFormatTests
{
    private static RecipeIngredientDto Line(decimal? amount, IngredientUnit unit, string name, decimal? max = null, string? prep = null, bool optional = false) =>
        new() { RecipeIngredientId = Guid.NewGuid(), Position = 0, Amount = amount, AmountMax = max, Unit = unit, Name = name, Preparation = prep, Optional = optional };

    [Fact]
    public void A_metric_amount_shows_the_abbreviation_and_speaks_the_unit()
    {
        var t = RecipeFormat.Ingredient(Line(500, IngredientUnit.Gram, "wheat flour", prep: "sifted"));

        Assert.Equal(("500", "g", "grams"), (t.Amount, t.UnitText, t.UnitSpoken));
        Assert.Equal("500 g wheat flour, sifted", t.Visible);
        Assert.Equal("500 grams wheat flour, sifted", t.Spoken);
    }

    [Fact]
    public void A_range_reads_with_an_en_dash_and_pluralises_by_its_upper_end()
    {
        var t = RecipeFormat.Ingredient(Line(4, IngredientUnit.Clove, "garlic", max: 6, prep: "unpeeled"));

        Assert.Equal("4–6 cloves garlic, unpeeled", t.Visible);
        Assert.Null(t.UnitSpoken);
    }

    [Theory]
    [InlineData(IngredientUnit.Clove, 1, "1 clove garlic")]
    [InlineData(IngredientUnit.Clove, 2, "2 cloves garlic")]
    [InlineData(IngredientUnit.Slice, 3, "3 slices garlic")]
    [InlineData(IngredientUnit.Can, 2, "2 cans garlic")]
    [InlineData(IngredientUnit.Cup, 2, "2 cups garlic")]
    [InlineData(IngredientUnit.Bunch, 2, "2 bunches garlic")]
    [InlineData(IngredientUnit.Pinch, 2, "2 pinches garlic")]
    [InlineData(IngredientUnit.Pinch, 1, "1 pinch garlic")]
    [InlineData(IngredientUnit.Piece, 2, "2 pc garlic")]
    [InlineData(IngredientUnit.None, 3, "3 garlic")]
    public void Word_units_pluralise_and_abbreviations_do_not(IngredientUnit unit, int amount, string expected) =>
        Assert.Equal(expected, RecipeFormat.Ingredient(Line(amount, unit, "garlic")).Visible);

    [Fact]
    public void An_abbreviated_unit_speaks_its_plural_name()
    {
        Assert.Equal("2 pieces garlic", RecipeFormat.Ingredient(Line(2, IngredientUnit.Piece, "garlic")).Spoken);
        Assert.Equal("1.5 teaspoons salt", RecipeFormat.Ingredient(Line(1.5m, IngredientUnit.Teaspoon, "salt")).Spoken);
        Assert.Equal("1 decilitre cream", RecipeFormat.Ingredient(Line(1, IngredientUnit.Decilitre, "cream")).Spoken);
    }

    [Fact]
    public void To_taste_takes_no_amount_and_optional_is_said_in_words()
    {
        Assert.Equal("salt and pepper, to taste", RecipeFormat.Ingredient(Line(null, IngredientUnit.ToTaste, "salt and pepper")).Visible);
        Assert.Equal("salt, to taste", RecipeFormat.Ingredient(Line(2, IngredientUnit.ToTaste, "salt")).Visible);
        Assert.Equal("1 dl cream (optional)", RecipeFormat.Ingredient(Line(1, IngredientUnit.Decilitre, "cream", optional: true)).Visible);
        Assert.Equal("red onion", RecipeFormat.Ingredient(Line(null, IngredientUnit.None, "red onion")).Visible);
    }

    [Fact]
    public void An_amount_prints_at_most_three_decimals_without_trailing_zeros()
    {
        Assert.Equal("1.2", RecipeFormat.Number(1.200m));
        Assert.Equal("0.125", RecipeFormat.Number(0.125m));
        Assert.Equal("1,200", RecipeFormat.Number(1200m));
    }

    [Fact]
    public void The_unit_select_lists_all_nineteen_units_with_the_metric_cup()
    {
        Assert.Equal(Enum.GetValues<IngredientUnit>(), RecipeFormat.Units.Select(u => u.Unit));
        Assert.Equal("Cup (250 ml)", RecipeFormat.UnitOf(IngredientUnit.Cup).Label);
        Assert.Equal("None", RecipeFormat.UnitOf((IngredientUnit)99).Label);
    }

    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(60, "1 h")]
    [InlineData(150, "2 h 30 min")]
    [InlineData(65, "1 h 5 min")]
    [InlineData(0, "0 min")]
    [InlineData(1500, "25 h")]
    public void Durations_are_whole_minutes_and_hours_never_days(int minutes, string expected) =>
        Assert.Equal(expected, RecipeFormat.Duration(minutes));

    [Fact]
    public void The_meta_line_reads_yield_times_and_total()
    {
        Assert.Null(RecipeFormat.Duration(null));
        Assert.Equal("Serves 4", RecipeFormat.Yield(4, null));
        Assert.Equal("Makes 12 pieces", RecipeFormat.Yield(12, "pieces"));
        Assert.Equal("Serves 8 (written for 4)", RecipeFormat.YieldMeta(8, 4, null));
        Assert.Equal("Serves 4", RecipeFormat.YieldMeta(4, 4, " "));
        Assert.Equal("Prep 20 min · Cook 45 min", RecipeFormat.Times(20, 45));
        Assert.Equal("Cook 45 min", RecipeFormat.Times(null, 45));
        Assert.Null(RecipeFormat.Times(null, null));
        Assert.Equal("Total 1 h 5 min", RecipeFormat.Total(65));
        Assert.Null(RecipeFormat.Total(null));
        Assert.Equal("for 1 serving", RecipeFormat.ForServings(1, null));
        Assert.Equal("for 8 servings", RecipeFormat.ForServings(8, null));
        Assert.Equal("for 2 loaf", RecipeFormat.ForServings(2, "loaf"));
    }

    [Fact]
    public void A_capped_count_reads_n_of_cap_from_80_percent()
    {
        Assert.Equal("79 lines", RecipeFormat.CapMeta(79, 100, "line"));
        Assert.Equal("80 of 100 lines", RecipeFormat.CapMeta(80, 100, "line"));
        Assert.Equal("1 line", RecipeFormat.CapMeta(1, 100, "line"));
    }

    [Theory]
    [InlineData("application/pdf", "PDF")]
    [InlineData("image/jpeg", "Image")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Word document")]
    [InlineData("text/plain", "Text")]
    [InlineData("application/zip", "File")]
    [InlineData(null, "File")]
    public void A_files_type_is_its_kind_from_the_content_type(string? contentType, string expected) =>
        Assert.Equal(expected, RecipeFormat.FileKind(contentType).Label);

    [Fact]
    public void Consecutive_rows_with_one_section_form_a_group_and_keep_their_positions()
    {
        var groups = RecipeFormat.Groups(["Tray", "Tray", null, "Tray"], s => s);

        Assert.Equal([("Tray", 2), (null, 1), ("Tray", 1)], groups.Select(g => (g.Section, g.Rows.Count)));
        Assert.Equal(3, groups[2].Rows[0].Index);
    }
}
