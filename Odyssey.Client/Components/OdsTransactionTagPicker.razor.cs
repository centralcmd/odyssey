using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

public partial class OdsTransactionTagPicker
{
    /// <summary>The default helper, shown whenever the selected tag carries no description.</summary>
    public const string DefaultHelp = "Matched transactions become this item's actual.";

    /// <summary>The tag currently planned for, or null.</summary>
    [Parameter] public Guid? Value { get; set; }

    [Parameter] public EventCallback<Guid?> ValueChanged { get; set; }

    /// <summary>Every tag the caller knows about, archived included — this control filters.</summary>
    [Parameter] public IReadOnlyList<ExistingTransactionTag> Tags { get; set; } = [];

    /// <summary>
    /// Tags already planned for elsewhere in the same budget. Rendered as unselectable rows marked
    /// "in use" rather than hidden, so the reason a tag is missing is visible instead of inferred.
    /// <see cref="Value"/> is always exempt.
    /// </summary>
    [Parameter] public IReadOnlyList<Guid> UsedTagIds { get; set; } = [];

    [Parameter] public string Label { get; set; } = "Transaction tag";

    /// <summary>
    /// The inner input's id — <b>mandatory and unique per instance</b>. OdsCombobox returns
    /// immediately from its first render when this is empty, so without it the aria-required and
    /// aria-describedby wiring silently does nothing. In a repeated row it must be PER ROW.
    /// </summary>
    [Parameter, EditorRequired] public string InputId { get; set; } = string.Empty;

    [Parameter] public bool Required { get; set; } = true;

    /// <summary>
    /// Helper text for when the selected tag has no description. Never blanked while an error shows:
    /// OdsFieldShell renders the error in ADDITION to the helper, and a helper that came and went
    /// would move the error's id out from under the constant aria-describedby.
    /// </summary>
    [Parameter] public string? Help { get; set; }

    /// <summary>The caller's own error — a failed submit, a server rejection.</summary>
    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Disabled { get; set; }

    /// <summary>
    /// Keep the real <c>&lt;label for&gt;</c> association but remove the label visually — for a grid
    /// cell labelled by a column header that is itself hidden at narrow widths.
    /// </summary>
    [Parameter] public bool HideLabel { get; set; }

    /// <summary>Drop the helper line (never the error) — a dense grid row would repeat it per item.</summary>
    [Parameter] public bool Dense { get; set; }

    [Parameter] public string Placeholder { get; set; } = "Search tags…";

    /// <summary>
    /// Stage a tag named by the typed text and return the option to select now, or null to skip.
    /// Pass it <b>only</b> where the caller holds <c>transactions.tags.create</c> AND the surface has
    /// a submit to resolve the staged create against; a null callback is how creation is suppressed,
    /// since OdsCombobox renders its create row exactly when OnCreate is non-null.
    /// </summary>
    [Parameter] public Func<string, OdsOption?>? OnCreateTag { get; set; }

    /// <summary>The tag list failed to load — distinct from it being empty, and with a retry.</summary>
    [Parameter] public bool LoadFailed { get; set; }

    [Parameter] public EventCallback OnRetry { get; set; }

    [Parameter] public string ManageHref { get; set; } = "/transaction-tags";

    [Parameter] public string ManageLabel { get; set; } = "Transaction tags";

    [Parameter] public string? Class { get; set; }

    // Options handed back by OnCreateTag. They are not in Tags — the POST behind them has not settled
    // — so the picker holds them itself until the host reloads.
    private readonly List<OdsOption> _staged = [];

    // The duplicate-name refusal, which is the picker's own verdict rather than the caller's.
    private string? _duplicateError;

    // Set when BeginCreate refuses. OdsCombobox raises ValueChanged(null) immediately afterwards —
    // its create row hands back no option — and without this flag that callback would wipe the very
    // message the refusal just produced, and clear a selection the user never touched.
    private bool _refusedCreate;

    private string? ShownError => Error ?? _duplicateError;

    private string ShellClass =>
        string.Join(' ', new[] { "odc-tagpick", HideLabel ? "hide-label" : null, Dense ? "dense" : null, Class }
            .Where(value => !string.IsNullOrEmpty(value)));

    private string? SelectedValue => Value is { } id && id != Guid.Empty ? id.ToString() : null;

    private ExistingTransactionTag? SelectedTag =>
        Value is { } id ? Tags.FirstOrDefault(tag => tag.TransactionTagId == id) : null;

    /// <summary>
    /// The selected tag's description, else the caller's helper, else the standard one. Never empty —
    /// see <see cref="Help"/> for why that matters beyond tidiness.
    /// </summary>
    private string HelpText
    {
        get
        {
            var description = SelectedTag?.Description;
            if (!string.IsNullOrWhiteSpace(description))
                return description!;
            return string.IsNullOrWhiteSpace(Help) ? DefaultHelp : Help!;
        }
    }

