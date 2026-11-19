using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Interop;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The calendar export writer's byte cap (issue #516 AC 3; PR #524 tester finding 2): past the cap it throws
/// <see cref="IcsExportSizeExceededException"/> and never writes the component that would cross it, on every scope.
/// The API tier only sees the aborted connection; this pins the writer's own contract.
/// </summary>
public class CalendarIcsExportByteCapTests
{
    private const long Cap = 1024 * 1024;

    public static TheoryData<string> Scopes() => new() { "calendar", "event", "pattern", "aggregate" };

    [Theory]
    [MemberData(nameof(Scopes))]
    public async Task Past_the_cap_the_writer_throws_and_writes_nothing_beyond_it(string kind)
    {
        await using var context = TestContextFactory.CreateJournal();
        var (calendarId, eventId, patternId) = await SeedAsync(context);
        // One row is ~2 KB, so the single-event scope needs a cap below one row to cross it at all.
        var cap = kind == "event" ? 1024 : Cap;
        var service = new CalendarIcsService(
            context, NullLogger<CalendarIcsService>.Instance,
            new FakeImportExportLimitsLookup { CalendarIcsMaxExportEvents = null, CalendarIcsMaxExportBytes = cap },
            new ClientDateHandlingTests.StubJournalLimits());
        var scope = kind switch
        {
            "calendar" => CalendarIcsExportScope.ForCalendar(calendarId),
            "event" => CalendarIcsExportScope.ForEvent(eventId),
            "pattern" => CalendarIcsExportScope.ForPattern(patternId),
            _ => CalendarIcsExportScope.ForAggregate(new Odyssey.Dtos.Journal.CalendarEventsIcsExportQueryParams
            {
                CalendarIds = [calendarId],
            }),
        };
        using var output = new MemoryStream();
        var ready = false;

        await Assert.ThrowsAsync<IcsExportSizeExceededException>(() =>
            service.ExportStreamingAsync(scope, output, (_, _) => ready = true));

        Assert.True(ready);
        Assert.InRange(output.Length, 1, cap);
        // What did go out ends on a whole component: nothing of the one that would have crossed the cap was written.
        var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("END:VCALENDAR", text);
        if (kind == "event")
        {
            Assert.DoesNotContain("BEGIN:VEVENT", text);
        }
        else
        {
            Assert.EndsWith("END:VEVENT\r\n", text);
        }
    }

    [Fact]
    public async Task Under_the_cap_the_same_calendar_exports_whole()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (calendarId, _, _) = await SeedAsync(context, rows: 10);
        var service = new CalendarIcsService(
            context, NullLogger<CalendarIcsService>.Instance,
            new FakeImportExportLimitsLookup { CalendarIcsMaxExportEvents = null, CalendarIcsMaxExportBytes = Cap },
            new ClientDateHandlingTests.StubJournalLimits());
        using var output = new MemoryStream();

        Assert.True(await service.ExportStreamingAsync(CalendarIcsExportScope.ForCalendar(calendarId), output, (_, _) => { }));

        Assert.EndsWith("END:VCALENDAR\r\n", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    // Each row carries a 2,000-character description, so 600 of them (~1.3 MB) cross a 1 MB cap. The series flattens
    // because one occurrence was edited, so the calendar, pattern and aggregate exports all write row by row.
    private static async Task<(Guid CalendarId, Guid EventId, Guid PatternId)> SeedAsync(OdysseyContext context, int rows = 600)
    {
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var calendar = new Calendar { Name = "Bulky" };
        context.Calendars.Add(calendar);
        var start = new DateTime(2031, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var pattern = new RecurrencePattern
        {
            CalendarId = calendar.CalendarId, Calendar = calendar, Title = "Bulky", Description = new string('x', 2000),
            StartDateTime = start, EndDateTime = start.AddHours(1), Frequency = RecurrenceFrequency.Daily, Interval = 1,
            OccurrenceCount = rows, CreatedAt = now, UpdatedAt = now,
        };
        context.RecurrencePatterns.Add(pattern);
        CalendarEvent? first = null;
        for (var i = 0; i < rows; i++)
        {
            var row = new CalendarEvent
            {
                CalendarId = calendar.CalendarId, Calendar = calendar, Title = i == 0 ? "Edited" : "Bulky", Description = new string('x', 2000),
                StartDateTime = start.AddDays(i), EndDateTime = start.AddDays(i).AddHours(1), RecurrencePattern = pattern,
                CreatedAt = now, UpdatedAt = now,
            };
            first ??= row;
            context.CalendarEvents.Add(row);
        }

        await context.SaveChangesAsync();
        return (calendar.CalendarId, first!.CalendarEventId, pattern.RecurrencePatternId);
    }
}
