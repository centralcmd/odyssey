namespace Odyssey.Client.Components;

/// <summary>
/// Pure date/coverage math for <see cref="OdsCalendarGrid"/>, split out so it's unit-testable
/// without rendering the component (matches this project's convention of extracting non-UI logic
/// into a plain static class — see <c>Odyssey.Client.Tests</c>).
/// </summary>
public static class CalendarGridMath
{
    private static readonly string[] WeekdayNamesSundayFirst = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>The 42 (6×7) grid dates for the month containing <paramref name="month"/>, backing up
    /// to the week start containing the 1st and running six full weeks forward.</summary>
    public static IReadOnlyList<DateOnly> DaysInGrid(DateOnly month, int weekStartsOn)
    {
        var firstOfMonth = new DateOnly(month.Year, month.Month, 1);
        var gridStart = StartOfWeek(firstOfMonth, weekStartsOn);
        return Enumerable.Range(0, 42).Select(gridStart.AddDays).ToList();
    }

    /// <summary>Weekday header labels in display order for <paramref name="weekStartsOn"/> (0 = Sunday, 1 = Monday).</summary>
    public static IReadOnlyList<string> WeekdayHeaders(int weekStartsOn) =>
        weekStartsOn == 1
            ? [.. WeekdayNamesSundayFirst.Skip(1), WeekdayNamesSundayFirst[0]]
            : WeekdayNamesSundayFirst;

    /// <summary>The Sunday/Monday (per <paramref name="weekStartsOn"/>) that starts the week containing <paramref name="day"/>.</summary>
    public static DateOnly StartOfWeek(DateOnly day, int weekStartsOn)
    {
        var diff = ((int)day.DayOfWeek - weekStartsOn + 7) % 7;
        return day.AddDays(-diff);
    }

    /// <summary>Whether <paramref name="calendarEvent"/> is drawn on <paramref name="day"/>: exact match for a
    /// timed event, or within the exclusive-end [Start, End) span for an all-day event.</summary>
    public static bool CoversDay(CalendarEventVm calendarEvent, DateOnly day)
    {
        if (calendarEvent.IsAllDay)
        {
            var start = DateOnly.FromDateTime(calendarEvent.Start);
            var endExclusive = DateOnly.FromDateTime(calendarEvent.End);
            return day >= start && day < endExclusive;
        }

        return DateOnly.FromDateTime(calendarEvent.Start) == day;
    }

    public static bool IsStartDay(CalendarEventVm calendarEvent, DateOnly day) =>
        DateOnly.FromDateTime(calendarEvent.Start) == day;

    /// <summary>True on the last day an all-day event covers (End is exclusive, so this is End - 1 day).
    /// Always true for a timed event (it only ever covers its single start day).</summary>
    public static bool IsEndDay(CalendarEventVm calendarEvent, DateOnly day)
    {
        if (!calendarEvent.IsAllDay)
        {
            return true;
        }

        var lastDay = DateOnly.FromDateTime(calendarEvent.End).AddDays(-1);
        return day == lastDay;
    }

    /// <summary>An all-day chip's title is shown on its start day, or the first column of any week it
    /// continues into (so a multi-week span still reads as labelled).</summary>
    public static bool ShowsLabel(CalendarEventVm calendarEvent, DateOnly day, int columnIndex) =>
        IsStartDay(calendarEvent, day) || columnIndex == 0;

    /// <summary>The calendar day an end instant belongs to for time-grid rendering: an exact-midnight
    /// end (e.g. 22:00→00:00) belongs to the PREVIOUS day, so a same-evening event ending at midnight
    /// is not misclassified as crossing into a second day. Deliberately distinct from
    /// <see cref="CoversDay"/>, which is pinned by a unit test to start-day-only matching for the
    /// Month view (issue #329 §5/§9).</summary>
    public static DateOnly EffectiveEndDate(DateTime end) =>
        end.TimeOfDay == TimeSpan.Zero ? DateOnly.FromDateTime(end).AddDays(-1) : DateOnly.FromDateTime(end);

    /// <summary>Whether a timed event occupies any part of <paramref name="day"/> — used only by
    /// <c>OdsCalTimeGrid</c> so a cross-midnight event renders clipped in every day it touches. All-day
    /// events use <see cref="CoversDay"/> instead (their lane matches the Month view).</summary>
    public static bool TimeGridOverlapsDay(CalendarEventVm calendarEvent, DateOnly day)
    {
        if (calendarEvent.IsAllDay)
        {
            return CoversDay(calendarEvent, day);
        }

        return DateOnly.FromDateTime(calendarEvent.Start) <= day && EffectiveEndDate(calendarEvent.End) >= day;
    }

    /// <summary>True when a timed event genuinely spans into a later calendar day (so it can't be
    /// dragged/resized in the time-grid). An exact-midnight end stays same-day (see
    /// <see cref="EffectiveEndDate"/>).</summary>
    public static bool CrossesMidnight(CalendarEventVm calendarEvent) =>
        !calendarEvent.IsAllDay && DateOnly.FromDateTime(calendarEvent.Start) != EffectiveEndDate(calendarEvent.End);
}
