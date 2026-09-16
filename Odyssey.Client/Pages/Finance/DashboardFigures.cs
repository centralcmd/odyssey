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
    /// <summary>Two decimals under a generic "$", for an amount whose currency is not the main one.</summary>
    internal static NumberFormatInfo GenericMoneyFormat() => BaseFormat();

    /// <summary>
    /// The main currency's symbol and minor units. <paramref name="currency"/> is the reference-data
    /// row when one was found. A known code with no usable symbol falls back to the CODE, not to "$":
    /// a wrong sigil misreports the denomination, where the code merely looks unpolished.
    /// </summary>
    internal static NumberFormatInfo MoneyFormat(string? currencyCode, ExistingCurrency? currency)
    {
        var format = BaseFormat();
        if (string.IsNullOrWhiteSpace(currencyCode))
            return format;

        if (currency is not null && !string.IsNullOrWhiteSpace(currency.Symbol))
        {
            format.CurrencySymbol = currency.Symbol;
            format.CurrencyDecimalDigits = currency.MinorUnits;
        }
        else
        {
            format.CurrencySymbol = currencyCode;
        }

        return format;
    }

    /// <summary>
    /// A compact axis label, e.g. "$52k" / "kr 52k" / "CHF 640".
    ///
    /// <para>
    /// The separator is the point. A sigil ("$", "€", "£") sits against its number; an ALPHABETIC
    /// symbol does not, and the app's own default main currency is one — NOK's symbol is "kr", so
    /// the unseparated form reads "kr52k", and CHF's symbol is the code itself. The headline figure
    /// has no such problem because <see cref="NumberFormatInfo"/>'s "C" format supplies the spacing;
    /// this label is hand-composed to get the "k" suffix, so it has to supply its own.
    /// </para>
    /// </summary>
    internal static string AxisLabel(decimal value, string symbol)
    {
        var prefix = Prefix(symbol);
        return value >= 1000 || value <= -1000
            ? $"{prefix}{value / 1000:0}k"
            : $"{prefix}{value:0}";
    }

    /// <summary>The advisory on a party the server could not convert. Order matters: FROM the account's currency, TO the main one.</summary>
    internal static string UnconvertedMessage(string accountCurrencyCode, string mainCurrencyCode) =>
        $"No exchange rate from {accountCurrencyCode} to {mainCurrencyCode}, "
        + "so this account counts as 0 towards net worth.";

    /// <summary>
    /// The chart's empty copy. The cause is <b>known</b>, so the copy says which one — never "no data
    /// yet", which tells a reader with a full portfolio that their accounts are empty.
    /// </summary>
    /// <param name="reason">
    /// The server's own discrimination. It is carried on the response precisely because it cannot be
    /// inferred: two of the four causes produce otherwise byte-identical payloads.
    /// </param>
    /// <param name="mainCurrencyCode">Interpolated into the conversion case, which names the currency it failed to reach.</param>
    internal static string ChartEmptyLabel(NetWorthEmptyReason? reason, string? mainCurrencyCode) => reason switch
    {
        NetWorthEmptyReason.NoAccounts => "No accounts to chart yet.",
        NetWorthEmptyReason.NothingConvertible => string.IsNullOrWhiteSpace(mainCurrencyCode)
            ? "Net worth could not be converted for any period."
            : $"Net worth could not be converted to {mainCurrencyCode} for any period.",
        NetWorthEmptyReason.WindowBeforeFirstAccount => "No accounts existed in this period.",
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
    /// Deriving it from anything else is how a financial figure gets mislabelled by one period, which
    /// is the defect this whole feature exists to fix in a smaller shape. The year is shown on the
    /// first point and wherever it changes, so a multi-year series is readable without repeating "'25"
    /// under every tick.
    /// </remarks>
    internal static string PointLabel(DateOnly date, NetWorthInterval interval, DateOnly? previous) =>
        interval switch
        {
            NetWorthInterval.Daily or NetWorthInterval.Weekly =>
                date.ToString("d MMM", CultureInfo.InvariantCulture),
            NetWorthInterval.Yearly => date.ToString("yyyy", CultureInfo.InvariantCulture),
            _ => previous is { } earlier && earlier.Year == date.Year
                ? date.ToString("MMM", CultureInfo.InvariantCulture)
                : date.ToString("MMM ", CultureInfo.InvariantCulture) + "\u2019" + date.ToString("yy", CultureInfo.InvariantCulture),
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
    internal static string ChartCaption(
        int pointCount, string? firstLabel, string? lastLabel, NetWorthInterval interval, string? currencyCode)
    {
        var word = IntervalWord(interval);
        var head = $"{pointCount} {word} point{(pointCount == 1 ? "" : "s")}";
        var span = string.IsNullOrEmpty(firstLabel) || string.IsNullOrEmpty(lastLabel)
            ? null
            : $"{firstLabel} – {lastLabel}";

        return string.Join(" · ", new[] { head, span, currencyCode }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// The disclosure sentence for the understated periods, or null when there are none.
    /// </summary>
    /// <remarks>
    /// Every condition the markers show is also stated here in text, because the markers rest on shape
    /// and stroke and a reader who cannot see the plot at all gets neither. The withheld-delta clause
    /// is added only when an <b>endpoint</b> is understated, which is the only case that withholds it.
    /// </remarks>
    internal static string? UnderstatedNote(
        IReadOnlyList<string> understatedLabels,
        IReadOnlyList<UnconvertedAccount> unconvertedAccounts,
        bool deltaWithheld)
    {
        if (understatedLabels.Count == 0)
            return null;

        var subject = understatedLabels.Count == 1
            ? $"{understatedLabels[0]} is understated"
            : $"{understatedLabels.Count} periods are understated";

        var because = unconvertedAccounts.Count == 1
            ? $" — {unconvertedAccounts[0].Name} ({unconvertedAccounts[0].CurrencyCode}) had no exchange rate"
            : " — an account had no exchange rate";

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
    internal static string? RevaluedNote(IReadOnlyList<string> revaluedLabels)
    {
        if (revaluedLabels.Count == 0)
            return null;

        var subject = revaluedLabels.Count == 1
            ? $"{revaluedLabels[0]} steps"
            : $"{revaluedLabels.Count} periods step";

        return $"{subject} because an account estimate took effect — a real movement, not a correction.";
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
        DateOnly? previous = null;
        foreach (var point in history.Points)
        {
            series.Add(new OdsLinePoint(
                PointLabel(point.Date, history.Interval, previous),
                point.NetWorth,
                KindOf(point)));
            previous = point.Date;
        }

        return series;
    }

    /// <summary>
    /// Understated beats revalued when a point is somehow both: "this figure is missing an account" is
    /// the stronger caveat, and it is the one that withholds the delta. Reporting the weaker of the two
    /// would let an understated endpoint keep a delta it cannot support.
    /// </summary>
    internal static OdsLinePointKind KindOf(NetWorthHistoryPoint point) =>
        point.UnconvertedAccountCount > 0 ? OdsLinePointKind.Partial
        : point.RevaluedAccountCount > 0 ? OdsLinePointKind.Revalued
        : OdsLinePointKind.Normal;

    /// <summary>The chart's accessible name: what it plots, at what resolution, over what span.</summary>
    internal static string ChartAriaLabel(
        int pointCount, string? firstLabel, string? lastLabel, NetWorthInterval interval)
    {
        if (pointCount == 0 || string.IsNullOrEmpty(firstLabel) || string.IsNullOrEmpty(lastLabel))
            return "Net worth over time";

        return $"Net worth over time, {pointCount} {IntervalWord(interval)} "
            + $"point{(pointCount == 1 ? "" : "s")} from {firstLabel} to {lastLabel}";
    }

    private static string Prefix(string symbol) =>
        symbol.Length > 0 && char.IsLetter(symbol[^1]) ? symbol + " " : symbol;

    private static NumberFormatInfo BaseFormat()
    {
        var format = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        format.CurrencySymbol = "$";
        format.CurrencyDecimalDigits = 2;
        format.CurrencyNegativePattern = 1; // "-$n" — leading minus, no parentheses
        return format;
    }
}
