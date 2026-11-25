using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes.Import;

/// <summary>
/// Model output → draft recipe (issue #602 §8, A14, S6). Character hygiene first, then composition (an unmapped unit
/// and a range are folded into the name), then truncation to <see cref="RecipeLimits"/> with a warning per field. The
/// result is data the client renders as plain text; nothing in it is fetched, executed or used as an address.
/// </summary>
public static partial class RecipeDraftSanitizer
{
    private static readonly decimal MinAmount = decimal.Parse(RecipeLimits.MinAmount, CultureInfo.InvariantCulture);
    private static readonly decimal MaxAmount = decimal.Parse(RecipeLimits.MaxAmount, CultureInfo.InvariantCulture);

    public sealed record Result(RecipeImportDraftContent Content, IReadOnlyList<string> Warnings);

    /// <param name="tagRefs">The refs the server sent, mapped back to ids; any other ref is dropped.</param>
    /// <param name="moreRecipesFound">The model returned more than the one recipe v1 drafts.</param>
    public static Result Sanitize(ExtractedRecipe recipe, IReadOnlyDictionary<string, Guid> tagRefs, bool moreRecipesFound)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(tagRefs);
        var warnings = new List<string>();
        foreach (var warning in recipe.Warnings)
        {
            Warn(warnings, warning);
        }

        if (moreRecipesFound)
        {
            Warn(warnings, RecipeImportWarningCodes.MultipleRecipesFound);
        }

        var content = new RecipeImportDraftContent
        {
            SchemaVersion = RecipeImportLimits.DraftSchemaVersion,
            Title = Line(recipe.Title, RecipeLimits.MaxTitleLength, "title", warnings),
            Description = Block(recipe.Description, RecipeLimits.MaxDescriptionLength, "description", warnings),
            Notes = Block(recipe.Notes, RecipeLimits.MaxNotesLength, "notes", warnings),
            Source = Line(recipe.Source, RecipeLimits.MaxSourceLength, "source", warnings),
            ServingsLabel = Line(recipe.ServingsLabel, RecipeLimits.MaxServingsLabelLength, "servingsLabel", warnings),
            PrepMinutes = Minutes(recipe.PrepMinutes),
            CookMinutes = Minutes(recipe.CookMinutes),
        };

        content.Servings = recipe.Servings is { } servings and >= RecipeLimits.MinServings and <= RecipeLimits.MaxServings
            ? servings
            : null;
        if (content.Servings is null)
        {
            Warn(warnings, RecipeImportWarningCodes.ServingsMissing);
        }

        var ingredients = new List<RecipeImportIngredient>();
        foreach (var item in recipe.Ingredients)
        {
            if (Ingredient(item, warnings) is { } ingredient)
            {
                ingredients.Add(ingredient);
            }
        }

        if (ingredients.Count > RecipeLimits.MaxIngredients)
        {
            ingredients = ingredients[..RecipeLimits.MaxIngredients];
            Warn(warnings, RecipeImportWarningCodes.TruncatedPrefix + "ingredients");
        }

        var steps = new List<RecipeImportStep>();
        foreach (var item in recipe.Steps)
        {
            var text = Block(item.Text, RecipeLimits.MaxStepTextLength, "step", warnings);
            if (!string.IsNullOrEmpty(text))
            {
                steps.Add(new RecipeImportStep { Section = Line(item.Section, RecipeLimits.MaxSectionLength, "section", warnings), Text = text });
            }
        }

        if (steps.Count > RecipeLimits.MaxSteps)
        {
            steps = steps[..RecipeLimits.MaxSteps];
            Warn(warnings, RecipeImportWarningCodes.TruncatedPrefix + "steps");
        }

