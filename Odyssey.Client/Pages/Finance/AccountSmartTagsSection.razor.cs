using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AccountSmartTagsSection
{
    [Parameter, EditorRequired] public ExistingAccount Account { get; set; } = default!;

    /// <summary>Gates the add/remove controls (accounts.update). Read-only viewers keep the chips + table.</summary>
    [Parameter] public bool CanWrite { get; set; }

    /// <summary>Gates the file-download buttons in an expanded row's attached-files detail.</summary>
    [Parameter] public bool CanDownloadFiles { get; set; }

    /// <summary>Formats a money amount in its currency — supplied by the host (per-account currency).</summary>
    [Parameter, EditorRequired] public Func<decimal, string?, string> FormatMoney { get; set; } = (v, _) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Raised with the new smart-tag count after a load or an add/remove, so the host can keep
    /// the account-row header badge live without re-fetching the whole account list.</summary>
    [Parameter] public EventCallback<int> OnCountChanged { get; set; }

    // The cap was `private const int MaxTags = 20` here, mirroring a server constant. Once the server
    // value became admin-editable (issue #434 key 15) that mirror was the defect class CLAUDE.md names
    // outright: lowering the setting would let a user add tags the server then refused, and raising it
    // would be unusable because this pre-check still stopped at 20. It is served from the claim-free
    // /api/account-limits endpoint through a session cache that a settings save invalidates, and the
    // effective number is interpolated into the child's message rather than written into it.
    //
    // There is deliberately NO failure branch: AccountLimitsCache.GetAsync cannot fail (it ends in
    // `?? Fallback`), the upload surfaces this mirrors do not disable either, and the server remains the
    // control — AccountSmartTagService rejects an over-cap add whatever this component believes.

    private List<ExistingTransactionTag> _smartTags = [];
    private List<OdsOption> _options = [];
    private List<ExistingTransaction> _transactions = [];

    private bool _isOpen;
    private bool _isLoaded;
    private bool _isLoadingTxns;
    private string? _error;

    private IReadOnlyCollection<string> _selectedIds = [];
    private bool _hasTags => _smartTags.Count > 0;
    private int _maxTags = AccountLimitsCache.Fallback.MaxSmartTagsPerAccount;
    private bool _atCap => _smartTags.Count >= _maxTags;
    private decimal _total => _transactions.Sum(t => t.Amount);

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

    private Task Reload() => LoadAsync();

    // Loads the configured smart tags + the selectable-tag option pool, then the matching
    // transactions when tags exist. Drives the inline error panel on failure (no snackbar).
    private async Task LoadAsync()
    {
        _error = null;
        _maxTags = (await AccountLimits.GetAsync()).MaxSmartTagsPerAccount;

        var smartResult = await Accounts.ListSmartTagsAsync(Account.AccountId);
        var smartTags = smartResult.ValueOr([]);
        if (!smartResult.IsSuccess)
        {
            _error = "Could not load smart tags. Try again.";
            _isLoaded = true;
            StateHasChanged();
            return;
        }

        // Served from the session's reference-data cache (issue #372), so expanding one account
        // after another doesn't re-fetch the tag catalogue each time. (Was TryLoadAsync<List<T>>
        // against an endpoint that returns PagedResult<T> — the deserialize always failed, so the
        // picker silently had no options.)
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

        // Cross-account: filter only by the watched tags, not by this account — a smart tag
        // surfaces every transaction carrying it, wherever it lives.
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
        if ((await Accounts.AddSmartTagAsync(Account.AccountId, Guid.Parse(tagId))).Toast(Snackbar, "Could not add tag"))
        {
            await ReloadTagsAndTransactions();
        }
    }

    private async Task RemoveTag(string tagId)
    {
        if ((await Accounts.RemoveSmartTagAsync(Account.AccountId, Guid.Parse(tagId))).Toast(Snackbar, "Could not remove tag"))
        {
            await ReloadTagsAndTransactions();
        }
    }

    private Task RemoveTag(Guid tagId) => RemoveTag(tagId.ToString());

    private async Task ReloadTagsAndTransactions()
    {
        _error = null;
        var smartResult = await Accounts.ListSmartTagsAsync(Account.AccountId);
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

    // Read-only row menu: expand/collapse the detail + copy the id (mirrors AccountTransactionsSection).
    private IReadOnlyList<OdsMenuItem> BuildActions(ExistingTransaction t, OdsRecordActionContext ctx) =>
    [
        new()
        {
            Icon = ctx.Expanded ? "close" : "expand_more",
            Label = ctx.Expanded ? "Collapse" : "View details",
            OnClick = EventCallback.Factory.Create(this, ctx.Toggle),
        },
        new()
        {
            Icon = "fingerprint",
            TrailingIcon = "content_copy",
            Label = "Copy ID",
            OnClick = EventCallback.Factory.Create(this, () => CopyTransactionId(t.TransactionId)),
        },
    ];

    private Task CopyTransactionId(Guid transactionId) =>
        Clipboard.CopyAsync(transactionId.ToString(), "Transaction ID copied to clipboard.");
}
