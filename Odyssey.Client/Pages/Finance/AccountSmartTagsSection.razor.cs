using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>Which record a smart-tag watchlist hangs off.</summary>
/// <remarks>
/// The two hosts differ in three things and nothing else: which endpoints the add/remove/list calls
/// go to, which claim-free limits endpoint serves the cap, and the noun in the default copy. Keeping
/// them one component is the design system's own instruction — a second component would be a second
/// place for the states to drift.
/// </remarks>
public enum SmartTagHost
{
    Account,
    Contract,

    /// <summary>A property (issue #167) — its own endpoints and its own <c>/api/property-limits</c> cap.</summary>
    Property,
}

public partial class AccountSmartTagsSection
{
    /// <summary>The record the watchlist hangs off.</summary>
    [Parameter] public SmartTagHost Host { get; set; } = SmartTagHost.Account;

    /// <summary>The record's id — an <c>AccountId</c>, a <c>ContractId</c> or a <c>PropertyId</c>, per <see cref="Host"/>.</summary>
    [Parameter, EditorRequired] public Guid SubjectId { get; set; }

    /// <summary>Gates the add/remove controls (<c>accounts.update</c> / <c>contracts.update</c> /
    /// <c>properties.update</c>).
    /// Read-only viewers keep the chips + table.</summary>
    [Parameter] public bool CanWrite { get; set; }

    /// <summary>
    /// The disclosure shell. False renders the section bare — no OdsCollapsible, no header — for a
    /// host that introduces it with its own OdsSectionDivider (an OdsRecordCard body). The content
    /// is then always visible, so it relies on the initial load rather than the lazy first-expand.
    /// </summary>
    [Parameter] public bool Chrome { get; set; } = true;

    /// <summary>
    /// The section's leading glyph. <c>sell</c> on an account; the contract host passes
    /// <c>local_offer</c>, because <c>sell</c> already denotes Terms on a contract record and one
    /// glyph meaning two things in one counts strip is worse than two glyphs meaning one thing each.
    /// </summary>
    [Parameter] public string Icon { get; set; } = "sell";

    /// <summary>Formats a money amount in its currency — supplied by the host (per-account currency).</summary>
    [Parameter, EditorRequired] public Func<decimal, string?, string> FormatMoney { get; set; } = (v, _) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Overrides the empty-state sentence. The contract host needs it: the default noun substitution
    /// would say the match is scoped to the record, and a contract's smart tags are not.
    /// </summary>
    [Parameter] public string? EmptyDesc { get; set; }

    /// <summary>Overrides the no-matching-transactions sentence, for the same reason.</summary>
    [Parameter] public string? NoMatchDesc { get; set; }

    /// <summary>Raised with the new smart-tag count after a load or an add/remove, so the host can keep
    /// the record-row header badge live without re-fetching the whole list.</summary>
    [Parameter] public EventCallback<int> OnCountChanged { get; set; }

    /// <summary>
    /// Contract host: the caller holds <c>transactions.read</c>. Without it the scoped read would be a
    /// <c>403</c>, so it is never issued — the chips stay and the body says which claim is missing.
    /// </summary>
    [Parameter] public bool CanReadTransactions { get; set; } = true;

    /// <summary>
    /// Contract host: the contract is a one-off (<c>CompletionDate</c> set). The response cannot say
    /// so — an unbounded window reads the same for a one-off and for a contract with no dates — and
    /// the scope line names the reason.
    /// </summary>
    [Parameter] public bool IsOneOff { get; set; }

    /// <summary>
    /// Contract host: a key over every contract input the match depends on besides its tags — the
    /// party contacts and the term dates. A change re-issues the read, so adding a party or fixing a
    /// date updates the section without a reload of the page.
    /// </summary>
    [Parameter] public string? ScopeKey { get; set; }

    /// <summary>Contract host: the "Add a party" action on the no-contact-party state. Unset hides it.</summary>
    [Parameter] public EventCallback OnAddParty { get; set; }

    /// <summary>Contract host: the "Edit dates" action on the invalid-term state. Unset hides it.</summary>
    [Parameter] public EventCallback OnEditContract { get; set; }