        content.Ingredients = [.. ingredients];
        content.Steps = [.. steps];
        content.TagIds = [.. recipe.TagRefs
            .Select(r => tagRefs.TryGetValue(r, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .Take(RecipeLimits.MaxTags)];

        if (ContainsUrl(content))
        {
            Warn(warnings, RecipeImportWarningCodes.ContainsUrl);
        }

        return new Result(content, warnings);
    }

    /// <summary>
    /// The solution's one control-and-bidi cleaner (<see cref="Odyssey.Dtos.Journal.LocationTextCleaner"/>, keeping line
    /// breaks; it also normalises to NFC and trims), then the zero-width characters it leaves: U+200B–200D, U+2060, U+FEFF.
    /// </summary>
    public static string Clean(string? value)
    {
        var cleaned = Odyssey.Dtos.Journal.LocationTextCleaner.Clean(value, allowLineBreaks: true);
        if (cleaned is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(cleaned.Length);
        foreach (var c in cleaned)
        {
            if (c is not ('\u200b' or '\u200c' or '\u200d' or '\u2060' or '\ufeff'))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    private static RecipeImportIngredient? Ingredient(ExtractedIngredient item, List<string> warnings)
    {
        var name = Clean(item.Name).ReplaceLineEndings(" ");
        if (name.Length == 0)
        {
            return null;
        }

        var unit = Enum.IsDefined(item.Unit) ? item.Unit : IngredientUnit.None;
        var unitText = Clean(item.UnitText).ReplaceLineEndings(" ");
        if (unit == IngredientUnit.None && unitText.Length > 0)
        {
            name = $"{unitText} {name}";
            Warn(warnings, RecipeImportWarningCodes.UnitUnmapped);
        }

        var amount = Amount(item.Amount, warnings);
        if (amount is { } low && Amount(item.AmountMax, warnings) is { } high && high > low)
        {
            name = $"{name} ({Text(low)}–{Text(high)})";
            Warn(warnings, RecipeImportWarningCodes.AmountRange);
        }

        return new RecipeImportIngredient
        {
            Section = Line(item.Section, RecipeLimits.MaxSectionLength, "section", warnings),
            Amount = amount,
            Unit = unit,
            Name = Truncate(name, RecipeLimits.MaxIngredientNameLength, "ingredient", warnings)!,
            Optional = item.Optional,
        };
    }

    private static decimal? Amount(decimal? value, List<string> warnings)
    {
        if (value is not { } amount)
        {
            return null;
        }

        var rounded = Math.Round(amount, RecipeLimits.AmountScale, MidpointRounding.AwayFromZero);
        if (rounded < MinAmount || rounded > MaxAmount)
        {
            Warn(warnings, RecipeImportWarningCodes.AmountUnclear);
            return null;
        }

        if (rounded != amount)
        {
            Warn(warnings, RecipeImportWarningCodes.AmountUnclear);
        }

        return rounded;
    }

    private static int? Minutes(int? value) => value is { } minutes and >= 0 and <= RecipeLimits.MaxMinutes ? minutes : null;

    private static string? Line(string? value, int max, string field, List<string> warnings) =>
        Truncate(Clean(value).ReplaceLineEndings(" "), max, field, warnings);

    private static string? Block(string? value, int max, string field, List<string> warnings) =>
        Truncate(Clean(value), max, field, warnings);

    private static string? Truncate(string value, int max, string field, List<string> warnings)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length <= max)
        {
            return value;
        }

        Warn(warnings, RecipeImportWarningCodes.TruncatedPrefix + field);
        var cut = value[..max];
        // Never leave half a surrogate pair at the cut.
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1].TrimEnd() : cut.TrimEnd();
    }

    private static string Text(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Warn(List<string> warnings, string code)
    {
        if (!warnings.Contains(code))
        {
            warnings.Add(code);
        }
    }

    private static bool ContainsUrl(RecipeImportDraftContent content)
    {
        IEnumerable<string?> texts =
        [
            content.Title, content.Description, content.Notes, content.Source, content.ServingsLabel,
            .. content.Ingredients.Select(i => i.Name), .. content.Ingredients.Select(i => i.Section),
            .. content.Steps.Select(s => s.Text), .. content.Steps.Select(s => s.Section),
        ];
        return texts.Any(t => t is not null && UrlPattern().IsMatch(t));
    }

    [GeneratedRegex(@"https?://|www\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}
