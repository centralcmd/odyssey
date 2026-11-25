using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>
/// One row of the dialog's Ingredients or Method list (design system components-recipes.md · The fields): a section
/// heading or a line. The key is stable across moves, so a moved row keeps its fields and its handle's focus.
/// </summary>
public abstract class RecipeRowDraft
{
    public Guid Key { get; } = Guid.NewGuid();
}

/// <summary>A section heading row: the lines below it, up to the next heading, carry its name.</summary>
public sealed class RecipeSectionDraft : RecipeRowDraft
{
    public string Title { get; set; } = string.Empty;
}

/// <summary>One ingredient line: amount · unit · name · optional. Its section is the heading above it.</summary>
public sealed class RecipeIngredientDraft : RecipeRowDraft
{
    public decimal? Amount { get; set; }

    public IngredientUnit Unit { get; set; } = IngredientUnit.None;

    public string Name { get; set; } = string.Empty;

    public bool Optional { get; set; }
}

/// <summary>One method step: only its text. Its section is the heading above it.</summary>
public sealed class RecipeStepDraft : RecipeRowDraft
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// The New / Edit recipe form (issue #526; design system components-recipes.md · The dialog). Ingredients and Method
/// are each ONE ordered list of section headings and lines; the wire is unchanged — every line carries its section —
/// so the list is built on open (<see cref="ToGrouped{T}"/>, the kit's <c>rcToGrouped</c>) and folded back on save
/// (<see cref="FromGrouped{TLine}"/>, <c>rcFromGrouped</c>).
/// </summary>
/// <remarks>
/// Error keys are the server's 422 keys, lower-cased: a line's are <c>ingredients[j].name</c> / <c>steps[j].text</c>
/// with <c>j</c> its position among the LINES (headings do not count), so a client error and a server error land on
/// the same field; a heading's are <c>ingredientsections[h].title</c> / <c>stepsections[h].title</c>, <c>h</c> its
/// ordinal among the headings. Caps are <see cref="RecipeLimits"/>, never a literal.
/// </remarks>
public sealed class RecipeDraft
{
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int? Servings { get; set; } = 4;

    public string ServingsLabel { get; set; } = string.Empty;

    public decimal? PrepMinutes { get; set; }

