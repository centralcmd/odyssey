using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Finance;

public partial class Home
{
    // ── Data ──
    private List<ExistingAccount> _accounts = [];
    private List<ExistingTransaction> _transactions = [];
    private List<OdsLinePoint> _chartSeries = [];

    // The reconstructed series (GET /api/accounts/net-worth-history). Null means the call did not
    // land, which is a different state from a 200 carrying an EmptyReason — one says the request
    // failed, the other says the data is known and known to be empty.
    private NetWorthHistory? _history;

    // Server-computed totals (GET /api/accounts/totals). Null means the call did not
    // succeed — the header and chart then withhold the figure rather than substituting
    // a naive sum, which is the defect this replaced.
    private AccountTotals? _totals;
    // The main currency's own decimals. Null means the reference-data lookup failed, which is what
    // _currencyFormatIsDegraded reports; the figures still render, in the default two decimals.
    private int? _mainCurrencyMinorUnits;

    // Every currency's decimals, for the recent-transaction rows: each is in its OWN account's
    // currency, not the main one, so one lookup is not enough. Empty on a failed load, which costs
    // the precision of a zero-decimal currency and nothing about the denomination.
    private Dictionary<string, int> _minorUnitsByCode = new(StringComparer.OrdinalIgnoreCase);

    // ── State ──
    private bool _isLoadingAccounts = true;
    private bool _isLoadingHistory = true;
    private bool _isLoadingTransactions = true;

    // ── Permissions ──
    private bool _canReadAccounts;
    private bool _canReadTransactions;

    private string _firstName = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await LoadPermissionsAsync();

        var work = new List<Task>();
        if (_canReadAccounts)
            work.Add(LoadAccountsAsync());
        else
            _isLoadingAccounts = false;
        if (_canReadTransactions)
            work.Add(LoadTransactionsAsync());
        else
            _isLoadingTransactions = false;

        await Task.WhenAll(work);
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
            StateHasChanged();
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();

        _canReadAccounts = user.HasPermission(PermissionClaims.AccountsRead);
        _canReadTransactions = user.HasPermission(PermissionClaims.TransactionsRead);

