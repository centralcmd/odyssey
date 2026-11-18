using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>A window status in the UI: the label carries the meaning; the icon and tone only repeat it.</summary>
public sealed record WeatherWindowStatusInfo(string Label, string Icon, OdsChipTone Tone);

/// <summary>One editor row: a stable key for <c>@key</c>, the dates, and the end kept while "No end date" is ticked.</summary>
public sealed class WeatherWindowRow
{
    public string Key { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public bool Open { get; set; }

    /// <summary>The typed end, restored when "No end date" is unticked.</summary>
    public DateOnly? KeptTo { get; set; }
}

/// <summary>
/// History windows in the client (issue #437; design system · <c>Weather.jsx</c> <c>WX_WINDOW_STATUS</c>,
/// <c>wxWindowMessage</c>, <c>wxParseWindowKey</c>, and <c>weather-data.js</c> <c>wxWindowsShrink</c>). The rules
/// themselves are <see cref="WeatherHistoryWindowRules"/> in <c>Odyssey.Dtos</c> — the server's function — and only
/// the wording is the client's.
/// </summary>
public static class WeatherHistoryWindows
{
    /// <summary>The one status registry. Existing <see cref="OdsChipTone"/> members only.</summary>
    public static WeatherWindowStatusInfo StatusOf(WeatherHistoryWindowStatus status) => status switch
    {
        WeatherHistoryWindowStatus.Upcoming => new("Upcoming", "event", OdsChipTone.Default),
        WeatherHistoryWindowStatus.Collecting => new("Collecting", "sync", OdsChipTone.Info),
        WeatherHistoryWindowStatus.Backfilling => new("Backfilling", "history", OdsChipTone.Pending),
        WeatherHistoryWindowStatus.Complete => new("Complete", "check_circle", OdsChipTone.Outline),
        WeatherHistoryWindowStatus.Unavailable => new("Unavailable", "block", OdsChipTone.Warning),
        _ => Paused,
    };

    /// <summary>The point cannot make progress: every window reads this.</summary>
    public static readonly WeatherWindowStatusInfo Paused = new("Paused", "pause_circle", OdsChipTone.Default);

    /// <summary>A range in words — "to", never a dash, so a screen reader says it.</summary>
    public static string Range(DateOnly from, DateOnly? to) =>
        to is { } end ? $"{WeatherVisuals.Date(from)} to {WeatherVisuals.Date(end)}" : $"From {WeatherVisuals.Date(from)}, ongoing";

    /// <summary>The line under a window's range on the record.</summary>
    public static string? Progress(ExistingWeatherHistoryWindow w) => w.Status switch
    {
        WeatherHistoryWindowStatus.Backfilling or WeatherHistoryWindowStatus.Collecting when w.FetchableDays is null => $"{w.CoveredDays} days checked",
        WeatherHistoryWindowStatus.Backfilling => $"{w.CoveredDays} of {w.FetchableDays} days checked",
        WeatherHistoryWindowStatus.Collecting => $"{w.CoveredDays} of {w.FetchableDays} days so far",
        WeatherHistoryWindowStatus.Unavailable => "Before this point moved — not fetched.",
        WeatherHistoryWindowStatus.Upcoming => $"Collection starts after {WeatherVisuals.Date(w.From)}.",
        _ => w.FetchableDays is { } n ? $"{n} {(n == 1 ? "day" : "days")}" : null,
    };

    /// <summary>The History tile's value: one ongoing window reads as before, so a home point looks unchanged.</summary>
    public static string TileValue(WeatherHistoryWindowSummary s) =>
        s.Count == 1 && s.HasOpenEnd && s.EarliestFrom is { } from ? $"From {WeatherVisuals.Date(from)}"
        : s.Count == 0 ? "No windows"
        : $"{s.Count} {(s.Count == 1 ? "window" : "windows")}";

    /// <summary>The span shown in the tile's foot, when there is more than one ongoing window to describe.</summary>
    public static string? TileSpan(WeatherHistoryWindowSummary s) =>
        s.Count == 0 || (s.Count == 1 && s.HasOpenEnd) || s.EarliestFrom is not { } from
            ? null
            : $"{WeatherVisuals.Date(from)} to {(s.LatestTo is { } to ? WeatherVisuals.Date(to) : "ongoing")}";

    /// <summary>The latest date the editor offers: today + <see cref="WeatherRules.HistoryWindowMaxDaysAhead"/>.</summary>
    public static DateOnly MaxDate(DateOnly today) => WeatherHistoryWindowRules.MaxDate(today);

