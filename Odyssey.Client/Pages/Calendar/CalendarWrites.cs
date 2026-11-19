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
/// <remarks>
/// Times are WALL-CLOCK plus zone (issue #520 §2 goal 3, #519 §8.1): the server derives the instant. A zoned time is
/// sent unlabelled (<c>Kind = Unspecified</c>, no <c>Z</c> and never an offset, which the server refuses); a floating
/// or all-day time keeps its UTC label and no zone, exactly as before #519, so a floating event saved unchanged stays
/// floating (AC 2).
/// </remarks>
public static class CalendarWrites
{
    /// <summary>The single construction of <see cref="NewCalendarEvent"/> in the client.</summary>
    public static NewCalendarEvent Event(
        Guid calendarId, string title, string? description, string? details,
        DateTime start, DateTime end, bool isAllDay, string? timeZoneId, LocationLink link) => new()
    {
        CalendarId = calendarId,
        Title = title,
        Description = description,
        Location = details,
        LocationId = link.Id,
        StartDateTime = Wire(start, Zone(isAllDay, timeZoneId)),
        EndDateTime = Wire(end, Zone(isAllDay, timeZoneId)),
        IsAllDay = isAllDay,
        TimeZoneId = Zone(isAllDay, timeZoneId),
    };

    /// <summary>
    /// A loaded event re-timed and nothing else — the calendar's drag (a day move) and resize (a new start
    /// and end). The stored place is KEPT: the VM carries no place, and an omitted id would unlink it.
    /// <paramref name="start"/> and <paramref name="end"/> are wall-clock in the event's OWN zone (issue #520 §3 "Drag
    /// and resize"), so its zone travels with them; a floating event keeps none.
    /// </summary>
    public static NewCalendarEvent Retimed(ExistingCalendarEvent existing, DateTime start, DateTime end) =>
        Event(existing.CalendarId, existing.Title, existing.Description, existing.Location,
            start, end, existing.IsAllDay, existing.TimeZoneId, LocationLink.Keep(existing.LocationId));

    /// <summary>The single construction of <see cref="NewRecurrencePattern"/> in the client.</summary>
    public static NewRecurrencePattern Pattern(
        Guid calendarId, string title, string? description, string? details,
        DateTime start, DateTime end, bool isAllDay, string? timeZoneId,
        RecurrenceFrequency frequency, int interval, DaysOfWeekFlags? daysOfWeek, int? dayOfMonth, int? monthOfYear,
        DateTime? recurrenceEndDate, int? occurrenceCount, LocationLink link) => new()
    {
        CalendarId = calendarId,
        Title = title,
        Description = description,
        Location = details,
        LocationId = link.Id,
        StartDateTime = Wire(start, Zone(isAllDay, timeZoneId)),
        EndDateTime = Wire(end, Zone(isAllDay, timeZoneId)),
        IsAllDay = isAllDay,
        TimeZoneId = Zone(isAllDay, timeZoneId),
        Frequency = frequency,
        Interval = interval,
        DaysOfWeek = daysOfWeek,
        DayOfMonth = dayOfMonth,
        MonthOfYear = monthOfYear,
        RecurrenceEndDate = recurrenceEndDate is { } until ? Wire(until, Zone(isAllDay, timeZoneId)) : null,
        OccurrenceCount = occurrenceCount,
    };

    /// <summary>
    /// A series write bound to what the user saw (issue #520 v3 §A, v5 B1): the <see cref="ExistingRecurrencePattern.Version"/>
    /// loaded when the dialog opened, and the drop counts the user confirmed (<c>{0, 0}</c> when no confirm step was shown).
    /// </summary>
    public static NewRecurrencePattern Bound(NewRecurrencePattern pattern, Guid expectedVersion, int confirmedOverrides, int confirmedExclusions) =>
        pattern with
        {
            ExpectedVersion = expectedVersion,
            ConfirmedDrops = new RecurrenceConfirmedDrops { Overrides = confirmedOverrides, Exclusions = confirmedExclusions },
        };

    // An all-day event carries no zone (the server stores it floating).
    private static string? Zone(bool isAllDay, string? timeZoneId) => isAllDay ? null : timeZoneId;

    // A zoned time is its wall-clock, unlabelled; a floating or all-day one is labelled UTC as before #519.
    private static DateTime Wire(DateTime value, string? zone) =>
        DateTime.SpecifyKind(value, zone is null ? DateTimeKind.Utc : DateTimeKind.Unspecified);
}
