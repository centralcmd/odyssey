using System.Globalization;
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

    // Server-computed totals (GET /api/accounts/totals). Null means the call did not
    // succeed — the header and chart then withhold the figure rather than substituting
    // a naive sum, which is the defect this replaced.
    private AccountTotals? _totals;
    private NumberFormatInfo? _mainCurrencyFormat;

    // ── State ──
    private bool _isLoadingAccounts = true;
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
        // The accounts list supplies the count and the chart's start year; the totals endpoint
        // supplies the money. Neither depends on the other, so they go out together — serialising
        // them would add a round trip to the critical load path for nothing.
        // Failures degrade silently — the dashboard just shows no data, no toast.
        var listTask = Accounts.ListAllAsync();
        var totalsTask = LoadTotalsAsync();
        await Task.WhenAll(listTask, totalsTask);

        var result = await listTask;
        _accounts = result.ValueOr([]);

        _isLoadingAccounts = false;
    }

    // Net worth is server-computed (issue #372's AccountTotalsService): it converts every
    // non-archived account into the user's main currency at the latest rate, applies the
    // in-force estimate replace policy (issue #182 §9), and splits assets from liabilities.
    // The dashboard used to sum ExistingAccount.Balance client-side instead, which added
    // unlike currencies as bare numbers and ignored estimates entirely.
    private async Task LoadTotalsAsync()
    {
        await UserPreferences.LoadUserPreferencesAsync();
        var mainCurrency = UserPreferences.MainCurrency ?? DefaultMainCurrency;

        var result = await Accounts.GetTotalsAsync(mainCurrency);
        if (!result.IsSuccess)
        {
            // Degrade silently, like the accounts load above: no toast, no figure.
            _totals = null;
            _mainCurrencyFormat = null;
            return;
        }

        _totals = result.Value;
        _mainCurrencyFormat = await BuildMainCurrencyFormatAsync(_totals?.MainCurrencyCode);
    }

    // The main currency's symbol and minor units, resolved through the shared reference-data
    // cache so the dashboard shows "kr 48 260,00" rather than the generic "$" the design
    // specimen used for its single-currency mock data. The mapping itself is in DashboardFigures,
    // where it is testable without a renderer.
    private async Task<NumberFormatInfo> BuildMainCurrencyFormatAsync(string? currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
            return DashboardFigures.MoneyFormat(currencyCode, null);

        var currencies = await ReferenceData.CurrenciesAsync();
        var currency = currencies.FirstOrDefault(c =>
            string.Equals(c.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase));

        return DashboardFigures.MoneyFormat(currencyCode, currency);
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

    // The empty state names its cause. There is no fallback series: when the data is not there,
    // the chart is not there (issue #90 §11).
    private string ChartEmptyLabel => DashboardFigures.ChartEmptyLabel(_totals is not null);

    // The caption is what the chart IS, not when it started. The year-prefixed branch it replaces
    // described the fabricated curve's span (earliest account year → this year), which was never a
    // property of any stored series.
    private string ChartSubLine => _totals?.MainCurrencyCode ?? string.Empty;

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

            var unconverted = _totals.UnconvertedAccounts;
            if (unconverted.Count == 0)
                return [];

            var main = _totals.MainCurrencyCode;
            return
            [
                .. unconverted.Select(account => new PageHeaderProblem
                {
                    Severity = PageHeaderSeverity.Warning,
                    Lead = account.Name,
                    Message = DashboardFigures.UnconvertedMessage(account.CurrencyCode, main),
                    Where = "Accounts",
                    ViewLabel = "Accounts",
                    OnView = EventCallback.Factory.Create(this, () => NavigationManager.NavigateTo("accounts")),
                }),
            ];
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

    // Compact y-axis label, e.g. "$52k" / "kr 52k", in the main currency.
    private string KLabel(decimal v) =>
        DashboardFigures.AxisLabel(v, MainCurrencyFormat.CurrencySymbol);

    // Net worth is converted server-side into the user's main currency, so it is formatted
    // in that currency's symbol and minor units rather than the specimen's generic "$".
    private string FormatMoney(decimal value) => value.ToString("C", MainCurrencyFormat);

    private NumberFormatInfo MainCurrencyFormat => _mainCurrencyFormat ??= GenericMoneyFormat;

    // Per-transaction amounts stay on the generic symbol: a transaction is in its account's
    // own currency, which is not necessarily the main one, and nothing converts it here.
    private static string FormatSignedMoney(decimal value)
    {
        var sign = value < 0 ? "−" : "+";
        return $"{sign}{Math.Abs(value).ToString("C", GenericMoneyFormat)}";
    }

    private static readonly NumberFormatInfo GenericMoneyFormat = DashboardFigures.GenericMoneyFormat();

    // Matches the API's own fallback when no main-currency preference is set.
    private const string DefaultMainCurrency = "NOK";

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
