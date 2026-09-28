using System.Text.Json.Serialization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>The period presets of the dashboard's net-worth chart settings (Odyssey Design System · Dashboard).</summary>
public enum NetWorthRangePreset
{
    SixMonths,
    TwelveMonths,
    All,
    Custom,
}

/// <summary>
/// The period the dashboard's net-worth chart covers, as the reader chose it. Persisted in the
/// <c>home-page</c> page state, so it sticks across visits. For <see cref="NetWorthRangePreset.Custom"/>
/// either end may be empty: an empty <see cref="From"/> starts at the earliest data, an empty
/// <see cref="To"/> runs to today. Only the three positional members are stored; the derived ones
/// are <see cref="JsonIgnoreAttribute"/>d, since <see cref="Normalized"/> returns another range.
/// </summary>
public sealed record NetWorthRange(NetWorthRangePreset Preset, DateOnly? From = null, DateOnly? To = null)
{
    /// <summary>The design's default: all time.</summary>
    public static readonly NetWorthRange Default = new(NetWorthRangePreset.All);

    /// <summary>The dialog's radio options, in reading order.</summary>
    public static readonly IReadOnlyList<(NetWorthRangePreset Preset, string Label)> Options =
    [
        (NetWorthRangePreset.SixMonths, "Last 6 months"),
        (NetWorthRangePreset.TwelveMonths, "Last 12 months"),
        (NetWorthRangePreset.All, "All time"),
        (NetWorthRangePreset.Custom, "Custom range"),
    ];

    /// <summary>The shortest custom span the dialog accepts — one month, as the design's 31 days.</summary>
    public const int MinimumCustomDays = 31;

    internal const string SpanTooShort = "The range must span at least one month.";

    /// <summary>
    /// The custom range's validation message, or <c>null</c> when it is acceptable. Only a range with
    /// both ends set can be too short; an open end runs as far as All time does.
    /// </summary>
    [JsonIgnore]
    public string? Error => Preset == NetWorthRangePreset.Custom
                            && From is { } from && To is { } to
                            && to.DayNumber - from.DayNumber < MinimumCustomDays
        ? SpanTooShort
        : null;

    /// <summary>The range as stored: the dates are dropped from a preset, which carries none.</summary>
    [JsonIgnore]
    public NetWorthRange Normalized => Preset == NetWorthRangePreset.Custom ? this : new NetWorthRange(Preset);

    /// <summary>
    /// The request for <c>GET /api/accounts/net-worth-history</c>: the window and the interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A preset of N months asks for N months back from today, which the server snaps to the period
    /// start — N + 1 period ends, so the first point is the figure N months ago and the delta spans
    /// the whole range. <c>To</c> is sent only for a custom end: a <c>to</c> built from local time is
    /// tomorrow-in-UTC east of UTC, which the server rejects, so "today" is left to the server.
    /// </para>
    /// <para>
    /// "All time" starts at <paramref name="earliest"/> (the earliest account opening). The server's
    /// point cap applies to the unclamped window, so the interval coarsens — monthly, then quarterly,
    /// then yearly — until the window fits. With no earliest date known the server's default window
    /// stands.
    /// </para>
    /// </remarks>
    public (DateOnly? From, DateOnly? To, NetWorthInterval Interval) ResolveRequest(DateOnly today, DateOnly? earliest)
    {
        DateOnly? from = Preset switch
        {
            NetWorthRangePreset.SixMonths => today.AddMonths(-6),
            NetWorthRangePreset.TwelveMonths => today.AddMonths(-12),
            NetWorthRangePreset.Custom => From ?? earliest,
            _ => earliest,
        };
        var to = Preset == NetWorthRangePreset.Custom ? To : null;

        // An earliest date after the chosen end (or after today) has nothing before it to show.
        if (from is { } f && f > (to ?? today))
            from = null;

        return (from, to, IntervalFor(from, to ?? today));
    }

    /// <summary>
    /// Whether the request starts at the earliest account opening, which only the account list can
    /// supply — so the dashboard must load the list before it asks for the history.
    /// </summary>
    [JsonIgnore]
    public bool NeedsEarliest =>
        Preset == NetWorthRangePreset.All || (Preset == NetWorthRangePreset.Custom && From is null);

    /// <summary>
    /// The earliest opening across every account, archived included: archiving is not a valuation
    /// event (issue #99), so an archived account's history is still part of "all time". Null with no
    /// accounts, which leaves the server's default window.
    /// </summary>
    public static DateOnly? EarliestOpening(IEnumerable<ExistingAccount> accounts) =>
        accounts.Select(a => (DateTime?)a.Opened).Min() is { } opened ? DateOnly.FromDateTime(opened) : null;

    /// <summary>The finest interval whose point count fits the server's cap for the window.</summary>
    internal static NetWorthInterval IntervalFor(DateOnly? from, DateOnly to)
    {
        if (from is not { } start)
            return NetWorthHistoryQuery.DefaultInterval;

        foreach (var interval in new[] { NetWorthInterval.Monthly, NetWorthInterval.Quarterly })
        {
            if (NetWorthPeriods.CountPeriods(start, to, interval) <= NetWorthHistoryQuery.PointCapFor(interval))
                return interval;
        }

        return NetWorthInterval.Yearly;
    }
}

/// <summary>The dashboard's persisted page state (<c>home-page</c>).</summary>
public sealed class HomePageState
{
    public NetWorthRange? NetWorthRange { get; set; }
}
