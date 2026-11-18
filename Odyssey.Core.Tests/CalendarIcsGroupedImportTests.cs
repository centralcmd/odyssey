using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Interop;
using Xunit;
using IcalCalendar = Ical.Net.Calendar;

namespace Odyssey.Core.Tests;

/// <summary>
/// The import grouper's bounds (issue #519 §3 component 6, v5 §1, implementation notes 5 and 6), the byte scanner's
/// parity with the splitter, the export's <c>VTIMEZONE</c>, the per-batch audit before a later failure (#516 AC 17b,
/// moved here so the grouping key can be fixed) and the key backfill job (§11).
/// </summary>
public class CalendarIcsGroupedImportTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ The component cap and the spill (AC 15c)

    [Fact]
    public async Task A_file_of_26_byte_components_is_refused_before_any_record_is_kept()
    {
        var file = Calendar(string.Concat(Enumerable.Repeat("BEGIN:VEVENT\r\nEND:VEVENT\r\n", 5000)));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => BuildAsync(file));

        Assert.Equal(IcsImportGrouper.TooManyComponentsCode, ex.Code);
    }

    [Fact]
    public async Task A_file_of_64_byte_components_completes_with_the_spill_within_one_and_a_quarter_times_the_upload()
    {
        var directory = Directory.CreateTempSubdirectory("odyssey-grouper-").FullName;
        try
        {
            // Exactly 64 bytes a component, with the 33-byte envelope and tail adding nothing to the count.
            var components = Enumerable.Range(0, 3000).Select(i => Exactly64($"u{i:D6}")).ToList();
            Assert.All(components, c => Assert.Equal(64, Encoding.UTF8.GetByteCount(c)));
            var file = Calendar(string.Concat(components));
            await using var upload = new MemoryStream(Encoding.UTF8.GetBytes(file));

            // 100 records a run forces 30 spilled runs, and so a merge round below 64 open runs is not needed; 20 a run
            // forces 150 runs and three merge rounds.
            foreach (var perRun in new[] { 100, 20 })
            {
                await using var grouper = await IcsImportGrouper.BuildAsync(upload, directory, null, default, perRun);
                var groups = new List<IReadOnlyList<ComponentRef>>();
                await foreach (var group in grouper.GroupsAsync())
                {
                    groups.Add(group);
                }

                Assert.Equal(3000, groups.Count);
                Assert.Equal(3000, grouper.ComponentCount);
                Assert.InRange(grouper.PeakSpilledBytes, 1, upload.Length + (upload.Length / 4));
                Assert.All(groups, g => Assert.Single(g));
            }

            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_spill_is_deleted_when_an_import_fails_part_way()
    {
        var directory = Directory.CreateTempSubdirectory("odyssey-grouper-").FullName;
        try
        {
            var file = Calendar(string.Concat(Enumerable.Range(0, 300).Select(i => Exactly64($"u{i:D6}"))) + "BEGIN:VEVENT\r\nUID:x\r\n");
            await using var upload = new MemoryStream(Encoding.UTF8.GetBytes(file));

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                IcsImportGrouper.BuildAsync(upload, directory, null, default, recordsPerRun: 10));

            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_tiny_file_always_allows_one_component()
    {
        Assert.Equal(1, IcsImportGrouper.ComponentCap(10));
        await using var grouper = await BuildAsync(Calendar("BEGIN:VEVENT\r\nUID:a\r\nEND:VEVENT\r\n"));
        Assert.Equal(1, grouper.ComponentCount);
    }

    [Fact]
    public async Task A_uid_spelled_in_two_cases_lands_in_one_group_with_the_master_first()
    {
        var file = Calendar(
            "BEGIN:VEVENT\r\nUID:Series@Example.com\r\nRECURRENCE-ID:20300108T090000Z\r\nSUMMARY:Override\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:other\r\nSUMMARY:Other\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:series@example.com\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nEND:VEVENT\r\n");

        await using var grouper = await BuildAsync(file);
        var groups = new List<IReadOnlyList<ComponentRef>>();
        await foreach (var group in grouper.GroupsAsync())
        {
            groups.Add(group);
        }

        var series = Assert.Single(groups, g => g.Count == 2);
        Assert.False(series[0].IsOverride);
        Assert.True(series[1].IsOverride);
        Assert.Contains("SUMMARY:Master", await grouper.ReadComponentAsync(series[0], default));
    }

    [Fact]
    public async Task The_scanner_reads_a_folded_uid_and_reports_each_components_exact_bytes()
    {
        const string first = "BEGIN:VEVENT\r\nUID:fold\r\n ed-uid\r\nSUMMARY:Ünïcödé\r\nEND:VEVENT\r\n";
        const string second = "BEGIN:VEVENT\nUID:lf-only\nEND:VEVENT\n";
        var prefix = "﻿BEGIN:VCALENDAR\r\nBEGIN:VTIMEZONE\r\nTZID:X\r\nEND:VTIMEZONE\r\n";
        var bytes = Encoding.UTF8.GetBytes(prefix + first + second + "END:VCALENDAR\r\n");
        await using var stream = new MemoryStream(bytes);

        var scanned = new List<ScannedEvent>();
        await foreach (var e in new IcsByteScanner(stream).ReadEventsAsync())
        {
            scanned.Add(e);
        }

        Assert.Equal(["folded-uid", "lf-only"], scanned.Select(s => s.Uid));
        Assert.Equal(first, Encoding.UTF8.GetString(bytes, (int)scanned[0].Offset, scanned[0].Length));
        Assert.Equal(second, Encoding.UTF8.GetString(bytes, (int)scanned[1].Offset, scanned[1].Length));
    }

    [Theory]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nBEGIN:VEVENT\r\nEND:VEVENT\r\n")]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\n")]
    public async Task The_scanner_refuses_what_the_splitter_refuses(string body)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => BuildAsync(Calendar(body, close: false)));
        Assert.Equal(IcsComponentSplitter.TooLargeMessage, ex.Message);
    }

    [Fact]
    public async Task A_vtimezone_with_more_than_200_observances_is_refused()
    {
        var observances = string.Concat(Enumerable.Range(0, 201).Select(_ => "BEGIN:STANDARD\r\nEND:STANDARD\r\n"));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            BuildAsync(Calendar("BEGIN:VTIMEZONE\r\nTZID:X\r\n" + observances + "END:VTIMEZONE\r\n")));
    }

    [Fact]
    public void Two_concurrent_imports_cannot_both_reserve_more_than_the_free_space()
    {
        // Large enough that the small reservations other tests in this process hold cannot change the outcome.
        const long upload = 4L * 1024 * 1024 * 1024;
        const long free = 10L * 1024 * 1024 * 1024;
        Assert.Equal(upload + (upload / 2), IcsSpillReservation.Required(upload));
        var first = IcsSpillReservation.Reserve("/tmp", upload, _ => free);

        Assert.Throws<FileStorageFullException>(() => IcsSpillReservation.Reserve("/tmp", upload, _ => free));
        first.Dispose();
        using var again = IcsSpillReservation.Reserve("/tmp", upload, _ => free);
    }

    // ------------------------------------------------------------------ The override cap (AC 15)

    [Fact]
    public async Task A_uid_with_more_than_ten_thousand_overrides_has_the_excess_skipped_and_counted()
    {
        await using var context = TestContextFactory.CreateJournal();
        var calendar = await SeedCalendarAsync(context);
        var overrides = string.Concat(Enumerable.Range(0, CalendarIcsService.MaxOverridesPerSeries + 1).Select(i =>
            $"BEGIN:VEVENT\r\nUID:capped\r\nRECURRENCE-ID:{new DateTime(2030, 1, 1).AddDays(i):yyyyMMdd}T090000\r\n"
            + $"DTSTART:{new DateTime(2030, 1, 1).AddDays(i):yyyyMMdd}T100000\r\nDTEND:{new DateTime(2030, 1, 1).AddDays(i):yyyyMMdd}T110000\r\nSUMMARY:O\r\nEND:VEVENT\r\n"));
        var file = Calendar("BEGIN:VEVENT\r\nUID:capped\r\nDTSTART:20300101T090000\r\nDTEND:20300101T100000\r\nSUMMARY:S\r\n"
            + "RRULE:FREQ=DAILY;COUNT=5\r\nEND:VEVENT\r\n" + overrides);
        var service = Service(context, new FakeImportExportLimitsLookup { CalendarIcsMaxImportEvents = null });

        var result = await ImportAsync(service, calendar, file);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(5, result.OverridesApplied);
        Assert.Equal(1, result.Skipped.Single(g => g.Reason == CalendarIcsService.OverrideCapReason).Count);
        Assert.Equal(CalendarIcsService.MaxOverridesPerSeries - 5,
            result.Skipped.Single(g => g.Reason == CalendarIcsService.OverrideMismatchReason).Count);
    }

    // ------------------------------------------------------------------ #516 AC 17b under hash order

    [Fact]
    public async Task A_removing_import_that_fails_after_its_removal_batch_has_already_audited_it()
    {
        await using var context = TestContextFactory.CreateJournal();
        var calendar = await SeedCalendarAsync(context);
        var removedId = Guid.NewGuid();
        context.CalendarEvents.Add(new Odyssey.Context.CalendarEvent
        {
            CalendarEventId = removedId, CalendarId = calendar, Title = "To remove", ExternalUid = "remove@example.com",
            StartDateTime = new DateTime(2030, 1, 1, 9, 0, 0), EndDateTime = new DateTime(2030, 1, 1, 10, 0, 0),
            CreatedAt = Now, UpdatedAt = Now,
        });
        await context.SaveChangesAsync();

        // With a fixed key the commit order is known: pick 600 series UIDs that all sort after the removal, so the
        // removal is in batch one and the occurrence budget (500) is crossed in batch two.
        var key = new byte[32];
        var removalHash = IcsImportGrouper.HashUid(key, "remove@example.com", 0);
        var later = Enumerable.Range(0, 100_000).Select(i => $"series-{i}@example.com")
            .Where(uid => IcsImportGrouper.HashUid(key, uid, 0) > removalHash).Take(600).ToList();
        var file = Calendar(
            "BEGIN:VEVENT\r\nUID:remove@example.com\r\nSTATUS:CANCELLED\r\nSUMMARY:To remove\r\nEND:VEVENT\r\n"
            + string.Concat(later.Select(uid => $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART:20300101T090000Z\r\nDTEND:20300101T100000Z\r\n"
                + "SUMMARY:S\r\nRRULE:FREQ=DAILY;COUNT=1\r\nEND:VEVENT\r\n")));
        var logs = new ListLogger();
        var service = new CalendarIcsService(
            context, logs, new FakeImportExportLimitsLookup { CalendarIcsMaxImportEvents = null, CalendarIcsMaxAggregateOccurrences = 550 },
            new ClientDateHandlingTests.StubJournalLimits(), new FakeTimeProvider(Now)) { GroupingKey = key };

        var ex = await Assert.ThrowsAsync<CalendarIcsImportPartiallyAppliedException>(() =>
            service.ImportAsync(calendar, new MemoryStream(Encoding.UTF8.GetBytes(file)), Encoding.UTF8.GetByteCount(file),
                "text/calendar", "user", allowRemovals: true, canDelete: true));

        Assert.Equal(1, ex.CommittedBatches);
        Assert.Null(await context.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.CalendarEventId == removedId));
        Assert.Contains(logs.Messages, m => m.Contains("removed") && m.Contains(removedId.ToString()));
    }

    // ------------------------------------------------------------------ VTIMEZONE (§8.3)

    [Fact]
    public void The_exported_vtimezone_gives_an_independent_reader_the_generators_offsets()
    {
        var oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
        var text = IcsTimeZoneWriter.Build(oslo, 2025, 2027)!;
        var calendar = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\n" + text
            + "BEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID=Europe/Oslo:20260330T090000\r\nDTEND;TZID=Europe/Oslo:20260330T100000\r\nSUMMARY:A\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        var parsed = IcalCalendar.Load(calendar)!;

        Assert.Single(parsed.TimeZones);
        Assert.DoesNotContain("RRULE", text);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, IcsTimeZoneWriter.MaxBytes);
        Assert.Equal(new DateTime(2026, 3, 30, 7, 0, 0, DateTimeKind.Utc), parsed.Events.Single().Start!.AsUtc);
        Assert.Contains("TZOFFSETFROM:+0100", text);
        Assert.Contains("TZOFFSETTO:+0200", text);
    }

    // ------------------------------------------------------------------ The backfill job (§11, AC 12)

    [Fact]
    public async Task The_backfill_keys_every_legacy_series_once_and_retries_one_that_fails()
    {
        var services = new ServiceCollection();
        var name = Guid.NewGuid().ToString();
        services.AddDbContext<OdysseyContext>(o => o.UseInMemoryDatabase(name));
        await using var provider = services.BuildServiceProvider();
        Guid good;
        Guid failing;
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var calendar = await SeedCalendarAsync(context);
            good = SeedLegacy(context, calendar, count: 3, until: null);
            // A daily rule over six years cannot be keyed under the 1,000 ceiling: it fails every tick.
            failing = SeedLegacy(context, calendar, count: null, until: new DateTime(2036, 1, 1));
            await context.SaveChangesAsync();
        }

        var job = new RecurrenceKeyBackfillJob(
            provider.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now), NullLogger<RecurrenceKeyBackfillJob>.Instance);
        var first = new RecurrenceKeyBackfillJob.Tally();
        await job.KeyPendingAsync(first, default);
        var second = new RecurrenceKeyBackfillJob.Tally();
        await job.KeyPendingAsync(second, default);

        Assert.Equal((1, 1), (first.Keyed, first.Failed));
        Assert.Equal((0, 1), (second.Keyed, second.Failed));
        await using var check = provider.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.NotNull((await db.RecurrencePatterns.SingleAsync(p => p.RecurrencePatternId == good)).KeysBackfilledAt);
        Assert.Null((await db.RecurrencePatterns.SingleAsync(p => p.RecurrencePatternId == failing)).KeysBackfilledAt);
        Assert.All(await db.CalendarEvents.Where(e => e.RecurrencePatternId == good).ToListAsync(), r => Assert.NotNull(r.OccurrenceKey));
    }

    // ------------------------------------------------------------------ helpers

    private static string Calendar(string body, bool close = true) =>
        "BEGIN:VCALENDAR\r\n" + body + (close ? "END:VCALENDAR\r\n" : string.Empty);

    // "BEGIN:VEVENT\r\nUID:" (19) + uid + "\r\nX:" + padding + "\r\nEND:VEVENT\r\n" (14) = 64 bytes.
    private static string Exactly64(string uid)
    {
        var fixedBytes = "BEGIN:VEVENT\r\nUID:".Length + uid.Length + "\r\nX:".Length + "\r\nEND:VEVENT\r\n".Length;
        return "BEGIN:VEVENT\r\nUID:" + uid + "\r\nX:" + new string('p', 64 - fixedBytes) + "\r\nEND:VEVENT\r\n";
    }

    private static async Task<IcsImportGrouper> BuildAsync(string file) =>
        await IcsImportGrouper.BuildAsync(new MemoryStream(Encoding.UTF8.GetBytes(file)), Path.GetTempPath(), null, default);

    private static CalendarIcsService Service(OdysseyContext context, FakeImportExportLimitsLookup limits) =>
        new(context, NullLogger<CalendarIcsService>.Instance, limits, new ClientDateHandlingTests.StubJournalLimits(),
            new FakeTimeProvider(Now));

    private static Task<Odyssey.Dtos.Journal.IcsImportResult> ImportAsync(CalendarIcsService service, Guid calendarId, string file) =>
        service.ImportAsync(calendarId, new MemoryStream(Encoding.UTF8.GetBytes(file)), Encoding.UTF8.GetByteCount(file),
            "text/calendar", "user");

    private static async Task<Guid> SeedCalendarAsync(OdysseyContext context)
    {
        var calendar = new Odyssey.Context.Calendar { Name = $"Cal {Guid.NewGuid():N}" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        return calendar.CalendarId;
    }

    private static Guid SeedLegacy(OdysseyContext context, Guid calendarId, int? count, DateTime? until)
    {
        var start = new DateTime(2026, 2, 2, 9, 0, 0);
        var pattern = new RecurrencePattern
        {
            RecurrencePatternId = Guid.NewGuid(), CalendarId = calendarId, Title = "Legacy",
            StartDateTime = start, EndDateTime = start.AddHours(1), Frequency = RecurrenceFrequency.Daily, Interval = 1,
            OccurrenceCount = count, RecurrenceEndDate = until, Version = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now,
        };
        context.RecurrencePatterns.Add(pattern);
        for (var i = 0; i < 3; i++)
        {
            context.CalendarEvents.Add(new Odyssey.Context.CalendarEvent
            {
                CalendarId = calendarId, Title = "Legacy", StartDateTime = start.AddDays(i), EndDateTime = start.AddDays(i).AddHours(1),
                RecurrencePatternId = pattern.RecurrencePatternId, CreatedAt = Now, UpdatedAt = Now,
            });
        }

        return pattern.RecurrencePatternId;
    }

    private sealed class ListLogger : ILogger<CalendarIcsService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
