using Odyssey.Context;
using CalendarEntity = Odyssey.Context.Calendar;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic calendar demo data (issue #323): three named calendars, a handful of standalone
/// events (including one single-day and one multi-day all-day event), and one bounded recurring
/// series with its occurrences pre-materialized — mirroring what <c>RecurrencePatternService</c>
/// would generate at create time, since the seeder writes rows directly rather than going through
/// the API (matches the direct-entity-insert convention every other generator here follows).
/// </summary>
public static class CalendarGenerator
{
    public sealed record Result(
        IReadOnlyList<CalendarEntity> Calendars,
        IReadOnlyList<CalendarEvent> CalendarEvents,
        IReadOnlyList<RecurrencePattern> RecurrencePatterns);

    public static Guid CalendarIdFor(string name) => DeterministicGuid.From($"calendar::{name}");

    public static Result Generate(DateTime anchor)
    {
        var ownerId = UserId("Owner");
        var userId = UserId("User");

        var calendars = new List<CalendarEntity>
        {
            BuildCalendar("Personal", "Day-to-day personal events", "#0369A1", ownerId, anchor),
            BuildCalendar("Bills", "Recurring and one-off payment due dates", "#F59E0B", ownerId, anchor),
            BuildCalendar("Family", "Shared household events", "#15803D", userId, anchor),
        };

        var events = new List<CalendarEvent>
        {
            BuildEvent("dentist", "Dentist appointment", "Routine check-up.", "BlueCross Clinic",
                CalendarIdFor("Personal"), anchor.AddDays(-5).Date.AddHours(9), anchor.AddDays(-5).Date.AddHours(9).AddMinutes(45),
                isAllDay: false, ownerId),

            BuildEvent("pay-rent", "Pay rent", null, null,
                CalendarIdFor("Bills"), anchor.AddDays(3).Date, anchor.AddDays(3).Date.AddDays(1),
                isAllDay: true, ownerId),

            BuildEvent("family-bbq", "Family BBQ", "Bring the folding chairs.", "Back garden",
                CalendarIdFor("Family"), anchor.AddDays(10).Date.AddHours(12), anchor.AddDays(10).Date.AddHours(15),
                isAllDay: false, userId),

            BuildEvent("long-weekend", "Long weekend trip", "Coastal cottage, booked since spring.", "Whitby, North Yorkshire",
                CalendarIdFor("Personal"), anchor.AddDays(20).Date, anchor.AddDays(23).Date,
                isAllDay: true, ownerId),

            BuildEvent("car-insurance", "Car insurance renewal", "Renews automatically unless cancelled.", null,
                CalendarIdFor("Bills"), anchor.AddDays(-30).Date, anchor.AddDays(-30).Date.AddDays(1),
                isAllDay: true, ownerId),
        };

        var (pattern, generatedEvents) = BuildTeamSyncSeries(ownerId);
        events.AddRange(generatedEvents);

        return new Result(calendars, events, [pattern]);
    }

    private static CalendarEntity BuildCalendar(string name, string description, string color, string userId, DateTime anchor) => new()
    {
        CalendarId = CalendarIdFor(name),
        Name = name,
        Description = description,
        Color = color,
        CreatedByUserId = userId,
        CreatedAt = anchor.AddDays(-60),
        UpdatedAt = anchor.AddDays(-60),
    };

    private static CalendarEvent BuildEvent(
        string key, string title, string? description, string? location,
        Guid calendarId, DateTime start, DateTime end, bool isAllDay, string userId) => new()
    {
        CalendarEventId = DeterministicGuid.From($"calendar-event::{key}"),
        CalendarId = calendarId,
        Title = title,
        Description = description,
        Location = location,
        StartDateTime = start,
        EndDateTime = end,
        IsAllDay = isAllDay,
        CreatedByUserId = userId,
        CreatedAt = start,
        UpdatedAt = start,
    };

    // Weekly, every Monday and Thursday at 10:00-10:30 UTC, starting Monday 2026-06-01, 10 occurrences —
    // hand-enumerated so the pre-materialized CalendarEvent rows are byte-identical to what
    // RecurrenceOccurrenceGenerator would produce for the same rule.
    private static (RecurrencePattern Pattern, List<CalendarEvent> Events) BuildTeamSyncSeries(string userId)
    {
        var patternId = DeterministicGuid.From("recurrence-pattern::team-sync");
        var seriesStart = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var seriesEnd = new DateTime(2026, 6, 1, 10, 30, 0, DateTimeKind.Utc);

        var pattern = new RecurrencePattern
        {
            RecurrencePatternId = patternId,
            CalendarId = CalendarIdFor("Family"),
            Title = "Team sync",
            Description = "Weekly household planning sync.",
            Location = null,
            StartDateTime = seriesStart,
            EndDateTime = seriesEnd,
            IsAllDay = false,
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            DaysOfWeek = DaysOfWeekFlags.Monday | DaysOfWeekFlags.Thursday,
            OccurrenceCount = 10,
            CreatedByUserId = userId,
            CreatedAt = seriesStart,
            UpdatedAt = seriesStart,
        };

        int[] dayOffsets = [0, 3, 7, 10, 14, 17, 21, 24, 28, 31]; // Mon/Thu pairs, week by week, from seriesStart
        var events = new List<CalendarEvent>(dayOffsets.Length);
        for (var i = 0; i < dayOffsets.Length; i++)
        {
            var start = seriesStart.AddDays(dayOffsets[i]);
            events.Add(new CalendarEvent
            {
                CalendarEventId = DeterministicGuid.From($"calendar-event::team-sync#{i}"),
                CalendarId = pattern.CalendarId,
                Title = pattern.Title,
                Description = pattern.Description,
                Location = pattern.Location,
                StartDateTime = start,
                EndDateTime = start.AddMinutes(30),
                IsAllDay = false,
                RecurrencePatternId = patternId,
                CreatedByUserId = userId,
                CreatedAt = start,
                UpdatedAt = start,
            });
        }

        return (pattern, events);
    }

    private static string UserId(string role) => DemoUsers.All.First(user => user.Role == role).Id;
}
