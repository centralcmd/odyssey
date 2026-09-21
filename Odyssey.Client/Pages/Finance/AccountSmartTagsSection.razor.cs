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
}

public partial class AccountSmartTagsSection
{
    /// <summary>The record the watchlist hangs off.</summary>
    [Parameter] public SmartTagHost Host { get; set; } = SmartTagHost.Account;

    /// <summary>The record's id — an <c>AccountId</c> or a <c>ContractId</c>, per <see cref="Host"/>.</summary>
    [Parameter, EditorRequired] public Guid SubjectId { get; set; }

    /// <summary>Gates the add/remove controls (<c>accounts.update</c> / <c>contracts.update</c>).
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

    private bool _capKnown => !_limitsDegraded && _maxTags > 0;
    private bool _atCap => _capKnown && _smartTags.Count >= _maxTags;
    private decimal _total => _transactions.Sum(t => t.Amount);

    private string Subject => Host == SmartTagHost.Contract ? "contract" : "account";

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

    private string NoMatchDescription => NoMatchDesc ?? "No transactions carry the selected tags yet.";

    // The header pill — matching-transaction count, only once tags exist and we're settled.
    private bool ShowCount => _hasTags && !_isLoadingTxns && _error is null;
    private bool ShowTotal => _hasTags && !_isLoadingTxns && _error is null && _transactions.Count > 0;

    private RenderFragment? CountFragment => ShowCount
        ? builder => builder.AddContent(0, _transactions.Count)
        : null;

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

        _maxTags = (await AccountLimits.GetAsync()).MaxSmartTagsPerAccount;
        _limitsDegraded = false;
    }

    private Task<ApiResult<List<ExistingTransactionTag>>> ListSmartTagsAsync() =>
        Host == SmartTagHost.Contract
            ? Contracts.ListSmartTagsAsync(SubjectId)
            : Accounts.ListSmartTagsAsync(SubjectId);

    private async Task LoadTransactionsAsync()
    {
        if (!_hasTags)
        {
            _transactions = [];
            StateHasChanged();
            return;
        }

        _isLoadingTxns = true;
        StateHasChanged();

        // Cross-record: filter only by the watched tags, not by this account or contract — a smart
        // tag surfaces every transaction carrying it, wherever it lives. On the contract host there
        // is no other option: no transaction carries a ContractId.
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

    private async Task AddTag(string tagId)
    {
        // Empty body — the association is identified entirely by the URL path.
        var id = Guid.Parse(tagId);
        var result = Host == SmartTagHost.Contract
            ? await Contracts.AddSmartTagAsync(SubjectId, id)
            : await Accounts.AddSmartTagAsync(SubjectId, id);

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
        var result = Host == SmartTagHost.Contract
            ? await Contracts.RemoveSmartTagAsync(SubjectId, id)
            : await Accounts.RemoveSmartTagAsync(SubjectId, id);

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
        await OnCountChanged.InvokeAsync(_smartTags.Count);
        await LoadTransactionsAsync();
    }

    private void SyncSelected() =>
        _selectedIds = _smartTags.Select(t => t.TransactionTagId.ToString()).ToList();
}
