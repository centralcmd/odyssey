using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// One history window as a request element (issue #436 §5.1): point-local dates, both ends inclusive; a null
/// <see cref="To"/> is an ongoing window. Deliberately carries <b>no</b> validation attributes — every rule, the
/// required <see cref="From"/> included, lives in <see cref="WeatherHistoryWindowRules"/>, so an impossibly large set is
/// refused with one error before any per-element work (AC 24).
/// </summary>
public sealed record WeatherHistoryWindowDto
{
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    /// <summary>Redacted: windows are travel dates (issue #436 §7.3).</summary>
    public override string ToString() => nameof(WeatherHistoryWindowDto) + " { (redacted) }";
}

/// <summary>A window's derived state (issue #436 §5.1). Serialized by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WeatherHistoryWindowStatus>))]
public enum WeatherHistoryWindowStatus
{
    /// <summary>Starts after the last complete day.</summary>
    Upcoming = 1,

    /// <summary>Every fetchable day so far is checked and the window reaches the last complete day.</summary>
    Collecting = 2,

    /// <summary>A fetchable day of the window is not checked yet.</summary>
    Backfilling = 3,

    /// <summary>The window has ended and every fetchable day is checked.</summary>
    Complete = 4,

    /// <summary>The window lies wholly before the current site's first owned date (the point moved).</summary>
    Unavailable = 5,
}

/// <summary>One window on the single-point read (issue #436 §5.1).</summary>
public sealed record ExistingWeatherHistoryWindow
{
    public DateOnly From { get; init; }

    public DateOnly? To { get; init; }

    public WeatherHistoryWindowStatus Status { get; init; }

    /// <summary>Checked days of this window inside the effective range.</summary>
    public int CoveredDays { get; init; }

    /// <summary>Days of this window inside the effective range; null for an ongoing window.</summary>
    public int? FetchableDays { get; init; }

    public override string ToString() => nameof(ExistingWeatherHistoryWindow) + " { (redacted) }";
}

/// <summary>The window summary on every <see cref="ExistingWeatherPoint"/>, list and single read alike.</summary>
public sealed record WeatherHistoryWindowSummary
{
    public int Count { get; init; }

    public DateOnly? EarliestFrom { get; init; }

    /// <summary>The latest end; null when there are no windows or one is ongoing.</summary>
    public DateOnly? LatestTo { get; init; }

    public bool HasOpenEnd { get; init; }

    public static WeatherHistoryWindowSummary From(IReadOnlyCollection<(DateOnly From, DateOnly? To)> windows)
    {
        if (windows.Count == 0)
        {
            return new WeatherHistoryWindowSummary();
        }

        var open = windows.Any(w => w.To is null);
        return new WeatherHistoryWindowSummary
        {
            Count = windows.Count,
            EarliestFrom = windows.Min(w => w.From),
            LatestTo = open ? null : windows.Max(w => w.To),
            HasOpenEnd = open,
        };
    }

    public override string ToString() => nameof(WeatherHistoryWindowSummary) + " { (redacted) }";
}

/// <summary>Which part of a request a window violation is about.</summary>
public enum WindowField
{
    /// <summary>The whole list (empty, impossible).</summary>
    List,

    /// <summary>The window as a whole (overlap, ongoing not latest).</summary>
    Row,

    From,
    To,
}

/// <summary>The rule a window violation breaks. Codes, never prose: each caller words its own message.</summary>
public enum WindowRule
{
    /// <summary>More windows than there are days in the allowed range: necessarily invalid.</summary>
    Impossible,

    /// <summary>History is on and there are no windows.</summary>
    Empty,

    FromMissing,
    ToBeforeFrom,
    OutOfRange,

    /// <summary>Shares at least one day with an earlier-starting window.</summary>
    Overlap,

    /// <summary>An ongoing window is followed by a later-starting one.</summary>
    OpenEndNotLast,
}

/// <summary>
/// One broken rule. <see cref="Index"/> is the element's position in the <b>request</b> order (-1 for the list);
/// <see cref="Other"/> is the request index of the window it conflicts with, where there is one.
/// </summary>
public readonly record struct WindowRuleViolation(int Index, WindowField Field, WindowRule Rule, int? Other = null);

/// <summary>
/// The history-window rules (issue #436 §8) — the <b>one</b> definition, public and pure so the server's model
/// validation and the WASM client name the same function (the <c>ContractPartyRoleMatrix</c> precedent). The caller
/// injects <c>today</c>: the server its UTC date, the client its local date.
/// </summary>
public static class WeatherHistoryWindowRules
{
    /// <summary>How far ahead a window may start or end, in days (a sanity bound, not a product limit).</summary>
    public const int MaxDaysAhead = WeatherRules.HistoryWindowMaxDaysAhead;

