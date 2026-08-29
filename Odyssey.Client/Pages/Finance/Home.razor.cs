using System.Globalization;
using System.Security.Claims;
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
        // Failures degrade silently here, so this deliberately does not toast.
        var result = await Accounts.ListAllAsync();
        _accounts = result.ValueOr([]);
        if (result.IsSuccess)
            BuildChart();
        _isLoadingAccounts = false;
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
            var count = BalanceAccounts.Count;
            return $"Net worth {FormatMoney(NetWorth)} across {count} account{(count == 1 ? "" : "s")}";
        }
    }

    // ── Net worth (naive cross-currency sum of non-archived balances) ──
    private List<ExistingAccount> BalanceAccounts => _accounts.Where(a => a.Archived is null).ToList();
    private decimal NetWorth => BalanceAccounts.Sum(a => a.Balance);

    private string ChartSubLine => _chartStartYear is int year ? $"Since {year} · USD" : "USD";

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
        var accounts = BalanceAccounts;
        if (accounts.Count == 0)
        {
            _chartSeries = [];
            _chartStartYear = null;
            return;
        }

        var current = (double)NetWorth;
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

    // Compact y-axis label, e.g. "$52k" / "$640".
    private static string KLabel(decimal v) =>
        v >= 1000 ? $"${v / 1000:0}k" : $"${v:0}";

    // Generic "$" money — the net worth is a naive cross-currency sum (no FX in
    // the app), mirroring the Accounts page's combined figure.
    private static string FormatMoney(decimal value)
    {
        var nf = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        nf.CurrencySymbol = "$";
        nf.CurrencyDecimalDigits = 2;
        nf.CurrencyNegativePattern = 1; // "-$n"
        return value.ToString("C", nf);
    }

    private static string FormatSignedMoney(decimal value)
    {
        var sign = value < 0 ? "−" : "+";
        return $"{sign}{FormatMoney(Math.Abs(value))}";
    }

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
