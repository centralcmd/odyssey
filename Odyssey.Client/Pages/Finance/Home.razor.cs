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
    private int? _chartStartYear;

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

    // The design's eased growth curve (2016 → 2026 in the specimen). Resampled
    // across the user's real account span so the last point always lands on the
    // current net worth regardless of how many years it spans.
    private static readonly double[] GrowthCurve =
        [0.017, 0.069, 0.137, 0.230, 0.338, 0.446, 0.546, 0.589, 0.748, 0.884, 1.0];

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
        // Failures degrade silently — the dashboard just shows no data, no toast.
        var result = await Accounts.ListAllAsync();
        _accounts = result.ValueOr([]);

        // The accounts list supplies the count and the chart's start year; the totals
        // endpoint supplies the money. Both are needed before the chart can be built.
        await LoadTotalsAsync();

        if (result.IsSuccess)
            BuildChart();
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
    // specimen used for its single-currency mock data.
    private async Task<NumberFormatInfo> BuildMainCurrencyFormatAsync(string? currencyCode)
    {
        var nf = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        nf.CurrencyNegativePattern = 1; // "-$n" — leading minus, no parentheses
        nf.CurrencySymbol = "$";
        nf.CurrencyDecimalDigits = 2;

        if (string.IsNullOrWhiteSpace(currencyCode))
            return nf;

        var currencies = await ReferenceData.CurrenciesAsync();
        var currency = currencies.FirstOrDefault(c =>
            string.Equals(c.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase));

        if (currency is not null && !string.IsNullOrWhiteSpace(currency.Symbol))
        {
            nf.CurrencySymbol = currency.Symbol;
            nf.CurrencyDecimalDigits = currency.MinorUnits;
        }
        else
        {
            // Known code, unknown symbol — the code itself beats a misleading "$".
            nf.CurrencySymbol = currencyCode;
        }

        return nf;
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

    // The chart's empty state has two causes and they are not interchangeable: nothing to
    // chart, or a figure the server could not give us.
    private string ChartEmptyLabel => _totals is null
        ? "Net worth is unavailable right now."
        : "No account balances to chart yet.";

    private string ChartSubLine
    {
        get
        {
            var currency = _totals?.MainCurrencyCode;
            if (string.IsNullOrWhiteSpace(currency))
                return _chartStartYear is int only ? $"Since {only}" : string.Empty;
            return _chartStartYear is int year ? $"Since {year} · {currency}" : currency;
        }
    }

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
                    Message = $"No exchange rate from {account.CurrencyCode} to {main}, "
                              + "so this account counts as 0 towards net worth.",
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

    // ── Net worth chart series ──
    // The synthetic history: there is no stored net-worth-over-time series, so the
    // design's eased growth curve is resampled across the real span (earliest
    // account year → this year) and anchored to land exactly on today's figure.
    // OdsLineChart owns the geometry, axis labels, figure and delta.
    private void BuildChart()
    {
        var accounts = ActiveAccounts;
        if (accounts.Count == 0 || NetWorth is not { } netWorth)
        {
            // No accounts, or no server total to anchor the curve to. A curve anchored on
            // a client-side guess would be doubly invented, so the chart shows its empty state.
            _chartSeries = [];
            _chartStartYear = null;
            return;
        }

        var current = (double)netWorth;
        var startYear = accounts.Min(a => a.Opened.Year);
        var endYear = Math.Max(startYear, DateTime.Now.Year);
        if (startYear >= endYear)
            startYear = endYear - 1; // guarantee at least two points

        var n = endYear - startYear + 1;
        var series = new List<OdsLinePoint>(n);
        for (var i = 0; i < n; i++)
        {
            var value = (decimal)(current * SampleCurve(i, n));
            series.Add(new OdsLinePoint($"'{(startYear + i) % 100:00}", value));
        }

        _chartSeries = series;
        _chartStartYear = startYear;
    }

    // Resample the canonical curve to n points; i=n-1 always maps to 1.0 so the
    // line lands exactly on the current net worth.
    private static double SampleCurve(int i, int n)
    {
        if (n <= 1)
            return 1.0;
        var pos = (double)i / (n - 1) * (GrowthCurve.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, GrowthCurve.Length - 1);
        var frac = pos - lo;
        return GrowthCurve[lo] * (1 - frac) + GrowthCurve[hi] * frac;
    }

    // Compact y-axis label, e.g. "kr 52k" / "kr 640", in the main currency.
    private string KLabel(decimal v)
    {
        var symbol = MainCurrencyFormat.CurrencySymbol;
        return v >= 1000 ? $"{symbol}{v / 1000:0}k" : $"{symbol}{v:0}";
    }

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

    private static readonly NumberFormatInfo GenericMoneyFormat = BuildGenericMoneyFormat();

    private static NumberFormatInfo BuildGenericMoneyFormat()
    {
        var nf = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        nf.CurrencySymbol = "$";
        nf.CurrencyDecimalDigits = 2;
        nf.CurrencyNegativePattern = 1; // "-$n"
        return nf;
    }

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
