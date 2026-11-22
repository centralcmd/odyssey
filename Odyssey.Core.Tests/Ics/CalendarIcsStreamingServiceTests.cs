using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Journal;
using Odyssey.Core.Locations;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Core.Tests.Ics;

/// <summary>
/// Calendar <c>.ics</c> export and import round-trip rules from issue #516 (and #519 where it superseded them) at the
/// service tier: moved occurrences, stamps, UID matching across calendars, place detail, cancellation and removals,
/// and the occurrence budget. The endpoints' throttling, headers, problem-details shapes, aborted streams and upload
/// spool stay in <c>Odyssey.Api.Tests/CalendarIcsStreamingApiTests</c>; the real-engine behaviour is in
/// <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class CalendarIcsStreamingServiceTests : IcsServiceTestBase
{
    private static readonly LocationClaims WithPlaces = new() { LocationsRead = true, LocationsCreate = true };

    // ------------------------------------------------------------------ AC 12: the moved-occurrence rule

    [Fact]
    public async Task An_occurrence_moved_to_another_calendar_is_an_exdate_here_and_a_standalone_event_there()
    {
        var a = await CreateCalendarAsync("A");
        var b = await CreateCalendarAsync("B");
        var series = await CreateSeriesAsync(a, "Moving", count: 3);
        var movedId = await WithContextAsync(async db =>
        {
            var first = await db.CalendarEvents.Where(e => e.RecurrencePatternId == series).OrderBy(e => e.StartDateTime).FirstAsync();
            first.CalendarId = b;
            await db.SaveChangesAsync();
            return first.CalendarEventId;
        });
        var movedUid = $"{movedId}@odyssey.local";

        var aBody = await ExportAsync(CalendarIcsExportScope.ForCalendar(a));
        var bBody = await ExportAsync(CalendarIcsExportScope.ForCalendar(b));
        var seriesBody = await ExportAsync(CalendarIcsExportScope.ForPattern(series));

        // Issue #519 AC 17: the series keeps its rule in its own calendar with the moved key as an EXDATE, the moved row
        // is a standalone event under its own synthetic UID in B, and the series export carries it as a RECURRENCE-ID.
        Assert.Equal(1, Count(aBody, "RRULE:"));
        Assert.Equal(1, Count(aBody, "BEGIN:VEVENT"));
        Assert.Contains("EXDATE:20300101T090000", aBody);
        Assert.DoesNotContain(movedUid, aBody);
        Assert.Equal(1, Count(bBody, "BEGIN:VEVENT"));
        Assert.Contains(movedUid, bBody);
        Assert.DoesNotContain("RRULE:", bBody);
        Assert.Equal(1, Count(seriesBody, "RRULE:"));
        Assert.Equal(2, Count(seriesBody, "BEGIN:VEVENT"));
        Assert.Contains("RECURRENCE-ID:20300101T090000", seriesBody);
    }

    [Fact]
    public async Task An_intact_series_still_collapses_to_one_rule_in_its_own_calendar()
    {
        var a = await CreateCalendarAsync("Intact");
        await CreateSeriesAsync(a, "Intact", count: 4);

        var body = await ExportAsync(CalendarIcsExportScope.ForCalendar(a));

        Assert.Equal(1, Count(body, "BEGIN:VEVENT"));
        Assert.Equal(1, Count(body, "RRULE:"));
    }

    // ------------------------------------------------------------------ AC 14: STATUS and stamps

    [Fact]
    public async Task Every_exported_vevent_carries_status_and_its_stamps()
    {
        var calendar = await CreateCalendarAsync("Stamps");
        await CreateSeriesAsync(calendar, "Rule", count: 2);
        await CreateEventAsync(calendar, "Single");
        var created = new DateTime(2029, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var updated = new DateTime(2029, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        await WithContextAsync(async db =>
        {
            foreach (var row in db.CalendarEvents.Where(e => e.CalendarId == calendar))
            {
                (row.CreatedAt, row.UpdatedAt) = (created, updated);
            }

            foreach (var pattern in db.RecurrencePatterns.Where(p => p.CalendarId == calendar))
            {
                (pattern.CreatedAt, pattern.UpdatedAt) = (created, updated);
            }

            await db.SaveChangesAsync();
        });

        var body = await ExportAsync(CalendarIcsExportScope.ForCalendar(calendar));

        Assert.Equal(2, Count(body, "BEGIN:VEVENT"));
        Assert.Equal(2, Count(body, "STATUS:CONFIRMED"));
        Assert.Equal(2, Count(body, "CREATED:20290304T050607Z"));
        Assert.Equal(2, Count(body, "LAST-MODIFIED:20290405T060708Z"));
        Assert.Equal(2, Count(body, "DTSTAMP:"));
    }

    // ------------------------------------------------------------------ the plan: summaries and the 404 rule

    [Fact]
    public async Task Summaries_match_their_exports_and_the_planned_row_count()
    {
        var calendar = await CreateCalendarAsync("Counts");
        var series = await CreateSeriesAsync(calendar, "Standup", count: 3);
        var one = await CreateEventAsync(calendar, "One");
        await CreateEventAsync(calendar, "Two");

        foreach (var (scope, events, rules, occurrences) in new[]
        {
            (CalendarIcsExportScope.ForEvent(one), 1, 0, 1),
            (CalendarIcsExportScope.ForCalendar(calendar), 3, 1, 5),
            (CalendarIcsExportScope.ForPattern(series), 1, 1, 3),
            (CalendarIcsExportScope.ForAggregate(new()), 3, 1, 5),
            (CalendarIcsExportScope.ForAggregate(new() { Search = "One" }), 1, 0, 1),
        })
        {
            var planned = (await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().SummarizeExportAsync(scope)))!;
            var rows = -1;
            var body = await ExportAsync(scope, (_, count) => rows = count);

            Assert.Equal((events, rules, occurrences), (planned.EventCount, planned.SeriesCount, planned.OccurrenceCount));
            Assert.Equal(planned.EventCount, Count(body, "BEGIN:VEVENT"));
            Assert.Equal(planned.SeriesCount, Count(body, "RRULE:"));
            Assert.Equal(planned.EventCount, rows);
        }
    }

    [Fact]
    public async Task Summaries_are_null_exactly_where_their_exports_find_nothing()
    {
        var unknown = Guid.NewGuid();

        foreach (var scope in new[] { CalendarIcsExportScope.ForCalendar(unknown), CalendarIcsExportScope.ForEvent(unknown), CalendarIcsExportScope.ForPattern(unknown) })
        {
            var named = false;
            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().SummarizeExportAsync(scope)));
            Assert.False(await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ExportStreamingAsync(
                scope, new MemoryStream(), (_, _) => named = true)));
            Assert.False(named);
        }
    }

    [Fact]
    public async Task Count_cap_refuses_each_multi_event_export_and_summary_with_the_calendar_code_and_unlimited_admits_them()
    {
        var calendar = await CreateCalendarAsync("Capped");
        var series = await CreateSeriesAsync(calendar, "Six", count: 6);
        // An edited occurrence is a RECURRENCE-ID component since issue #519, so every one of the three exports below
        // plans more than one VEVENT.
        await WithContextAsync(async db =>
        {
            var first = await db.CalendarEvents.Where(e => e.RecurrencePatternId == series).OrderBy(e => e.StartDateTime).FirstAsync();
            first.Title = "Edited";
            await db.SaveChangesAsync();
        });
        var single = await CreateEventAsync(calendar, "Single");
        var capped = new[]
        {
            CalendarIcsExportScope.ForCalendar(calendar), CalendarIcsExportScope.ForPattern(series), CalendarIcsExportScope.ForAggregate(new()),
        };

        Limits.CalendarIcsMaxExportEvents = 1;
        foreach (var scope in capped)
        {
            var summary = await Assert.ThrowsAsync<ExportLimitExceededException>(() =>
                InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().SummarizeExportAsync(scope)));
            var export = await Assert.ThrowsAsync<ExportLimitExceededException>(() => ExportAsync(scope));
            Assert.Equal(ExportLimitExceededException.CalendarEventsCode, summary.Code);
            Assert.Equal(ExportLimitExceededException.CalendarEventsCode, export.Code);
        }

        // The single-event export plans exactly one VEVENT, and the cap's floor is 1, so no cap refuses it.
        Assert.Equal(1, Count(await ExportAsync(CalendarIcsExportScope.ForEvent(single)), "BEGIN:VEVENT"));

        Limits.CalendarIcsMaxExportEvents = null;
        foreach (var scope in capped)
        {
            Assert.Contains("BEGIN:VEVENT", await ExportAsync(scope));
        }
    }

    // ------------------------------------------------------------------ AC 10, 11: UIDs on import

    [Fact]
    public async Task An_aggregate_export_imported_back_keeps_the_stored_external_uid()
    {
        var calendar = await CreateCalendarAsync("Google");
        var id = await SeedRowAsync(calendar, "From Google", externalUid: "abc@google.com");

        var file = await ExportAsync(CalendarIcsExportScope.ForAggregate(new()));
        Assert.Contains($"{id}@odyssey.local", file);
        var result = await ImportAsync(calendar, file);

        Assert.Equal((0, 1), (result.ImportedCount, result.UpdatedCount));
        Assert.Equal("abc@google.com", (await RowAsync(id))!.ExternalUid);
    }

    [Fact]
    public async Task Importing_one_export_into_another_calendar_twice_updates_the_second_time()
    {
        var a = await CreateCalendarAsync("Source");
        var b = await CreateCalendarAsync("Copy");
        await CreateEventAsync(a, "One");
        await CreateEventAsync(a, "Two");
        await CreateSeriesAsync(a, "Rule", count: 3);
        var file = await ExportAsync(CalendarIcsExportScope.ForCalendar(a));

        var first = await ImportAsync(b, file);
        var second = await ImportAsync(b, file);

        Assert.Equal(3, first.ImportedCount);
        Assert.Equal((0, 3), (second.ImportedCount, second.UpdatedCount));
        var rows = await RowsAsync(b);
        Assert.Equal(5, rows.Count);
        Assert.All(rows.Where(e => e.RecurrencePatternId == null), e => Assert.Null(e.ExternalUid));
    }

    [Fact]
    public async Task The_345_F1_chain_cannot_overwrite_a_row_of_the_originating_calendar()
    {
        var a = await CreateCalendarAsync("A");
        var b = await CreateCalendarAsync("B");
        var x = await CreateEventAsync(a, "X");
        var before = (await RowAsync(x))!;

        await ImportAsync(b, await ExportAsync(CalendarIcsExportScope.ForCalendar(a)));
        var fromB = await ExportAsync(CalendarIcsExportScope.ForCalendar(b));
        // B's file names only B's own rows.
        Assert.DoesNotContain($"{x}@odyssey.local", fromB);
        // B's copy is edited before it travels back (tester finding 5): an overwrite of X would now change its title,
        // where an identical copy would have left the title equal either way.
        Assert.Contains("SUMMARY:X\r\n", fromB);
        var back = await ImportAsync(a, fromB.Replace("SUMMARY:X\r\n", "SUMMARY:Edited in B\r\n", StringComparison.Ordinal));

        var after = (await RowAsync(x))!;
        Assert.Equal("X", after.Title);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        var aRows = await RowsAsync(a);
        // B's UID names nothing in A, so rule 4 creates B's copy beside X; it never lands on X.
        Assert.Equal((1, 0), (back.ImportedCount, back.UpdatedCount));
        Assert.Equal(["Edited in B", "X"], aRows.Select(r => r.Title).Order(StringComparer.Ordinal));
        var aIds = aRows.Select(r => $"{r.CalendarEventId}@odyssey.local").ToHashSet();
        Assert.All(aRows, r => Assert.False(r.ExternalUid?.EndsWith("@odyssey.local") ?? false));
        // A copy imported back carries B's synthetic UID as its match key — never one naming a row of A.
        Assert.All(aRows, r => Assert.DoesNotContain(r.ImportSourceUid ?? string.Empty, aIds));
    }

    // ------------------------------------------------------------------ AC 13: place detail

    [Fact]
    public async Task Place_detail_round_trips_without_growing_through_an_importer_without_locations_read()
    {
        var calendar = await CreateCalendarAsync("Places");
        var cafe = await WithContextAsync(async db =>
        {
            var location = new Odyssey.Context.Location { Name = "Café", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Locations.Add(location);
            await db.SaveChangesAsync();
            return location.LocationId;
        });
        var id = await SeedRowAsync(calendar, "Coffee", location: "Back room", locationId: cafe);

        // What a locations.read holder's export carries for the row.
        var holderFile = Vcalendar(Vevent($"{id}@odyssey.local", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z",
            "SUMMARY:Coffee", "LOCATION:Café — Back room", "X-ODYSSEY-LOCATION-DETAIL:Back room"));
        for (var i = 0; i < 3; i++)
        {
            await ImportAsync(calendar, holderFile);
            var row = (await RowAsync(id))!;
            Assert.Equal(("Back room", (Guid?)cafe), (row.Location, row.LocationId));
        }

        // This caller's own export writes the detail alone, and re-imports unchanged.
        var ownFile = await ExportAsync(CalendarIcsExportScope.ForCalendar(calendar));
        Assert.Contains("X-ODYSSEY-LOCATION-DETAIL:Back room", ownFile);
        await ImportAsync(calendar, ownFile);
        Assert.Equal("Back room", (await RowAsync(id))!.Location);
    }

    [Fact]
    public async Task A_file_exported_without_locations_read_never_turns_its_detail_into_a_place()
    {
        var calendar = await CreateCalendarAsync("Holder");
        var file = Vcalendar(Vevent("detail-only@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z",
            "SUMMARY:Coffee", "LOCATION:Back room", "X-ODYSSEY-LOCATION-DETAIL:Back room"));

        var result = await ImportAsync(calendar, file, claims: WithPlaces);

        Assert.Equal(0, result.PlacesCreated);
        Assert.False(await WithContextAsync(db => db.Locations.AnyAsync(l => l.Name == "Back room")));
        var row = Assert.Single(await RowsAsync(calendar));
        Assert.Equal(("Back room", (Guid?)null), (row.Location, row.LocationId));
    }

    // ------------------------------------------------------------------ AC 15: cancellation

    [Fact]
    public async Task A_cancelled_uid_the_calendar_does_not_hold_is_skipped()
    {
        var calendar = await CreateCalendarAsync("Unknown");

        var result = await ImportAsync(calendar, Vcalendar(Cancelled("nobody@example.com", "Gone")));

        Assert.Equal("Event is cancelled.", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task A_matching_cancelled_event_is_kept_unless_removals_are_allowed()
    {
        var calendar = await CreateCalendarAsync("Kept");
        var id = await SeedRowAsync(calendar, "Keep me", externalUid: "keep@example.com");

        var result = await ImportAsync(calendar, Vcalendar(Cancelled("keep@example.com", "Keep me")));

        Assert.Contains("kept, because removing cancelled events was not requested", Assert.Single(result.Skipped).Reason);
        Assert.Equal(0, result.RemovedCount);
        Assert.NotNull(await RowAsync(id));
    }

    [Fact]
    public async Task With_allow_removals_and_calendar_delete_a_cancelled_event_is_removed_and_audited_by_id()
    {
        var calendar = await CreateCalendarAsync("Removed");
        var id = await SeedRowAsync(calendar, "Secret title", externalUid: "gone@example.com");

        var result = await ImportAsync(calendar, Vcalendar(Cancelled("gone@example.com", "Secret title")), allowRemovals: true);

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(["Secret title"], result.RemovedSampleTitles);
        Assert.Null(await RowAsync(id));
        var audit = Assert.Single((await HostAsync()).Logs.Entries,
            e => e.Category == typeof(CalendarIcsService).FullName && e.Message.Contains("removed"));
        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Contains(id.ToString(), audit.Message);
        Assert.Contains(ActorUserId, audit.Message);
        Assert.DoesNotContain("Secret title", audit.Message);
    }

    [Fact]
    public async Task Allow_removals_without_calendar_delete_is_refused_and_nothing_is_applied()
    {
        var calendar = await CreateCalendarAsync("NoDelete");
        var file = Vcalendar(
            Vevent("new@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:New"),
            Cancelled("other@example.com", "Other"));

        await Assert.ThrowsAsync<DomainForbiddenException>(() => ImportAsync(calendar, file, allowRemovals: true, canDelete: false));

        Assert.Empty(await RowsAsync(calendar));
    }

    [Fact]
    public async Task A_cancelled_series_is_removed_with_delete_semantics()
    {
        // A fixed clock, so which occurrences are past is decided by the test and never by when it runs.
        var now = new DateTime(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        Clock.Now = now;
        var calendar = await CreateCalendarAsync("SeriesGone");
        var series = await CreateSeriesAsync(calendar, "Daily", count: 4, start: now.AddHours(-36));
        var file = Vcalendar(Vevent($"{series}@odyssey.local", "STATUS:CANCELLED", "SUMMARY:Daily",
            "DTSTART:20300101T090000Z", "RRULE:FREQ=DAILY;COUNT=4"));

        var result = await ImportAsync(calendar, file, allowRemovals: true);

        Assert.Equal(1, result.RemovedCount);
        Assert.False(await WithContextAsync(db => db.RecurrencePatterns.AnyAsync(p => p.RecurrencePatternId == series)));
        var left = await RowsAsync(calendar);
        Assert.Equal(2, left.Count); // the two past occurrences, detached and kept
        Assert.All(left, e => Assert.Null(e.RecurrencePatternId));
        Assert.All(left, e => Assert.True(e.StartDateTime < now));
    }

    [Fact]
    public async Task A_cancelled_recurrence_id_component_excludes_that_occurrence_and_keeps_the_series()
    {
        // Issue #519 supersedes this test's Spec A form (S6): a cancelled override is an exclusion, gated like a removal.
        var calendar = await CreateCalendarAsync("Exception");
        var series = await CreateSeriesAsync(calendar, "Weekly", count: 3);
        var file = Vcalendar(Vevent($"{series}@odyssey.local", "STATUS:CANCELLED", "SUMMARY:Weekly",
            "RECURRENCE-ID:20300102T090000Z", "DTSTART:20300102T090000Z"));

        var gated = await ImportAsync(calendar, file);
        Assert.Equal(1, gated.GatedSeriesCount);
        Assert.Equal(CalendarIcsService.GatedReason, Assert.Single(gated.Skipped).Reason);

        var result = await ImportAsync(calendar, file, allowRemovals: true);

        Assert.Equal(1, result.ExclusionsApplied);
        Assert.Empty(result.Skipped);
        Assert.True(await WithContextAsync(db => db.RecurrencePatterns.AnyAsync(p => p.RecurrencePatternId == series)));
        Assert.Equal(2, await WithContextAsync(db => db.CalendarEvents.CountAsync(e => e.RecurrencePatternId == series)));
        Assert.Equal(new DateTime(2030, 1, 2, 9, 0, 0), Assert.Single(await WithContextAsync(db =>
            db.RecurrenceExclusions.Where(x => x.RecurrencePatternId == series).ToListAsync())).OccurrenceKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task For_one_uid_the_last_component_wins_between_live_and_cancelled(bool liveFirst)
    {
        // AC 17c: live then cancelled removes the event; cancelled then live leaves it live.
        var calendar = await CreateCalendarAsync("Order");
        await SeedRowAsync(calendar, "Stored", externalUid: "same@example.com");
        var live = Vevent("same@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Live");
        var cancelled = Cancelled("same@example.com", "Live");

        await ImportAsync(calendar, liveFirst ? Vcalendar(live, cancelled) : Vcalendar(cancelled, live), allowRemovals: true);

        var rows = await RowsAsync(calendar);
        if (liveFirst)
        {
            Assert.Empty(rows);
        }
        else
        {
            Assert.Equal("Live", Assert.Single(rows).Title);
        }
    }

    [Fact]
    public async Task An_event_created_and_cancelled_in_one_batch_counts_as_neither_imported_nor_removed()
    {
        // Architect nit 9: the row never reached the database, so the result must not report a removal.
        var calendar = await CreateCalendarAsync("Net zero");
        var live = Vevent("fresh@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:Fresh");

        var result = await ImportAsync(calendar, Vcalendar(live, Cancelled("fresh@example.com", "Fresh")), allowRemovals: true);

        Assert.Equal((0, 0), (result.ImportedCount, result.RemovedCount));
        Assert.Empty(result.RemovedSampleTitles);
        Assert.Empty(await RowsAsync(calendar));
    }

    [Fact]
    public async Task A_uid_over_255_characters_skips_its_event_and_the_batch_still_commits()
    {
        // Architect finding 4: under strict mode it used to fail the whole batch's save, so the file never completed.
        var calendar = await CreateCalendarAsync("Long uid");

        var result = await ImportAsync(calendar, Vcalendar(
            Standalone(new string('u', 256) + "@example.com", "Too long"), Standalone("fine@example.com", "Fine")));

        Assert.Equal(1, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Equal(CalendarIcsService.UidTooLongReason, group.Reason);
        Assert.Equal(["Too long"], group.SampleTitles);
    }

    // ------------------------------------------------------------------ AC 16b, 17: caps and batches

    [Theory]
    [InlineData("line")]
    [InlineData("component")]
    [InlineData("unterminated")]
    public async Task An_oversized_or_unclosed_component_refuses_the_whole_file_before_anything_is_applied(string shape)
    {
        var calendar = await CreateCalendarAsync("Broken");
        var bad = shape switch
        {
            // One unfolded line of 2 MiB, folded on the wire as RFC 5545 allows.
            "line" => "BEGIN:VEVENT\r\nUID:bad\r\nDESCRIPTION:" + string.Join("\r\n ", Enumerable.Repeat(new string('a', 70), 30_000)) + "\r\nEND:VEVENT\r\n",
            // A 5 MiB VEVENT of ordinary lines.
            "component" => "BEGIN:VEVENT\r\nUID:bad\r\n" + string.Concat(Enumerable.Repeat("X-PAD:" + new string('b', 1000) + "\r\n", 5200)) + "END:VEVENT\r\n",
            _ => "BEGIN:VEVENT\r\nUID:bad\r\nSUMMARY:Never closed\r\n",
        };
        var file = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n"
            + string.Concat(Enumerable.Range(0, CalendarIcsService.ImportBatchSize).Select(i => Standalone($"ok-{i}@example.com", $"Ok {i}")))
            + bad + (shape == "unterminated" ? string.Empty : "END:VCALENDAR\r\n");

        // Issue #519 §3 component 6: pass 1 reads the whole file before any batch, so a structural fault anywhere in it
        // refuses the file and the batches before it are never applied.
        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(calendar, file));

        Assert.Contains("too large or not closed", refused.Message);
        Assert.Empty(await RowsAsync(calendar));
    }

    [Fact]
    public async Task Standalone_events_beyond_the_occurrence_budget_import_under_the_shipped_limits()
    {
        // Architect finding 1: CalendarIcsMaxAggregateOccurrences bounds series expansion only (§3 component 5,
        // Non-Goal 4). The harness serves the shipped defaults.
        var calendar = await CreateCalendarAsync("Many standalone");
        var count = SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences + 1;
        var file = Vcalendar([.. Enumerable.Range(0, count).Select(i => Standalone($"many-{i}@example.com", $"Row {i}"))]);

        var result = await ImportAsync(calendar, file);

        Assert.Equal(count, result.ImportedCount);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Generated_series_occurrences_still_count_against_the_budget_across_the_whole_import()
    {
        Limits.CalendarIcsMaxAggregateOccurrences = 15;
        var calendar = await CreateCalendarAsync("Budgeted");
        var series = Enumerable.Range(0, 2).Select(i => Vevent(
            $"series-{i}@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:Series {i}",
            "RRULE:FREQ=DAILY;COUNT=10"));
        // Twenty standalone events first: none of them spends the budget, so only the second series crosses it.
        var file = Vcalendar([.. Enumerable.Range(0, 20).Select(i => Standalone($"plain-{i}@example.com", $"Plain {i}")), .. series]);

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(calendar, file));

        Assert.Contains("more than 15 series occurrences", refused.Message);
    }

    [Fact]
    public async Task Near_cap_components_flush_a_batch_by_size_before_it_reaches_its_event_count()
    {
        // Security finding 1: a batch is applied once its components reach ImportBatchCharBudget, so 500 near-cap
        // components can never be held at once. Each component here is ~3 Mi characters, so 16 Mi is crossed by the
        // sixth: twelve components commit as two batches of six, which the per-batch log line shows.
        var calendar = await CreateCalendarAsync("Sized");
        var padding = string.Concat(Enumerable.Repeat("X-PAD:" + new string('p', 1018) + "\r\n", 3 * 1024));
        var big = Enumerable.Range(0, 12).Select(i => "BEGIN:VEVENT\r\nUID:big-" + i + "@example.com\r\n"
            + "DTSTART:20300101T090000Z\r\nDTEND:20300101T100000Z\r\nSUMMARY:Big " + i + "\r\n" + padding + "END:VEVENT\r\n");
        var file = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(big) + "END:VCALENDAR\r\n";
        Assert.InRange(padding.Length * 6, CalendarIcsService.ImportBatchCharBudget, int.MaxValue);
        Assert.InRange(padding.Length * 5, 0, CalendarIcsService.ImportBatchCharBudget - 64 * 1024);

        var result = await ImportAsync(calendar, file);

        Assert.Equal(12, result.ImportedCount);
        Assert.Equal(2, (await HostAsync()).Logs.Entries.Count(e =>
            e.Category == typeof(CalendarIcsService).FullName && e.Message.Contains("batch committed")));
    }

    // ------------------------------------------------------------------ helpers

    private static string Standalone(string uid, string title) =>
        Vevent(uid, "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:{title}");

    private static string Cancelled(string uid, string title) =>
        Vevent(uid, "STATUS:CANCELLED", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:{title}");

    private static string Vcalendar(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private static string Vevent(string uid, params string[] lines) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    private async Task<Guid> CreateCalendarAsync(string name) =>
        (await InScopeAsync(sp => sp.GetRequiredService<CalendarService>().Create(new NewCalendar { Name = name, Color = "#0369A1" }, ActorUserId)))
        .CalendarId;

    private async Task<Guid> CreateEventAsync(Guid calendarId, string title)
    {
        var start = new DateTime(2030, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        return (await InScopeAsync(sp => sp.GetRequiredService<CalendarEventService>().Create(new NewCalendarEvent
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = start.AddHours(1),
        }, ActorUserId))).CalendarEventId;
    }

    private async Task<Guid> CreateSeriesAsync(Guid calendarId, string title, int count, DateTime? start = null)
    {
        var first = start ?? new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        return (await InScopeAsync(sp => sp.GetRequiredService<RecurrencePatternService>().Create(new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = first,
            EndDateTime = first.AddHours(1),
            Frequency = RecurrenceFrequency.Daily,
            Interval = 1,
            OccurrenceCount = count,
        }, ActorUserId))).RecurrencePatternId;
    }

    private Task<Guid> SeedRowAsync(Guid calendarId, string title, string? externalUid = null, string? location = null, Guid? locationId = null) =>
        WithContextAsync(async db =>
        {
            var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
            var row = new ContextCalendarEvent
            {
                CalendarId = calendarId,
                Title = title,
                StartDateTime = start,
                EndDateTime = start.AddHours(1),
                ExternalUid = externalUid,
                Location = location,
                LocationId = locationId,
                CreatedByUserId = ActorUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.CalendarEvents.Add(row);
            await db.SaveChangesAsync();
            return row.CalendarEventId;
        });

    private Task<ContextCalendarEvent?> RowAsync(Guid id) =>
        WithContextAsync(db => db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.CalendarEventId == id));

    private Task<List<ContextCalendarEvent>> RowsAsync(Guid calendarId) =>
        WithContextAsync(db => db.CalendarEvents.AsNoTracking().Where(e => e.CalendarId == calendarId).ToListAsync());

    private async Task<IcsImportResult> ImportAsync(
        Guid calendarId, string ics, bool allowRemovals = false, bool canDelete = true, LocationClaims? claims = null)
    {
        await using var stream = Utf8(ics);
        return await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ImportAsync(
            calendarId, stream, stream.Length, "text/calendar", ActorUserId, claims: claims, allowRemovals: allowRemovals, canDelete: canDelete));
    }

    private async Task<string> ExportAsync(CalendarIcsExportScope scope, Action<string, int>? onReady = null)
    {
        var output = new MemoryStream();
        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<CalendarIcsService>().ExportStreamingAsync(
            scope, output, onReady ?? ((_, _) => { }), userId: ActorUserId)));
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
