using System.Globalization;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>
/// How a unit reads on an ingredient line (design system <c>recipes-data.js</c> · <c>RECIPE_UNITS</c>). <see cref="Abbr"/>
/// is what the line shows; <see cref="Spoken"/> is what a screen reader hears instead (the abbreviation is
/// <c>aria-hidden</c> beside an sr-only word). A <see cref="Word"/> unit reads as a noun after the number and pluralises
/// ("2 cloves"); <see cref="Trailing"/> is "to taste", which takes no amount.
/// </summary>
public sealed record RecipeUnit(
    IngredientUnit Unit, string Label, string Abbr, string Spoken, bool Word = false, string? Plural = null, bool Trailing = false);

/// <summary>One ingredient line, split into the parts the open card renders.</summary>
/// <param name="Amount">"500", "4–6", or <c>null</c> for an unquantified line.</param>
/// <param name="UnitText">The visible unit ("g", "cloves"), <c>aria-hidden</c>; <c>null</c> for a bare count.</param>
/// <param name="UnitSpoken">The sr-only spoken unit ("grams"), or <c>null</c> when it reads the same as <see cref="UnitText"/>.</param>
/// <param name="Name">The ingredient.</param>
/// <param name="Preparation">", sifted", or <c>null</c>.</param>
/// <param name="ToTaste">The line ends ", to taste".</param>
/// <param name="Optional">The line ends " (optional)".</param>
public sealed record RecipeIngredientText(
    string? Amount, string? UnitText, string? UnitSpoken, string Name, string? Preparation, bool ToTaste, bool Optional)
{
    /// <summary>The line as it reads on screen: "500 g wheat flour, sifted".</summary>
    public string Visible => Compose(UnitText);

    /// <summary>The line as a screen reader hears it: "500 grams wheat flour, sifted".</summary>
    public string Spoken => Compose(UnitSpoken ?? UnitText);

    private string Compose(string? unit)
    {
        var lead = Amount is null ? null : unit is null ? Amount : $"{Amount} {unit}";
        var text = lead is null ? Name : $"{lead} {Name}";
        if (Preparation is not null)
        {
            text += $", {Preparation}";
        }

        if (ToTaste)
        {
            text += ", to taste";
        }

        return Optional ? text + " (optional)" : text;
    }
}

/// <summary>
/// The Recipes page's formatting rules (issue #526; design system <c>components-recipes.md</c>). Pure, so they are
/// pinned by unit tests. The client never does arithmetic on an amount: the server scales (<c>?servings=</c>) and this
/// only prints what came back.
/// </summary>
public static class RecipeFormat
{
    /// <summary>The 19 <see cref="IngredientUnit"/> members in ordinal order, as the dialog's Unit select lists them.</summary>
    public static readonly IReadOnlyList<RecipeUnit> Units =
    [
        new(IngredientUnit.None, "None", "", ""),
        new(IngredientUnit.Milligram, "Milligram (mg)", "mg", "milligram"),
        new(IngredientUnit.Gram, "Gram (g)", "g", "gram"),
        new(IngredientUnit.Kilogram, "Kilogram (kg)", "kg", "kilogram"),
        new(IngredientUnit.Millilitre, "Millilitre (ml)", "ml", "millilitre"),
        new(IngredientUnit.Centilitre, "Centilitre (cl)", "cl", "centilitre"),
        new(IngredientUnit.Decilitre, "Decilitre (dl)", "dl", "decilitre"),
        new(IngredientUnit.Litre, "Litre (l)", "l", "litre"),
        new(IngredientUnit.Teaspoon, "Teaspoon (tsp)", "tsp", "teaspoon"),
        new(IngredientUnit.Tablespoon, "Tablespoon (tbsp)", "tbsp", "tablespoon"),
        new(IngredientUnit.Cup, "Cup (250 ml)", "cup", "cup", Word: true),
        new(IngredientUnit.Piece, "Piece", "pc", "piece"),
        new(IngredientUnit.Clove, "Clove", "clove", "clove", Word: true),
        new(IngredientUnit.Slice, "Slice", "slice", "slice", Word: true),
        new(IngredientUnit.Can, "Can", "can", "can", Word: true),
        new(IngredientUnit.Package, "Package", "pkg", "package"),
        new(IngredientUnit.Bunch, "Bunch", "bunch", "bunch", Word: true, Plural: "bunches"),
        new(IngredientUnit.Pinch, "Pinch", "pinch", "pinch", Word: true, Plural: "pinches"),
        new(IngredientUnit.ToTaste, "To taste", "to taste", "to taste", Trailing: true),
    ];

    /// <summary>The unit's reading; an unknown ordinal reads as <see cref="IngredientUnit.None"/>.</summary>
    public static RecipeUnit UnitOf(IngredientUnit unit) => Units.FirstOrDefault(u => u.Unit == unit) ?? Units[0];

    /// <summary>An amount as the API returns it (at most three decimals, trailing zeros trimmed).</summary>
    public static string Number(decimal value) => value.ToString("#,0.###", CultureInfo.InvariantCulture);

