using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #215: properties in the dashboard's net worth — the <see cref="DashboardFigures"/> gate and
/// copy, the rollup rows and their grouping, the chart's kinds, notes and table wording, and the
/// source-lints that keep every property read inside the gate.
/// </summary>
/// <remarks>
/// <c>Home</c> cannot be rendered in a test (its <c>OnInitializedAsync</c> early-returns off the
/// browser), so, per <see cref="DashboardFiguresTests"/>, the rules live in pure functions and are
/// asserted there; the source-lints prove <c>Home</c> reaches them only through the gate.
/// </remarks>
public class DashboardPropertyFiguresTests
{
    static DashboardPropertyFiguresTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static string Nok(decimal value) => OdsMoney.Format(value, "NOK", 2);

    private static NetWorthHistoryPoint Point(
        decimal netWorth = 100m,
        int accountUnconverted = 0,
        int accountRevalued = 0,
        int accountContributing = 1,
        decimal? propertyValue = null,
        int propertyContributing = 0,
        int propertyUnconverted = 0,
        int propertyRevalued = 0,
        int propertyUnvalued = 0) => new()
    {
        Date = new DateOnly(2026, 5, 1),
        TotalAssets = netWorth,
        TotalLiabilities = 0m,
        NetWorth = netWorth,
        UnconvertedAccountCount = accountUnconverted,
        RevaluedAccountCount = accountRevalued,
        ContributingAccountCount = accountContributing,
        PropertyValue = propertyValue,
        ContributingPropertyCount = propertyContributing,
        UnconvertedPropertyCount = propertyUnconverted,
        RevaluedPropertyCount = propertyRevalued,
        UnvaluedPropertyCount = propertyUnvalued,
    };

    private static NetWorthHistory History(bool included, params NetWorthHistoryPoint[] points) => new()
    {
        MainCurrencyCode = "NOK",
        Interval = NetWorthInterval.Monthly,
        From = new DateOnly(2026, 1, 1),
        To = new DateOnly(2026, 4, 30),
        Points = [.. points],
        PropertiesIncluded = included,
    };

    private static AccountTotals Totals(
        bool included,
        int contributing = 0,
        int unvalued = 0,
        params UnconvertedProperty[] unconverted) => new()
    {
        MainCurrencyCode = "NOK",
        TotalAssets = 1m,
        TotalLiabilities = 0m,
        NetWorth = 1m,
        PropertiesIncluded = included,
        PropertyValue = included ? 1m : null,
        ContributingPropertyCount = contributing,
        UnvaluedPropertyCount = unvalued,
        UnconvertedProperties = [.. unconverted],
    };

    private static UnconvertedProperty Property(string name, string currency = "EUR") =>
        new() { PropertyId = Guid.NewGuid(), Name = name, CurrencyCode = currency };

    private static UnconvertedAccount Account(string name, string currency = "CHF") =>
        new() { AccountId = Guid.NewGuid(), Name = name, CurrencyCode = currency };

    // ── AC1 — held property count ─────────────────────────────────────────────────────────────

    [Fact]
    public void HeldPropertyCount_IsContributingPlusUnvaluedPlusUnconverted()
    {
        var totals = Totals(true, contributing: 3, unvalued: 1, unconverted: Property("City apartment"));

        Assert.Equal(5, DashboardFigures.HeldPropertyCount(totals));
        Assert.True(DashboardFigures.PropertiesContribute(totals));
    }

    [Fact]
    public void WithNothingContributing_PropertiesDoNotContribute_SoTheHeaderReadsAsToday()
    {
        Assert.False(DashboardFigures.PropertiesContribute(Totals(true, contributing: 0, unvalued: 2)));
    }

    // ── AC2 — the composition sentence ────────────────────────────────────────────────────────

    [Fact]
    public void CompositionNote_DescribesTheLastPoint()
    {
        var history = History(true,
            Point(netWorth: 1m, propertyValue: 9m, propertyContributing: 9),
            Point(netWorth: 3_323_000m, propertyValue: 3_450_000m, propertyContributing: 3));

        Assert.Equal(
            $"Includes {Nok(3_450_000m)} in property estimates from 3 valued properties; accounts alone: {Nok(-127_000m)}.",
            DashboardFigures.CompositionNote(history, Nok));
    }

