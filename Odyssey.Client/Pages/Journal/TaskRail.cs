using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// The task list card's rail: the deadline as a signed whole-day count, with the edges of the range
/// spelled out instead of computed. A bare "0" reads as "no deadline", so today and tomorrow are
/// words; past 90 days a raw day count stops being parseable, so it rolls to months. A completed task
/// shows a tick — it has no countdown left to run.
/// </summary>
/// <remarks>
/// <see cref="Tone"/> drives colour only; <see cref="AccessibleText"/> is the same fact in words, so
/// nothing in the rail is conveyed by colour or by an abbreviation a screen reader would mangle.
/// </remarks>
public readonly record struct TaskRail
{
    /// <summary>Past this many days a day count stops being readable and the rail counts months.</summary>
    private const int MonthRollover = 90;

    /// <summary>Within this many days the deadline is "soon" — the same window the deadline chip uses.</summary>
    private const int SoonWindowDays = 3;

    private const int DaysPerMonth = 30;

    /// <summary>The em-dash rail of a task with no deadline.</summary>
    public static readonly TaskRail Undated = new() { Dash = true, AccessibleText = "No deadline" };

    /// <summary>overdue · soon · done · muted — or null for the resting tone.</summary>
    public string? Tone { get; private init; }

    public bool Dash { get; private init; }

    public bool Tick { get; private init; }

    /// <summary>"today" / "tmrw" — rendered uppercase; never read aloud (see <see cref="AccessibleText"/>).</summary>
    public string? Word { get; private init; }

    /// <summary>The minus sign is U+2212, not a hyphen — the same glyph negative money uses.</summary>
    public string? Sign { get; private init; }

    public int? Count { get; private init; }

    public string? Unit { get; private init; }

    /// <summary>The rail in words, for assistive technology.</summary>
    public string AccessibleText { get; private init; }

    private static DateOnly TodayLocal => DateOnly.FromDateTime(DateTime.Now);

    public static TaskRail For(JournalTaskSummary task) => For(task.Status, task.Deadline);

    public static TaskRail For(JournalTaskStatus status, DateOnly? deadline)
    {
        if (status == JournalTaskStatus.Done)
        {
            return new TaskRail { Tone = "done", Tick = true, Unit = "done", AccessibleText = "Done" };
        }

        if (status == JournalTaskStatus.Archived)
        {
            return new TaskRail { Tone = "muted", Tick = true, Unit = "archived", AccessibleText = "Archived" };
        }

        if (deadline is not { } due)
        {
            return Undated;
        }

        var n = due.DayNumber - TodayLocal.DayNumber;
        var tone = n < 0 ? "overdue" : n <= SoonWindowDays ? "soon" : null;

        if (n == 0)
        {
            return new TaskRail { Tone = tone, Word = "today", AccessibleText = "Due today" };
        }

        if (n == 1)
        {
            return new TaskRail { Tone = tone, Word = "tmrw", AccessibleText = "Due tomorrow" };
        }

        var abs = Math.Abs(n);
        var sign = n < 0 ? "−" : "+";
        if (abs > MonthRollover)
        {
            var months = (int)Math.Round(abs / (double)DaysPerMonth, MidpointRounding.AwayFromZero);
            return new TaskRail
            {
                Tone = tone,
                Sign = sign,
                Count = months,
                Unit = "months",
                AccessibleText = n < 0 ? $"{months} months overdue" : $"Due in {months} months",
            };
        }

        var unit = abs == 1 ? "day" : "days";
        return new TaskRail
        {
            Tone = tone,
            Sign = sign,
            Count = abs,
            Unit = unit,
            AccessibleText = n < 0 ? $"{abs} {unit} overdue" : $"Due in {abs} {unit}",
        };
    }
}
