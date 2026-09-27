using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The dashboard's money formatting and advisory wording, split out of <c>Home</c> for the same
/// reason <see cref="AllocationLegend"/> was split out of <c>AccountsOverview</c>: a branch that
/// lives as a private method on a component is checkable only by rendering the page, and
/// <c>Home</c> cannot be rendered in a test — its <c>OnInitializedAsync</c> early-returns on
/// <c>!OperatingSystem.IsBrowser()</c> and it exposes no <c>InteractiveCheck</c> seam.
/// </summary>
/// <remarks>
/// These are the branches a source-lint cannot reach. A lint proves the page asks the server for its
/// net worth; only a behavioural test proves the answer is then rendered in the right currency and
/// that the advisory names the right pair of them.
/// </remarks>
internal static class DashboardFigures
{
    /// <summary>
    /// The currency's own decimals, for a figure the caller renders through <see cref="OdsMoney"/>.
    /// <paramref name="currency"/> is the reference-data row when one was found; an unknown code
    /// takes the default two.
    /// </summary>
    /// <remarks>
    /// This replaced a <see cref="NumberFormatInfo"/> carrying a currency SYMBOL. Money is written
    /// as the amount followed by its ISO 4217 code now (Odyssey Design System · README "Numbers"),
    /// so there is no sigil left to resolve — only the decimals, which are still the currency's own.
    /// </remarks>
    internal static int MinorUnits(ExistingCurrency? currency) => OdsMoney.MinorUnitsOf(currency);

    /// <summary>
    /// A per-transaction amount, written in its OWN account's currency rather than the page's main one.
    /// </summary>
    /// <param name="minorUnitsByCode">Every known currency's decimals; empty when the lookup failed.</param>
    /// <remarks>
    /// <para>
    /// Two things this gets right that the old generic-"$" formatting did not. The row names the
    /// currency the amount is actually IN — nothing on this page converts a transaction to the main
    /// currency, so labelling it with the main one asserted a denomination it may not have had. And
    /// the decimals come from THAT currency's own row, so a JPY amount renders whole while a USD one
    /// beside it keeps its cents; reading the main currency's decimals would round one of them wrong.
    /// </para>
    /// <para>
    /// Presented as SIGNED: on a ledger row the direction is the point, and a bare positive would
    /// read as an ordinary total. An unknown or missing code degrades to the default two decimals
    /// rather than throwing — a failed reference-data load must not blank the dashboard.
    /// </para>
    /// </remarks>
    internal static string TransactionAmount(
        decimal value, string? currencyCode, IReadOnlyDictionary<string, int> minorUnitsByCode) =>
        OdsMoney.Signed(value, currencyCode,
            currencyCode is not null && minorUnitsByCode.TryGetValue(currencyCode, out var units)
                ? units
                : OdsMoney.DefaultMinorUnits);

    /// <summary>
    /// A compact axis label, e.g. "52k USD" / "640 CHF". The code trails, exactly as it does on the
    /// headline figure above the chart, so an axis and its headline read as one denomination.
    /// </summary>
    internal static string AxisLabel(decimal value, string? currencyCode) =>
        OdsMoney.Compact(value, currencyCode);

    /// <summary>The advisory on a party the server could not convert. Order matters: FROM the account's currency, TO the main one.</summary>
    internal static string UnconvertedMessage(string accountCurrencyCode, string mainCurrencyCode) =>
        $"No exchange rate from {accountCurrencyCode} to {mainCurrencyCode}, "
        + "so this account counts as 0 towards net worth.";

    /// <summary>The same advisory for a property (issue #215 state 5). FROM the property's currency, TO the main one.</summary>
    internal static string UnconvertedPropertyMessage(string propertyCurrencyCode, string mainCurrencyCode) =>
        $"No exchange rate from {propertyCurrencyCode} to {mainCurrencyCode}, "
        + "so this property counts as 0 towards net worth.";

    // ── The property gate (issue #215 §3.3) ─────────────────────────────────────────────────────
    //
    // Every property-derived read goes through one of these, keyed on the RESPONSE's own
    // PropertiesIncluded, and each returns zero / empty / null when that flag is false. So a response
    // whose flag is false but whose property members are non-zero — a server regression — still
    // renders exactly the accounts-only page. Home names none of the property members itself (a
    // source-lint pins that), and it never decides inclusion from a claim: a second authority would
    // drift from the server's decision, e.g. after a role change while the cookie carries old claims.