    /// <summary>
    /// The rules on the editor's rows. "To missing" is a client-only form rule — on the wire an empty end means
    /// ongoing — so such a row is checked here and handed to the shared rules with its start as its end.
    /// </summary>
    public static IReadOnlyList<WindowRuleViolation> Validate(IReadOnlyList<WeatherWindowRow> rows, DateOnly today, out IReadOnlyList<int> toMissing)
    {
        var missing = new List<int>();
        var dtos = new List<WeatherHistoryWindowDto>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.Open && row.To is null && row.From is not null)
            {
                missing.Add(i);
                dtos.Add(new WeatherHistoryWindowDto { From = row.From, To = row.From });
            }
            else
            {
                dtos.Add(new WeatherHistoryWindowDto { From = row.From, To = row.Open ? null : row.To });
            }
        }

        toMissing = missing;
        return WeatherHistoryWindowRules.Validate(dtos, today, requireAny: true);
    }

    /// <summary>The client's words for a rule. Row numbers are DOM positions.</summary>
    public static string Message(WindowRuleViolation v, DateOnly today) => v.Rule switch
    {
        WindowRule.FromMissing => "Enter a start date.",
        WindowRule.ToBeforeFrom => "The end must be on or after the start.",
        WindowRule.OutOfRange => $"Choose a date between {WeatherVisuals.Date(WeatherRules.HistoryFloor)} and {WeatherVisuals.Date(MaxDate(today))}.",
        WindowRule.Overlap => $"This window overlaps window {(v.Other ?? 0) + 1}.",
        WindowRule.OpenEndNotLast => $"Only the latest window can have no end date. Add an end date here, or remove window {(v.Other ?? 0) + 1}, which starts later.",
        WindowRule.Empty => EmptyMessage,
        _ => "There are more history windows than there are days they could cover.",
    };

    public const string ToMissingMessage = "Enter an end date, or tick No end date.";

    public const string EmptyMessage = "Add a history window, or turn historical data off.";

    /// <summary>
    /// A server error key (<c>HistoryWindows[i].From</c>/<c>.To</c>/<c>[i]</c>/<c>HistoryWindows</c>) as a row index
    /// and field. Untrusted input: matched case-insensitively; anything that does not parse, or names a row that does
    /// not exist, falls back to the list. Never throws.
    /// </summary>
    public static (int Index, WindowField Field) ParseKey(string? key, int rowCount)
    {
        const string prefix = "HistoryWindows";
        if (key is null || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return (-1, WindowField.List);
        }

        var rest = key[prefix.Length..];
        if (rest.Length == 0 || rest[0] != '[')
        {
            return (-1, WindowField.List);
        }

        var close = rest.IndexOf(']');
        if (close is < 2 or > 6 || !int.TryParse(rest.AsSpan(1, close - 1), System.Globalization.NumberStyles.None, null, out var index)
            || index >= rowCount)
        {
            return (-1, WindowField.List);
        }

        var tail = rest[(close + 1)..];
        return tail.Length == 0 ? (index, WindowField.Row)
            : tail.Equals(".From", StringComparison.OrdinalIgnoreCase) ? (index, WindowField.From)
            : tail.Equals(".To", StringComparison.OrdinalIgnoreCase) ? (index, WindowField.To)
            : (-1, WindowField.List);
    }

    /// <summary>
    /// Does the edited set leave out a day the loaded set had? Incomplete rows are ignored. Drives the "days already
    /// stored are kept" note while editing (issue #437 §3.2).
    /// </summary>
    public static bool Shrinks(IReadOnlyList<ExistingWeatherHistoryWindow> loaded, IReadOnlyList<WeatherWindowRow> edited)
    {
        var far = DateOnly.MaxValue;
        var ranges = edited
            .Where(r => r.From is not null && (r.Open || r.To is not null))
            .Select(r => (From: r.From!.Value, To: r.Open ? far : r.To!.Value))
            .Where(r => r.To >= r.From)
            .OrderBy(r => r.From)
            .ToList();
        var merged = new List<(DateOnly From, DateOnly To)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && (merged[^1].To == far || r.From.DayNumber <= merged[^1].To.DayNumber + 1))
            {
                if (r.To > merged[^1].To)
                {
                    merged[^1] = (merged[^1].From, r.To);
                }
            }
            else
            {
                merged.Add(r);
            }
        }

        return loaded.Any(w => !merged.Any(m => m.From <= w.From && m.To >= (w.To ?? far)));
    }

    public static List<WeatherWindowRow> RowsFrom(IEnumerable<ExistingWeatherHistoryWindow> windows) =>
        [.. windows.OrderBy(w => w.From).Select(w => new WeatherWindowRow { From = w.From, To = w.To, Open = w.To is null, KeptTo = w.To })];

    public static List<WeatherHistoryWindowDto> ToRequest(IEnumerable<WeatherWindowRow> rows) =>
        [.. rows.Select(r => new WeatherHistoryWindowDto { From = r.From, To = r.Open ? null : r.To })];
}

/// <summary>One row's errors: a field's own, and a row-level one shown under the legend and tied to From.</summary>
public sealed record WeatherWindowRowErrors(string? From = null, string? To = null, string? Row = null);
