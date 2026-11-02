using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The one builder per calendar write DTO (issue #383 §5.2). The server's <c>PUT</c> is a full replacement
/// and an omitted <c>locationId</c> clears an event's place, so every client construction of
/// <see cref="NewCalendarEvent"/> and <see cref="NewRecurrencePattern"/> goes through here, with the link as
/// a REQUIRED <see cref="LocationLink"/> argument. A source-lint (<c>LocationWriteBuilderLintTests</c>) fails
/// on either DTO constructed anywhere else in the client.
/// </summary>
public static class CalendarWrites
{
    /// <summary>The single construction of <see cref="NewCalendarEvent"/> in the client.</summary>
    public static NewCalendarEvent Event(
        Guid calendarId, string title, string? description, string? details,
        DateTime start, DateTime end, bool isAllDay, LocationLink link) => new()
    {
        CalendarId = calendarId,
        Title = title,
        Description = description,
        Location = details,
        LocationId = link.Id,
        StartDateTime = start,
        EndDateTime = end,
        IsAllDay = isAllDay,
    };

    /// <summary>
    /// A loaded event re-timed and nothing else — the calendar's drag (a day move) and resize (a new start
    /// and end). The stored place is KEPT: the VM carries no place, and an omitted id would unlink it.
    /// </summary>
    public static NewCalendarEvent Retimed(ExistingCalendarEvent existing, DateTime start, DateTime end) =>
        Event(existing.CalendarId, existing.Title, existing.Description, existing.Location,
            start, end, existing.IsAllDay, LocationLink.Keep(existing.LocationId));

    /// <summary>The single construction of <see cref="NewRecurrencePattern"/> in the client.</summary>
    public static NewRecurrencePattern Pattern(
        Guid calendarId, string title, string? description, string? details,
        DateTime start, DateTime end, bool isAllDay,
        RecurrenceFrequency frequency, int interval, DaysOfWeekFlags? daysOfWeek, int? dayOfMonth, int? monthOfYear,
        DateTime? recurrenceEndDate, int? occurrenceCount, LocationLink link) => new()
    {
        CalendarId = calendarId,
        Title = title,
        Description = description,
        Location = details,
        LocationId = link.Id,
        StartDateTime = start,
        EndDateTime = end,
        IsAllDay = isAllDay,
        Frequency = frequency,
        Interval = interval,
        DaysOfWeek = daysOfWeek,
        DayOfMonth = dayOfMonth,
        MonthOfYear = monthOfYear,
        RecurrenceEndDate = recurrenceEndDate,
        OccurrenceCount = occurrenceCount,
    };
}
