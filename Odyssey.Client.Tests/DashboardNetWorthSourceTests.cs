using System.Globalization;
using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The dashboard's net worth must come from <c>GET /api/accounts/totals</c>, never from a
/// client-side aggregate over the accounts list.
///
/// <para>
/// The page was transcribed from the design specimen (<c>Odyssey Design System ·
/// Dashboard.jsx</c>), whose mock data was single-currency and had no value estimates, so its
/// <c>accounts.reduce((s, a) =&gt; s + a.balance, 0)</c> was correct there. Carried into the real
/// contracts it was not: it added unlike currencies as bare numbers, ignored the in-force estimate
/// replace policy (issue #182 §9), counted <c>AccountType.Unknown</c>, and could not know which
/// accounts had no rate to the main currency.
/// </para>
///
/// <para>
/// This is the same class of defect CLAUDE.md already forbids for admin-editable caps — a
/// client-side reimplementation of a server calculation, which is wrong the moment the two
/// disagree and silent about it. These lints are cheap where a behavioural test would need the
/// whole page rendered against a faked API.
/// </para>
/// </summary>
public class DashboardNetWorthSourceTests
{
    private static string CodeBehind() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "Home.razor.cs"));

    private static string Markup() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "Home.razor"));

    [Fact]
    public void NetWorth_ComesFromTheServerTotalsEndpoint()
    {
        Assert.Contains("GetTotalsAsync", CodeBehind(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The specimen's aggregate, in any of the shapes it could come back as. The accounts list is
    /// still loaded — for the account count — so the guard has to name the summation rather than
    /// the list.
    /// </summary>
    [Theory]
    [InlineData(@"Sum\s*\(\s*\w+\s*=>\s*\w+\.Balance\s*\)")]
    [InlineData(@"Sum\s*\(\s*\w+\s*=>\s*\w+\.CurrentEstimatedValue")]
    public void NetWorth_IsNotSummedClientSide(string pattern)
    {
        var match = Regex.Match(CodeBehind(), pattern);
        Assert.False(
            match.Success,
            $"Home.razor.cs:{ClientSource.LineAt(CodeBehind(), match.Index)} sums account balances "
            + "client-side. Net worth is server-computed by AccountTotalsService — a local sum "
            + "skips FX conversion and the estimate replace policy.");
    }

    /// <summary>
    /// A figure the server did not give us is withheld, not replaced. Substituting the old naive sum
    /// on a failed call would reintroduce the defect exactly when it is least visible — the reader
    /// would see a plausible number with nothing marking it as a fallback.
    /// </summary>
    [Fact]
    public void NetWorth_IsNullableSoAFailedCallShowsNoFigure()
    {
        Assert.Contains("private decimal? NetWorth", CodeBehind(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Accounts with no rate to the main currency contribute 0, so the headline is understated.
    /// The server names them; the header has to pass them on rather than swallow them.
    /// </summary>
    [Fact]
    public void UnconvertedAccounts_AreSurfacedInTheProblemRollup()
    {
        Assert.Contains("UnconvertedAccounts", CodeBehind(), StringComparison.Ordinal);
        Assert.Contains("Problems=\"HeaderProblems\"", Markup(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The specimen hardcoded "$" because its mock data was in one currency. The real figure is
    /// converted into the user's main currency, so the symbol has to follow that currency.
    /// </summary>
    [Fact]
    public void Money_IsFormattedInTheMainCurrencyNotAHardcodedDollar()
    {
        var source = CodeBehind();
        Assert.Contains("MainCurrencyCode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static string FormatMoney", source, StringComparison.Ordinal);
    }

    // ── AC1 — the fabricated series is gone, and cannot come back ──────────────────────────────
    //
    // The chart used to resample an 11-element constant across the user's account span and scale it
    // so the last point landed on today's real figure. Every point but the last was invented and the
    // curve was monotonic, so it rose for everyone (issue #88). These name the residue by the exact
    // identifiers it went by, because a half-removal — the constant deleted but the caption kept, or
    // the reverse — reads as intentional.

    [Theory]
    [InlineData("GrowthCurve")]
    [InlineData("SampleCurve")]
    [InlineData("_chartStartYear")]
    public void TheSyntheticGrowthCurve_IsGoneFromTheClient(string identifier)
    {
        var offenders = ClientSource.SourceFiles()
            .Where(file => File.ReadAllText(file).Contains(identifier, StringComparison.Ordinal))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{identifier}' is part of the fabricated net-worth curve issue #88 removed. "
            + "The series comes from GET /api/accounts/net-worth-history now, and there is no "
            + "fallback series: when the data is not there, the chart is not there. Found in: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The caption said "Since {year}", which described the fabricated curve's span (earliest account
    /// year → this year) rather than any stored series. Keeping it over the real endpoint's points
    /// would misdate the window the reader is being shown.
    /// </summary>
    [Fact]
    public void TheSinceYearCaption_IsGone()
    {
        Assert.DoesNotContain("Since {", CodeBehind(), StringComparison.Ordinal);
        Assert.DoesNotContain("$\"Since ", CodeBehind(), StringComparison.Ordinal);
    }

    // ── The series comes from the server, and only from the server ────────────────────────────

    [Fact]
    public void TheChartSeries_ComesFromTheNetWorthHistoryEndpoint()
    {
        Assert.Contains("GetNetWorthHistoryAsync", CodeBehind(), StringComparison.Ordinal);
    }

    /// <summary>
    /// AC19 / AC11. A failed call yields no series at all — there is no fallback, and a
    /// plausible-looking line assembled client-side is exactly the defect issue #88 removed. An empty
    /// RESULT is a different thing: it comes back with a cause, and the copy names it.
    /// </summary>
    [Fact]
    public void AFailedHistoryCall_YieldsNoSeriesAndItsOwnCopy()
    {
        Assert.Empty(DashboardFigures.BuildSeries(null));

        var loadFailed = DashboardFigures.ChartEmptyLabel(null, "NOK");
        var emptyResult = DashboardFigures.ChartEmptyLabel(NetWorthEmptyReason.NoAccounts, "NOK");
        Assert.NotEqual(loadFailed, emptyResult);
    }

    /// <summary>
    /// The points are carried across as they came, in order, with their state — never re-derived,
    /// re-scaled or smoothed.
    /// </summary>
    [Fact]
    public void TheSeries_IsTheResponsesPointsUnaltered()
    {
        var history = new NetWorthHistory
        {
            MainCurrencyCode = "NOK",
            Interval = NetWorthInterval.Monthly,
            From = new DateOnly(2024, 10, 1),
            To = new DateOnly(2025, 1, 15),
            Points =
            [
                Point(new DateOnly(2024, 11, 1), -827_700m),
                Point(new DateOnly(2024, 12, 1), -402_300m, unconverted: 1),
                Point(new DateOnly(2025, 1, 1), 2_801_755.50m, revalued: 1),
            ],
        };

        var series = DashboardFigures.BuildSeries(history);

        Assert.Equal([-827_700m, -402_300m, 2_801_755.50m], series.Select(point => point.Value));
        Assert.Equal(
            [OdsLinePointKind.Normal, OdsLinePointKind.Partial, OdsLinePointKind.Revalued],
            series.Select(point => point.Kind));
    }

    /// <summary>
    /// An understated point is PLOTTED and marked, never dropped. Dropping one would move the first or
    /// last point, so the delta would silently span a shorter window than its own suffix claims.
    /// </summary>
    [Fact]
    public void AnUnderstatedPoint_IsKeptInTheSeries()
    {
        var history = new NetWorthHistory
        {
            MainCurrencyCode = "NOK",
            Interval = NetWorthInterval.Monthly,
            From = new DateOnly(2024, 10, 1),
            To = new DateOnly(2024, 12, 15),
            Points =
            [
                Point(new DateOnly(2024, 11, 1), 100m, unconverted: 1),
                Point(new DateOnly(2024, 12, 1), 200m),
            ],
        };

        var series = DashboardFigures.BuildSeries(history);

        Assert.Equal(2, series.Count);
        Assert.Equal(OdsLinePointKind.Partial, series[0].Kind);
        Assert.All(series, point => Assert.NotNull(point.Value));
    }

    /// <summary>
    /// A point that is somehow both understated and revalued reports the STRONGER caveat. Reporting
    /// the weaker one would let an understated endpoint keep a delta it cannot support.
    /// </summary>
    [Fact]
    public void APointThatIsBoth_ReportsTheStrongerCaveat()
    {
        Assert.Equal(
            OdsLinePointKind.Partial,
            DashboardFigures.KindOf(Point(new DateOnly(2025, 1, 1), 1m, unconverted: 1, revalued: 1)));
    }

    // ── AC30 — a totals failure must not change how the chart's figures are denominated ────────

    /// <summary>
    /// The currency is resolved from the user's PREFERENCE before the fan-out, not from the totals
    /// response. It used to be assigned inside the totals load, so a totals failure left the page with
    /// no denomination at all while the history call succeeded.
    ///
    /// <para>
    /// The old defect this guarded — a NOK series rendered under a generic "$" — is now structurally
    /// impossible: money carries its ISO code, and the code comes from <c>_mainCurrencyCode</c>, which
    /// is always a real code. What the ordering still buys is the currency's DECIMALS, resolved before
    /// anything renders rather than after a call that may fail.
    /// </para>
    /// </summary>
    [Fact]
    public void TheMoneyFormat_IsNotOwnedByTheTotalsResponse()
    {
        var source = CodeBehind();
        var totalsLoad = Between(source, "private async Task LoadTotalsAsync()", "private async Task<int?>");

        Assert.DoesNotContain("_mainCurrencyMinorUnits", totalsLoad, StringComparison.Ordinal);

        // …and it is resolved before the three calls fan out, not after any of them returns.
        var accountsLoad = Between(source, "private async Task LoadAccountsAsync()", "private async Task LoadHistoryAsync()");
        var resolvedAt = accountsLoad.IndexOf("ResolveMainCurrencyMinorUnitsAsync", StringComparison.Ordinal);
        var fannedOutAt = accountsLoad.IndexOf("Task.WhenAll", StringComparison.Ordinal);

        Assert.True(resolvedAt >= 0 && fannedOutAt > resolvedAt,
            "The currency has to be resolved above the Task.WhenAll: a totals failure must not be able "
            + "to leave the chart's figures rendering in the wrong precision.");
    }

    /// <summary>
    /// A main-currency figure is always denominated by <c>_mainCurrencyCode</c>, never by a generic
    /// fallback.
    ///
    /// <para>
    /// This is a regression guard for a real defect found in review. Decoupling the format from the
    /// totals response (above) made a previously-unreachable state reachable: totals succeeding while
    /// the reference-data lookup failed. The chart was guarded against it, but <c>HeaderSubLine</c>
    /// was not, and the format fell back to a generic "$" — so the header rendered a NOK net worth as
    /// "$48,260.00". That is the same misreported-denomination defect this page removes from the
    /// chart, relocated to the header.
    /// </para>
    ///
    /// <para>
    /// The fix is now structural rather than a fallback rule: there is no generic format left to reach
    /// for, and the only thing a failed lookup costs is the currency's decimals.
    /// </para>
    /// </summary>
    [Fact]
    public void TheMainCurrencyFormat_NeverFallsBackToTheGenericDollarFormat()
    {
        var source = CodeBehind();
        var accessor = Between(source, "private string FormatMoney(decimal value)", ";");

        Assert.Contains("_mainCurrencyCode", accessor, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$\"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure the page degrades through still has to be SAID. The chart region used to render
    /// nothing at all — no skeleton, no chart, no copy — when the format was unresolved, which is the
    /// one outcome this page's whole design forbids elsewhere.
    /// </summary>
    [Fact]
    public void ADegradedCurrencyFormat_IsDisclosedInTheHeaderRollup()
    {
        var source = CodeBehind();

        Assert.Contains("_currencyFormatIsDegraded", source, StringComparison.Ordinal);

        var rollup = Between(source, "private IReadOnlyCollection<PageHeaderProblem> HeaderProblems", "// ── Recent transactions");
        Assert.Contains("_currencyFormatIsDegraded", rollup, StringComparison.Ordinal);

        // …and the chart region no longer has a branch that can render nothing.
        Assert.DoesNotContain("_chartCanRender", Markup(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The denomination is the CODE, trailing the figure, whether or not the reference row resolved —
    /// so a missing row costs the currency's decimals and nothing about what the figure is in. The
    /// generic "$" this used to fall back to asserted USD about a NOK balance.
    /// </summary>
    [Fact]
    public void TheResolvedFigure_NeverFallsBackToAGenericDollar()
    {
        var known = OdsMoney.Format(48260m, "NOK",
            new ExistingCurrency { CurrencyCode = "NOK", Name = "Norwegian Krone", Symbol = "kr", MinorUnits = 2 });
        Assert.EndsWith(" NOK", known, StringComparison.Ordinal);
        Assert.DoesNotContain("$", known, StringComparison.Ordinal);

        var unknownRow = OdsMoney.Format(48260m, "NOK", currency: null);
        Assert.EndsWith(" NOK", unknownRow, StringComparison.Ordinal);
        Assert.DoesNotContain("$", unknownRow, StringComparison.Ordinal);
    }

    // ── AC37 — no client-side copy of a server cap ────────────────────────────────────────────

    /// <summary>
    /// AC37. The point caps are server-side, and <c>NetWorthHistoryQuery</c> lives in
    /// <c>Odyssey.Dtos</c> — which the WASM client can reference — so the client SHARES the constants
    /// rather than copying them. A copy is the defect CLAUDE.md already forbids for admin-editable
    /// caps: lowering the server's would let the page ask for a window the server refuses, and raising
    /// it would make the extra range unusable.
    ///
    /// <para>
    /// The dashboard currently names no cap at all — it sends no window and takes the server's
    /// defaults, which is the strongest form of the same property. This fires if that changes and the
    /// number is written in rather than referenced.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(NetWorthHistoryQuery.MaxPoints)]
    [InlineData(NetWorthHistoryQuery.MaxDailyPoints)]
    [InlineData(NetWorthHistoryQuery.MaxWeeklyPoints)]
    [InlineData(NetWorthHistoryQuery.DefaultPoints)]
    public void ThePointCaps_AreNeverWrittenIntoThePage(int cap)
    {
        var text = CodeBehind() + Markup();
        var literal = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var match = Regex.Match(text, $@"(?<![\w.]){literal}(?![\w])");
        Assert.False(match.Success,
            $"Home.razor.cs:{ClientSource.LineAt(text, match.Index)} writes {literal} in. The point caps "
            + "and the default window live on NetWorthHistoryQuery in Odyssey.Dtos, which the client can "
            + "reference — name the constant rather than copying the number.");
    }

    private static NetWorthHistoryPoint Point(DateOnly date, decimal netWorth, int unconverted = 0, int revalued = 0) => new()
    {
        Date = date,
        TotalAssets = netWorth > 0 ? netWorth : 0m,
        TotalLiabilities = netWorth < 0 ? -netWorth : 0m,
        NetWorth = netWorth,
        UnconvertedAccountCount = unconverted,
        RevaluedAccountCount = revalued,
        ContributingAccountCount = 1,
    };

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Home.razor.cs no longer contains '{start}'.");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? source[from..] : source[from..to];
    }
}