    /// <summary>A point's property counts, or all zero when its response did not include properties.</summary>
    internal static (int Unconverted, int Revalued, int Contributing) IncludedPropertyCounts(
        NetWorthHistoryPoint point, bool included) =>
        included
            ? (point.UnconvertedPropertyCount, point.RevaluedPropertyCount, point.ContributingPropertyCount)
            : (0, 0, 0);

    /// <summary>The history's unconvertible properties, or none when it did not include properties.</summary>
    internal static IReadOnlyList<UnconvertedProperty> IncludedUnconvertedProperties(NetWorthHistory? history) =>
        history is { PropertiesIncluded: true } ? history.UnconvertedProperties : [];

    /// <summary>Whether properties contribute to the LATEST point — the one the composition sentence describes.</summary>
    internal static bool PropertiesContribute(NetWorthHistory? history) =>
        history is { PropertiesIncluded: true, Points.Count: > 0 }
        && history.Points[^1].ContributingPropertyCount > 0;

    /// <summary>Held, valued and converted properties now, or 0 when not included.</summary>
    internal static int IncludedContributingPropertyCount(AccountTotals? totals) =>
        totals is { PropertiesIncluded: true } ? totals.ContributingPropertyCount : 0;

    /// <summary>Held properties with no estimate yet, or 0 when not included.</summary>
    internal static int IncludedUnvaluedPropertyCount(AccountTotals? totals) =>
        totals is { PropertiesIncluded: true } ? totals.UnvaluedPropertyCount : 0;

    /// <summary>In-term accounts of type Property or Vehicle, or null when not included.</summary>
    internal static int? IncludedAssetTypedAccountCount(AccountTotals? totals) =>
        totals is { PropertiesIncluded: true } ? totals.AssetTypedAccountCount : null;

    /// <summary>The totals' unconvertible properties, or none when not included.</summary>
    internal static IReadOnlyList<UnconvertedProperty> IncludedUnconvertedProperties(AccountTotals? totals) =>
        totals is { PropertiesIncluded: true } ? totals.UnconvertedProperties : [];

    /// <summary>
    /// Every property held now, whether or not it could be valued or converted — contributing +
    /// unvalued + unconverted — or 0 when not included. The header's M and the advisory's precondition.
    /// </summary>
    internal static int HeldPropertyCount(AccountTotals? totals) =>
        IncludedContributingPropertyCount(totals)
        + IncludedUnvaluedPropertyCount(totals)
        + IncludedUnconvertedProperties(totals).Count;

    /// <summary>Whether properties contribute to the headline figure now (issue #215 state 2 vs 3).</summary>
    internal static bool PropertiesContribute(AccountTotals? totals) => IncludedContributingPropertyCount(totals) > 0;

    /// <summary>
    /// The composition sentence for the chart's sub-line (state 2): the property share of the LATEST
    /// point, and the accounts-alone figure beside it. <c>null</c> when properties are not included,
    /// when none contributes to that point, or when its property value is absent — absence is
    /// fail-closed and never read as zero.
    /// </summary>
    /// <remarks>
    /// The one subtraction the page performs, of two server figures in one currency at one instant.
    /// The last point equals <c>/totals</c> (issue #214 AC5), so this describes the headline figure.
    /// </remarks>
    internal static string? CompositionNote(NetWorthHistory? history, Func<decimal, string> formatMoney)
    {
        if (!PropertiesContribute(history))
            return null;

        var last = history!.Points[^1];
        if (last.PropertyValue is not { } propertyValue)
            return null;

        var count = last.ContributingPropertyCount;
        var accountsAlone = last.NetWorth - last.PropertyValue.Value;
        return $"Includes {formatMoney(propertyValue)} in property estimates from {count} valued "
            + $"propert{(count == 1 ? "y" : "ies")}; accounts alone: {formatMoney(accountsAlone)}.";
    }

    /// <summary>The Information entry for held properties with no estimate yet (state 6), or null.</summary>
    internal static string? UnvaluedMessage(AccountTotals? totals)
    {
        var count = IncludedUnvaluedPropertyCount(totals);
        return count switch
        {
            <= 0 => null,
            1 => "1 property has no estimate yet, so it counts as 0 towards net worth.",
            _ => $"{count} properties have no estimate yet, so they count as 0 towards net worth.",
        };
    }

    /// <summary>
    /// The double-count advisory (state 8): an account typed Property/Vehicle may describe the same
    /// asset as a property record. Only while properties are held — otherwise the overlap cannot exist.
    /// </summary>
    internal static string? DoubleCountAdvisory(AccountTotals? totals)
    {
        if (IncludedAssetTypedAccountCount(totals) is not { } count || count <= 0 || HeldPropertyCount(totals) <= 0)
            return null;

        return count == 1
            ? "1 account is of type Property or Vehicle. If it describes the same asset as a property, that asset is counted twice."
            : $"{count} accounts are of type Property or Vehicle. If one describes the same asset as a property, that asset is counted twice.";
    }

