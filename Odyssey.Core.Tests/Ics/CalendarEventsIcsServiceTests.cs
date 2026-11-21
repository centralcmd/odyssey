using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Core.Tests.Ics;

/// <summary>
/// The single-event, series and aggregate <c>.ics</c> exports (issue #340) at the service tier: which VEVENTs each
/// scope writes, when a series collapses to one rule, the aggregate's filters and their validation, and its caps. The
/// endpoints' headers, file names, 404s, claim gates, problem-details code and the aborted stream stay in
/// <c>Odyssey.Api.Tests/CalendarEventsIcsApiTests</c>.
/// </summary>
public class CalendarEventsIcsServiceTests : IcsServiceTestBase
{
    private const int TestMaxExportEvents = 5;

    // ------------------------------------------------------------------ single event (AC 1)

    [Fact]
    public async Task ExportEvent_NonRecurring_IsOneStandaloneVEvent()
    {
        var calendar = await CreateCalendarAsync("Solo");
        var ev = await CreateEventAsync(calendar, "Dentist", new DateTime(2030, 3, 1, 9, 0, 0, DateTimeKind.Utc));

        var body = await ExportAsync(CalendarIcsExportScope.ForEvent(ev));

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Dentist", body);
        Assert.DoesNotContain("RRULE:", body);
    }

    // Exporting one dated occurrence that belongs to a series: the single-event export does not know or care about
    // RecurrencePatternId and always emits a standalone VEVENT for that one row (the occurrence-vs-series choice lives
    // client-side, in ExportEventScopeDialog — the series path is the pattern export instead).
    [Fact]
    public async Task ExportEvent_OccurrenceOfSeries_IsOneStandaloneVEvent()
    {
        var calendar = await CreateCalendarAsync("OccurrenceExport");
        var pattern = await CreatePatternAsync(calendar, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 4,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);

        var body = await ExportAsync(CalendarIcsExportScope.ForEvent(await FirstGeneratedEventAsync(pattern)));

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Standup", body);
        Assert.DoesNotContain("RRULE:", body);
    }

    // ------------------------------------------------------------------ series (AC 2, 19)

    [Fact]
    public async Task ExportPattern_IntactSeries_EmitsSingleRRule_UnderAFileNamedForIt()
    {
        var calendar = await CreateCalendarAsync("Series");
        var pattern = await CreatePatternAsync(calendar, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 5,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);
        string? name = null;

        var body = await ExportAsync(CalendarIcsExportScope.ForPattern(pattern), (fileName, _) => name = fileName);

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.Contains("RRULE:", body);
        Assert.Matches(@"^\d{8}_Standup\.ics$", name!);
    }