    [Fact]
    public void CompositionNote_WithALegalZeroEstimate_IsRendered_Singular()
    {
        var note = DashboardFigures.CompositionNote(
            History(true, Point(netWorth: 500m, propertyValue: 0m, propertyContributing: 1)), Nok);

        Assert.Equal($"Includes {Nok(0m)} in property estimates from 1 valued property; accounts alone: {Nok(500m)}.", note);
    }

    [Fact]
    public void CompositionNote_IsAbsent_WhenNothingContributes_OrTheValueIsNull()
    {
        Assert.Null(DashboardFigures.CompositionNote(History(true, Point(propertyValue: 0m, propertyContributing: 0)), Nok));
        // Absence is fail-closed, never read as ?? 0.
        Assert.Null(DashboardFigures.CompositionNote(History(true, Point(propertyValue: null, propertyContributing: 2)), Nok));
        Assert.Null(DashboardFigures.CompositionNote(History(true), Nok));
        Assert.Null(DashboardFigures.CompositionNote(null, Nok));
    }

    // ── AC3 — accounts only ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AccountsOnly_AddsTheCaptionSegmentAndTheAccessibleNameSuffix()
    {
        Assert.EndsWith(" · accounts only",
            DashboardFigures.ChartCaption(4, "Feb ’26", "May ’26", NetWorthInterval.Monthly, "NOK", accountsOnly: true),
            StringComparison.Ordinal);
        Assert.EndsWith(", accounts only",
            DashboardFigures.ChartAriaLabel(4, "Feb ’26", "May ’26", NetWorthInterval.Monthly, accountsOnly: true, propertiesContribute: false),
            StringComparison.Ordinal);
        Assert.DoesNotContain("accounts only",
            DashboardFigures.ChartCaption(4, "Feb ’26", "May ’26", NetWorthInterval.Monthly, "NOK", accountsOnly: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChartAriaLabel_NamesPropertyEstimates_WhenTheyContribute()
    {
        Assert.EndsWith(", including property estimates",
            DashboardFigures.ChartAriaLabel(4, "Feb ’26", "May ’26", NetWorthInterval.Monthly, accountsOnly: false, propertiesContribute: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChartAriaLabel_WithNoPoints_IsTriState()
    {
        Assert.Equal("Net worth over time, accounts only",
            DashboardFigures.ChartAriaLabel(0, null, null, NetWorthInterval.Monthly, accountsOnly: true, propertiesContribute: false));
        // Flag true, or no history at all: nothing is plotted, so no suffix applies.
        Assert.Equal("Net worth over time",
            DashboardFigures.ChartAriaLabel(0, null, null, NetWorthInterval.Monthly, accountsOnly: false, propertiesContribute: false));
    }

    // ── AC4 — fail closed ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFlagFalseResponse_WithNonZeroPropertyMembers_RendersExactlyAccountsOnly()
    {
        var leakingPoint = Point(netWorth: 10m, propertyValue: 5m, propertyContributing: 2,
            propertyUnconverted: 1, propertyRevalued: 1, propertyUnvalued: 1);
        var history = History(false, leakingPoint, leakingPoint);
        history.UnconvertedProperties = [Property("City apartment")];

        var totals = new AccountTotals
        {
            MainCurrencyCode = "NOK", TotalAssets = 1m, TotalLiabilities = 0m, NetWorth = 1m,
            PropertiesIncluded = false, PropertyValue = 5m, ContributingPropertyCount = 2,
            UnvaluedPropertyCount = 1, UnconvertedProperties = [Property("Boat")],
        };

        Assert.Equal(OdsLinePointKind.Normal, DashboardFigures.KindOf(leakingPoint, history.PropertiesIncluded));
        Assert.All(DashboardFigures.BuildSeries(history), p => Assert.Equal(OdsLinePointKind.Normal, p.Kind));
        Assert.Empty(DashboardFigures.IncludedUnconvertedProperties(history));
        Assert.False(DashboardFigures.PropertiesContribute(history));
        Assert.Null(DashboardFigures.CompositionNote(history, Nok));
        Assert.Equal((0, 0, 0), DashboardFigures.IncludedPropertyCounts(leakingPoint, history.PropertiesIncluded));

        Assert.Equal(0, DashboardFigures.HeldPropertyCount(totals));
        Assert.False(DashboardFigures.PropertiesContribute(totals));
        Assert.Empty(DashboardFigures.IncludedUnconvertedProperties(totals));
        Assert.Null(DashboardFigures.UnvaluedMessage(totals));

        // A point with no ACCOUNT contribution does not borrow the property one to show a delta.
        var propertyOnly = Point(accountContributing: 0, propertyValue: 5m, propertyContributing: 1);
        Assert.False(DashboardFigures.Contributes(propertyOnly, included: false));

        // The notes: property counts are never labelled and the property list is never named.
        Assert.False(DashboardFigures.IsUnderstated(leakingPoint, included: false));
        Assert.False(DashboardFigures.IsRevaluedByProperty(leakingPoint, included: false));
    }

    // ── AC5, AC6, AC7, AC8 — kinds and the delta ──────────────────────────────────────────────

    [Fact]
    public void AnUnconvertedProperty_MakesThePointPartial()
    {
        var point = Point(propertyUnconverted: 1, propertyValue: 0m);

        Assert.Equal(OdsLinePointKind.Partial, DashboardFigures.KindOf(point, included: true));
    }

    [Fact]
    public void AnUnvaluedProperty_LeavesThePointNormal_AndDoesNotWithholdTheDelta()
    {
        var point = Point(propertyUnvalued: 1, propertyValue: 0m);

        Assert.Equal(OdsLinePointKind.Normal, DashboardFigures.KindOf(point, included: true));
        Assert.True(DashboardFigures.Contributes(point, included: true));
    }

    [Fact]
    public void ARevaluedProperty_MakesThePointRevalued()
    {
        Assert.Equal(OdsLinePointKind.Revalued,
            DashboardFigures.KindOf(Point(propertyRevalued: 1, propertyValue: 1m, propertyContributing: 1), included: true));
    }

    [Theory]
    [InlineData(true, false, "because an account estimate took effect")]
    [InlineData(false, true, "because a property estimate took effect")]
    [InlineData(true, true, "because an account or property estimate took effect")]
    public void RevaluedNote_NamesTheKindOfEstimate(bool anyAccount, bool anyProperty, string expected)
    {
        var note = DashboardFigures.RevaluedNote(["Mar ’26"], anyAccount, anyProperty);

        Assert.Equal($"Mar ’26 steps {expected} — a real movement, not a correction.", note);
    }

    [Fact]
    public void APropertyOnlySeries_ShowsTheDelta()
    {
        var first = Point(accountContributing: 0, propertyContributing: 1, propertyValue: 1m);
        var last = Point(accountContributing: 0, propertyContributing: 1, propertyValue: 2m);

        Assert.True(DashboardFigures.Contributes(first, included: true));
        Assert.True(DashboardFigures.Contributes(last, included: true));
    }

    // ── AC9 — the understated note by kind ────────────────────────────────────────────────────

    [Fact]
    public void UnderstatedNote_NamesTheOneEntry_AcrossBothLists()
    {
        Assert.Equal("Mar ’25 is understated — City apartment (EUR) had no exchange rate.",
            DashboardFigures.UnderstatedNote(["Mar ’25"], [], [Property("City apartment")], deltaWithheld: false));
        Assert.Equal("Mar ’25 is understated — Zurich (CHF) had no exchange rate.",
            DashboardFigures.UnderstatedNote(["Mar ’25"], [Account("Zurich")], [], deltaWithheld: false));
    }

    [Fact]
    public void UnderstatedNote_DescribesSeveralByKind()
    {
        Assert.Contains("— an account had no exchange rate",
            DashboardFigures.UnderstatedNote(["a"], [Account("A"), Account("B")], [], false), StringComparison.Ordinal);
        Assert.Contains("— a property had no exchange rate",
            DashboardFigures.UnderstatedNote(["a"], [], [Property("A"), Property("B")], false), StringComparison.Ordinal);
        Assert.Contains("— an account or property had no exchange rate",
            DashboardFigures.UnderstatedNote(["a"], [Account("A")], [Property("B")], false), StringComparison.Ordinal);
    }

    // ── AC10 — the table text ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false, "Understated — an account had no exchange rate for this period")]
    [InlineData(false, true, "Understated — a property had no exchange rate for this period")]
    [InlineData(true, true, "Understated — an account or property had no exchange rate for this period")]
    public void PartialDescription_NamesTheKind(bool anyAccount, bool anyProperty, string expected)
    {
        Assert.Equal(expected, DashboardFigures.PartialDescription(anyAccount, anyProperty));
    }

    [Theory]
    [InlineData(true, false, "Revalued — an account estimate took effect in this period")]
    [InlineData(false, true, "Revalued — a property estimate took effect in this period")]
    [InlineData(true, true, "Revalued — an account or property estimate took effect in this period")]
    public void RevaluedDescription_NamesTheKind(bool anyAccount, bool anyProperty, string expected)
    {
        Assert.Equal(expected, DashboardFigures.RevaluedDescription(anyAccount, anyProperty));
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static List<string> TableStates(IRenderedComponent<OdsLineChart> chart) =>
        chart.FindAll(".odc-sr-only table tbody td:last-child").Select(e => e.TextContent.Trim()).ToList();

    private static readonly OdsLinePoint[] MarkedSeries =
    [
        new("a", 100m, OdsLinePointKind.Normal),
        new("b", 200m, OdsLinePointKind.Partial),
        new("c", 300m, OdsLinePointKind.Revalued),
    ];

    [Fact]
    public void TheChartTable_UsesThePassedDescriptions()
    {
        using var ctx = NewContext();
        var chart = ctx.Render<OdsLineChart>(p => p
            .Add(c => c.Series, MarkedSeries)
            .Add(c => c.Format, static n => n.ToString("0", CultureInfo.InvariantCulture))
            .Add(c => c.TextEquivalent, true)
            .Add(c => c.PartialDescription, DashboardFigures.PartialDescription(false, true))
            .Add(c => c.RevaluedDescription, DashboardFigures.RevaluedDescription(false, true)));

        var states = TableStates(chart);
        Assert.Contains("Understated — a property had no exchange rate for this period", states);
        Assert.Contains("Revalued — a property estimate took effect in this period", states);
    }

    [Fact]
    public void TheChartTable_WithNullDescriptions_ReadsExactlyAsBefore()
    {
        using var ctx = NewContext();
        var chart = ctx.Render<OdsLineChart>(p => p
            .Add(c => c.Series, MarkedSeries)
            .Add(c => c.Format, static n => n.ToString("0", CultureInfo.InvariantCulture))
            .Add(c => c.TextEquivalent, true));

        var states = TableStates(chart);
        Assert.Contains("Understated — an account had no exchange rate for this period", states);
        Assert.Contains("Revalued — an estimate took effect in this period", states);
    }

    // ── AC11, AC12, AC13 — rollup copy ────────────────────────────────────────────────────────

    [Fact]
    public void UnconvertedPropertyMessage_NamesFromThenTo()
    {
        Assert.Equal("No exchange rate from EUR to NOK, so this property counts as 0 towards net worth.",
            DashboardFigures.UnconvertedPropertyMessage("EUR", "NOK"));
    }

    [Fact]
    public void UnvaluedMessage_IsSingularOrPlural()
    {
        Assert.Equal("1 property has no estimate yet, so it counts as 0 towards net worth.",
            DashboardFigures.UnvaluedMessage(Totals(true, unvalued: 1)));
        Assert.Equal("2 properties have no estimate yet, so they count as 0 towards net worth.",
            DashboardFigures.UnvaluedMessage(Totals(true, unvalued: 2)));
        Assert.Null(DashboardFigures.UnvaluedMessage(Totals(true, unvalued: 0)));
    }

    // ── AC14 — grouping and the toggle label ──────────────────────────────────────────────────

    private static PageHeaderProblem Row(PageHeaderSeverity severity, string message) =>
        new() { Severity = severity, Message = message };

    [Fact]
    public void AMixedPanel_PutsWarningsFirst_UnderTwoHeadings()
    {
        var grouped = DashboardFigures.GroupProblems([
            Row(PageHeaderSeverity.Information, "i1"),
            Row(PageHeaderSeverity.Warning, "w1"),
            Row(PageHeaderSeverity.Information, "i2"),
        ]);

        Assert.Equal(["w1", "i1", "i2"], grouped.Select(r => r.Message));
        Assert.Equal(["Needs attention", "For your information", "For your information"], grouped.Select(r => r.Group));
        Assert.Equal("Net worth issues", DashboardFigures.ProblemsLabel(grouped));
    }

    [Fact]
    public void ASingleSeverityPanel_CarriesNoHeading()
    {
        var warnings = DashboardFigures.GroupProblems([Row(PageHeaderSeverity.Warning, "w1"), Row(PageHeaderSeverity.Warning, "w2")]);
        var information = DashboardFigures.GroupProblems([Row(PageHeaderSeverity.Information, "i1")]);

        Assert.All(warnings, r => Assert.Null(r.Group));
        Assert.All(information, r => Assert.Null(r.Group));
        Assert.Equal("Net worth issues", DashboardFigures.ProblemsLabel(warnings));
        Assert.Equal("Net worth notes", DashboardFigures.ProblemsLabel(information));
    }

    // ── AC16 — every empty-copy row ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(NetWorthEmptyReason.NoAccounts, false, "No accounts to chart yet.")]
    [InlineData(NetWorthEmptyReason.NoAccounts, true, "No accounts or valued properties to chart yet.")]
    [InlineData(NetWorthEmptyReason.NothingConvertible, false, "Net worth could not be converted to NOK for any period.")]
    [InlineData(NetWorthEmptyReason.NothingConvertible, true, "Net worth could not be converted to NOK for any period.")]
    [InlineData(NetWorthEmptyReason.WindowBeforeFirstAccount, false, "No accounts existed in this period.")]
    [InlineData(NetWorthEmptyReason.WindowBeforeFirstAccount, true, "No accounts or valued properties existed in this period.")]
    [InlineData(NetWorthEmptyReason.WindowAfterAllAccountsClosed, false, "Every account had closed before this period.")]
    [InlineData(NetWorthEmptyReason.WindowAfterAllAccountsClosed, true, "No account was open and no valued property was held during this period.")]
    [InlineData(NetWorthEmptyReason.NotBuilt, false, "Net-worth history is not available yet.")]
    [InlineData(NetWorthEmptyReason.NotBuilt, true, "Net-worth history is not available yet.")]
    [InlineData(null, false, "Net-worth history could not be loaded.")]
    [InlineData(null, true, "Net-worth history could not be loaded.")]
    public void ChartEmptyLabel_PicksCopyByReasonAndFlag(NetWorthEmptyReason? reason, bool included, string expected)
    {
        Assert.Equal(expected, DashboardFigures.ChartEmptyLabel(reason, included, "NOK"));
    }

    // ── AC18 — a property name is text, never markup ──────────────────────────────────────────

    // Built with Concat so the runtime value carries REAL angle brackets (issue #215 AC18).
    private static readonly string HostileName = string.Concat('<', "img src=x onerror=alert(1)", '>');

    [Fact]
    public void AHostilePropertyName_RendersAsTextInTheRollupLead()
    {
        using var ctx = NewContext();
        var header = ctx.Render<PageHeader>(p => p
            .Add(h => h.Title, "Dashboard")
            .Add(h => h.Problems, [new PageHeaderProblem
            {
                Severity = PageHeaderSeverity.Warning,
                Lead = HostileName,
                Message = DashboardFigures.UnconvertedPropertyMessage("EUR", "NOK"),
            }]));

        Assert.Empty(header.FindAll("img"));
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", header.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostilePropertyName_RendersAsTextInTheUnderstatedNote()
    {
        var note = DashboardFigures.UnderstatedNote(["Mar ’25"], [], [Property(HostileName)], deltaWithheld: false)!;
        using var ctx = NewContext();
        // The page renders the note exactly like this: an encoded @string inside the chart's Sub.
        RenderFragment sub = builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "odc-lc-note");
            builder.AddContent(2, note);
            builder.CloseElement();
        };
        var chart = ctx.Render<OdsLineChart>(p => p
            .Add(c => c.Series, MarkedSeries)
            .Add(c => c.Format, static n => n.ToString("0", CultureInfo.InvariantCulture))
            .Add(c => c.Sub, sub));

        Assert.Empty(chart.FindAll("img"));
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", chart.Markup, StringComparison.Ordinal);
    }

    // ── AC17, AC19 — source-lints on Home ─────────────────────────────────────────────────────

    private static string CodeBehind() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "Home.razor.cs"));

    private static string Markup() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "Home.razor"));

    private static string Figures() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "DashboardFigures.cs"));

