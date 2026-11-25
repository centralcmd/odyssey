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

    internal const string IngredientNoun = "ingredient";

    internal const string StepNoun = "step";

    private const string RowSection = "rc-sec";
    private const string RowIngredient = "rc-ing";
    private const string RowStep = "rc-step";

    private const double AmountMin = 0.001;
    private const double AmountMax = 999999;

    /// <summary>The three steps' names, in order — the path's labels and the panels' accessible names.</summary>
    internal static readonly string[] StepLabels = ["Details", "Ingredients", "Method"];

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The recipe to edit, read unscaled; <c>null</c> for New recipe.</summary>
    [Parameter] public ExistingRecipe? Recipe { get; set; }

    /// <summary><c>recipes.tags.read</c> at open: the Tags field, and whether <c>tagIds</c> is the edited list.</summary>
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

    // ── The wizard ──
    private int _step;

    // Steps the person has moved through. Editing counts all three as visited: the record already holds them.
    private bool[] _visited = [true, false, false];

    /// <summary>The open step — for tests.</summary>
    internal int Step => _step;

    private static readonly IReadOnlyList<OdsOption> UnitOptions =
        [.. RecipeFormat.Units.Select(u => new OdsOption(u.Unit.ToString(), u.Label))];

    private bool IsEdit => _read is not null;

    private bool IsLastStep => _step == RecipeDraft.MethodStep;

    private RenderFragment TitleFragment => b => b.AddContent(0, IsImport ? "Review imported recipe" : IsEdit ? "Edit recipe" : "New recipe");

    private string ServingsUnit => string.IsNullOrWhiteSpace(_draft.ServingsLabel) ? "serving" : _draft.ServingsLabel.Trim();

    private string ServingsUnitPlural => string.IsNullOrWhiteSpace(_draft.ServingsLabel) ? "servings" : _draft.ServingsLabel.Trim();

    private int IngredientCount => RecipeDraft.LineCount(_draft.Ingredients);

    private int StepCount => RecipeDraft.LineCount(_draft.Method);

    /// <summary>"n of 20 tags" as help from 80 % of the cap.</summary>
    private string? TagsHelp => AtEighty(_draft.TagIds.Count, RecipeLimits.MaxTags) ? $"{_draft.TagIds.Count} of {RecipeLimits.MaxTags} tags" : null;

    /// <summary>The step path: a ✓ once a step is left with nothing wrong in it, ! with "Has errors" while it has any,
    /// and "n of 100" under Ingredients / Method from 80 % of the cap.</summary>
    private IReadOnlyList<OdsWizardStep> WizardSteps =>
    [
        new(StepLabels[0], null, StateOf(RecipeDraft.DetailsStep), "details"),
        new(StepLabels[1], CapMeta(IngredientCount, RecipeLimits.MaxIngredients), StateOf(RecipeDraft.IngredientsStep), "ingredients"),
        new(StepLabels[2], CapMeta(StepCount, RecipeLimits.MaxSteps), StateOf(RecipeDraft.MethodStep), "method"),
    ];

    private OdsWizardStepState StateOf(int step)
    {
        if (_errors.Keys.Any(k => RecipeDraft.StepOf(k) == step))
        {
            return OdsWizardStepState.Error;
        }

        // "Done" only once the step has been left behind — never a guess about whether optional fields are complete.
        var done = _visited[step] && _step != step && (step != RecipeDraft.DetailsStep || !string.IsNullOrWhiteSpace(_draft.Title));
        return done ? OdsWizardStepState.Done : OdsWizardStepState.None;
    }

    internal static bool AtEighty(int n, int cap) => n >= (int)Math.Ceiling(cap * 0.8);

    /// <summary>"84 of 100" from 80 % of the cap, else nothing.</summary>
    internal static string? CapMeta(int n, int cap) => AtEighty(n, cap) ? $"{n} of {cap}" : null;

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
            if (ImportReview is { } review)
            {
                SeedImport(review);
            }
            else
            {
                Seed(Recipe);
            }

            _step = RecipeDraft.DetailsStep;
            _visited = Recipe is null && ImportReview is null ? [true, false, false] : [true, true, true];
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
            // The dialog's body is teleported into the dialog provider, which renders after this component, so the
            // move waits two frames for a new step's (or a new row's) field to be in the DOM.
            await Js.InvokeVoidAsync("odsFocusFirstIn", id, true, 2);
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

    internal static string PanelId(int step) => $"rc-wiz-panel-{step}";

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

    // ── Steps ────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// The first field of a step — where focus lands on a step change: Title; the first row's first input (a heading's
    /// name, an ingredient's Amount, a step's text) — never its drag handle; else the Add row when the list is empty.
    /// </summary>
    internal string FirstFieldOf(int step)
    {
        if (step == RecipeDraft.DetailsStep)
        {
            return F("title");
        }

        var (rows, list, field, noun) = step == RecipeDraft.IngredientsStep
            ? (_draft.Ingredients, "ingredients", "amount", IngredientNoun)
            : (_draft.Method, "steps", "text", StepNoun);
        return rows.Count == 0 ? AddRowId(noun) : F(RecipeDraft.RowKey(rows, 0, list, field));
    }

    /// <summary>Opens a step: focus to <paramref name="focusId"/> (an ErrorSummary jump) or the step's first field, and
    /// the polite region says where you are — the path in the header is the only step title.</summary>
    private async Task GoAsync(int step, string? focusId = null)
    {
        step = Math.Clamp(step, RecipeDraft.DetailsStep, RecipeDraft.MethodStep);
        for (var i = 0; i <= step; i++)
        {
            _visited[i] = true;
        }

        var changed = step != _step;
        _step = step;
        _focusId = focusId ?? FirstFieldOf(step);
        StateHasChanged();
        if (changed)
        {
            await Announce(StepAnnouncement(step));
        }
    }

    /// <summary>"Step 2 of 3: Ingredients".</summary>
    internal static string StepAnnouncement(int step) => $"Step {step + 1} of {StepLabels.Length}: {StepLabels[step]}";

    /// <summary>Next checks only the step it is on; its errors stay there and focus goes to the first.</summary>
    private async Task NextAsync()
    {
        var problems = _draft.Validate(_step);
        foreach (var key in _errors.Keys.Where(k => RecipeDraft.StepOf(k) == _step).ToList())
        {
            _errors.Remove(key);
        }

        if (problems.Count > 0)
        {
            foreach (var (key, message) in problems)
            {
                _errors.TryAdd(key, message);
            }

            _focusId = F(problems[0].Key);
            return;
        }

        await GoAsync(_step + 1);
    }

    private Task BackAsync() => GoAsync(_step - 1);

    /// <summary>
    /// After Save (or a 422) found problems: stay on this step if it has one, else open the first step that does, and
    /// focus its first invalid field.
    /// </summary>
    private async Task ShowErrorsAsync()
    {
        var keys = _errors.Keys.ToList();
        var target = keys.FirstOrDefault(k => RecipeDraft.StepOf(k) == _step)
            ?? keys.OrderBy(RecipeDraft.StepOf).First();
        var step = RecipeDraft.StepOf(target);
        if (step == _step)
        {
            _focusId = F(target);
            return;
        }

        await GoAsync(step, F(target));
    }

    // ── Rows ─────────────────────────────────────────────────────────────────────────
    private OdsLiveAnnouncer? _announcer;

    private Task Announce(string message) => _announcer?.Announce(message) ?? Task.CompletedTask;

    private async Task AddIngredientAsync()
    {
        if (IngredientCount >= RecipeLimits.MaxIngredients)
        {
            return;
        }

        _draft.Ingredients.Add(new RecipeIngredientDraft());
        _focusId = F(RecipeDraft.RowKey(_draft.Ingredients, _draft.Ingredients.Count - 1, "ingredients", "amount"));
        if (CapMessage(IngredientNoun, IngredientCount, RecipeLimits.MaxIngredients) is { } cap)
        {
            await Announce(cap);
        }
    }

    private async Task AddStepAsync()
    {
        if (StepCount >= RecipeLimits.MaxSteps)
        {
            return;
        }

        _draft.Method.Add(new RecipeStepDraft());
        _focusId = F(RecipeDraft.RowKey(_draft.Method, _draft.Method.Count - 1, "steps", "text"));
        if (CapMessage(StepNoun, StepCount, RecipeLimits.MaxSteps) is { } cap)
        {
            await Announce(cap);
        }
    }

    private void AddSection(List<RecipeRowDraft> rows, string list)
    {
        rows.Add(new RecipeSectionDraft());
        _focusId = F(RecipeDraft.RowKey(rows, rows.Count - 1, list, "title"));
    }

    /// <summary>Every way of moving a row ends here. Moving a heading moves only the heading: its lines regroup under
    /// whatever heading is then above them. The list keeps focus on the handle and announces the move.</summary>
    private void MoveRow(List<RecipeRowDraft> rows, OdsSortMove move)
    {
        if (move.From < 0 || move.From >= rows.Count || move.To < 0 || move.To >= rows.Count)
        {
            return;
        }

        var row = rows[move.From];
        rows.RemoveAt(move.From);
        rows.Insert(move.To, row);
        ClearRowErrors();
    }

    /// <summary>Removing a heading keeps its lines: they join the section above (or none). Focus goes to the row now in
    /// its place, else the one before, else the Add row.</summary>
    private async Task RemoveRowAsync(List<RecipeRowDraft> rows, int index, string noun)
    {
        if (index < 0 || index >= rows.Count)
        {
            return;
        }

        var label = RecipeDraft.LabelOf(rows, index, noun);
        rows.RemoveAt(index);
        ClearRowErrors();
        var list = noun == IngredientNoun ? "ingredients" : "steps";
        var field = noun == IngredientNoun ? "amount" : "text";
        _focusId = rows.Count == 0
            ? AddRowId(noun)
            : F(RecipeDraft.RowKey(rows, Math.Min(index, rows.Count - 1), list, field));
        await Announce(RemovedMessage(label, noun, RecipeDraft.LineCount(rows)));
    }

    /// <summary>The closing Add row's id.</summary>
    internal static string AddRowId(string noun) => $"rc-{noun}-add";

    /// <summary>"Step 2 removed. 4 steps." / "Section 1 removed. 3 ingredients."</summary>
    internal static string RemovedMessage(string label, string noun, int lines) =>
        $"{Cap(label)} removed. {RecipeFormat.Count(lines, noun)}.";

    /// <summary>"A recipe holds at most 100 ingredients." once the list reaches its cap; otherwise nothing.</summary>
    internal static string? CapMessage(string noun, int count, int cap) =>
        count >= cap ? $"A recipe holds at most {cap} {noun}s." : null;

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