    /// <summary>"an account" / "a property" / "an account or property" — the member kind a note or table names.</summary>
    private static string MemberKind(bool anyAccount, bool anyProperty) => (anyAccount, anyProperty) switch
    {
        (false, true) => "a property",
        (true, true) => "an account or property",
        _ => "an account",
    };

    /// <summary>The chart table's State text for a Partial point, naming the member kind as the note does.</summary>
    internal static string PartialDescription(bool anyAccount, bool anyProperty) =>
        $"Understated — {MemberKind(anyAccount, anyProperty)} had no exchange rate for this period";

    /// <summary>The chart table's State text for a Revalued point, naming the kind of estimate.</summary>
    internal static string RevaluedDescription(bool anyAccount, bool anyProperty) =>
        $"Revalued — {MemberKind(anyAccount, anyProperty)} estimate took effect in this period";

    // ── The rollup (issue #215 §4) ──────────────────────────────────────────────────────────────

    internal const string NeedsAttentionGroup = "Needs attention";
    internal const string ForYourInformationGroup = "For your information";

    /// <summary>
    /// Warnings first, then Information. A heading is set ONLY when the panel mixes the two, so a
    /// warnings-only panel renders exactly as it always has and an information-only one carries none.
    /// </summary>
    internal static IReadOnlyList<PageHeaderProblem> GroupProblems(IReadOnlyList<PageHeaderProblem> rows)
    {
        var attention = rows.Where(row => row.Severity != PageHeaderSeverity.Information).ToList();
        var information = rows.Where(row => row.Severity == PageHeaderSeverity.Information).ToList();
        var mixed = attention.Count > 0 && information.Count > 0;

        foreach (var row in attention)
            row.Group = mixed ? NeedsAttentionGroup : null;
        foreach (var row in information)
            row.Group = mixed ? ForYourInformationGroup : null;

        return [.. attention, .. information];
    }

    /// <summary>The rollup toggle's label: a standing advisory must not read as a standing fault.</summary>
    internal static string ProblemsLabel(IReadOnlyCollection<PageHeaderProblem> rows) =>
        rows.Any(row => row.Severity != PageHeaderSeverity.Information) ? "Net worth issues" : "Net worth notes";

    /// <summary>
    /// The chart's empty copy. The cause is <b>known</b>, so the copy says which one — never "no data
    /// yet", which tells a reader with a full portfolio that their accounts are empty.
    /// </summary>
    /// <param name="reason">
    /// The server's own discrimination. It is carried on the response precisely because it cannot be
    /// inferred: several of the causes produce otherwise byte-identical payloads.
    /// </param>
    /// <param name="included">
    /// The response's <c>PropertiesIncluded</c>: with properties the members are "accounts or valued
    /// properties" (issue #214 §5.4), so three of the causes say so.
    /// </param>
    /// <param name="mainCurrencyCode">Interpolated into the conversion case, which names the currency it failed to reach.</param>
    internal static string ChartEmptyLabel(NetWorthEmptyReason? reason, bool included, string? mainCurrencyCode) => reason switch
    {
        NetWorthEmptyReason.NoAccounts => included
            ? "No accounts or valued properties to chart yet."
            : "No accounts to chart yet.",
        NetWorthEmptyReason.NothingConvertible => string.IsNullOrWhiteSpace(mainCurrencyCode)
            ? "Net worth could not be converted for any period."
            : $"Net worth could not be converted to {mainCurrencyCode} for any period.",
        NetWorthEmptyReason.WindowBeforeFirstAccount => included
            ? "No accounts or valued properties existed in this period."
            : "No accounts existed in this period.",
        // Not "every property had been disposed of": a held but never-estimated property is also this.
        NetWorthEmptyReason.WindowAfterAllAccountsClosed => included
            ? "No account was open and no valued property was held during this period."
            : "Every account had closed before this period.",
        NetWorthEmptyReason.NotBuilt => "Net-worth history is not available yet.",
        // No reason at all means the call did not land. Distinct copy, because "not available yet" is
        // a statement about the data and this is a statement about the request.
        _ => "Net-worth history could not be loaded.",
    };

