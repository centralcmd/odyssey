using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

// CS8524 is the "someone cast an out-of-range integer to the enum" case, which is what a discard arm
// would swallow. It is disabled here rather than answered with a `_ =>` arm, because the arm would
// also silence CS8509 — the check that actually matters: the build runs at zero warnings, so a
// NetWorthInterval member added without a case here has to fail the build. An out-of-range cast is
// kept out by [EnumDataType] at the HTTP boundary, and a direct caller passing one gets a
// SwitchExpressionException, which fails closed.
#pragma warning disable CS8524


/// <summary>
/// The query string of <c>GET /api/accounts/net-worth-history</c> (issue #90 §7), bound with
/// <c>[FromQuery]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>sealed class</c> rather than a <c>record</c>, following the list-query convention: these are
/// query-string binding models, not form DTOs.
/// </para>
/// <para>
/// It lives in <c>Odyssey.Dtos</c>, which has zero project references and is reachable from the WASM
/// client, so the client <b>shares</b> the point caps rather than holding a copy of them. That is the
/// pattern CLAUDE.md blesses, not the client-side copy of a server cap it forbids — the forbidden
/// thing is a second declaration that can drift, and there is only one here.
/// </para>
/// <para>
/// The window and cap rules are not data annotations because <b>no single property carries the
/// bound</b>: the point count is derived from <c>From</c>, <c>To</c> and <c>Interval</c> together, and
/// an attribute can only see one property. None of the decorative-ceiling failure mode CLAUDE.md's
/// rule guards against is possible here — there is no attribute the check could duplicate.
/// </para>
/// </remarks>
public sealed class NetWorthHistoryQuery
{
    /// <summary>The cap for <see cref="NetWorthInterval.Monthly"/>, <c>Quarterly</c> and <c>Yearly</c>.</summary>
    public const int MaxPoints = 120;

    /// <summary>The cap for <see cref="NetWorthInterval.Daily"/>.</summary>
    public const int MaxDailyPoints = 31;

    /// <summary>The cap for <see cref="NetWorthInterval.Weekly"/>.</summary>
    public const int MaxWeeklyPoints = 53;

    /// <summary>Points in the default window — <c>to</c> minus 23 intervals, so 24 inclusive.</summary>
    public const int DefaultPoints = 24;

    public const NetWorthInterval DefaultInterval = NetWorthInterval.Monthly;

    /// <summary>The currency to convert into. Defaults to NOK; validated against the currency table.</summary>
    [StringLength(3)]
    public string? MainCurrency { get; set; }

    [EnumDataType(typeof(NetWorthInterval))]
    public NetWorthInterval? Interval { get; set; }

    /// <summary>Window start; absent resolves to <see cref="DefaultPoints"/> periods back from <see cref="To"/>.</summary>
    public DateOnly? From { get; set; }

    /// <summary>Window end; absent resolves to today (UTC). Must not be in the future.</summary>
    public DateOnly? To { get; set; }

    public NetWorthInterval EffectiveInterval => Interval ?? DefaultInterval;

    /// <summary>
    /// The point cap for an interval. An exhaustive <c>switch</c> expression with <b>no default
    /// arm</b>: the build runs at zero warnings, so adding a <see cref="NetWorthInterval"/> member
    /// without capping it is CS8509 and fails the build. A flat constant list plus a <c>default</c>
    /// would let a new interval ship uncapped and unnoticed.
    /// </summary>
    public static int PointCapFor(NetWorthInterval interval) => interval switch
    {
        NetWorthInterval.Daily => MaxDailyPoints,
        NetWorthInterval.Weekly => MaxWeeklyPoints,
        NetWorthInterval.Monthly => MaxPoints,
        NetWorthInterval.Quarterly => MaxPoints,
        NetWorthInterval.Yearly => MaxPoints,
    };

    /// <summary>
    /// The window the request resolves to, before any clamp against the accounts: <see cref="To"/> or
    /// today, and <see cref="From"/> snapped outward to its period start, or the default span.
    /// </summary>
    /// <remarks>
    /// The arithmetic <b>saturates</b> at <see cref="DateOnly.MinValue"/>/<see cref="DateOnly.MaxValue"/>
    /// rather than throwing: <c>?to=0001-01-01</c> is a valid request, and an
    /// <c>ArgumentOutOfRangeException</c> from stepping back 23 months from it would be a <c>500</c>
    /// on well-formed input.
    /// </remarks>
    public (DateOnly From, DateOnly To) ResolveWindow(DateOnly today)
    {
        var to = To ?? today;
        var from = From ?? NetWorthPeriods.AddSaturating(to, EffectiveInterval, -(DefaultPoints - 1));
        return (NetWorthPeriods.StartOfPeriod(from, EffectiveInterval), to);
    }

    /// <summary>
    /// The range and cap rules, as <c>(field, message)</c> pairs for <c>ModelState</c> — so they answer
    /// with an <c>errors</c> dictionary, the same shape <c>[ApiController]</c> model validation
    /// produces for a malformed date or an unbindable interval. (The currency check is a database
    /// lookup, so it throws and answers with a flat <c>detail</c> instead; §11 keeps the two apart.)
    /// </summary>
    public IReadOnlyList<(string Field, string Message)> Validate(DateOnly today)
    {
        var errors = new List<(string, string)>();
        var to = To ?? today;

        if (To is { } requested && requested > today)
        {
            errors.Add((nameof(To),
                $"'to' must not be in the future; today is {today:yyyy-MM-dd} (UTC)."));
        }

        // Only a caller-supplied 'from' can invert the window: the default saturates backwards and
        // the snap moves it earlier. A window that inverts LATER, against the earliest account's
        // open date, is not this — the caller's input was valid, so that is a 200 with no points
        // and WindowBeforeFirstAccount (issue #90 V3b).
        if (From is { } from && from > to)
        {
            errors.Add((nameof(From), "'from' must not be after 'to'."));
        }

        if (errors.Count == 0)
        {
            var window = ResolveWindow(today);
            var cap = PointCapFor(EffectiveInterval);
            var points = NetWorthPeriods.CountPeriods(window.From, window.To, EffectiveInterval);
            if (points > cap)
            {
                errors.Add((nameof(Interval),
                    $"The window covers {points} {EffectiveInterval} points; the maximum for that "
                    + $"interval is {cap}. Narrow the window or use a coarser interval."));
            }
        }

        return errors;
    }
}

#pragma warning restore CS8524