    public decimal? CookMinutes { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    /// <summary>Section headings and ingredient lines, in order.</summary>
    public List<RecipeRowDraft> Ingredients { get; set; } = [new RecipeIngredientDraft()];

    /// <summary>Section headings and method steps, in order.</summary>
    public List<RecipeRowDraft> Method { get; set; } = [new RecipeStepDraft()];

    public IReadOnlyCollection<string> TagIds { get; set; } = [];

    /// <summary>The three wizard steps, in order.</summary>
    public const int DetailsStep = 0;

    /// <inheritdoc cref="DetailsStep"/>
    public const int IngredientsStep = 1;

    /// <inheritdoc cref="DetailsStep"/>
    public const int MethodStep = 2;

    /// <summary>A blank recipe: one empty ingredient and one empty step, written for 4.</summary>
    public static RecipeDraft New() => new();

    /// <summary>A draft seeded from a read (the unscaled one: the dialog edits what is stored).</summary>
    public static RecipeDraft From(ExistingRecipe r) => new()
    {
        Title = r.Title,
        Description = r.Description ?? string.Empty,
        Servings = r.Servings,
        ServingsLabel = r.ServingsLabel ?? string.Empty,
        PrepMinutes = r.PrepMinutes,
        CookMinutes = r.CookMinutes,
        Source = r.Source ?? string.Empty,
        Notes = r.Notes ?? string.Empty,
        Ingredients = ToGrouped(r.Ingredients.OrderBy(i => i.Position), i => i.Section, i => new RecipeIngredientDraft
        {
            Amount = i.Amount,
            Unit = i.Unit,
            Name = i.Name,
            Optional = i.Optional,
        }),
        Method = ToGrouped(r.Steps.OrderBy(s => s.Position), s => s.Section, s => new RecipeStepDraft { Text = s.Text }),
        TagIds = [.. r.TagIds.Select(id => id.ToString())],
    };

    /// <summary>
    /// A draft seeded from a draft recipe an AI import made (issue #604). Sections become heading rows; a null servings
    /// stays empty — the new-recipe default of 4 is not applied — and so does any other missing field.
    /// </summary>
    public static RecipeDraft FromImport(RecipeImportDraftContent c) => new()
    {
        Title = c.Title ?? string.Empty,
        Description = c.Description ?? string.Empty,
        Servings = c.Servings,
        ServingsLabel = c.ServingsLabel ?? string.Empty,
        PrepMinutes = c.PrepMinutes,
        CookMinutes = c.CookMinutes,
        Source = c.Source ?? string.Empty,
        Notes = c.Notes ?? string.Empty,
        Ingredients = c.Ingredients.Length == 0
            ? [new RecipeIngredientDraft()]
            : ToGrouped(c.Ingredients, i => i.Section, i => new RecipeIngredientDraft
            {
                Amount = i.Amount,
                Unit = i.Unit,
                Name = i.Name,
                Optional = i.Optional,
            }),
        Method = c.Steps.Length == 0
            ? [new RecipeStepDraft()]
            : ToGrouped(c.Steps, s => s.Section, s => new RecipeStepDraft { Text = s.Text }),
        TagIds = [.. (c.TagIds ?? []).Select(id => id.ToString())],
    };

    /// <summary>
    /// The draft recipe as Save and finish later stores it (<c>PUT …/drafts/{id}</c>): no wizard validation, so an
    /// empty line is left out rather than refused. Without <c>recipes.tags.read</c> <c>tagIds</c> is <c>null</c>, which
    /// keeps the stored suggestions for everyone else.
    /// </summary>
    public RecipeImportDraftContent ToImportContent(bool canReadTags) => new()
    {
        SchemaVersion = RecipeImportLimits.DraftSchemaVersion,
        Title = Clean(Title),
        Description = Clean(Description),
        Notes = Clean(Notes),
        Source = Clean(Source),
        Servings = Servings,
        ServingsLabel = Clean(ServingsLabel),
        PrepMinutes = PrepMinutes is { } p ? (int)p : null,
        CookMinutes = CookMinutes is { } c ? (int)c : null,
        Ingredients = [.. FromGrouped<RecipeIngredientDraft>(Ingredients)
            .Where(x => !string.IsNullOrWhiteSpace(x.Line.Name))
            .Select(x => new RecipeImportIngredient
            {
                Section = x.Section,
                Amount = x.Line.Amount,
                Unit = x.Line.Unit,
                Name = x.Line.Name.Trim(),
                Optional = x.Line.Optional,
            })],
        Steps = [.. FromGrouped<RecipeStepDraft>(Method)
            .Where(x => !string.IsNullOrWhiteSpace(x.Line.Text))
            .Select(x => new RecipeImportStep { Section = x.Section, Text = x.Line.Text.Trim() })],
        TagIds = canReadTags ? ParsedTags() : null,
    };

    /// <summary>
    /// Stored lines → one list of headings and lines: a heading wherever the section changes to a new non-empty value.
    /// A line with no section after a sectioned one cannot be shown without ending the section, so it joins the
    /// section above — visible before saving (the kit's <c>rcToGrouped</c>).
    /// </summary>
    internal static List<RecipeRowDraft> ToGrouped<T>(IEnumerable<T> lines, Func<T, string?> sectionOf, Func<T, RecipeRowDraft> toRow)
    {
        var rows = new List<RecipeRowDraft>();
        string? current = null;
        foreach (var line in lines)
        {
            var section = string.IsNullOrWhiteSpace(sectionOf(line)) ? null : sectionOf(line)!.Trim();
            if (section is not null && section != current)
            {
                rows.Add(new RecipeSectionDraft { Title = section });
            }

            current = section;
            rows.Add(toRow(line));
        }

        return rows;
    }

    /// <summary>The list → stored lines: each line takes the heading above it; none above = no section.</summary>
    internal static IReadOnlyList<(string? Section, TLine Line)> FromGrouped<TLine>(IEnumerable<RecipeRowDraft> rows) where TLine : RecipeRowDraft
    {
        string? section = null;
        var lines = new List<(string?, TLine)>();
        foreach (var row in rows)
        {
            if (row is RecipeSectionDraft heading)
            {
                section = string.IsNullOrWhiteSpace(heading.Title) ? null : heading.Title.Trim();
            }
            else if (row is TLine line)
            {
                lines.Add((section, line));
            }
        }

        return lines;
    }

    /// <summary>A row's position among its kind — line <c>j</c> or heading <c>h</c> — counted from 0.</summary>
    internal static int OrdinalOf(IReadOnlyList<RecipeRowDraft> rows, int index)
    {
        var heading = rows[index] is RecipeSectionDraft;
        var n = 0;
        for (var i = 0; i < index; i++)
        {
            if (rows[i] is RecipeSectionDraft == heading)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>"ingredient 3" / "step 3" / "section 2" — lines number on across sections.</summary>
    internal static string LabelOf(IReadOnlyList<RecipeRowDraft> rows, int index, string lineNoun) =>
        $"{(rows[index] is RecipeSectionDraft ? "section" : lineNoun)} {OrdinalOf(rows, index) + 1}";

    /// <summary>The number of lines (headings excluded) — what the caps count.</summary>
    internal static int LineCount(IEnumerable<RecipeRowDraft> rows) => rows.Count(r => r is not RecipeSectionDraft);

    /// <summary>The error key of a row's field: a line's in the server's form, a heading's in the client's.</summary>
    internal static string RowKey(IReadOnlyList<RecipeRowDraft> rows, int index, string list, string field) =>
        rows[index] is RecipeSectionDraft
            ? $"{Singular(list)}sections[{OrdinalOf(rows, index)}].title"
            : $"{list}[{OrdinalOf(rows, index)}].{field}";

    private static string Singular(string list) => list == "ingredients" ? "ingredient" : "step";

    /// <summary>The wizard step an error key belongs to: Ingredients, Method, else Details (tags included).</summary>
    public static int StepOf(string key)
    {
        var k = key.ToLowerInvariant();
        if (k.StartsWith("rc-f-", StringComparison.Ordinal))
        {
            k = k[5..];
        }

        return k.StartsWith("ingredient", StringComparison.Ordinal) ? IngredientsStep
            : k.StartsWith("step", StringComparison.Ordinal) ? MethodStep
            : DetailsStep;
    }

    /// <summary>A comparable snapshot of every field, for the discard-changes check.</summary>
    public string Fingerprint() => string.Join('\u001f',
        new[] { Title, Description, Servings?.ToString(), ServingsLabel, PrepMinutes?.ToString(), CookMinutes?.ToString(), Source, Notes }
            .Concat(Ingredients.Select(Print))
            .Append("§")
            .Concat(Method.Select(Print))
            .Append("§")
            .Concat(TagIds.Order(StringComparer.Ordinal)));

    private static string Print(RecipeRowDraft row) => row switch
    {
        RecipeSectionDraft s => $"#{s.Title}",
        RecipeIngredientDraft i => $"{i.Amount}|{i.Unit}|{i.Name}|{i.Optional}",
        RecipeStepDraft s => s.Text,
        _ => string.Empty,
    };

    /// <summary>The field id of an error key — also the ErrorSummary's jump target.</summary>
    public static string FieldId(string key) =>
        "rc-f-" + key.ToLowerInvariant().Replace('[', '-').Replace(']', '-').Replace('.', '-').Replace("--", "-");

    /// <summary>Every step's problems, in step and field order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Validate() =>
        [.. Validate(DetailsStep), .. Validate(IngredientsStep), .. Validate(MethodStep)];

    /// <summary>
    /// One step's problems — what Next checks. The rules the client mirrors from the annotations: a title, servings
    /// and minutes in range; a heading with a name and a line under it; a named ingredient, no amount on "To taste";
    /// text on every step.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Validate(int step)
    {
        var e = new List<KeyValuePair<string, string>>();
        void Add(string key, string message) => e.Add(new(key, message));

        if (step == DetailsStep)
        {
            if (string.IsNullOrWhiteSpace(Title))
            {
                Add("title", "Give the recipe a title.");
            }

            if (Servings is not { } s || s < RecipeLimits.MinServings || s > RecipeLimits.MaxServings)
            {
                Add("servings", $"Enter a number from {RecipeLimits.MinServings} to {RecipeLimits.MaxServings}.");
            }

            foreach (var (key, value) in new[] { ("prepminutes", PrepMinutes), ("cookminutes", CookMinutes) })
            {
                if (value is { } m && (m < 0 || m > RecipeLimits.MaxMinutes || m != decimal.Truncate(m)))
                {
                    Add(key, $"Enter 0 to {RecipeLimits.MaxMinutes} whole minutes.");
                }
            }
        }
        else if (step == IngredientsStep)
        {
            CheckSections(Ingredients, "ingredients", "an ingredient", Add);
            for (var i = 0; i < Ingredients.Count; i++)
            {
                if (Ingredients[i] is not RecipeIngredientDraft row)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.Name))
                {
                    Add(RowKey(Ingredients, i, "ingredients", "name"), "Name the ingredient.");
                }

                if (row.Unit == IngredientUnit.ToTaste && row.Amount is not null)
                {
                    Add(RowKey(Ingredients, i, "ingredients", "amount"), "Leave the amount empty for “To taste”.");
                }
            }
        }
        else if (step == MethodStep)
        {
            CheckSections(Method, "steps", "a step", Add);
            for (var i = 0; i < Method.Count; i++)
            {
                if (Method[i] is RecipeStepDraft row && string.IsNullOrWhiteSpace(row.Text))
                {
                    Add(RowKey(Method, i, "steps", "text"), "Write the step, or remove it.");
                }
            }
        }

        return e;
    }

    // A heading needs a name, and a line directly under it.
    private static void CheckSections(List<RecipeRowDraft> rows, string list, string aLine, Action<string, string> add)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not RecipeSectionDraft heading)
            {
                continue;
            }

