using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

public partial class RecipeDialog
{
    internal const string ReloadId = "rc-dlg-reload";

    private const double AmountMin = 0.001;
    private const double AmountMax = 999999;

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The recipe to edit, read unscaled; <c>null</c> for New recipe.</summary>
    [Parameter] public ExistingRecipe? Recipe { get; set; }

    /// <summary><c>recipes.tags.read</c> at open: the Tags section, and whether <c>tagIds</c> is the edited list.</summary>
    [Parameter] public bool CanReadTags { get; set; }

    [Parameter] public bool CanCreateTags { get; set; }

    [Parameter] public IReadOnlyList<ExistingRecipeTag> Tags { get; set; } = [];

    [Parameter] public string? ReturnFocusToId { get; set; }

    /// <summary>The saved recipe, as the server returned it (its new version).</summary>
    [Parameter] public EventCallback<ExistingRecipe> OnSaved { get; set; }

    private RecipeDraft _draft = RecipeDraft.New();
    private string _baseline = string.Empty;
    private ExistingRecipe? _read;
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OdsOption> _createdTags = [];
    private string? _formAlert;
    private bool _conflict;
    private bool _reloading;
    private bool _saving;
    private bool _modalOpen;
    private bool _wasOpen;
    private bool _discardOpen;
    private bool _reloadOpen;
    private bool _closing;
    private string? _focusId;

    private static readonly IReadOnlyList<OdsOption> UnitOptions =
        [.. RecipeFormat.Units.Select(u => new OdsOption(u.Unit.ToString(), u.Label))];

    private bool IsEdit => _read is not null;

    private RenderFragment TitleFragment => b => b.AddContent(0, IsEdit ? "Edit recipe" : "New recipe");

    private RenderFragment SubtitleFragment => b => b.AddContent(0, IsEdit
        ? "Update the recipe, its ingredients, method and tags. Photos and files are managed from the recipe’s ⋯ menu."
        : "A recipe the whole household can cook from. Add photos and files from its ⋯ menu once it exists.");

    private string ServingsUnit => string.IsNullOrWhiteSpace(_draft.ServingsLabel) ? "serving" : _draft.ServingsLabel.Trim();

    private string ServingsUnitPlural => string.IsNullOrWhiteSpace(_draft.ServingsLabel) ? "servings" : _draft.ServingsLabel.Trim();

    private string? TagsMeta => _draft.TagIds.Count >= (int)Math.Ceiling(RecipeLimits.MaxTags * 0.8)
        ? $"{_draft.TagIds.Count} of {RecipeLimits.MaxTags} tags"
        : null;

    private IReadOnlyList<OdsOption> TagOptions
    {
        get
        {
            var known = Tags.Where(t => t.Archived is null)
                .Select(t => JournalOverviews.TagOption(t.RecipeTagId, t.Name, t.Icon, t.Color, JournalOverviews.TagRowColor)).ToList();
            return [.. _createdTags.Where(c => known.All(o => o.Value != c.Value)), .. known];
        }
    }

    protected override void OnInitialized() => TagCreator.OnCreateFailed = OnTagCreateFailed;

    protected override void OnParametersSet()
    {
        if (Open && !_wasOpen)
        {
            Seed(Recipe);
            _closing = false;
            _modalOpen = true;
        }

        _wasOpen = Open;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusId is not { } id)
        {
            return;
        }

        _focusId = null;
        try
        {
            await Js.InvokeVoidAsync("odsFocusById", id);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // No JS runtime (bUnit, teardown).
        }
    }

    private void Seed(ExistingRecipe? read)
    {
        _read = read;
        _draft = read is null ? RecipeDraft.New() : RecipeDraft.From(read);
        _baseline = _draft.Fingerprint();
        _errors.Clear();
        _formAlert = null;
        _conflict = false;
    }

    /// <summary>The field id of an error key.</summary>
    private static string F(string key) => RecipeDraft.FieldId(key);

    private string? Err(string key) => _errors.TryGetValue(key, out var message) ? message : null;

    private void Set(Action apply, string? key)
    {
        apply();
        if (key is not null)
        {
            _errors.Remove(key);
        }
    }

    private static string Cap(string noun) => char.ToUpperInvariant(noun[0]) + noun[1..];

    /// <summary>"n of cap ingredients" from 80 % of the cap, else "n lines".</summary>
    internal static string RowsMeta(int n, int cap, string noun, string unit) =>
        n >= (int)Math.Ceiling(cap * 0.8) ? $"{n} of {cap} {noun}s" : RecipeFormat.Count(n, unit);

    // ── Rows ─────────────────────────────────────────────────────────────────────────
    private void AddIngredient()
    {
        if (_draft.Ingredients.Count < RecipeLimits.MaxIngredients)
        {
            _draft.Ingredients.Add(new RecipeIngredientDraft());
            _focusId = F($"ingredients[{_draft.Ingredients.Count - 1}].amount");
        }
    }

    private void AddStep()
    {
        if (_draft.Steps.Count < RecipeLimits.MaxSteps)
        {
            _draft.Steps.Add(new RecipeStepDraft());
            _focusId = F($"steps[{_draft.Steps.Count - 1}].section");
        }
    }

    private void Move<T>(List<T> rows, int from, int to)
    {
        if (to < 0 || to >= rows.Count)
        {
            return;
        }

        (rows[from], rows[to]) = (rows[to], rows[from]);
        ClearRowErrors();
    }

    private void Remove<T>(List<T> rows, int index)
    {
        rows.RemoveAt(index);
        ClearRowErrors();
    }

    // A row error is keyed by position, so a move or removal would leave it on the wrong row.
    private void ClearRowErrors()
    {
        foreach (var key in _errors.Keys.Where(k => k.Contains('[')).ToList())
        {
            _errors.Remove(key);
        }
    }

    // ── Tags ─────────────────────────────────────────────────────────────────────────
    private void OnTagsChanged(IReadOnlyCollection<string> ids)
    {
        _draft.TagIds = ids;
        _errors.Remove("tagids");
    }

    private OdsOption? CreateTagOption(string text, string? kind)
    {
        var option = TagCreator.Begin(text);
        if (option is not null)
        {
            _createdTags.Add(option);
            StateHasChanged();
        }

        return option;
    }

    private void OnTagCreateFailed(string tempId)
    {
        _createdTags.RemoveAll(o => o.Value == tempId);
        _draft.TagIds = [.. _draft.TagIds.Where(id => id != tempId)];
        _ = InvokeAsync(StateHasChanged);
    }

    // A recipe tag name is unique across active and archived tags (RecipeTagService.EnsureNameAvailable).
    private string? BlockedTagName(string text) =>
        TagNameRule.Blocked(
            Tags.Select(t => new TagNameEntry(t.Name, t.Archived is not null))
                .Concat(_createdTags.Select(o => new TagNameEntry(o.Label, false))),
            text, TagNameRule.Pages.Recipe);
}