        var name = user.FindFirst(ClaimTypes.Name)?.Value ?? user.Identity?.Name ?? string.Empty;
        _firstName = FirstNameFrom(name);
    }

    private async Task LoadAccountsAsync()
    {
        // The currency is resolved before the fan-out. It used to be resolved inside LoadTotalsAsync,
        // so a totals failure left the dashboard with no denomination at all while the history call
        // succeeded. The preference and the reference data are both client-side caches, so paying for
        // them first costs a round trip only on the very first load.
        _mainCurrencyMinorUnits = await ResolveMainCurrencyMinorUnitsAsync();

        // The accounts list supplies the count, the totals endpoint the headline figure, and the
        // history endpoint the series. None depends on the others, so they go out together —
        // serialising them would add round trips to the critical load path for nothing.
        // Failures degrade silently into the header's problem rollup: no toast.
        var listTask = Accounts.ListAllAsync();
        var totalsTask = LoadTotalsAsync();
        var historyTask = LoadHistoryAsync();
        await Task.WhenAll(listTask, totalsTask, historyTask);

        var result = await listTask;
        _accounts = result.ValueOr([]);

        _isLoadingAccounts = false;
    }

    // The reconstructed series. The client sends NO from/to: the server's defaults already are the
    // v1 window, and a `to` built from DateTime.Now (local) is tomorrow-in-UTC anywhere east of UTC,
    // which the server rejects outright with a 400.
    private async Task LoadHistoryAsync()
    {
        var result = await Accounts.GetNetWorthHistoryAsync(_mainCurrencyCode);

        // A failed call leaves _history null. There is no fallback series: when the data is not
        // there, the chart is not there.
        _history = result.IsSuccess ? result.Value : null;
        _chartSeries = DashboardFigures.BuildSeries(_history);
        _isLoadingHistory = false;
    }

    // Net worth is server-computed (issue #372's AccountTotalsService): it converts every
    // non-archived account into the user's main currency at the latest rate, applies the
    // in-force estimate replace policy (issue #182 §9), and splits assets from liabilities.
    // The dashboard used to sum ExistingAccount.Balance client-side instead, which added
    // unlike currencies as bare numbers and ignored estimates entirely.
    private async Task LoadTotalsAsync()
    {
        var result = await Accounts.GetTotalsAsync(_mainCurrencyCode);

        // Degrade silently, like the accounts load above: no toast, no figure. The FORMAT is not
        // touched here — it belongs to the user's preference, not to this response, and a totals
        // failure must not change how the chart's own figures are denominated.
        _totals = result.IsSuccess ? result.Value : null;
    }

    // The main currency's own decimals, resolved through the shared reference-data cache (JPY renders
    // none). The CODE is never at risk: _mainCurrencyCode is a real code either way, and money is
    // written with its ISO code trailing, so a failed lookup costs the minor-unit count and nothing
    // about the denomination. The header rollup says what was lost.
    private async Task<int?> ResolveMainCurrencyMinorUnitsAsync()
    {
        try
        {
            await UserPreferences.LoadUserPreferencesAsync();
            _mainCurrencyCode = UserPreferences.MainCurrency ?? DefaultMainCurrency;

            var currencies = await ReferenceData.CurrenciesAsync();
            _minorUnitsByCode = currencies
                .GroupBy(c => c.CurrencyCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => DashboardFigures.MinorUnits(g.First()), StringComparer.OrdinalIgnoreCase);

            var currency = currencies.FirstOrDefault(c =>
                string.Equals(c.CurrencyCode, _mainCurrencyCode, StringComparison.OrdinalIgnoreCase));

            return currency is null ? null : DashboardFigures.MinorUnits(currency);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task LoadTransactionsAsync()
    {
        _transactions = (await Transactions.ListAllAsync(sortBy: "date", sortDir: "desc"))
            .ItemsOrToast(Snackbar, "transactions");
        _isLoadingTransactions = false;
    }

    // ── Header ──
    private string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            var partOfDay = hour < 12 ? "morning" : hour < 18 ? "afternoon" : "evening";
            return string.IsNullOrWhiteSpace(_firstName)
                ? $"Good {partOfDay}"
                : $"Good {partOfDay}, {_firstName}";
        }
    }

    private string HeaderSubLine
    {
        get
        {
            if (!_canReadAccounts)
                return "Welcome back to Odyssey.";

            var count = ActiveAccounts.Count;
            var accounts = $"{count} account{(count == 1 ? "" : "s")}";

            // No totals → no figure. A silently-wrong number is worse than an absent one,
            // and the problem rollup below says why it is missing.
            return NetWorth is { } netWorth
                ? $"Net worth {FormatMoney(netWorth)} across {accounts}"
                : $"{accounts} · net worth unavailable";
        }
    }

    // ── Net worth ──
    // The account list still drives the count and the chart's span; the FIGURE comes from
    // the server, which is the only place the exchange rates and the estimate replace
    // policy live.
    private List<ExistingAccount> ActiveAccounts => _accounts.Where(a => a.Archived is null).ToList();
    private decimal? NetWorth => _totals?.NetWorth;

    // ── The net-worth chart ──
    // Everything below reads the response and nothing else. There is no fallback series: when the
    // data is not there, the chart is not there (issue #90 §11).

    private bool _chartIsLoading => _isLoadingAccounts || _isLoadingHistory;

    // There is no "withhold the chart" state any more. It existed because an unresolved format meant a
    // wrong sigil, and the region then rendered NOTHING — no skeleton, no chart, no explanation — which
    // is its own defect. With the code-based fallback above, a failed reference-data lookup costs the
    // symbol and the minor-unit count, and the degradation is disclosed in the header rollup instead.
    private bool _currencyFormatIsDegraded => _mainCurrencyMinorUnits is null;

    private IReadOnlyList<NetWorthHistoryPoint> HistoryPoints => _history?.Points ?? [];

    private NetWorthInterval ChartInterval => _history?.Interval ?? NetWorthHistoryQuery.DefaultInterval;

    private string? FirstPointLabel => _chartSeries.Count > 0 ? _chartSeries[0].Label : null;

    private string? LastPointLabel => _chartSeries.Count > 0 ? _chartSeries[^1].Label : null;

    // The cause is carried on the response, so the copy names it. Inferring it from the payload is
    // impossible for several of the causes, which is why the field exists at all.
    private string ChartEmptyLabel =>
        DashboardFigures.ChartEmptyLabel(_history?.EmptyReason, _mainCurrencyCode);

    private string ChartCaption => DashboardFigures.ChartCaption(
        _chartSeries.Count, FirstPointLabel, LastPointLabel, ChartInterval, _mainCurrencyCode);

    private string ChartAriaLabel =>
        DashboardFigures.ChartAriaLabel(_chartSeries.Count, FirstPointLabel, LastPointLabel, ChartInterval);

    // V15: the delta is an absolute money difference, and it renders only when both endpoints are
    // fully measured AND actually contributed. A revalued endpoint is a real movement and does not
    // withhold it — the component applies the partial half itself, so this is the contributing half.
    private bool ChartShowsDelta =>
        _chartSeries.Count > 1
        && HistoryPoints[0].ContributingAccountCount > 0
        && HistoryPoints[^1].ContributingAccountCount > 0;

    private string? ChartDeltaSuffix => FirstPointLabel is { } first ? $"since {first}" : null;

    private bool DeltaWithheldByAnUnderstatedEndpoint =>
        _chartSeries.Count > 1
        && (_chartSeries[0].Kind == OdsLinePointKind.Partial
            || _chartSeries[^1].Kind == OdsLinePointKind.Partial);

    private IReadOnlyList<string> LabelsWhere(Func<NetWorthHistoryPoint, bool> predicate) =>
        [.. _chartSeries
            .Zip(HistoryPoints)
            .Where(pair => predicate(pair.Second))
            .Select(pair => pair.First.Label)];

    // Every condition the markers show is also stated in text: the markers rest on shape and stroke,
    // and a reader who cannot see the plot gets neither.
    private string? UnderstatedNote => DashboardFigures.UnderstatedNote(
        LabelsWhere(point => point.UnconvertedAccountCount > 0),
        _history?.UnconvertedAccounts ?? [],
        DeltaWithheldByAnUnderstatedEndpoint);

    private string? RevaluedNote =>
        DashboardFigures.RevaluedNote(LabelsWhere(point => point.RevaluedAccountCount > 0));

    // ── Problem rollup ──
    // The server reports accounts it could not convert into the main currency; each one
    // contributed 0, so the headline figure is understated and the reader has to be told.
    // Surfaced through the canonical PageHeader rollup rather than a bespoke banner.
    private IReadOnlyCollection<PageHeaderProblem> HeaderProblems
    {
        get
        {
            if (!_canReadAccounts || _isLoadingAccounts)
                return [];

            if (_totals is null)
            {
                return
                [
                    new PageHeaderProblem
                    {
                        Severity = PageHeaderSeverity.Warning,
                        Message = "Net worth could not be calculated. Totals are unavailable right now.",
                        Where = "Dashboard header and chart",
                    },
                ];
            }

            var problems = new List<PageHeaderProblem>();

            if (_currencyFormatIsDegraded)
            {
                problems.Add(new PageHeaderProblem
                {
                    Severity = PageHeaderSeverity.Warning,
                    Message = $"Currency details for {_mainCurrencyCode} could not be loaded, so amounts "
                        + "are shown with the currency code instead of its symbol.",
                    Where = "Dashboard header and chart",
                });
            }

            var unconverted = _totals.UnconvertedAccounts;
            if (unconverted.Count == 0)
                return problems;

            var main = _totals.MainCurrencyCode;
            problems.AddRange(
                unconverted.Select(account => new PageHeaderProblem
                {
                    Severity = PageHeaderSeverity.Warning,
                    Lead = account.Name,
                    Message = DashboardFigures.UnconvertedMessage(account.CurrencyCode, main),
                    Where = "Accounts",
                    ViewLabel = "Accounts",
                    OnView = EventCallback.Factory.Create(this, () => NavigationManager.NavigateTo("accounts")),
                }));

            return problems;
        }
    }

    // ── Recent transactions (eight newest) ──
    private IReadOnlyList<ExistingTransaction> RecentTransactions =>
        _transactions.OrderByDescending(t => t.TimeStamp).Take(8).ToList();

    private IReadOnlyList<OdsTableColumn<ExistingTransaction>> Columns =>
    [
        new() { Key = "icon", HeaderText = "", Width = "56px", Cell = IconCell },
        new() { Key = "description", HeaderText = "Description", Cell = DescriptionCell },
        new() { Key = "tag", HeaderText = "Tag", Cell = TagCell },
        new() { Key = "status", HeaderText = "Status", Cell = StatusCell },
        new() { Key = "amount", HeaderText = "Amount", Align = OdsAlign.End, Cell = AmountCell },
        new() { Key = "date", HeaderText = "Date", Align = OdsAlign.End, Cell = DateCell },
    ];

    private static bool IsIncome(ExistingTransaction txn) => txn.Amount >= 0;

    private static OdsChipTone StatusTone(TransactionStatus status) => status switch
    {
        TransactionStatus.Approved => OdsChipTone.Income,
        TransactionStatus.Flagged => OdsChipTone.Expense,
        _ => OdsChipTone.Info,
    };

    // Compact y-axis label, e.g. "52k NOK", in the main currency. The code trails here exactly as it
    // does on the headline figure, so the axis and the headline read as one denomination.
    private string KLabel(decimal v) => DashboardFigures.AxisLabel(v, _mainCurrencyCode);

    // Net worth is converted server-side into the user's main currency, so it is written in that
    // currency's code and decimals.
    private string FormatMoney(decimal value) =>
        OdsMoney.Format(value, _mainCurrencyCode, _mainCurrencyMinorUnits ?? OdsMoney.DefaultMinorUnits);

    // A per-transaction amount is in its ACCOUNT's own currency, which is not necessarily the main
    // one, and nothing converts it here — so the row names that currency rather than the main one.
    // It is presented as signed: the direction is the point of the row, and a bare positive would
    // read as an ordinary total.
    private string FormatSignedMoney(decimal value, string? currencyCode) =>
        OdsMoney.Signed(value, currencyCode,
            currencyCode is not null && _minorUnitsByCode.TryGetValue(currencyCode, out var units)
                ? units
                : OdsMoney.DefaultMinorUnits);

    // Matches the API's own fallback when no main-currency preference is set.
    private const string DefaultMainCurrency = "NOK";

    // The currency the whole page is denominated in, resolved from the user's preference before any
    // call goes out — so the totals request, the history request and the formatting all name the same
    // one, and a failure in any of them cannot change the others' denomination.
    private string _mainCurrencyCode = DefaultMainCurrency;

    private static string FirstNameFrom(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var local = raw.Split('@')[0];
        var first = local.Split('.', '_', '-', ' ').FirstOrDefault(p => p.Length > 0);
        if (string.IsNullOrEmpty(first))
            return string.Empty;
        return char.ToUpperInvariant(first[0]) + first[1..];
    }
}