    [Fact]
    public void Home_ReadsTheFlag()
    {
        Assert.Matches(@"\bPropertiesIncluded\b", CodeBehind());
    }

    [Theory]
    [InlineData("ContributingPropertyCount")]
    [InlineData("UnvaluedPropertyCount")]
    [InlineData("UnconvertedPropertyCount")]
    [InlineData("RevaluedPropertyCount")]
    [InlineData("PropertyValue")]
    [InlineData("UnconvertedProperties")]
    public void Home_NamesNoPropertyMember_EveryReadIsInsideAGate(string member)
    {
        foreach (var (file, source) in new[] { ("Home.razor.cs", CodeBehind()), ("Home.razor", Markup()) })
        {
            var match = Regex.Match(source, $@"\b{member}\b");
            Assert.False(match.Success,
                $"{file}:{ClientSource.LineAt(source, match.Index)} reads {member} directly. Every property "
                + "read goes through a DashboardFigures gate keyed on the response's PropertiesIncluded "
                + "(issue #215 §3.3), so a flag-false response can never render property data.");
        }
    }

    [Theory]
    [InlineData("PropertiesRead")]
    [InlineData("PropertiesEstimatesRead")]
    public void Home_NeverDecidesInclusionFromAClaim(string claim)
    {
        Assert.DoesNotMatch($@"\b{claim}\b", CodeBehind());
    }