    // The cap was `private const int MaxTags = 20` here, mirroring a server constant. Once the server
    // value became admin-editable (issue #434 key 15) that mirror was the defect class CLAUDE.md names
    // outright: lowering the setting would let a user add tags the server then refused, and raising it
    // would be unusable because this pre-check still stopped at 20. It is served from the claim-free
    // /api/account-limits or /api/contract-limits endpoint through a session cache that a settings save
    // invalidates, and the effective number is interpolated into the message rather than written into it.

    private List<ExistingTransactionTag> _smartTags = [];
    private List<OdsOption> _options = [];
    private List<ExistingTransaction> _transactions = [];

    private bool _isOpen;
    private bool _isLoaded;
    private bool _isLoadingTxns;
    private string? _error;

    /// <summary>
    /// A refused add, verbatim from the server — the 422 at the cap (which names the effective
    /// number), the 422 on an archived tag, the 409 on an already-linked pair. Rendered on the bar
    /// rather than toasted: the refusal explains a control the reader is still looking at.
    /// </summary>
    private string? _addError;

    private IReadOnlyCollection<string> _selectedIds = [];
    private bool _hasTags => _smartTags.Count > 0;

    private int _maxTags = AccountLimitsCache.Fallback.MaxSmartTagsPerAccount;

    /// <summary>
    /// The limits read failed or came back <c>503</c>. There is then no number to pre-check against,
    /// so the adder stays open and the server's conservative bound does the refusing — never a
    /// guessed ceiling. Only the contract host can reach this state: its cache reports the degraded
    /// read, while <see cref="AccountLimitsCache"/> deliberately resolves to its fallback.
    /// </summary>
    private bool _limitsDegraded;

    // ── Contract host (issue #226): the server-scoped, server-paged read ──────────────────────────
    private static readonly OdsTableSort DefaultSort = new("date", OdsSortDirection.Desc);

    /// <summary>
    /// Table header key → the endpoint's <c>TransactionSortBy</c>. The Tag column has no server key,
    /// so its header is inert here — a click keeps the current sort rather than sorting one page.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ServerSortKeys = new Dictionary<string, string>
    {
        ["date"] = "Date",
        ["amount"] = "Amount",
        ["desc"] = "Desc",
        ["contact"] = "Contact",
        ["account"] = "Account",
        ["status"] = "Status",
    };

    private string _search = "";
    private OdsTableSort _sort = DefaultSort;
    private int _page = 1;
    private int _pageSize = OdsPageSizes.Default[0];
    private int _totalCount;
    private ContractSmartTagScope? _scope;
    private ContractSmartTagSummary? _summary;
    private string? _loadedScopeKey;

    /// <summary>The outcome of the last scoped read, for the polite status region.</summary>
    private string? _liveMessage;

    private ElementReference _scopeLine;

    /// <summary>Set when a blocked state cleared on the last read; focus then moves to the scope line.</summary>
    private bool _focusScopeLine;

    /// <summary>
    /// The latest scoped read issued. A search keystroke, a sort and a page change can overlap, and
    /// only the newest may land — an older response arriving last would show rows for a query the
    /// reader has already replaced.
    /// </summary>
    private int _readSequence;

    private bool IsScoped => Host == SmartTagHost.Contract;

    private int MatchCount => IsScoped ? _totalCount : _transactions.Count;

    /// <summary>The one currency row, when the match has exactly one — otherwise there is no single sum.</summary>
    private ContractSmartTagCurrencyTotal? _singleCurrency =>
        _summary is { ByCurrency: [var only] } ? only : null;

    private string TotalClass => IsScoped
        ? _singleCurrency is { } net ? (net.Net < 0 ? "expense" : "income") : ""
        : _total < 0 ? "expense" : "income";

    private ContractSmartTagEmptyReason Reason => _scope?.EmptyReason ?? ContractSmartTagEmptyReason.None;

    private bool ShowToolbar => IsScoped && Blocked is null && (_totalCount > 0 || _search.Length > 0);

    private sealed record ScopeItem(string Icon, string Label, string Value);

    private sealed record BlockedState(
        string Icon, string Title, string Description, string? ActionLabel = null, string? ActionIcon = null,
        EventCallback OnAction = default);

    private static string ScopeDay(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture);