    /// <summary>
    /// The x-axis label for a point, derived from its own <c>Date</c> — which is the period END, the
    /// instant the point describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deriving it from anything else is how a financial figure gets mislabelled by one period, which
    /// is the defect this whole feature exists to fix in a smaller shape.
    /// </para>
    /// <para>
    /// A monthly or quarterly label <b>always</b> carries its year, even though that repeats "’25"
    /// across a run. Showing the year only where it changes reads better on paper and is wrong here:
    /// the chart draws every Nth label, the component picks N from the point count, and the page
    /// cannot know which labels survive — so the year-bearing ones are exactly the ones a stride can
    /// drop. A 24-point series then renders "Feb" and "May" twice with nothing to tell the two years
    /// apart, which on a net-worth chart is worse than a little repetition.
    /// </para>
    /// </remarks>
    internal static string PointLabel(DateOnly date, NetWorthInterval interval) => interval switch
    {
        // At most 31 days or 53 weeks, so the day and month place a point without a year.
        NetWorthInterval.Daily or NetWorthInterval.Weekly =>
            date.ToString("d MMM", CultureInfo.InvariantCulture),
        NetWorthInterval.Yearly => date.ToString("yyyy", CultureInfo.InvariantCulture),
        _ => date.ToString("MMM ", CultureInfo.InvariantCulture)
            + "’" + date.ToString("yy", CultureInfo.InvariantCulture),
    };

    /// <summary>The interval, in the lower-case adjective form the caption and the ARIA label read in.</summary>
    internal static string IntervalWord(NetWorthInterval interval) => interval switch
    {
        NetWorthInterval.Daily => "daily",
        NetWorthInterval.Weekly => "weekly",
        NetWorthInterval.Monthly => "monthly",
        NetWorthInterval.Quarterly => "quarterly",
        NetWorthInterval.Yearly => "yearly",
        _ => "",
    };