    // Constant from the first render and naming BOTH ids: OdsFieldShell renders the error at
    // {HtmlFor}-help-error while help is present, and the wiring runs on firstRender only, so a
    // single -help value would never announce the error.
    internal string DescribedBy => $"{InputId}-help {InputId}-help-error";

    private string EmptyText => CreateHandler is null ? "No tags match" : "No matches — type a name to create one";

    internal Func<string, string?, OdsOption?>? CreateHandler =>
        OnCreateTag is null ? null : (text, _) => BeginCreate(text);

    internal IReadOnlyList<OdsOption> Options
    {
        get
        {
            var used = UsedTagIds.Where(id => id != Guid.Empty && id != Value).ToHashSet();
            var options = new List<OdsOption>(_staged);

            // The record's own tag stays selectable even once archived: keeping a link that already
            // exists removes no capability, while making a new one does.
            var selected = SelectedTag;
            if (selected is { Archived: not null })
                options.Add(OptionFor(selected, used));

            options.AddRange(Tags
                .Where(tag => tag.Archived is null)
                .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(tag => OptionFor(tag, used)));

            return options;
        }
    }

    private static OdsOption OptionFor(ExistingTransactionTag tag, IReadOnlySet<Guid> used)
    {
        var inUse = used.Contains(tag.TransactionTagId);
        return new OdsOption(
            tag.TransactionTagId.ToString(),
            tag.Archived is null ? tag.Name : $"{tag.Name} · Archived")
        {
            Icon = "local_offer",
            Note = inUse ? "in use" : null,
            Disabled = inUse,
        };
    }

    /// <summary>
    /// No live tag is selectable, and none is selected. In the dialog with the create claim this is
    /// informational — the create row is the way forward — so the empty state renders only where there
    /// is no create affordance to offer.
    /// </summary>
    private bool NoneSelectable =>
        CreateHandler is null
        && SelectedValue is null
        && !Options.Any(option => !option.Disabled);

    private async Task OnValueChanged(string? value)
    {
        if (_refusedCreate)
        {
            // The null that follows a refused create is the refusal, not a deselection.
            _refusedCreate = false;
            return;
        }

        _duplicateError = null;
        Value = Guid.TryParse(value, out var parsed) ? parsed : null;
        await ValueChanged.InvokeAsync(Value);
    }

    /// <summary>
    /// The inline create, with the duplicate-name pre-check in front of it.
    /// </summary>
    /// <remarks>
    /// The check is client-side because <c>ITagQuickCreate.OnCreateFailed</c> is
    /// <c>Action&lt;string&gt;</c>: it carries a temporary id and nothing else, so the server's reason
    /// never reaches the caller and a refused duplicate would be indistinguishable from any other
    /// failure. Widening that service for one caller was rejected (issue #75 Non-Goal 9). The server's
    /// 409 stays authoritative for the genuine race, where the user sees the creator's own snackbar.
    /// </remarks>
    internal OdsOption? BeginCreate(string text)
    {
        var clean = (text ?? string.Empty).Trim();
        if (clean.Length == 0)
            return null;

        // Archived rows included: the unique index covers them, so an archived clash is a real one —
        // and it has no inline remedy, which the message has to say.
        var clash = Tags.FirstOrDefault(tag => string.Equals(tag.Name, clean, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            return Refuse(clash.Archived is null
                ? $"A tag called “{clash.Name}” already exists. Pick it from the list."
                : $"A tag called “{clash.Name}” already exists but is archived. Restore or rename it on {ManageLabel}.");
        }

        if (_staged.Any(option => string.Equals(option.Label, clean, StringComparison.OrdinalIgnoreCase)))
        {
            return Refuse($"A tag called “{clean}” is already being created.");
        }

        _duplicateError = null;
        var created = OnCreateTag!(clean);
        if (created is null)
            return null;

        var staged = created with { Icon = created.Icon ?? "local_offer" };
        _staged.Add(staged);
        StateHasChanged();
        return staged;
    }

    private OdsOption? Refuse(string message)
    {
        _duplicateError = message;
        _refusedCreate = true;
        StateHasChanged();
        return null;
    }

    /// <summary>
    /// Drops a staged option whose create failed, clearing the selection when it pointed at it. Called
    /// by the host from <c>ITagQuickCreate.OnCreateFailed</c>, which carries the temporary id.
    /// </summary>
    public async Task DropStagedAsync(string temporaryId)
    {
        _staged.RemoveAll(option => string.Equals(option.Value, temporaryId, StringComparison.Ordinal));

        if (string.Equals(SelectedValue, temporaryId, StringComparison.Ordinal))
        {
            Value = null;
            await ValueChanged.InvokeAsync(null);
        }

        StateHasChanged();
    }

    /// <summary>Clears the picker's own duplicate-name verdict — for a host reopening or resubmitting.</summary>
    public void ClearDuplicateError() => _duplicateError = null;
}