    /// <summary>
    /// The scope line, straight off the response. <c>toExclusive</c> is the NEXT midnight, so the
    /// last covered day is the one before it. Withheld for an invalid term, whose own state says it.
    /// </summary>
    private IReadOnlyList<ScopeItem>? ScopeItems
    {
        get
        {
            if (!IsScoped || !CanReadTransactions || _scope is null || Reason == ContractSmartTagEmptyReason.InvalidTerm)
                return null;

            var from = _scope.From is { } f ? ScopeDay(f) : null;
            var last = _scope.ToExclusive is { } t ? ScopeDay(t.AddDays(-1)) : null;
            var window = (from, last) switch
            {
                ({ } a, { } b) => $"{a} – {b}",
                ({ } a, null) => $"from {a}, no end",
                (null, { } b) => $"until {b}",
                _ => IsOneOff ? "any date — one-off contract" : "any date — no term set",
            };
            var parties = _scope.PartyContactCount;
            var merchant = parties == 0
                ? "no contact party"
                : $"one of {parties} contact part{(parties == 1 ? "y" : "ies")}";

            return [new("date_range", "Dated", window), new("group", "Merchant", merchant)];
        }
    }

    /// <summary>
    /// A structural empty result: the read was never issued (no <c>transactions.read</c>), or the
    /// server named a reason nothing can match yet. <c>NoSmartTags</c> is not here — it is the
    /// existing first-tag empty state.
    /// </summary>
    private BlockedState? Blocked
    {
        get
        {
            if (!IsScoped || !_hasTags)
                return null;

            if (!CanReadTransactions)
                return new("lock", "Transactions are not visible to you",
                    "The watched tags stay listed. Showing what matches them needs transactions.read as well as contracts.read.");

            return Reason switch
            {
                ContractSmartTagEmptyReason.NoContactParties => new("person_off", "No contact is a party yet",
                    "A transaction counts here only when its merchant is a contact that is a party to this contract. Account and property parties are not merchants.",
                    CanWrite && OnAddParty.HasDelegate ? "Add a party" : null, "group_add", OnAddParty),
                ContractSmartTagEmptyReason.InvalidTerm when _scope is { From: { } from, ToExclusive: { } to } =>
                    new("event_busy", "The term ends before it starts",
                        $"Start date {ScopeDay(from)} is after end date {ScopeDay(to.AddDays(-1))}, so no date can fall inside the term. Correct the dates to see matching transactions.",
                        CanWrite && OnEditContract.HasDelegate ? "Edit dates" : null, "edit_calendar", OnEditContract),
                _ => null,
            };
        }
    }

    private bool _capKnown => !_limitsDegraded && _maxTags > 0;
    private bool _atCap => _capKnown && _smartTags.Count >= _maxTags;
    private decimal _total => _transactions.Sum(t => t.Amount);

    private string Subject => Host switch
    {
        SmartTagHost.Contract => "contract",
        SmartTagHost.Property => "property",
        _ => "account",
    };

    /// <summary>
    /// The cap sentence, shown both inside the adder and on the bar. It interpolates the effective
    /// number — never a literal, which would go stale the moment an administrator changed the
    /// setting — and says nothing at all while there is room left.
    /// </summary>
    private string? CapNote => _atCap
        ? $"Watching the maximum of {_maxTags} tag{(_maxTags == 1 ? "" : "s")} — remove one to add another."
        : _limitsDegraded
            ? "The tag limit is unavailable right now, so an add may be refused."
            : null;

    private string EmptyDescription => EmptyDesc ?? (CanWrite
        ? $"Pin a tag to watch its transactions from this {Subject} without re-filtering the ledger."
        : $"No tags are being watched on this {Subject}.");

    private string NoMatchDescription => IsScoped && _search.Length > 0
        ? $"No matching transaction mentions “{_search}”."
        : NoMatchDesc ?? "No transactions carry the selected tags yet.";

    // The header pill — matching-transaction count, only once tags exist and we're settled.
    private bool ShowCount => _hasTags && !_isLoadingTxns && _error is null && Blocked is null;
    private bool ShowTotal => ShowCount && MatchCount > 0;

    private RenderFragment? CountFragment => ShowCount
        ? builder => builder.AddContent(0, MatchCount)
        : null;

