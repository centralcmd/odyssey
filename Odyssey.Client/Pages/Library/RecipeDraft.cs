using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>One ingredient row of the New / Edit recipe dialog.</summary>
public sealed class RecipeIngredientDraft
{
    /// <summary>A stable render key, so a moved row keeps its fields.</summary>
    public Guid Key { get; } = Guid.NewGuid();

    public decimal? Amount { get; set; }

    public decimal? AmountMax { get; set; }

    public IngredientUnit Unit { get; set; } = IngredientUnit.None;

    public string Name { get; set; } = string.Empty;

    public string Preparation { get; set; } = string.Empty;

    public string Section { get; set; } = string.Empty;

    public bool Optional { get; set; }
}

/// <summary>One step row of the dialog.</summary>
public sealed class RecipeStepDraft
{
    public Guid Key { get; } = Guid.NewGuid();

    public string Section { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// The New / Edit recipe form (issue #526; design system components-recipes.md · The dialog). Keys of
/// <see cref="Validate"/> are the server's 422 keys, lower-cased (<c>ingredients[2].name</c>), so a client error and a
/// server error land on the same field. Caps are <see cref="RecipeLimits"/>, never a literal.
/// </summary>
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

    public List<RecipeIngredientDraft> Ingredients { get; set; } = [new()];

    public List<RecipeStepDraft> Steps { get; set; } = [new()];

    public IReadOnlyCollection<string> TagIds { get; set; } = [];

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
        Ingredients = [.. r.Ingredients.OrderBy(i => i.Position).Select(i => new RecipeIngredientDraft
        {
            Amount = i.Amount,
            AmountMax = i.AmountMax,
            Unit = i.Unit,
            Name = i.Name,
            Preparation = i.Preparation ?? string.Empty,
            Section = i.Section ?? string.Empty,
            Optional = i.Optional,
        })],
        Steps = [.. r.Steps.OrderBy(s => s.Position).Select(s => new RecipeStepDraft { Section = s.Section ?? string.Empty, Text = s.Text })],
        TagIds = [.. r.TagIds.Select(id => id.ToString())],
    };

    /// <summary>A comparable snapshot of every field, for the discard-changes check.</summary>
    public string Fingerprint() => string.Join('\u001f',
        new[] { Title, Description, Servings?.ToString(), ServingsLabel, PrepMinutes?.ToString(), CookMinutes?.ToString(), Source, Notes }
            .Concat(Ingredients.Select(i => $"{i.Amount}|{i.AmountMax}|{i.Unit}|{i.Name}|{i.Preparation}|{i.Section}|{i.Optional}"))
            .Append("§")
            .Concat(Steps.Select(s => $"{s.Section}|{s.Text}"))
            .Append("§")
            .Concat(TagIds.Order(StringComparer.Ordinal)));

    /// <summary>The field id of an error key — also the ErrorSummary's jump target.</summary>
    public static string FieldId(string key) =>
        "rc-f-" + key.ToLowerInvariant().Replace('[', '-').Replace(']', '-').Replace('.', '-').Replace("--", "-");

    /// <summary>
    /// The rules the client mirrors from the annotations, in field order: a title, servings in range, minutes in range,
    /// a named ingredient, "To" larger than the amount, no amount on "To taste", text on every step.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Validate()
    {
        var e = new List<KeyValuePair<string, string>>();
        void Add(string key, string message) => e.Add(new(key, message));

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

        for (var i = 0; i < Ingredients.Count; i++)
        {
            var row = Ingredients[i];
            if (row.Unit == IngredientUnit.ToTaste && row.Amount is not null)
            {
                Add($"ingredients[{i}].amount", "Leave the amount empty for “To taste”.");
            }

            if (row.AmountMax is { } max && (row.Amount is not { } min || max <= min))
            {
                Add($"ingredients[{i}].amountmax", "Make “To” larger than the amount.");
            }

            if (string.IsNullOrWhiteSpace(row.Name))
            {
                Add($"ingredients[{i}].name", "Name the ingredient.");
            }
        }

        for (var i = 0; i < Steps.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Steps[i].Text))
            {
                Add($"steps[{i}].text", "Write the step, or remove it.");
            }
        }

        return e;
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
        r.Ingredients = [.. Ingredients.Select(i => new RecipeIngredientRequest
        {
            Amount = i.Amount,
            AmountMax = i.AmountMax,
            Unit = i.Unit,
            Name = i.Name.Trim(),
            Preparation = Clean(i.Preparation),
            Section = Clean(i.Section),
            Optional = i.Optional,
        })];
        r.Steps = [.. Steps.Select(s => new RecipeStepRequest { Section = Clean(s.Section), Text = s.Text.Trim() })];
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
            AmountMax = i.AmountMax,
            Unit = i.Unit,
            Name = i.Name,
            Preparation = i.Preparation,
            Optional = i.Optional,
        })],
        Steps = [.. read.Steps.OrderBy(s => s.Position).Select(s => new RecipeStepRequest { Section = s.Section, Text = s.Text })],
        TagIds = [.. read.TagIds],
        PhotoIds = photoIds,
        AttachmentFileIds = attachmentFileIds,
    };
}
