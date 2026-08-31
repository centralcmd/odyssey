using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Microsoft.AspNetCore.Components;
using System.Net.Http.Json;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Auth;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Models;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AccountsCard
{
    // ── Data ───────────────────────────────────────────────────────────────
    private List<ExistingAccount> _accounts  = [];
    // Server-computed rollup backing the overview donuts + header count/balance (issue #372). It
    // spans every account — unlike the display list, which the server filters — and replaces the
    // full-table fetch the page used to do just to count and sum rows in the browser.
    private AccountSummary? _summary;
    private List<ExistingCurrency> _currencies = [];
    private Dictionary<string, ExistingCurrency> _currenciesByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NumberFormatInfo> _moneyFormatCache = new(StringComparer.OrdinalIgnoreCase);

    // ── Account problems (exchange-rate signals) ──
    // Server totals are loaded only to learn which accounts have no rate to the main
    // currency; those become per-account "missing rate" warnings (chip + record alert +
    // header rollup). Keyed by account id.
    private AccountTotals? _totals;
    private Dictionary<Guid, AccountProblem> _problems = new();
    private Guid? _flashId;          // one-shot attention ring after a header-rollup jump

    // Card-list windowing (OdsInfiniteList): "Load N at a time" batch size.
    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;
    private Guid _filesRefreshToken = Guid.NewGuid();

    // ── Persisted page state (header sections + filters) ─────────────────────
    // Defaults match the page's design: Problems/Overview/Search open. Restored
    // from (and saved to) the user's preferences under PageStateKey.
    private const string PageStateKey = "accounts-page";
    private bool _problemsOpen = true;
    private bool _overviewOpen = true;
    private bool _searchOpen   = true;

    // ── Filter state ─────────────────────────────────────────────────────────
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _typeFilter   = [];
    private IReadOnlyCollection<string> _statusFilter = [];

    // The list is server-filtered, so an empty result only means "first run" when nothing is filtering it.
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString)
        || _typeFilter.Count > 0 || _statusFilter.Count > 0;

    private static readonly IReadOnlyList<OdsOption> _typeOptions =
        [.. AccountTypeVisuals.Selectable.Select(t => new OdsOption(t.ToString(), AccountTypeVisuals.Label(t))
            { Icon = AccountTypeVisuals.MaterialIcon(t), IconColor = AccountTypeVisuals.FgColor(t) })];

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [new("Open", "Open"), new("Closed", "Closed"), new("Archived", "Archived")];

    // ── Sort (§6.1) ──────────────────────────────────────────────────────────
    // The toolbar OdsSortSelect is the SOLE sort surface — this card list has no
    // column headers. Balance / Transaction count compare raw values (no FX, §14);
    // Account type sorts by the enum's declared order (not the label).
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingAccount>> _sortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = a => a.Name.ToLowerInvariant() },
        new() { Key = "balance", Label = "Balance", Type = OdsSortType.Number, SortValue = a => a.Balance },
        new() { Key = "type", Label = "Account type", Type = OdsSortType.Status, SortValue = a => (int)a.AccountType },
        new() { Key = "opened", Label = "Date opened", Type = OdsSortType.Date, SortValue = a => a.Opened },
        new() { Key = "txnCount", Label = "Transaction count", Type = OdsSortType.Number, SortValue = a => a.TransactionCount },
    ];

    // ── Permissions ──────────────────────────────────────────────────────────
    private bool _canCreateAccounts;
    private bool _canUpdateAccounts;
    private bool _canDeleteAccounts;
    private bool _canDownloadFiles;
    private bool _canUploadFiles;
    private bool _canDeleteFiles;
    private bool _canEditFiles;
    private bool _canAnalyzeFiles;
    private bool _canCreateTransactions;
    private bool _canReadTransactions;
    private bool _canReadTerms;
    private bool _canWriteTerms;
    private bool _canReadEstimates;
    private bool _canWriteEstimates;

    // ── Computed ─────────────────────────────────────────────────────────────
    private int ActiveCount => _summary?.CountsByStatus.Open ?? 0;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        await Task.WhenAll(GetAccounts(), LoadSummary(), LoadCurrencies());
        await LoadTotals();
    }

    // ── Page-state persistence (sections + filters) ───────────────────────────
    // Loads the saved layout; if none (or it can't be read) the page keeps its
    // defaults and writes them straight back, so a preference always exists going
    // forward. A stale blob from an older header is tolerated — unknown members are
    // ignored, missing ones default, and filter values no longer offered are dropped.
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<AccountsPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(AccountsPageState state)
    {
        _problemsOpen  = state.ProblemsOpen;
        _overviewOpen  = state.OverviewOpen;
        _searchOpen    = state.SearchOpen;
        _searchString  = state.Search ?? string.Empty;
        _typeFilter    = _typeOptions.KnownValues(state.TypeFilter);
        _statusFilter  = _statusOptions.KnownValues(state.StatusFilter);
        _sort          = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch         = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private AccountsPageState BuildPageState() => new()
    {
        ProblemsOpen  = _problemsOpen,
        OverviewOpen  = _overviewOpen,
        SearchOpen    = _searchOpen,
        Search        = _searchString,
        TypeFilter    = [.. _typeFilter],
        StatusFilter  = [.. _statusFilter],
        SortField     = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize     = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnProblemsToggled(bool open) { _problemsOpen = open; PersistPageState(); }
    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open)   { _searchOpen = open;   PersistPageState(); }

    // Value change persists + keeps the box current; the server refetch is debounced via the field's
    // DebounceInterval → OnSearch (ReloadAsync). Filter/sort changes refetch immediately.
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private async Task OnTypeFilterChanged(IReadOnlyCollection<string> values) { _typeFilter = values ?? []; PersistPageState(); await GetAccounts(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await GetAccounts(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await GetAccounts(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _typeFilter = [];
        _statusFilter = [];
        PersistPageState();
        await GetAccounts();
    }

    /// <summary>The Accounts page layout persisted to user preferences. A plain
    /// mutable record so System.Text.Json round-trips it and tolerates schema drift
    /// (unknown members ignored, missing ones defaulted).</summary>
    private sealed class AccountsPageState
    {
        public bool ProblemsOpen { get; set; } = true;
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TypeFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    // Fetches server-computed totals to learn which accounts have no rate to the main
    // currency, then rebuilds the rate-warning signals. Safe to call repeatedly after
    // mutations — it does not clear the current signals upfront, so re-fetching never
    // flashes them away; they're replaced atomically once the new data arrives. A 503
    // means the exchange-rates feature is off → no signals.
    private async Task LoadTotals()
    {
        try
        {
            await UserPreferences.LoadUserPreferencesAsync();
            var mainCurrency = UserPreferences.MainCurrency ?? "NOK";

            var result = await Accounts.GetTotalsAsync(mainCurrency);
            if (result.Status == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                // A missing exchange rate — the card renders "totals unavailable" rather than an error.
                _totals = null;
                _problems = new();
                return;
            }

            if (!result.IsSuccess)
                return;

            _totals = result.Value;
            BuildProblems();
        }
        catch (Exception)
        {
            // Totals only drive rate-warning signals; on failure, leave the last-known set.
        }
        finally
        {
            StateHasChanged();
        }
    }

    // Translates the server's "unconverted accounts" (no rate to the main currency) into
    // per-account warning signals. Today this is the only problem kind; the model leaves
    // room for an error severity (e.g. a future sync failure) without reworking the UI.
    private void BuildProblems()
    {
        _problems = new();
        if (_totals is null)
            return;

        var main = _totals.MainCurrencyCode;
        foreach (var unconverted in _totals.UnconvertedAccounts)
        {
            _problems[unconverted.AccountId] = new AccountProblem(
                AccountId: unconverted.AccountId,
                Name: unconverted.Name,
                Severity: "warning",
                Chip: "No rate",
                Title: "Missing exchange rate",
                Summary: $"No {unconverted.CurrencyCode} → {main} rate for today, so this account is left out of the combined total.",
                Detail: $"Odyssey converts every account into your main currency ({main}) to show a combined value. There's no {unconverted.CurrencyCode} → {main} rate stored for today, so this balance is temporarily left out of the total. Add today's rate on the Exchange rates page to include it.");
        }
    }

    // Header-rollup → account: open the record, scroll it into view, flash an attention ring.
    private async Task JumpToAccount(Guid accountId)
    {
        _expandedId = accountId;
        _flashId = accountId;
        StateHasChanged();

        try
        {
            await ScrollManager.ScrollIntoViewAsync($"#acct-{accountId}", ScrollBehavior.Smooth);
        }
        catch (Exception)
        {
            // Scrolling is best-effort; the record is already expanded if the scroll fails.
        }

        await Task.Delay(OdsTiming.RowFlashMs);
        if (_flashId == accountId)
        {
            _flashId = null;
            StateHasChanged();
        }
    }

    // The contextual fix for a missing-rate problem: the Exchange rates page.
    private void GoToExchangeRates() => NavigationManager.NavigateTo("exchange-rates");

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreateAccounts    = user.HasPermission(PermissionClaims.AccountsCreate);
        _canUpdateAccounts    = user.HasPermission(PermissionClaims.AccountsUpdate);
        _canDeleteAccounts    = user.HasPermission(PermissionClaims.AccountsDelete);
        _canDownloadFiles     = user.HasPermission(PermissionClaims.FilesRead);
        _canUploadFiles       = user.HasPermission(PermissionClaims.FilesCreate);
        _canDeleteFiles       = user.HasPermission(PermissionClaims.AccountsUpdate);
        _canEditFiles         = user.HasPermission(PermissionClaims.AccountsUpdate)
                             && user.HasPermission(PermissionClaims.FilesUpdate);
        _canAnalyzeFiles      = user.HasPermission(PermissionClaims.FileAnalysisCreate)
                             && user.HasPermission(PermissionClaims.FileAnalysisRead)
                             && user.HasPermission(PermissionClaims.FileAnalysisImport);
        _canCreateTransactions = user.HasPermission(PermissionClaims.TransactionsCreate);
        _canReadTransactions   = user.HasPermission(PermissionClaims.TransactionsRead);
        _canReadTerms          = user.HasPermission(PermissionClaims.AccountsTermsRead);
        _canWriteTerms         = user.HasPermission(PermissionClaims.AccountsTermsWrite);
        _canReadEstimates      = user.HasPermission(PermissionClaims.AccountsEstimatesRead);
        _canWriteEstimates     = user.HasPermission(PermissionClaims.AccountsEstimatesWrite);
    }

    // Server-side fetch (issue #277): search/filters/sort are applied by the API, not in the browser.
    // A single large page returns the whole result set (deferred pager UI). Called on first load and
    // on every search/filter/sort change; the full-page spinner shows only on first load — a refetch
    // shows the inline _refetching bar instead so the filter bar stays interactive.
    private async Task GetAccounts()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var result = await Accounts.ListAllAsync(
            search: _searchString,
            types: _typeFilter,
            statuses: _statusFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        if (result.IsSuccess)
        {
            _accounts = result.ValueOr([]);
            _loadError = false;
            _announce = _accounts.Count == 0 ? "No accounts match your filters."
                : $"Showing {_accounts.Count} account{(_accounts.Count == 1 ? "" : "s")}.";
        }
        else
        {
            Snackbar.Add($"Unable to load accounts: {result.Error}", Severity.Error);
            _loadError = true;
            _announce = "Couldn't load accounts.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // Unfiltered rollup for the overview donuts + header count/balance (issue #372). Silent on
    // failure like the other summary cards: the header simply renders its zero state.
    private async Task LoadSummary()
    {
        _summary = await Accounts.GetSummaryAsync();
        StateHasChanged();
    }

    private async Task LoadCurrencies()
    {
        _currencies = [.. await ReferenceData.ActiveCurrenciesAsync()];
        _currenciesByCode = _currencies.ToDictionary(c => c.CurrencyCode, StringComparer.OrdinalIgnoreCase);
        _moneyFormatCache.Clear(); // currency symbols/minor-units may have changed
    }

    // Projects the per-account problems into the PageHeader's rollup rows. The header
    // owns the toggle + count badge + severity-tinted alert rows; each row's View jumps
    // to the affected account (the contextual fix lives in the expanded record).
    private List<PageHeaderProblem> HeaderProblems =>
        _problems.Values
            .OrderBy(p => p.Name, StringComparer.CurrentCulture)
            .Select(p => new PageHeaderProblem
            {
                Severity = p.Severity == "error" ? PageHeaderSeverity.Error : PageHeaderSeverity.Warning,
                Lead = p.Name,
                Message = p.Summary,
                OnView = EventCallback.Factory.Create(this, () => JumpToAccount(p.AccountId)),
            })
            .ToList();

    private void ToggleExpand(Guid accountId)
    {
        _expandedId = _expandedId == accountId ? null : accountId;
    }

    private ExistingAccount? _uploadAccount;
    private Guid _uploadKey;
    private bool _uploadOpen;

    private void AddFile(ExistingAccount account)
    {
        if (!_canUploadFiles)
            return;

        _uploadAccount = account;
        _uploadKey = Guid.NewGuid();
        _uploadOpen = true;
    }

    private async Task OnFilesUploaded()
    {
        if (_uploadAccount is null)
            return;

        // Expand the account and force its files section to reload by changing its @key.
        _expandedId = _uploadAccount.AccountId;
        _filesRefreshToken = Guid.NewGuid();
        await GetAccounts();
    }

    // ── Edit mode (design-system update: the create dialog reused in edit mode, not an inline panel) ──
    private ExistingAccount? _editAccount;
    private Guid _editAccountKey;
    private bool _editAccountOpen;

    private void EditClicked(ExistingAccount account)
    {
        if (!_canUpdateAccounts)
            return;

        _editAccount = account;
        _editAccountKey = Guid.NewGuid();
        _editAccountOpen = true;
    }

    private async Task OnAccountEdited()
    {
        await LoadSummary();
        await GetAccounts();
        // Currency (or archived/closed) may have changed → the rate-warning set can shift.
        await LoadTotals();
    }

    // ── CRUD actions ─────────────────────────────────────────────────────────
    private bool _createAccountOpen;
    private Guid _createAccountKey;

    private void AddClicked()
    {
        if (!_canCreateAccounts) return;
        _createAccountKey = Guid.NewGuid();
        _createAccountOpen = true;
    }

    private async Task OnAccountCreated()
    {
        await LoadSummary();
        await GetAccounts();
        // A new account may be in a currency with no rate to the main one → refresh signals.
        await LoadTotals();
    }

    private ExistingAccount? _txnAccount;
    private Guid _txnKey;
    private bool _txnOpen;

    private void AddTransaction(ExistingAccount account)
    {
        _txnAccount = account;
        _txnKey = Guid.NewGuid();
        _txnOpen = true;
    }

    private ExistingAccount? _termAccount;
    private Guid _termKey;
    private bool _termOpen;
    private Guid _termsRefreshToken = Guid.NewGuid();

    private void AddTerm(ExistingAccount account)
    {
        if (!_canWriteTerms) return;
        _termAccount = account;
        _termKey = Guid.NewGuid();
        _termOpen = true;
    }

    // Recreate any open Rate & fees section so a menu-triggered create shows immediately,
    // and reload the accounts so the header subtitle picks up the new in-force rate.
    private async Task OnTermCreated()
    {
        _termsRefreshToken = Guid.NewGuid();
        await GetAccounts();
    }

    private ExistingAccount? _estimateAccount;
    private Guid _estimateKey;
    private bool _estimateOpen;
    private Guid _estimatesRefreshToken = Guid.NewGuid();

    private void AddEstimate(ExistingAccount account)
    {
        if (!_canWriteEstimates) return;
        _estimateAccount = account;
        _estimateKey = Guid.NewGuid();
        _estimateOpen = true;
    }

    // Recreate any open Estimates section so a menu-triggered create shows immediately, and reload
    // the accounts so the header picks up the new in-force estimate as the headline value.
    private async Task OnEstimateCreated()
    {
        _estimatesRefreshToken = Guid.NewGuid();
        await LoadSummary();
        await GetAccounts();
    }

    private Task CopyAccountId(Guid accountId) =>
        Clipboard.CopyAsync(accountId.ToString(), "Account ID copied.");

    // Builds the API update payload from an account as it currently stands, overriding
    // only the lifecycle fields the row-menu toggles touch (close/reopen, archive).
    private static NewAccount ToNewAccount(ExistingAccount a, DateTime? closed, bool archived) => new()
    {
        Name          = a.Name,
        Description   = a.Description,
        AccountNumber = a.AccountNumber,
        AccountType   = a.AccountType,
        Opened        = a.Opened,
        Closed        = closed,
        CurrencyCode  = a.CurrencyCode,
        Archived      = archived,
        // Preserve the custodian link on a lifecycle-only toggle (omitting it would clear the link).
        CustodianId   = a.CustodianId,
    };

    private async Task ToggleCloseAccount(ExistingAccount account)
    {
        if (!_canUpdateAccounts) return;
        var targetClosed = account.Closed is null ? (DateTime?)DateTime.UtcNow : null;
        var update = ToNewAccount(account, targetClosed, account.Archived is not null);
        if ((await Accounts.UpdateAsync(account.AccountId, update)).Toast(Snackbar, "Update failed",
                targetClosed.HasValue ? "Account closed." : "Account reopened."))
        {
            account.Closed = targetClosed;
            await LoadSummary();
            StateHasChanged();
        }
    }

    private async Task ToggleArchive(ExistingAccount account)
    {
        if (!_canUpdateAccounts) return;
        var targetArchived = account.Archived is null;
        var update = ToNewAccount(account, account.Closed, targetArchived);
        if ((await Accounts.UpdateAsync(account.AccountId, update)).Toast(Snackbar, "Update failed",
                targetArchived ? "Account archived." : "Account unarchived."))
        {
            account.Archived = targetArchived ? DateTime.UtcNow : null;
            await LoadSummary();
            StateHasChanged();
            // Archived accounts drop out of the server's unconverted set → refresh signals.
            await LoadTotals();
        }
    }

    private async Task ConfirmDeleteAccount(ExistingAccount account)
    {
        if (!_canDeleteAccounts) return;
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete account",
            $"Delete '{account.Name}'? This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");
        if (confirmed == true)
            await DeleteAccount(account);
    }

    private async Task DeleteAccount(ExistingAccount account)
    {
        if ((await Accounts.DeleteAsync(account.AccountId)).Toast(Snackbar, "Delete failed", "Account deleted."))
        {
            _accounts.Remove(account);
            await LoadSummary();
            if (_expandedId == account.AccountId)
            {
                _expandedId = null;
            }
            // A deleted account's warning must clear from the rollup → refresh signals.
            await LoadTotals();
        }
    }

    // ── Display helpers ───────────────────────────────────────────────────────
    private static string GetAccountTypeLabel(AccountType type) => AccountTypeVisuals.Label(type);

    // ── Record-card presentation ──────────────────────────────────────────────────

    /// <summary>The headline figure's colour role. The in-force estimate is what counts toward net
    /// worth, so it always reads income; a plain balance follows its own sign.</summary>
    private static OdsRecordFigureTone FigureTone(ExistingAccount a) =>
        a.CurrentEstimatedValue is not null ? OdsRecordFigureTone.Income
        : a.Balance < 0 ? OdsRecordFigureTone.Expense
        : a.Balance > 0 ? OdsRecordFigureTone.Income
        : OdsRecordFigureTone.Neutral;

    private static string StatusIcon(ExistingAccount a) =>
        a.Archived is not null ? "inventory_2" : a.Closed is not null ? "lock" : "lock_open";

    /// <summary>The Status tile's value tint, from the same tone the status chip uses, so the chip in
    /// the header and the tile in the body can never disagree.</summary>
    private static OdsInfoTileTone StatusTone(ExistingAccount a) => GetStatusChipTone(a) switch
    {
        OdsChipTone.Income => OdsInfoTileTone.Income,
        OdsChipTone.Pending => OdsInfoTileTone.Pending,
        OdsChipTone.Expense => OdsInfoTileTone.Expense,
        _ => OdsInfoTileTone.Muted,
    };

    /// <summary>The single in-force rate the collapsed row headlines on, rebuilt as a term so the
    /// shared TermKindVisuals formatting (sign, cost colour, unit) applies unchanged.</summary>
    private static ExistingAccountTerm? RateTermOf(ExistingAccount a) =>
        a.CurrentInterestRateKind is { } kind
            ? new ExistingAccountTerm
            {
                AccountTermId = Guid.Empty,
                AccountId = a.AccountId,
                TermKind = kind,
                ValueUnit = TermValueUnit.Percentage,
                Value = a.CurrentInterestRate ?? 0m,
            }
            : null;

    /// <summary>Adapts a current-term projection to the shape TermKindVisuals formats, so the Current
    /// band's tiles read exactly like the same term does in the Terms section.</summary>
    private static ExistingAccountTerm ToTerm(ExistingAccount a, AccountCurrentTerm term) => new()
    {
        AccountTermId = Guid.Empty,
        AccountId = a.AccountId,
        TermKind = term.TermKind,
        ValueUnit = term.ValueUnit,
        Value = term.Value,
        CurrencyCode = term.CurrencyCode,
        BillingPeriod = term.BillingPeriod,
        EffectiveFrom = term.EffectiveFrom,
    };

    /// <summary>How many values are in force — the estimate and its balance count as a pair, since the
    /// balance is only shown as the estimate's secondary reading.</summary>
    private static int InForceCount(ExistingAccount a) =>
        a.CurrentTerms.Count + (a.CurrentEstimatedValue is not null ? 2 : 0);

    private static string? EstimateFoot(ExistingAccount a) =>
        a.CurrentEstimatedValueEffectiveFrom is { } from
            ? $"in force since {from.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture)} · in net worth"
            : "in net worth";

    private static string BalanceFoot(ExistingAccount a) =>
        a.TransactionCount == 0
            ? "No transactions"
            : $"{a.TransactionCount} transaction{(a.TransactionCount == 1 ? "" : "s")} · secondary";

    /// <summary>A term's tile foot: when it took effect, plus its billing period where it has one.
    /// The period is what separates a 695 annual fee from a 695 monthly one, so it rides along.</summary>
    private static string TermFoot(AccountCurrentTerm term)
    {
        var since = $"since {term.EffectiveFrom.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture)}";
        var period = TermKindVisuals.BillingInfo(term.BillingPeriod);
        return period is null || term.BillingPeriod == Odyssey.Dtos.Finance.BillingPeriod.OneTime
            ? since
            : $"{since} · {period.Label}";
    }

    /// <summary>
    /// The Currency tile's foot, which only has something to say when the account is NOT already in
    /// the reporting currency. The comparison is against the user's actual main currency (from the
    /// totals the page already loads), not a hardcoded one — an account in the reporting currency is
    /// not "converted", and saying so would be wrong for anyone whose main currency is not the dollar.
    /// Silent while totals are unavailable, rather than guessing.
    /// </summary>
    private string? CurrencyFoot(ExistingAccount a) =>
        _totals?.MainCurrencyCode is not { } main
            || string.Equals(a.CurrencyCode, main, StringComparison.OrdinalIgnoreCase)
                ? null
                : "converted for reporting";

    /// <summary>The row action menu, permission-gated. "New term" leads with the § glyph, which
    /// OdsMenu renders as a literal rather than a Material ligature.</summary>
    private IReadOnlyList<OdsMenuItem> RowActions(ExistingAccount account)
    {
        var items = new List<OdsMenuItem>();

        if (_canUpdateAccounts)
        {
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit account", OnClick = EventCallback.Factory.Create(this, () => EditClicked(account)) });
        }

        if (_canUploadFiles)
        {
            items.Add(new OdsMenuItem { Icon = "upload_file", Label = "Upload file", OnClick = EventCallback.Factory.Create(this, () => AddFile(account)) });
        }

        if (_canCreateTransactions && account.Closed is null && account.Archived is null)
        {
            items.Add(new OdsMenuItem { Icon = "receipt_long", Label = "New transaction", OnClick = EventCallback.Factory.Create(this, () => AddTransaction(account)) });
        }

        if (_canWriteEstimates)
        {
            items.Add(new OdsMenuItem { Icon = "monitor", Label = "New estimate", OnClick = EventCallback.Factory.Create(this, () => AddEstimate(account)) });
        }

        if (_canWriteTerms)
        {
            items.Add(new OdsMenuItem { Icon = "§", Label = "New term", OnClick = EventCallback.Factory.Create(this, () => AddTerm(account)) });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            Label = "Copy ID",
            TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => CopyAccountId(account.AccountId)),
        });

        if (_canUpdateAccounts)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = account.Closed != null ? "lock_open" : "lock",
                Label = account.Closed != null ? "Reopen" : "Close",
                OnClick = EventCallback.Factory.Create(this, () => ToggleCloseAccount(account)),
            });
            items.Add(new OdsMenuItem
            {
                Icon = account.Archived != null ? "unarchive" : "archive",
                Label = account.Archived != null ? "Unarchive" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(account)),
            });
        }

        if (_canDeleteAccounts)
        {
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, () => ConfirmDeleteAccount(account)) });
        }

        return items;
    }

    /// <summary>An optional tile foot. Returns null for an absent caption so the tile renders no foot
    /// element at all — a foot has to earn its place, and an empty one is not the same as none.</summary>
    private static RenderFragment? Caption(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : builder => builder.AddContent(0, text);

    private static string GetStatusLabel(ExistingAccount a)
    {
        if (a.Archived != null) return "Archived";
        if (a.Closed   != null) return "Closed";
        return "Open";
    }

    // Status → dot tone for OdsAccountStatusChip, mirroring the design system's
    // AccountStatusChip: Open (income) · Closed (pending) · Archived (outline).
    private static OdsChipTone GetStatusChipTone(ExistingAccount a)
    {
        if (a.Archived != null) return OdsChipTone.Outline;
        if (a.Closed   != null) return OdsChipTone.Pending;
        return OdsChipTone.Income;
    }

    // The full account lifecycle as one line — opened, then closed and/or archived when
    // present. Consolidates the formerly-separate Opened / Closed tiles under the Status tile.
    private static string GetLifecycle(ExistingAccount a)
    {
        var parts = new List<string> { $"Opened {a.Opened.ToString("MMM dd, yyyy")}" };
        if (a.Closed   is { } closed)   parts.Add($"Closed {closed.ToString("MMM dd, yyyy")}");
        if (a.Archived is { } archived) parts.Add($"Archived {archived.ToString("MMM dd, yyyy")}");
        return string.Join(" · ", parts);
    }

    private static string GetTypeMaterialIcon(AccountType type) => AccountTypeVisuals.MaterialIcon(type);
    private static string GetTypeFgColor(AccountType type)      => AccountTypeVisuals.FgColor(type);
    private static string GetTypeBgColor(AccountType type)      => AccountTypeVisuals.BgColor(type);

    // ── Balance display ───────────────────────────────────────────────────────
    private static string BalanceColor(decimal balance) => balance switch
    {
        > 0 => "var(--finance-income)",
        < 0 => "var(--finance-expense)",
        _   => "var(--mud-palette-text-secondary)",
    };

    // Formats an amount in the given currency's symbol/minor-units when known, else a
    // generic "$" with two decimals. A null currencyCode means a naive cross-currency
    // aggregate (no FX in the app), so the generic symbol is intentional.
    private string FormatMoney(decimal value, string? currencyCode) =>
        value.ToString("C", MoneyFormat(currencyCode));

    // The currency's display symbol when known, else the code itself — for the estimate value
    // chart's compact y-axis (the section formats full amounts via FormatMoney).
    private string CurrencySymbol(string? currencyCode)
    {
        if (!string.IsNullOrWhiteSpace(currencyCode)
            && _currenciesByCode.TryGetValue(currencyCode, out var currency)
            && !string.IsNullOrWhiteSpace(currency.Symbol))
            return currency.Symbol;
        return string.IsNullOrWhiteSpace(currencyCode) ? "$" : currencyCode;
    }

    // One configured NumberFormatInfo per currency code (and one for the generic
    // fallback), cached so list re-renders don't clone+configure a fresh one per row.
    private NumberFormatInfo MoneyFormat(string? currencyCode)
    {
        var key = string.IsNullOrWhiteSpace(currencyCode) ? string.Empty : currencyCode;
        if (_moneyFormatCache.TryGetValue(key, out var cached))
            return cached;

        var nf = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        if (key.Length > 0 && _currenciesByCode.TryGetValue(key, out var currency))
        {
            nf.CurrencySymbol = currency.Symbol;
            nf.CurrencyDecimalDigits = currency.MinorUnits;
        }
        else
        {
            nf.CurrencySymbol = "$";
            nf.CurrencyDecimalDigits = 2;
        }
        nf.CurrencyNegativePattern = 1; // "-$n" — leading minus, no parentheses
        _moneyFormatCache[key] = nf;
        return nf;
    }

    // ── Balance aggregates (sub-line) ───────────────────────────────────────────
    // Server-computed (issue #372): excludes archived accounts, counts closed ones, and applies the
    // net-worth replace policy. The asset/liability donut split lives in <AccountsOverview>.
    private decimal CombinedBalance => _summary?.CombinedValue ?? 0m;

    // ── Account problem signal (exchange-rate warning) ───────────────────────
    // Severity is "warning" | "error" so markup reads by meaning; only "warning"
    // is produced today (missing rate). Mirrors the design-system problem shape:
    // a short chip, a title + detail for the record, and a triage summary.
    private sealed record AccountProblem(
        Guid AccountId,
        string Name,
        string Severity,
        string Chip,
        string Title,
        string Summary,
        string Detail);
}
