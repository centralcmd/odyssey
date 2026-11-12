using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.JobRuns;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.Instruments;

/// <summary>The price-sources panel's state, its edits and the one <c>PUT</c>. Markup in the <c>.razor</c>.</summary>
public partial class InstrumentPriceSourcesPanel
{
    internal const string NoSourcesInstalled = "No price sources are installed on this server yet. Prices can be entered by hand.";
    internal const string SymbolChangeWarning = "Changing the symbol refetches this source’s history.";
    internal const string SymbolRequired = "Enter the symbol as the source writes it.";
    internal const string SymbolFormat = "Use letters, digits, dots, hyphens and underscores, without '..'.";
    internal const string DailyLimitReached = "Daily request limit reached";
    internal const string GenericSymbolHelp = "The symbol as the source writes it, e.g. EQNR.OL";
    internal const string BackgroundJobsHref = "/background-jobs?job=InstrumentPriceSync";

    [Parameter, EditorRequired] public ExistingInstrument Instrument { get; set; } = default!;

    /// <summary>The page's one sources read for this visit; null while it loads.</summary>
    [Parameter] public InstrumentPriceSourcesStatus? Status { get; set; }

    [Parameter] public bool StatusFailed { get; set; }

    [Parameter] public EventCallback OnRetryStatus { get; set; }

    /// <summary><c>system-settings.update</c>: without it the panel is the same list as text.</summary>
    [Parameter] public bool CanEdit { get; set; }

    [Parameter] public EventCallback<bool> OnDirtyChanged { get; set; }

    /// <summary>The number of sources in the (draft) list, for the section's "{n} of {max}".</summary>
    [Parameter] public EventCallback<int> OnBindingCountChanged { get; set; }

    [Parameter] public EventCallback OnSaved { get; set; }