    protected override async Task OnParametersSetAsync()
    {
        // A party or a date changed on the contract: the scope moved, so the match did too. The first
        // load records the key; only a CHANGE after it re-reads.
        if (IsScoped && _isLoaded && _loadedScopeKey != ScopeKey)
        {
            _loadedScopeKey = ScopeKey;
            // The match may have shrunk: a page past its end would show nothing.
            _page = 1;
            await LoadTransactionsAsync();
        }
    }

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;
        await LoadAsync();
    }

    private async Task ToggleOpen()
    {
        _isOpen = !_isOpen;
        // Lazy-load on first expand if the initial fetch was skipped (e.g. prerender).
        if (_isOpen && !_isLoaded && _error is null)
            await LoadAsync();
    }

    /// <summary>
    /// Re-reads the cap, the watchlist and the matching transactions. Public because
    /// <c>OnInitializedAsync</c> early-returns outside the browser, so a render test has no other way
    /// in — the same seam <c>ContractEventsSection</c> exposes, and the same one the retry uses.
    /// </summary>
    public Task ReloadAsync() => LoadAsync();

    private Task Reload() => LoadAsync();

    private void DismissAddError() => _addError = null;

    // Loads the configured smart tags + the selectable-tag option pool, then the matching
    // transactions when tags exist. Drives the inline error panel on failure (no snackbar).
    private async Task LoadAsync()
    {
        _error = null;
        await LoadLimitsAsync();

        var smartResult = await ListSmartTagsAsync();
        var smartTags = smartResult.ValueOr([]);
        if (!smartResult.IsSuccess)
        {
            _error = "Could not load smart tags. Try again.";
            _isLoaded = true;
            StateHasChanged();
            return;
        }

        // Served from the session's reference-data cache (issue #372), so expanding one record
        // after another doesn't re-fetch the tag catalogue each time.
        var allTags = await ReferenceData.TransactionTagsAsync();

        _smartTags = smartTags;
        _options = allTags
            .Where(t => t.Archived is null)
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new OdsOption(t.TransactionTagId.ToString(), t.Name))
            .ToList();
        SyncSelected();

        _isLoaded = true;
        _loadedScopeKey = ScopeKey;
        await LoadTransactionsAsync();
    }

    private async Task LoadLimitsAsync()
    {
        if (Host == SmartTagHost.Contract)
        {
            var limits = await ContractLimits.GetAsync();
            _maxTags = limits.MaxSmartTagsPerContract;
            _limitsDegraded = limits.IsDegraded;
            return;
        }

        if (Host == SmartTagHost.Property)
        {
            var limits = await PropertyLimits.GetAsync();
            _maxTags = limits.MaxSmartTagsPerProperty;
            _limitsDegraded = limits.IsDegraded;
            return;
        }

        _maxTags = (await AccountLimits.GetAsync()).MaxSmartTagsPerAccount;
        _limitsDegraded = false;
    }

    private Task<ApiResult<List<ExistingTransactionTag>>> ListSmartTagsAsync() => Host switch
    {
        SmartTagHost.Contract => Contracts.ListSmartTagsAsync(SubjectId),
        SmartTagHost.Property => Properties.ListSmartTagsAsync(SubjectId),
        _ => Accounts.ListSmartTagsAsync(SubjectId),
    };

    private async Task LoadTransactionsAsync()
    {
        if (!_hasTags)
        {
            _transactions = [];
            _totalCount = 0;
            _scope = null;
            _summary = null;
            StateHasChanged();
            return;
        }

        if (IsScoped)
        {
            await LoadScopedTransactionsAsync();
            return;
        }

        _isLoadingTxns = true;
        StateHasChanged();

        // Cross-record: filter only by the watched tags, not by this account or property — a smart
        // tag surfaces every transaction carrying it, wherever it lives.
        var result = await Transactions.ListAllAsync(
            tagIds: [.. _smartTags.Select(t => t.TransactionTagId.ToString())]);

        if (!result.IsSuccess)
        {
            _error = "Could not load matching transactions. Try again.";
            _transactions = [];
        }
        else
        {
            _transactions = result.ValueOr([]);
        }

        _isLoadingTxns = false;
        StateHasChanged();
    }

    // The contract host's one read: the server applies the tag, term and merchant-is-party rules and
    // returns a page, the scope it applied and the totals over the whole match. Nothing here
    // re-derives any of it.
    private async Task LoadScopedTransactionsAsync()
    {
        if (!CanReadTransactions)
        {
            _transactions = [];
            StateHasChanged();
            return;
        }

        var sequence = ++_readSequence;
        _error = null;
        _isLoadingTxns = true;
        StateHasChanged();

        var sortBy = ServerSortKeys.GetValueOrDefault(_sort.Key);
        var result = await Contracts.ListSmartTagTransactionsAsync(
            SubjectId, _page, _pageSize,
            search: _search.Length > 0 ? _search : null,
            sortBy: sortBy,
            sortDir: sortBy is null ? null : _sort.Dir == OdsSortDirection.Asc ? "Asc" : "Desc");

        if (sequence != _readSequence)
            return;

        if (result is { IsSuccess: true, Value: { } value })
        {
            var wasBlocked = Blocked is not null;
            _scope = value.Scope;
            _summary = value.Summary;
            _transactions = [.. value.Page.Items];
            _totalCount = value.Page.TotalCount;

            // The fixing action (Add a party / Edit dates) unmounted with the blocked state, so focus
            // would fall to the page; land it on the scope line, which now states the working scope.
            _focusScopeLine = wasBlocked && Blocked is null;
            _liveMessage = Blocked is { } blocked
                ? blocked.Title
                : _totalCount == 0
                    ? NoMatchDescription
                    : $"{_totalCount} matching transaction{(_totalCount == 1 ? "" : "s")}.";
        }
        else
        {
            _error = "Could not load matching transactions. Try again.";
            _transactions = [];
            _totalCount = 0;
            _scope = null;
            _summary = null;
            _liveMessage = null;
        }

        _isLoadingTxns = false;
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusScopeLine)
            return;

        _focusScopeLine = false;
        try
        {
            await _scopeLine.FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // Not rendered after all (a read that ended in an error) — nothing to land on.
        }
    }

    private async Task OnSearchChanged(string? value)
    {
        var next = value?.Trim() ?? "";
        if (next == _search)
            return;

        _search = next;
        _page = 1;
        await LoadTransactionsAsync();
    }

    private async Task OnSortChanged(OdsTableSort sort)
    {
        // A column the server cannot sort by keeps the current order.
        if (!ServerSortKeys.ContainsKey(sort.Key))
            return;

        _sort = sort;
        _page = 1;
        await LoadTransactionsAsync();
    }

    private async Task OnPageChanged(int page)
    {
        _page = page;
        await LoadTransactionsAsync();
    }

    private async Task OnPageSizeChanged(int size)
    {
        _pageSize = size;
        _page = 1;
        await LoadTransactionsAsync();
    }

    private async Task AddTag(string tagId)
    {
        // Empty body — the association is identified entirely by the URL path.
        var id = Guid.Parse(tagId);
        var result = Host switch
        {
            SmartTagHost.Contract => await Contracts.AddSmartTagAsync(SubjectId, id),
            SmartTagHost.Property => await Properties.AddSmartTagAsync(SubjectId, id),
            _ => await Accounts.AddSmartTagAsync(SubjectId, id),
        };

        if (result.IsSuccess)
        {
            _addError = null;
            await ReloadTagsAndTransactions();
            return;
        }

        // The server's own sentence, on the bar. A toast would be gone before the reader looked back
        // at the control that refused, and at the cap the message carries the effective number — the
        // one thing this component must never restate itself.
        _addError = result.Problem?.Detail is { Length: > 0 } detail
            ? detail
            : "Could not add tag.";
        StateHasChanged();
    }

    private async Task RemoveTag(string tagId)
    {
        var id = Guid.Parse(tagId);
        var result = Host switch
        {
            SmartTagHost.Contract => await Contracts.RemoveSmartTagAsync(SubjectId, id),
            SmartTagHost.Property => await Properties.RemoveSmartTagAsync(SubjectId, id),
            _ => await Accounts.RemoveSmartTagAsync(SubjectId, id),
        };

        if (result.Toast(Snackbar, "Could not remove tag"))
        {
            _addError = null;
            await ReloadTagsAndTransactions();
        }
    }

    private Task RemoveTag(Guid tagId) => RemoveTag(tagId.ToString());

    private async Task ReloadTagsAndTransactions()
    {
        _error = null;
        var smartResult = await ListSmartTagsAsync();
        var smartTags = smartResult.ValueOr([]);
        if (!smartResult.IsSuccess)
        {
            _error = "Could not load smart tags. Try again.";
            StateHasChanged();
            return;
        }

        _smartTags = smartTags;
        SyncSelected();
        _page = 1;
        await OnCountChanged.InvokeAsync(_smartTags.Count);
        await LoadTransactionsAsync();
    }

    private void SyncSelected() =>
        _selectedIds = _smartTags.Select(t => t.TransactionTagId.ToString()).ToList();
}