            var key = RowKey(rows, i, list, "title");
            if (string.IsNullOrWhiteSpace(heading.Title))
            {
                add(key, "Name the section, or remove it.");
            }
            else if (i + 1 >= rows.Count || rows[i + 1] is RecipeSectionDraft)
            {
                add(key, $"Add {aLine} under this section, or remove it.");
            }
        }
    }

    /// <summary>
    /// The POST body. <c>tagIds</c> follows the claim: the edited list with <c>recipes.tags.read</c>, else <c>[]</c>.
    /// Photos and files are never edited here, so a new recipe starts with none.
    /// </summary>
    public NewRecipe ToNew(bool canReadTags) => Fill(new NewRecipe
    {
        PhotoIds = [],
        AttachmentFileIds = [],
    }, canReadTags ? ParsedTags() : []);

    /// <summary>
    /// The PUT body: the read's version and archived state, <c>photoIds</c> and
    /// <c>attachmentFileIds</c> <c>null</c> (unchanged). Without <c>recipes.tags.read</c> the read's own tag ids go back
    /// unchanged: this API treats a <c>null</c> <c>tagIds</c> as an empty replacement, so <c>null</c> would clear them.
    /// </summary>
    public UpdateRecipe ToUpdate(ExistingRecipe read, bool canReadTags) => Fill(new UpdateRecipe
    {
        Version = read.Version,
        Archived = read.Archived is not null,
        PhotoIds = null,
        AttachmentFileIds = null,
    }, canReadTags ? ParsedTags() : [.. read.TagIds]);

    private Guid[] ParsedTags() => [.. TagIds.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct()];

    private T Fill<T>(T r, Guid[] tags) where T : RecipeRequest
    {
        r.Title = Title.Trim();
        r.Description = Clean(Description);
        r.Notes = Clean(Notes);
        r.Source = Clean(Source);
        r.Servings = Servings;
        r.ServingsLabel = Clean(ServingsLabel);
        r.PrepMinutes = PrepMinutes is { } p ? (int)p : null;
        r.CookMinutes = CookMinutes is { } c ? (int)c : null;
        r.Ingredients = [.. FromGrouped<RecipeIngredientDraft>(Ingredients).Select(x => new RecipeIngredientRequest
        {
            Amount = x.Line.Amount,
            Unit = x.Line.Unit,
            Name = x.Line.Name.Trim(),
            Section = x.Section,
            Optional = x.Line.Optional,
        })];
        r.Steps = [.. FromGrouped<RecipeStepDraft>(Method).Select(x => new RecipeStepRequest { Section = x.Section, Text = x.Line.Text.Trim() })];
        r.TagIds = tags;
        return r;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// The body of a record action — Archive / Restore, a photo change, a file change — which the recipe API only takes as a
/// full <c>PUT</c> (issue #525 has no attach endpoints). Every field is the read's, with only the one array set and
/// the rest <c>null</c> (unchanged); the tags go back as read, since a <c>null</c> would clear them.
/// </summary>
public static class RecipeWrites
{
    public static UpdateRecipe From(ExistingRecipe read, bool? archived = null, Guid[]? photoIds = null, Guid[]? attachmentFileIds = null) => new()
    {
        Version = read.Version,
        Archived = archived ?? read.Archived is not null,
        Title = read.Title,
        Description = read.Description,
        Notes = read.Notes,
        Source = read.Source,
        Servings = read.Servings,
        ServingsLabel = read.ServingsLabel,
        PrepMinutes = read.PrepMinutes,
        CookMinutes = read.CookMinutes,
        Ingredients = [.. read.Ingredients.OrderBy(i => i.Position).Select(i => new RecipeIngredientRequest
        {
            Section = i.Section,
            Amount = i.Amount,
            Unit = i.Unit,
            Name = i.Name,
            Optional = i.Optional,
        })],
        Steps = [.. read.Steps.OrderBy(s => s.Position).Select(s => new RecipeStepRequest { Section = s.Section, Text = s.Text })],
        TagIds = [.. read.TagIds],
        PhotoIds = photoIds,
        AttachmentFileIds = attachmentFileIds,
    };
}