    [Fact]
    public void TheOneAllowedSubtraction_IsInCompositionNote_Once()
    {
        var figures = Figures();
        var subtraction = new Regex(@"NetWorth\s*-\s*[\w.]*PropertyValue");

        Assert.Single(subtraction.Matches(figures));
        var composition = figures[figures.IndexOf("internal static string? CompositionNote", StringComparison.Ordinal)..];
        composition = composition[..composition.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Matches(subtraction, composition);
    }

    [Fact]
    public void TheCompositionSentence_IsPlainTextInTheExistingNoteStyle_AndNoNameReachesMarkup()
    {
        var markup = Markup();

        Assert.Matches(@"<span class=""odc-lc-note"">@composition</span>", markup);
        Assert.DoesNotContain("MarkupString", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkupString", CodeBehind(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRollupLabel_IsComputed()
    {
        Assert.Contains(@"ProblemsLabel=""@ProblemsLabel""", Markup(), StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProblemsLabel=""Net worth issues""", Markup(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRollup_HasNoEarlyReturnBeforeItsPropertyRows()
    {
        var code = CodeBehind();
        var rows = code[code.IndexOf("HeaderProblemRows", StringComparison.Ordinal)..];
        rows = rows[..rows.IndexOf("NavigateTo(string route)", StringComparison.Ordinal)];

        // The only `return problems;` comes after the last row producer.
        Assert.Single(Regex.Matches(rows, @"return problems;"));
        Assert.True(rows.IndexOf("return problems;", StringComparison.Ordinal)
                    > rows.IndexOf("DashboardFigures.UnvaluedMessage(_totals)", StringComparison.Ordinal));
        Assert.Contains("DashboardFigures.IncludedUnconvertedProperties(_totals)", rows, StringComparison.Ordinal);
        Assert.Contains("DashboardFigures.UnvaluedMessage(_totals)", rows, StringComparison.Ordinal);
    }
}