    [Fact]
    public async Task ExportPattern_ClampedSeries_FlattensToStandaloneVEvents()
    {
        var calendar = await CreateCalendarAsync("Clamped");
        var pattern = await CreatePatternAsync(calendar, "Month end", new DateTime(2030, 1, 31, 9, 0, 0, DateTimeKind.Utc), 4,
            RecurrenceFrequency.Monthly, dayOfMonth: 31);

        var body = await ExportAsync(CalendarIcsExportScope.ForPattern(pattern));

        Assert.Equal(4, Count(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    // Security review (PR #345 F1): a degenerate rule whose requested day never exists (Feb 30 every year) used to make
    // GenerateRfcLiteral's strict-RFC projection climb years without ever hitting its own step guard, eventually
    // throwing out of CanExportAsRule — and because the unfiltered aggregate export runs it over every pattern in every
    // calendar, one such rule anywhere broke the export for every caller. The pattern is buildable through the service
    // (no DayOfMonth/MonthOfYear existence cross-check), so this needs no direct DB manipulation.
    [Fact]
    public async Task A_degenerate_yearly_rule_flattens_in_the_aggregate_and_the_series_export_instead_of_throwing()
    {
        var calendar = await CreateCalendarAsync("Degenerate");
        var pattern = await CreatePatternAsync(calendar, "Never happens", new DateTime(2030, 2, 28, 9, 0, 0, DateTimeKind.Utc), 3,
            RecurrenceFrequency.Yearly, dayOfMonth: 30);

        var aggregate = await ExportAsync(CalendarIcsExportScope.ForAggregate(new()));
        var series = await ExportAsync(CalendarIcsExportScope.ForPattern(pattern));

        Assert.DoesNotContain("RRULE:", aggregate);
        Assert.True(Count(aggregate, "BEGIN:VEVENT") > 0);
        Assert.DoesNotContain("RRULE:", series);
    }

    // ------------------------------------------------------------------ aggregate: no filter (AC 3, 12)

    [Fact]
    public async Task ExportAggregate_NoFilter_IncludesEventsFromEveryCalendar_AndCollapsesIntactSeries()
    {
        var calendarA = await CreateCalendarAsync("Work");
        var calendarB = await CreateCalendarAsync("Home");
        await CreateEventAsync(calendarA, "Solo A", new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        await CreateEventAsync(calendarB, "Solo B", new DateTime(2030, 2, 2, 9, 0, 0, DateTimeKind.Utc));
        await CreatePatternAsync(calendarA, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 4,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new()));

        // 2 standalone + 1 collapsed RRULE series = 3 VEVENTs.
        Assert.Equal(3, Count(body, "BEGIN:VEVENT"));
        Assert.Contains("RRULE:", body);
    }

    [Fact]
    public async Task ExportAggregate_ZeroMatches_IsAnEmptyValidCalendar_UnderTheFixedName()
    {
        string? name = null;

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new()), (fileName, _) => name = fileName);

        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VEVENT", body);
        Assert.Matches(@"^\d{8}_calendar-events\.ics$", name!);
    }

    [Fact]
    public async Task ExportAggregate_EachVEvent_CarriesSourceCalendarCategory()
    {
        var calendar = await CreateCalendarAsync("Categorized");
        await CreateEventAsync(calendar, "Tagged", new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        Assert.Contains("CATEGORIES:Categorized", await ExportAsync(CalendarIcsExportScope.ForAggregate(new())));
    }

    [Fact]
    public async Task ExportAggregate_DuplicateExternalUidAcrossCalendars_EmitsDistinctUids()
    {
        var calendarA = await CreateCalendarAsync("ImportA");
        var calendarB = await CreateCalendarAsync("ImportB");
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:shared-uid\r\n"
            + "DTSTART:20300101T090000Z\r\nDTEND:20300101T100000Z\r\nSUMMARY:Shared\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await ImportAsync(calendarA, ics);
        await ImportAsync(calendarB, ics);

        var uids = (await ExportAsync(CalendarIcsExportScope.ForAggregate(new())))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("UID:", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, uids.Count);
        Assert.Equal(2, uids.Distinct().Count());
        Assert.DoesNotContain(uids, u => u == "UID:shared-uid");
    }

    // ------------------------------------------------------------------ aggregate: cross-calendar (AC 4, 5)

    [Fact]
    public async Task ExportAggregate_CalendarFilterNoDate_MatchesWholeCalendarExport_WhenNothingMoved()
    {
        var calendar = await CreateCalendarAsync("Equivalence");
        await CreatePatternAsync(calendar, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 4,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);
        await CreateEventAsync(calendar, "Solo", new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var whole = await ExportAsync(CalendarIcsExportScope.ForCalendar(calendar));
        var aggregate = await ExportAsync(CalendarIcsExportScope.ForAggregate(new() { CalendarIds = [calendar] }));

        Assert.Equal(Count(whole, "BEGIN:VEVENT"), Count(aggregate, "BEGIN:VEVENT"));
        Assert.Equal(Count(whole, "RRULE:"), Count(aggregate, "RRULE:"));
    }

    [Fact]
    public async Task ExportAggregate_OccurrenceMovedOutOfFilteredCalendar_FlattensAndExcludesMovedRow()
    {
        var calendarA = await CreateCalendarAsync("MoveOutA");
        var calendarB = await CreateCalendarAsync("MoveOutB");
        var pattern = await CreatePatternAsync(calendarA, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 3,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);
        await MoveEventToCalendarAsync(await FirstGeneratedEventAsync(pattern), calendarB, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc));

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new() { CalendarIds = [calendarA] }));

        // Row-count mismatch (2 of 3 fetched) forces the flattened fallback; the moved row (now in B) is excluded.
        Assert.Equal(2, Count(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    [Fact]
    public async Task ExportAggregate_OccurrenceMovedIntoFilteredCalendar_IncludesItAsStandaloneVEvent()
    {
        var calendarA = await CreateCalendarAsync("MoveInA");
        var calendarB = await CreateCalendarAsync("MoveInB");
        var pattern = await CreatePatternAsync(calendarB, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 3,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);
        await MoveEventToCalendarAsync(await FirstGeneratedEventAsync(pattern), calendarA, "Standup",
            new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc));

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new() { CalendarIds = [calendarA] }));

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    [Fact]
    public async Task ExportAggregate_EntirePatternRelocated_StaysFlat()
    {
        var calendarA = await CreateCalendarAsync("RelocateA");
        var calendarB = await CreateCalendarAsync("RelocateB");
        var pattern = await CreatePatternAsync(calendarA, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 3,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);

        // Reassign every generated row to calendar B, individually, preserving each row's own Start/End.
        foreach (var row in await SeriesRowsAsync(pattern))
        {
            await MoveEventToCalendarAsync(row.CalendarEventId, calendarB, "Standup", row.StartDateTime, row.EndDateTime);
        }

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new() { CalendarIds = [calendarB] }));