    /// <summary>The latest date a window may name, given <paramref name="today"/>.</summary>
    public static DateOnly MaxDate(DateOnly today) => today.AddDays(MaxDaysAhead);

    /// <summary>
    /// The most windows a valid set can hold: non-overlapping whole days in [floor, today + 730]. Not a policy limit —
    /// every larger set is necessarily invalid (issue #436 §7.7).
    /// </summary>
    public static int MaxValidCount(DateOnly today) => MaxDate(today).DayNumber - WeatherRules.HistoryFloor.DayNumber + 1;

    /// <param name="windows">The request's windows, in request order.</param>
    /// <param name="today">The caller's today.</param>
    /// <param name="requireAny">History is on, so an empty set is refused.</param>
    public static IReadOnlyList<WindowRuleViolation> Validate(
        IReadOnlyList<WeatherHistoryWindowDto> windows, DateOnly today, bool requireAny)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var violations = new List<WindowRuleViolation>();

        // Checked first and alone: an impossible set gets one error, never per-element work.
        if (windows.Count > MaxValidCount(today))
        {
            violations.Add(new WindowRuleViolation(-1, WindowField.List, WindowRule.Impossible));
            return violations;
        }

        if (windows.Count == 0)
        {
            if (requireAny)
            {
                violations.Add(new WindowRuleViolation(-1, WindowField.List, WindowRule.Empty));
            }

            return violations;
        }

        var max = MaxDate(today);
        var usable = new List<(int Index, DateOnly From, DateOnly? To)>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var ok = true;
            if (window?.From is not { } from)
            {
                violations.Add(new WindowRuleViolation(i, WindowField.From, WindowRule.FromMissing));
                continue;
            }

            if (from < WeatherRules.HistoryFloor || from > max)
            {
                violations.Add(new WindowRuleViolation(i, WindowField.From, WindowRule.OutOfRange));
                ok = false;
            }

            if (window.To is { } to)
            {
                if (to > max)
                {
                    violations.Add(new WindowRuleViolation(i, WindowField.To, WindowRule.OutOfRange));
                    ok = false;
                }
                else if (to < from)
                {
                    violations.Add(new WindowRuleViolation(i, WindowField.To, WindowRule.ToBeforeFrom));
                    ok = false;
                }
            }

            if (ok)
            {
                usable.Add((i, from, window.To));
            }
        }

        // Sort, then compare neighbours: O(n log n) whatever the count. Ties keep request order.
        usable.Sort((a, b) => a.From != b.From ? a.From.CompareTo(b.From) : a.Index.CompareTo(b.Index));
        if (usable.Count == 0)
        {
            return violations;
        }

        var latest = usable[^1].Index;
        for (var k = 1; k < usable.Count; k++)
        {
            var previous = usable[k - 1];
            var current = usable[k];
            if (previous.To is null)
            {
                violations.Add(new WindowRuleViolation(previous.Index, WindowField.Row, WindowRule.OpenEndNotLast, latest));
            }
            else if (previous.To.Value >= current.From)
            {
                violations.Add(new WindowRuleViolation(current.Index, WindowField.Row, WindowRule.Overlap, previous.Index));
            }
        }

        return violations;
    }

    /// <summary>The server's wording and member key for a violation (the client words its own).</summary>
    public static ValidationResult ToValidationResult(WindowRuleViolation violation, DateOnly today)
    {
        const string list = "HistoryWindows";
        var key = violation.Field switch
        {
            WindowField.List => list,
            WindowField.Row => $"{list}[{violation.Index}]",
            WindowField.From => $"{list}[{violation.Index}].From",
            _ => $"{list}[{violation.Index}].To",
        };
        var message = violation.Rule switch
        {
            WindowRule.Impossible => "There are more history windows than there are days they could cover.",
            WindowRule.Empty => "Add a history window, or turn historical data off.",
            WindowRule.FromMissing => "Enter a start date.",
            WindowRule.ToBeforeFrom => "The end must be on or after the start.",
            WindowRule.OutOfRange =>
                $"Choose a date between {WeatherRules.HistoryFloor:yyyy-MM-dd} and {MaxDate(today):yyyy-MM-dd}.",
            WindowRule.Overlap => "This window overlaps another window.",
            _ => "Only the latest window can have no end date.",
        };
        return new ValidationResult(message, [key]);
    }
}