    private static readonly Regex SourceSymbol = new(InstrumentIdentifierRules.SourceSymbolPattern, RegexOptions.CultureInvariant);
    private static readonly Regex BindingErrorKey = new(@"^bindings\[(\d+)\]\.(\w+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private List<ExistingInstrumentPriceBinding> _saved = [];
    private List<SourceDraft> _draft = [];
    private readonly Dictionary<Guid, string> _itemErrors = [];
    private AddDraft? _adding;
    private PendingEnable? _confirm;
    private bool _loading = true;
    private bool _loadFailed;
    private bool _saving;
    private bool _lastDirty;
    private int _lastCount = -1;
    private string? _focusId;
    private bool _focusSummary;
    private OdsLiveAnnouncer? _polite;

    /// <summary>One source in the draft list. <see cref="Key"/> is the <c>@key</c> and the id stem; it never changes.</summary>
    internal sealed class SourceDraft
    {
        public Guid Key { get; } = Guid.NewGuid();
        public required string SourceKey { get; init; }
        public string SourceSymbol { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public DateOnly? HistoryStartDate { get; init; }
        public ExistingInstrumentPriceBinding? Saved { get; init; }
        public int ResetKey { get; set; }

        public bool SymbolChanged =>
            Saved is { } saved && !string.Equals(saved.SourceSymbol, SourceSymbol.Trim(), StringComparison.Ordinal);

        public static SourceDraft From(ExistingInstrumentPriceBinding b) => new()
        {
            SourceKey = b.SourceKey, SourceSymbol = b.SourceSymbol, Enabled = b.Enabled, HistoryStartDate = b.HistoryStartDate, Saved = b,
        };
    }

    private sealed class AddDraft
    {
        public string? SourceKey { get; set; }
        public string? Symbol { get; set; }
        public DateTime? HistoryStart { get; set; }
        public bool Enabled { get; set; }
        public string? SourceError { get; set; }
        public string? SymbolError { get; set; }
    }

    private sealed record PendingEnable(Guid Key, SourceDraft? Item, string ProviderName, string Symbol, string ReturnFocusToId, string? DataProcessingNote);

    private bool Editable => CanEdit && Instrument.Archived is null;

    /// <summary>Whether the draft differs from what is stored: order, symbols, switches or start dates.</summary>
    internal static bool IsDirty(IReadOnlyList<ExistingInstrumentPriceBinding> saved, IReadOnlyList<SourceDraft> draft) =>
        saved.Count != draft.Count
        || saved.Zip(draft).Any(pair =>
            !string.Equals(pair.First.SourceKey, pair.Second.SourceKey, StringComparison.Ordinal)
            || !string.Equals(pair.First.SourceSymbol, pair.Second.SourceSymbol.Trim(), StringComparison.Ordinal)
            || pair.First.Enabled != pair.Second.Enabled
            || pair.First.HistoryStartDate != pair.Second.HistoryStartDate);

    private bool Dirty => IsDirty(_saved, _draft);

    private string SummaryId => $"ins-src-errsum-{_uid}";

    private string AddButtonId => $"ins-src-{_uid}-add";

    private string SymbolId(SourceDraft item) => $"ins-src-{item.Key:N}-symbol";

    private string SwitchHostId(SourceDraft item) => $"ins-src-{item.Key:N}-switch";

    private static string MoveId(SourceDraft item, bool up) => $"ins-src-{item.Key:N}-{(up ? "up" : "down")}";

    private static string RemoveId(SourceDraft item) => $"ins-src-{item.Key:N}-remove";

    private DateTime TodayMax => (InstrumentVisuals.TryOsloToday(Time) ?? DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime))
        .ToDateTime(TimeOnly.MinValue);

    internal static string MaximumText(int max) => $"This instrument has the maximum of {max} sources.";

    private InstrumentPriceSourceInfo? InfoOf(string? key) =>
        Status?.Sources.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    private string? NoteOf(string key) => InfoOf(key)?.DataProcessingNote is { Length: > 0 } note ? note.Trim() : null;

    /// <summary>The source-symbol help line, in the provider's own notation when it declares one (issue #447).</summary>
    internal static string SymbolHelp(InstrumentPriceSourceInfo? info) =>
        info?.SymbolExample is { Length: > 0 } example
            ? $"The symbol as {(string.IsNullOrWhiteSpace(info.DisplayName) ? info.Key : info.DisplayName.Trim())} writes it, e.g. {example.Trim()}"
            : GenericSymbolHelp;

    private string SymbolHelp(string? key) => SymbolHelp(InfoOf(key));

    /// <summary>"Not asked again until {date}…" — the server's date for a binding skipped after a definitive failure (A9).</summary>
    internal static string RetryText(DateOnly retryOn) =>
        $"Not asked again until {RunTime.Date(retryOn)}. Change the symbol, or turn the source off and on, to try now.";

    private string NameOf(string key) => NameOf(Status, key);

    internal static string NameOf(InstrumentPriceSourcesStatus? status, string key) =>
        status?.Sources.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal)) is { } info
        && !string.IsNullOrWhiteSpace(info.DisplayName)
            ? info.DisplayName.Trim()
            : key;

    private IReadOnlyList<InstrumentPriceSourceInfo> Addable =>
        Status is null ? [] : [.. Status.Sources.Where(s => s.SupportedTypes.Contains(Instrument.Type)
                                                            && !_draft.Any(d => d.SourceKey == s.Key))];

    private IReadOnlyList<OdsOption> AddableOptions => [.. Addable.Select(s => new OdsOption(s.Key, NameOf(s.Key)))];

    /// <summary>
    /// A source's state as words (§3.4; design system · <c>INS_VOCAB.sourceStates</c>): Off, Not installed, Not
    /// configured, Daily request limit reached, Paused until {time}, or Active. Never a colour alone.
    /// </summary>
    internal static string StateText(InstrumentPriceSourceInfo? info, bool enabled, bool registered, DateTime nowUtc)
    {
        if (!enabled)
            return "Off";
        if (info is null || !registered)
            return "Not installed";
        if (!info.Configured)
            return "Not configured";
        if (info.MaxRequestsPerDay > 0 && info.RequestsToday >= info.MaxRequestsPerDay)
            return DailyLimitReached;
        if (info.HourlyLimitResetsAtUtc is { } resets && resets > nowUtc)
            return $"Hourly request limit reached · resumes at {RunTime.Clock(resets)}";
        if (info.BackOffUntilUtc is { } until && until > nowUtc)
            return $"Paused until {RunTime.Clock(until)}";
        return "Active";
    }

    private string StateText(SourceDraft item) =>
        StateText(Status?.Sources.FirstOrDefault(s => s.Key == item.SourceKey), item.Enabled,
            item.Saved?.SourceRegistered ?? true, Time.GetUtcNow().UtcDateTime);

    private static string FailureText(JobFailureCategory failure, DateTime? at) =>
        at is { } when ? $"{BackgroundJobPresentation.FailureOf(failure)} · {RunTime.Full(when)}" : BackgroundJobPresentation.FailureOf(failure);

    internal static string HistoryText(ExistingInstrumentPriceBinding saved) =>
        saved.BackfillComplete ? "Complete"
        : saved.BackfilledFrom is { } from ? $"History filled back to {RunTime.Date(from)}"
        : "History not fetched yet";

    /// <summary>The client half of the source-symbol rule (#446 §8): the shared pattern, no "..", within the length.</summary>
    internal static string? ValidateSymbol(string? symbol)
    {
        var value = symbol?.Trim();
        if (string.IsNullOrEmpty(value))
            return SymbolRequired;
        return value.Length > InstrumentIdentifierRules.SourceSymbolMaxLength || !SourceSymbol.IsMatch(value)
               || InstrumentIdentifierRules.HasDotSegment(value)
            ? SymbolFormat
            : null;
    }

    private string? ItemError(SourceDraft item) => _itemErrors.GetValueOrDefault(item.Key);

    private IReadOnlyList<KeyValuePair<string, string>> SummaryErrors =>
    [
        .. _draft.Where(d => _itemErrors.ContainsKey(d.Key))
            .Select(d => new KeyValuePair<string, string>(d.Key.ToString("N"), $"{NameOf(d.SourceKey)}: {_itemErrors[d.Key]}")),
    ];

    private IReadOnlyDictionary<string, string> SummaryLinks =>
        _draft.ToDictionary(d => d.Key.ToString("N"), SymbolId);

    // ── Load ──────────────────────────────────────────────────────────────────────────────────────

    protected override Task OnInitializedAsync() => LoadAsync();

    /// <summary>The bindings read, on each expand (§3.4) and after a save.</summary>
    private async Task LoadAsync()
    {
        _loading = true;
        _loadFailed = false;
        var result = await PriceSources.GetBindingsAsync(Instrument.InstrumentId);
        _loading = false;
        if (result.IsSuccess)
        {
            _saved = [.. (result.Value ?? []).OrderBy(b => b.Priority)];
            _draft = [.. _saved.Select(SourceDraft.From)];
            _itemErrors.Clear();
        }
        else
        {
            _loadFailed = true;
        }

        await NotifyAsync();
    }

    private async Task NotifyAsync()
    {
        var dirty = Dirty;
        if (dirty != _lastDirty)
        {
            _lastDirty = dirty;
            await OnDirtyChanged.InvokeAsync(dirty);
        }

        if (_draft.Count != _lastCount && !_loadFailed && !_loading)
        {
            _lastCount = _draft.Count;
            await OnBindingCountChanged.InvokeAsync(_draft.Count);
        }
    }

    // ── Edits ─────────────────────────────────────────────────────────────────────────────────────

    private async Task OnSymbolChanged(SourceDraft item, string? value)
    {
        item.SourceSymbol = value ?? string.Empty;
        _itemErrors.Remove(item.Key);
        await NotifyAsync();
    }

    /// <summary>Turning a source on asks first; turning one off does not (§3.4).</summary>
    private async Task OnEnabledChanged(SourceDraft item, bool on)
    {
        if (!on)
        {
            item.Enabled = false;
            await NotifyAsync();
            return;
        }

        _confirm = new PendingEnable(Guid.NewGuid(), item, NameOf(item.SourceKey), item.SourceSymbol.Trim(), SwitchHostId(item), NoteOf(item.SourceKey));
    }

    private async Task OnEnableDecided(bool confirmed)
    {
        if (_confirm is not { } pending)
            return;
        _confirm = null;

        if (pending.Item is { } item)
        {
            if (confirmed)
                item.Enabled = true;
            // MudSwitch keeps its own checked state: rebuild it from the model, which also hands focus back to it.
            item.ResetKey++;
        }
        else if (confirmed)
        {
            CommitAdd(enabled: true);
        }

        await NotifyAsync();
    }

    /// <summary>Moves a source one place; focus stays on the move button that still exists (§3.4).</summary>
    private async Task Move(int index, int by)
    {
        var target = index + by;
        if (target < 0 || target >= _draft.Count)
            return;

        var item = _draft[index];
        _draft.RemoveAt(index);
        _draft.Insert(target, item);
        var up = by < 0;
        var keepsButton = up ? target > 0 : target < _draft.Count - 1;
        _focusId = MoveId(item, up: keepsButton ? up : !up);
        _ = _polite?.Announce($"{NameOf(item.SourceKey)} moved to priority {target + 1}");
        await NotifyAsync();
    }

    /// <summary>Removes a source; focus goes to the next item's Remove, else the previous one's, else Add source.</summary>
    private async Task Remove(int index)
    {
        var item = _draft[index];
        _draft.RemoveAt(index);
        _itemErrors.Remove(item.Key);
        _focusId = index < _draft.Count ? RemoveId(_draft[index])
            : index > 0 ? RemoveId(_draft[index - 1])
            : AddButtonId;
        _ = _polite?.Announce($"{NameOf(item.SourceKey)} removed");
        await NotifyAsync();
    }

    private void StartAdd()
    {
        var first = Addable.FirstOrDefault();
        _adding = new AddDraft { SourceKey = first?.Key };
        _focusId = $"{_uid}-add-symbol";
    }

    private void CancelAdd()
    {
        _adding = null;
        _focusId = AddButtonId;
    }

    private void OnAddSourceChanged(string value)
    {
        if (_adding is null)
            return;
        _adding.SourceKey = value;
        _adding.SourceError = null;
    }

    private void OnAddSymbolChanged(string? value)
    {
        if (_adding is null)
            return;
        _adding.Symbol = value;
        _adding.SymbolError = null;
    }

    private void TryAdd()
    {
        if (_adding is not { } adding)
            return;

        adding.SourceError = string.IsNullOrEmpty(adding.SourceKey) || Addable.All(s => s.Key != adding.SourceKey) ? "Choose a source." : null;
        adding.SymbolError = ValidateSymbol(adding.Symbol);
        if (adding.SourceError is not null || adding.SymbolError is not null)
            return;

        if (adding.Enabled)
        {
            _confirm = new PendingEnable(Guid.NewGuid(), null, NameOf(adding.SourceKey!), adding.Symbol!.Trim(), $"{_uid}-add-on {AddButtonId}", NoteOf(adding.SourceKey!));
            return;
        }

        CommitAdd(enabled: false);
        _ = NotifyAsync();
    }

    private void CommitAdd(bool enabled)
    {
        if (_adding is not { SourceKey: { } key } adding)
            return;

        var item = new SourceDraft
        {
            SourceKey = key,
            SourceSymbol = adding.Symbol?.Trim() ?? string.Empty,
            Enabled = enabled,
            HistoryStartDate = adding.HistoryStart is { } start ? DateOnly.FromDateTime(start) : null,
        };
        _draft.Add(item);
        _adding = null;
        _focusId = Status is { } status && _draft.Count < status.MaxBindingsPerInstrument && Addable.Count > 0 ? AddButtonId : RemoveId(item);
        _ = _polite?.Announce($"{NameOf(key)} added. Save to keep it.");
    }

    private async Task Discard()
    {
        _draft = [.. _saved.Select(SourceDraft.From)];
        _adding = null;
        _itemErrors.Clear();
        _ = _polite?.Announce("Changes discarded");
        await NotifyAsync();
    }

    // ── Save: one PUT for the whole list; priority is position ──────────────────────────────────────

    private async Task SaveAsync()
    {
        if (_saving)
            return;

        _itemErrors.Clear();
        foreach (var item in _draft)
        {
            if (ValidateSymbol(item.SourceSymbol) is { } error)
                _itemErrors[item.Key] = error;
        }

        if (_itemErrors.Count > 0)
        {
            _focusSummary = true;
            return;
        }

        _saving = true;
        StateHasChanged();
        var update = new InstrumentPriceBindingsUpdate
        {
            Bindings =
            [
                .. _draft.Select(d => new InstrumentPriceBindingInput
                {
                    SourceKey = d.SourceKey, SourceSymbol = d.SourceSymbol.Trim(), Enabled = d.Enabled, HistoryStartDate = d.HistoryStartDate,
                }),
            ],
        };

        var result = await PriceSources.ReplaceBindingsAsync(Instrument.InstrumentId, update);
        _saving = false;
        if (result.IsSuccess)
        {
            _ = _polite?.Announce("Price sources saved");
            // Last write wins (accepted); the panel re-reads what is stored now.
            await LoadAsync();
            await OnSaved.InvokeAsync();
            return;
        }

        var outcome = ApiResult.Failure(result.Status, result.Problem!);
        var lead = result.Status == HttpStatusCode.Forbidden
            ? "You don't have permission to change price sources"
            : "Unable to save the price sources";
        outcome.ToastOrFields(Snackbar, lead, AssignServerError);
        _focusSummary = _itemErrors.Count > 0;
        StateHasChanged();
    }

    /// <summary>Places a <c>bindings[i].*</c> error on that source's row; anything else is toasted.</summary>
    private bool AssignServerError(string field, string message)
    {
        var match = BindingErrorKey.Match(field);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index < 0 || index >= _draft.Count)
            return false;
        _itemErrors[_draft[index].Key] = message;
        return true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var target = _focusSummary ? SummaryId : _focusId;
        _focusSummary = false;
        _focusId = null;
        if (target is null)
            return;
        try { await Js.InvokeVoidAsync("odsFocusById", target); }
        catch (JSException) { }
        catch (InvalidOperationException) { /* no JS runtime under bUnit */ }
    }
}