    /// <summary>
    /// The chart caption: how many points, over what span, in what currency. What the chart IS —
    /// not when it started, which was what the removed "Since {year}" said about a curve that had no
    /// real span at all.
    /// </summary>
    /// <param name="accountsOnly">
    /// True only when a history was received with <c>PropertiesIncluded</c> false (issue #215 state 4)
    /// — not when properties are included, and not when there is no history at all.
    /// </param>
    internal static string ChartCaption(
        int pointCount, string? firstLabel, string? lastLabel, NetWorthInterval interval, string? currencyCode,
        bool accountsOnly)
    {
        var word = IntervalWord(interval);
        var head = $"{pointCount} {word} point{(pointCount == 1 ? "" : "s")}";
        var span = string.IsNullOrEmpty(firstLabel) || string.IsNullOrEmpty(lastLabel)
            ? null
            : $"{firstLabel} – {lastLabel}";

        return string.Join(" · ", new[] { head, span, currencyCode, accountsOnly ? "accounts only" : null }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// The disclosure sentence for the understated periods, or null when there are none.
    /// </summary>
    /// <remarks>
    /// Every condition the markers show is also stated here in text, because the markers rest on shape
    /// and stroke and a reader who cannot see the plot at all gets neither. The withheld-delta clause
    /// is added only when an <b>endpoint</b> is understated, which is the only case that withholds it.
    /// </remarks>
    /// <param name="unconvertedProperties">Already gated: empty whenever the history did not include properties.</param>
    internal static string? UnderstatedNote(
        IReadOnlyList<string> understatedLabels,
        IReadOnlyList<UnconvertedAccount> unconvertedAccounts,
        IReadOnlyList<UnconvertedProperty> unconvertedProperties,
        bool deltaWithheld)
    {
        if (understatedLabels.Count == 0)
            return null;

        var subject = understatedLabels.Count == 1
            ? $"{understatedLabels[0]} is understated"
            : $"{understatedLabels.Count} periods are understated";

        // Exactly one member across both lists is named; several are described by kind. Neither list
        // populated keeps today's account wording.
        var because = (unconvertedAccounts.Count, unconvertedProperties.Count) switch
        {
            (1, 0) => $" — {unconvertedAccounts[0].Name} ({unconvertedAccounts[0].CurrencyCode}) had no exchange rate",
            (0, 1) => $" — {unconvertedProperties[0].Name} ({unconvertedProperties[0].CurrencyCode}) had no exchange rate",
            (0, > 0) => $" — {MemberKind(anyAccount: false, anyProperty: true)} had no exchange rate",
            ( > 0, > 0) => $" — {MemberKind(anyAccount: true, anyProperty: true)} had no exchange rate",
            _ => $" — {MemberKind(anyAccount: true, anyProperty: false)} had no exchange rate",
        };

        var withheld = deltaWithheld
            ? " The change since the first period is withheld while an endpoint is understated."
            : string.Empty;

        return $"{subject}{because}.{withheld}";
    }

    /// <summary>
    /// The disclosure sentence for the revalued periods, or null when there are none. Deliberately its
    /// own sentence: a revaluation is a real movement and an understatement is a missing one, so one
    /// sentence serving both would make opposites read alike.
    /// </summary>
    /// <remarks>
    /// A property revaluation is only ever an estimate change — never an acquisition or a disposal
    /// (issue #214 V10) — so the sentence names the kind of estimate and nothing else.
    /// </remarks>
    internal static string? RevaluedNote(IReadOnlyList<string> revaluedLabels, bool anyAccount, bool anyProperty)
    {
        if (revaluedLabels.Count == 0)
            return null;

        var subject = revaluedLabels.Count == 1
            ? $"{revaluedLabels[0]} steps"
            : $"{revaluedLabels.Count} periods step";

        return $"{subject} because {MemberKind(anyAccount, anyProperty)} estimate took effect — a real movement, not a correction.";
    }

    /// <summary>
    /// The chart points, straight from the response — no interpolation, no scaling, no smoothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A partial or revalued point is plotted and marked, never nulled: the component skips a null
    /// value, so nulling one would move the first or last point and make the delta span a window its
    /// own suffix misdescribes.
    /// </para>
    /// <para>
    /// A null history is a failed CALL, and it produces no series at all. There is no fallback: a
    /// plausible-looking line assembled client-side is the defect issue #88 removed.
    /// </para>
    /// </remarks>
    internal static List<OdsLinePoint> BuildSeries(NetWorthHistory? history)
    {
        if (history is null || history.Points.Count == 0)
            return [];

        var series = new List<OdsLinePoint>(history.Points.Count);
        foreach (var point in history.Points)
        {
            series.Add(new OdsLinePoint(
                PointLabel(point.Date, history.Interval),
                point.NetWorth,
                KindOf(point, history.PropertiesIncluded)));
        }

        return series;
    }

    /// <summary>
    /// Understated beats revalued when a point is somehow both: "this figure is missing an account" is
    /// the stronger caveat, and it is the one that withholds the delta. Reporting the weaker of the two
    /// would let an understated endpoint keep a delta it cannot support.
    /// </summary>
    /// <remarks>
    /// A property counts exactly as an account does, through the gate (issue #215 §3.3). An UNVALUED
    /// property never marks a point: that would be true of every point for a never-estimated plot and
    /// would withhold the delta forever (issue #214 D1).
    /// </remarks>
    internal static OdsLinePointKind KindOf(NetWorthHistoryPoint point, bool included) =>
        IsUnderstated(point, included) ? OdsLinePointKind.Partial
        : IsRevaluedByAccount(point) || IsRevaluedByProperty(point, included) ? OdsLinePointKind.Revalued
        : OdsLinePointKind.Normal;

    /// <summary>A member had no rate at this point, so its figure is understated.</summary>
    internal static bool IsUnderstated(NetWorthHistoryPoint point, bool included) =>
        point.UnconvertedAccountCount + IncludedPropertyCounts(point, included).Unconverted > 0;

    internal static bool IsRevaluedByAccount(NetWorthHistoryPoint point) => point.RevaluedAccountCount > 0;

    internal static bool IsRevaluedByProperty(NetWorthHistoryPoint point, bool included) =>
        IncludedPropertyCounts(point, included).Revalued > 0;

    /// <summary>Some member contributed a converted figure to this point — the delta's endpoint test.</summary>
    internal static bool Contributes(NetWorthHistoryPoint point, bool included) =>
        point.ContributingAccountCount + IncludedPropertyCounts(point, included).Contributing > 0;

    /// <summary>The chart's accessible name: what it plots, at what resolution, over what span.</summary>
    /// <param name="accountsOnly">As for <see cref="ChartCaption"/>: a history received with the flag false.</param>
    /// <param name="propertiesContribute">Properties contribute to the latest point.</param>
    internal static string ChartAriaLabel(
        int pointCount, string? firstLabel, string? lastLabel, NetWorthInterval interval,
        bool accountsOnly, bool propertiesContribute)
    {
        if (pointCount == 0 || string.IsNullOrEmpty(firstLabel) || string.IsNullOrEmpty(lastLabel))
            return accountsOnly ? "Net worth over time, accounts only" : "Net worth over time";

        var suffix = accountsOnly ? ", accounts only"
            : propertiesContribute ? ", including property estimates"
            : string.Empty;

        return $"Net worth over time, {pointCount} {IntervalWord(interval)} "
            + $"point{(pointCount == 1 ? "" : "s")} from {firstLabel} to {lastLabel}{suffix}";
    }

}