    /// <summary>Splits a line into what the open card renders: "4–6 cloves garlic, unpeeled".</summary>
    public static RecipeIngredientText Ingredient(RecipeIngredientDto line)
    {
        var unit = UnitOf(line.Unit);
        var preparation = string.IsNullOrWhiteSpace(line.Preparation) ? null : line.Preparation.Trim();
        if (line.Amount is not { } amount || unit.Trailing)
        {
            return new RecipeIngredientText(null, null, null, line.Name, preparation, unit.Trailing, line.Optional);
        }

        var text = line.AmountMax is { } max ? $"{Number(amount)}–{Number(max)}" : Number(amount);
        var plural = (line.AmountMax ?? amount) != 1m;
        var word = unit.Abbr.Length == 0 ? null
            : unit.Word && plural ? unit.Plural ?? unit.Abbr + "s"
            : unit.Abbr;
        var spoken = unit.Spoken.Length == 0 ? null
            : plural ? unit.Plural ?? unit.Spoken + "s"
            : unit.Spoken;
        return new RecipeIngredientText(text, word, spoken == word ? null : spoken, line.Name, preparation, false, line.Optional);
    }

    /// <summary>"45 min", "1 h", "2 h 30 min" — whole minutes, never days. <c>null</c> when unset.</summary>
    public static string? Duration(int? minutes)
    {
        if (minutes is not { } m)
        {
            return null;
        }

        var (h, rest) = (m / 60, m % 60);
        return h == 0 ? $"{rest} min" : rest == 0 ? $"{h} h" : $"{h} h {rest} min";
    }

    /// <summary>"Serves 4", or "Makes 12 pieces" when the recipe has a servings label.</summary>
    public static string Yield(int servings, string? label) =>
        string.IsNullOrWhiteSpace(label) ? $"Serves {servings}" : $"Makes {servings} {label.Trim()}";

    /// <summary>The card's yield meta: "Serves 8 (written for 4)" once scaled.</summary>
    public static string YieldMeta(int displayed, int written, string? label) =>
        displayed == written ? Yield(written, label) : $"{Yield(displayed, label)} (written for {written})";

    /// <summary>"Prep 20 min · Cook 45 min", each part only when set; <c>null</c> when neither is.</summary>
    public static string? Times(int? prep, int? cook)
    {
        var parts = new[] { prep is null ? null : $"Prep {Duration(prep)}", cook is null ? null : $"Cook {Duration(cook)}" }
            .OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"Total 1 h 5 min", or <c>null</c> without times.</summary>
    public static string? Total(int? total) => total is null ? null : $"Total {Duration(total)}";

    /// <summary>"for 8 servings" / "for 1 loaf" beside the Ingredients title.</summary>
    public static string ForServings(int displayed, string? label)
    {
        var unit = string.IsNullOrWhiteSpace(label) ? (displayed == 1 ? "serving" : "servings") : label.Trim();
        return $"for {displayed} {unit}";
    }

    /// <summary>"3 photos", "1 file" — a count and its noun.</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>A capped section's meta: "n of cap" from 80 % of the cap, else the plain count (the Books counter rule).</summary>
    public static string CapMeta(int n, int cap, string singular, string? plural = null) =>
        n >= (int)Math.Ceiling(cap * 0.8) ? $"{n} of {cap} {plural ?? singular + "s"}" : Count(n, singular, plural);

    /// <summary>The file KIND from a content type — a recipe file carries no document type (the Books rule).</summary>
    public static (string Label, string Icon) FileKind(string? contentType)
    {
        var ct = contentType ?? string.Empty;
        return ct switch
        {
            "application/pdf" => ("PDF", "picture_as_pdf"),
            _ when ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => ("Image", "image"),
            _ when ct.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase) || ct.Contains("msword", StringComparison.OrdinalIgnoreCase)
                => ("Word document", "description"),
            "text/plain" => ("Text", "notes"),
            _ => ("File", "draft"),
        };
    }

    /// <summary>Sections in first-appearance order; consecutive rows with the same section form a group.</summary>
    public static IReadOnlyList<(string? Section, IReadOnlyList<(T Row, int Index)> Rows)> Groups<T>(
        IReadOnlyList<T> rows, Func<T, string?> section)
    {
        var groups = new List<(string? Section, List<(T Row, int Index)> Rows)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var key = string.IsNullOrWhiteSpace(section(rows[i])) ? null : section(rows[i])!.Trim();
            if (groups.Count > 0 && groups[^1].Section == key)
            {
                groups[^1].Rows.Add((rows[i], i));
            }
            else
            {
                groups.Add((key, [(rows[i], i)]));
            }
        }

        return [.. groups.Select(g => (g.Section, (IReadOnlyList<(T Row, int Index)>)g.Rows))];
    }

    /// <summary>A short date for the Added by / Updated by tiles ("14 Mar 2026").</summary>
    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