        // Each moved row is an override (issue #519 §3 component 4), and the aggregate stays flat for a series with any
        // exception (§8.3): three standalone events, each attributed to B.
        Assert.Equal(3, Count(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
        Assert.Equal(3, Count(body, "CATEGORIES:RelocateB"));
    }

    [Fact]
    public async Task ExportAggregate_PatternSplitAcrossCalendars_FlattensDespiteCanExportAsRulePassing()
    {
        var calendarA = await CreateCalendarAsync("SplitA");
        var calendarB = await CreateCalendarAsync("SplitB");
        var pattern = await CreatePatternAsync(calendarA, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 4,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);
        var first = (await SeriesRowsAsync(pattern))[0];

        // Move only the first row to B; the rest stay in A. All 4 still individually pass CanExportAsRule's field
        // comparison — only the uniformity guard catches the split.
        await MoveEventToCalendarAsync(first.CalendarEventId, calendarB, "Standup", first.StartDateTime, first.StartDateTime.AddMinutes(30));

        // No calendar filter: both calendars' rows are fetched, so the whole (still-linked) group is considered together
        // and must flatten because it disagrees on CalendarId.
        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new()));

        Assert.Equal(4, Count(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    // ------------------------------------------------------------------ aggregate: date/search filter (AC 6)

    [Fact]
    public async Task ExportAggregate_DateRangeFilter_NeverCollapsesEvenAnIntactSeries()
    {
        var calendar = await CreateCalendarAsync("Windowed");
        await CreatePatternAsync(calendar, "Standup", new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc), 4,
            RecurrenceFrequency.Weekly, DaysOfWeekFlags.Monday);

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new()
        {
            From = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2030, 1, 31, 0, 0, 0, DateTimeKind.Utc),
        }));

        Assert.Equal(4, Count(body, "BEGIN:VEVENT"));
        Assert.DoesNotContain("RRULE:", body);
    }

    // The aggregate export's date filter has the list's overlap (not start-only) semantics: an event that starts before
    // From but is still in progress is included, which a start-only mutant would miss.
    [Fact]
    public async Task ExportAggregate_DateFilter_IncludesEventOverlappingWindowStart()
    {
        var calendar = await CreateCalendarAsync("Overlap");
        var overnight = await CreateEventAsync(calendar, "Overnight shift", new DateTime(2030, 8, 4, 22, 0, 0, DateTimeKind.Utc));
        await MoveEventToCalendarAsync(overnight, calendar, "Overnight shift",
            new DateTime(2030, 8, 4, 22, 0, 0, DateTimeKind.Utc), new DateTime(2030, 8, 5, 6, 0, 0, DateTimeKind.Utc));

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new()
        {
            From = new DateTime(2030, 8, 5, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2030, 8, 6, 0, 0, 0, DateTimeKind.Utc),
        }));

        Assert.Contains("SUMMARY:Overnight shift", body);
    }

    [Fact]
    public async Task ExportAggregate_SearchFilter_MatchesTitleOnly()
    {
        var calendar = await CreateCalendarAsync("Search");
        await CreateEventAsync(calendar, "Team Standup", new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        await CreateEventAsync(calendar, "Dentist", new DateTime(2030, 2, 2, 9, 0, 0, DateTimeKind.Utc));
        // Negative case: "standup" appears in Location, not Title — must NOT match (the aggregate export's search is
        // Title-only, unlike the Calendar page's own client-side live filter).
        await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Create(new NewCalendarEvent
        {
            CalendarId = calendar,
            Title = "Team sync",
            Location = "Standup room",
            StartDateTime = new DateTime(2030, 2, 3, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 2, 3, 10, 0, 0, DateTimeKind.Utc),
        }, ActorUserId));

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new() { Search = "standup" }));

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Team Standup", body);
        Assert.DoesNotContain("SUMMARY:Team sync", body);
    }

    // The bulk-export dialog applies calendarIds + date range + search together, so the conjunction itself (AND, not
    // OR) is pinned: one filter silently overriding another would pass every single-filter test.
    [Fact]
    public async Task ExportAggregate_CombinedFilters_AppliesAllConjunctively()
    {
        var calendarA = await CreateCalendarAsync("CombinedA");
        var calendarB = await CreateCalendarAsync("CombinedB");
        await CreateEventAsync(calendarA, "Standup", new DateTime(2030, 3, 10, 9, 0, 0, DateTimeKind.Utc)); // matches all three
        await CreateEventAsync(calendarA, "Standup", new DateTime(2030, 4, 10, 9, 0, 0, DateTimeKind.Utc)); // wrong date
        await CreateEventAsync(calendarB, "Standup", new DateTime(2030, 3, 11, 9, 0, 0, DateTimeKind.Utc)); // wrong calendar
        await CreateEventAsync(calendarA, "Dentist", new DateTime(2030, 3, 12, 9, 0, 0, DateTimeKind.Utc)); // wrong search

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new()
        {
            CalendarIds = [calendarA],
            Search = "standup",
            From = new DateTime(2030, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2030, 3, 31, 0, 0, 0, DateTimeKind.Utc),
        }));

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
    }

    // ------------------------------------------------------------------ aggregate: validation (AC 7)

    [Fact]
    public async Task ExportAggregate_OnlyFromSet_ToBeforeFrom_AndASpanOver92Days_AreRefused()
    {
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var query in new CalendarEventsIcsExportQueryParams[]
        {
            new() { From = from },
            new() { From = new DateTime(2030, 2, 1, 0, 0, 0, DateTimeKind.Utc), To = from },
            new() { From = from, To = from.AddDays(93) },
        })
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => ExportAsync(CalendarIcsExportScope.ForAggregate(query)));
        }
    }

    // Boundary-pass companions to the refusals above: only a value exactly AT the boundary that must still succeed can
    // catch a `>` loosened to `>=` (or `<` to `<=`).
    [Fact]
    public async Task ExportAggregate_ToEqualsFrom_AndASpanOfExactly92Days_AreAccepted()
    {
        var calendar = await CreateCalendarAsync("ExactSpan");
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(92);
        await CreateEventAsync(calendar, "Edge event", to.AddHours(-1));

        await ExportAsync(CalendarIcsExportScope.ForAggregate(new() { From = from, To = from }));
        Assert.Contains("SUMMARY:Edge event", await ExportAsync(CalendarIcsExportScope.ForAggregate(new() { From = from, To = to })));
    }

    // ------------------------------------------------------------------ aggregate: caps (AC 8, 9; issue #516 AC 4)

    [Fact]
    public async Task ExportAggregate_FilteredMatchExceedsMaxVEvents_IsRefusedWithTheCalendarCode()
    {
        Limits.CalendarIcsMaxExportEvents = TestMaxExportEvents;
        var calendar = await CreateCalendarAsync("TooManyFiltered");
        await SeedStandaloneRowsAsync(calendar, TestMaxExportEvents + 1, "Match");

        var refused = await Assert.ThrowsAsync<ExportLimitExceededException>(() =>
            ExportAsync(CalendarIcsExportScope.ForAggregate(new() { Search = "Match" })));

        Assert.Equal(ExportLimitExceededException.CalendarEventsCode, refused.Code);
    }

    // Boundary-pass companion: exactly the cap succeeds (matched rows and output VEVENTs are 1:1 on the filtered path).
    [Fact]
    public async Task ExportAggregate_FilteredMatchExactlyMaxVEvents_IsExported()
    {
        Limits.CalendarIcsMaxExportEvents = TestMaxExportEvents;
        var calendar = await CreateCalendarAsync("ExactlyMaxFiltered");
        await SeedStandaloneRowsAsync(calendar, TestMaxExportEvents, "Match");

        var body = await ExportAsync(CalendarIcsExportScope.ForAggregate(new() { Search = "Match" }));

        Assert.Equal(TestMaxExportEvents, Count(body, "BEGIN:VEVENT"));
    }

    [Fact]
    public async Task ExportAggregate_NoFilterRawRowsExceedFetchCap_IsRefused()
    {
        var calendar = await CreateCalendarAsync("TooManyRaw");
        await SeedStandaloneRowsAsync(calendar, SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows + 1, "Row");

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => ExportAsync(CalendarIcsExportScope.ForAggregate(new())));

        Assert.Contains("would need to fetch", refused.Message);
    }

    // Boundary-pass companion: exactly the shipped fetch cap in raw rows does NOT trip it, and with the event cap
    // unlimited since issue #516 the export plans every row.
    [Fact]
    public async Task ExportAggregate_NoFilterRawRowsExactlyFetchCap_PassesFetchCheck()
    {
        var calendar = await CreateCalendarAsync("ExactlyFetchCap");
        await SeedStandaloneRowsAsync(calendar, SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows, "Row");
        var rows = -1;

        await ExportAsync(CalendarIcsExportScope.ForAggregate(new()), (_, count) => rows = count);

        Assert.Equal(SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows, rows);
    }

    [Fact]
    public async Task ExportAggregate_NoFilterPostCollapseCountExceedsOutputCap_IsRefusedWithTheCalendarCode()
    {
        Limits.CalendarIcsMaxExportEvents = TestMaxExportEvents;
        var calendar = await CreateCalendarAsync("TooManyOutput");
        // Under the fetch cap but over the output cap — all standalone, so nothing collapses and the raw count IS the
        // eventual VEVENT count.
        await SeedStandaloneRowsAsync(calendar, TestMaxExportEvents + 1, "Row");

        var refused = await Assert.ThrowsAsync<ExportLimitExceededException>(() => ExportAsync(CalendarIcsExportScope.ForAggregate(new())));

        Assert.Equal(ExportLimitExceededException.CalendarEventsCode, refused.Code);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> CreateCalendarAsync(string name) =>
        (await InScopeAsync(sp => sp.GetRequiredService<CalendarService>().Create(new NewCalendar { Name = name, Color = "#0369A1" }, ActorUserId)))
        .CalendarId;

    private async Task<Guid> CreateEventAsync(Guid calendarId, string title, DateTime start) =>
        (await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Create(new NewCalendarEvent
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = start.AddHours(1),
        }, ActorUserId))).CalendarEventId;

    private async Task<Guid> CreatePatternAsync(
        Guid calendarId, string title, DateTime start, int occurrenceCount, RecurrenceFrequency frequency,
        DaysOfWeekFlags? daysOfWeek = null, int? dayOfMonth = null) =>
        (await InScopeAsync(sp => sp.GetRequiredService<RecurrencePatternService>().Create(new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = start.AddMinutes(30),
            Frequency = frequency,
            DaysOfWeek = daysOfWeek,
            DayOfMonth = dayOfMonth,
            MonthOfYear = frequency == RecurrenceFrequency.Yearly ? start.Month : null,
            OccurrenceCount = occurrenceCount,
        }, ActorUserId))).RecurrencePatternId;

    private Task<List<ContextCalendarEvent>> SeriesRowsAsync(Guid patternId) =>
        WithContextAsync(db => db.CalendarEvents.AsNoTracking().Where(e => e.RecurrencePatternId == patternId).OrderBy(e => e.StartDateTime).ToListAsync());

    private async Task<Guid> FirstGeneratedEventAsync(Guid patternId) => (await SeriesRowsAsync(patternId))[0].CalendarEventId;

    private async Task MoveEventToCalendarAsync(Guid eventId, Guid newCalendarId, string title, DateTime start, DateTime end) =>
        Assert.NotNull(await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Update(eventId, new NewCalendarEvent
        {
            CalendarId = newCalendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = end,
        }, ActorUserId)));

    // Standalone rows straight through the context: the cap tests need thousands of them.
    private Task SeedStandaloneRowsAsync(Guid calendarId, int count, string titlePrefix) => WithContextAsync(async db =>
    {
        var now = DateTime.UtcNow;
        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        db.CalendarEvents.AddRange(Enumerable.Range(0, count).Select(i => new ContextCalendarEvent
        {
            CalendarId = calendarId,
            Title = $"{titlePrefix} {i}",
            StartDateTime = start.AddMinutes(i),
            EndDateTime = start.AddMinutes(i).AddMinutes(30),
            CreatedByUserId = ActorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        }));
        await db.SaveChangesAsync();
    });

    private async Task ImportAsync(Guid calendarId, string ics)
    {
        await using var stream = Utf8(ics);
        await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ImportAsync(calendarId, stream, stream.Length, "text/calendar", ActorUserId));
    }

    private async Task<string> ExportAsync(CalendarIcsExportScope scope, Action<string, int>? onReady = null)
    {
        var output = new MemoryStream();
        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ExportStreamingAsync(
            scope, output, onReady ?? ((_, _) => { }), userId: ActorUserId)));
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
