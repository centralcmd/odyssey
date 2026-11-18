using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The calendar <c>.ics</c> streaming export and import against real MariaDB with <c>EnableRetryOnFailure</c>, as
/// production runs (issue #516): no export opens a transaction (AC 1), the per-calendar planner's query count does not
/// grow with its series (AC 19), an import batch resolves with a bounded number of queries and commits in its own
/// transaction under the execution strategy, a failure after a commit keeps the earlier batches (AC 17), and memory
/// stays flat on a large export and import (AC 1, AC 16, at reduced scale). The EF InMemory provider honours neither
/// transactions nor the execution strategy and runs no interceptor, so none of this is observable on the fast tiers.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class CalendarIcsStreamingIntegrationTests(MariaDbFixture fixture)
{
    private const string User = "calendar-ics-streaming";
    private const long MiB = 1024 * 1024;

    [SkippableFact]
    public async Task No_export_opens_a_transaction_and_a_large_calendar_streams_in_bounded_memory()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var transactions = new TransactionCounter();
        var options = await OptionsAsync(transactions);
        var calendarId = await SeedCalendarAsync(options, rows: 100_000, series: 3);
        try
        {
            await using var context = new OdysseyContext(options);
            var service = Service(context);
            var patternId = await context.RecurrencePatterns.Where(p => p.CalendarId == calendarId)
                .Select(p => p.RecurrencePatternId).FirstAsync();
            var eventId = await context.CalendarEvents.Where(e => e.CalendarId == calendarId)
                .Select(e => e.CalendarEventId).FirstAsync();
            // Seeding saved through transactions of its own; only the exports below are measured.
            transactions.Reset();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var baseline = GC.GetTotalMemory(forceFullCollection: true);
            var output = new SamplingStream(sampleEvery: 2000);
            var planned = 0;
            Assert.True(await service.ExportStreamingAsync(
                CalendarIcsExportScope.ForCalendar(calendarId), output, (_, count) => planned = count));
            Assert.Equal(100_000 + 3, planned);
            Assert.True(output.PeakLiveBytes - baseline < 64 * MiB,
                $"Peak live heap grew by {(output.PeakLiveBytes - baseline) / MiB} MiB.");

            foreach (var scope in new[]
            {
                CalendarIcsExportScope.ForEvent(eventId), CalendarIcsExportScope.ForPattern(patternId),
                CalendarIcsExportScope.ForAggregate(new CalendarEventsIcsExportQueryParams { CalendarIds = [calendarId] }),
                CalendarIcsExportScope.ForAggregate(new CalendarEventsIcsExportQueryParams { Search = $"Row {calendarId:N}" }),
            })
            {
                await service.SummarizeExportAsync(scope);
                await service.ExportStreamingAsync(scope, System.IO.Stream.Null, (_, _) => { });
            }

            Assert.Equal(0, transactions.Started);
        }
        finally
        {
            await DeleteCalendarAsync(options, calendarId);
        }
    }

    [SkippableFact]
    public async Task The_per_calendar_planner_issues_at_most_four_queries_for_fifty_series()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var commands = new CommandCounter();
        var options = await OptionsAsync(commands);
        var calendarId = await SeedCalendarAsync(options, rows: 20, series: 50);
        try
        {
            await using var context = new OdysseyContext(options);
            commands.Reset();

            var summary = await Service(context).SummarizeExportAsync(CalendarIcsExportScope.ForCalendar(calendarId));

            Assert.Equal(50, summary!.SeriesCount);
            Assert.InRange(commands.Selects, 1, 4);
        }
        finally
        {
            await DeleteCalendarAsync(options, calendarId);
        }
    }

    [SkippableFact]
    public async Task Import_commits_per_batch_under_the_execution_strategy_and_resolves_each_batch_by_keyed_queries()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var commands = new CommandCounter();
        var options = await OptionsAsync(commands);
        var calendarId = await SeedCalendarAsync(options, rows: 0, series: 0);
        try
        {
            var file = File(1200, i => $"batch-{calendarId:N}-{i}@example.com");
            await using (var context = new OdysseyContext(options))
            {
                var first = await Import(context, calendarId, file);
                Assert.Equal(1200, first.ImportedCount);
            }

            // A re-import of 500 that updates every row: one existence check, then two keyed resolution queries.
            await using (var context = new OdysseyContext(options))
            {
                commands.Reset();
                var second = await Import(context, calendarId, File(500, i => $"batch-{calendarId:N}-{i}@example.com"));
                Assert.Equal(500, second.UpdatedCount);
                Assert.InRange(commands.Selects, 1, 1 + 3);
            }

            await using var check = new OdysseyContext(options);
            Assert.Equal(1200, await check.CalendarEvents.CountAsync(e => e.CalendarId == calendarId));
        }
        finally
        {
            await DeleteCalendarAsync(options, calendarId);
        }
    }

    [SkippableFact]
    public async Task A_failure_in_batch_three_keeps_two_committed_batches_and_a_rerun_completes_the_file()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options, rows: 0, series: 0);
        try
        {
            var file = File(1500, i => $"{Guid.NewGuid()}@odyssey.local");
            await using (var context = new OdysseyContext(options))
            {
                var capped = new CalendarIcsService(
                    context, NullLogger<CalendarIcsService>.Instance, new Limits(importEvents: 1001), new StubJournalLimitsLookup());
                var error = await Assert.ThrowsAsync<CalendarIcsImportPartiallyAppliedException>(() =>
                    capped.ImportAsync(calendarId, Stream(file), file.Length, "text/calendar", User));
                Assert.Equal((2, 1000, 400), (error.CommittedBatches, error.CommittedEvents, error.StatusCode));
            }

            await using (var check = new OdysseyContext(options))
            {
                var rows = await check.CalendarEvents.Where(e => e.CalendarId == calendarId).ToListAsync();
                Assert.Equal(1000, rows.Count);
                Assert.All(rows, r => Assert.NotNull(r.ImportSourceUid));
            }

            await using (var context = new OdysseyContext(options))
            {
                var rerun = await Import(context, calendarId, file);
                Assert.Equal((1000, 500), (rerun.UpdatedCount, rerun.ImportedCount));
            }
        }
        finally
        {
            await DeleteCalendarAsync(options, calendarId);
        }
    }

    [SkippableFact]
    public async Task A_large_import_streams_in_bounded_memory()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options, rows: 0, series: 0);
        try
        {
            const int events = 50_000;
            var file = File(events, i => $"memory-{calendarId:N}-{i}@example.com");
            await using var context = new OdysseyContext(options);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var input = new SamplingReadStream(Stream(file), sampleEveryBytes: 1024 * 1024);
            var baseline = GC.GetTotalMemory(forceFullCollection: true);

            var result = await Service(context).ImportAsync(calendarId, input, file.Length, "text/calendar", User);

            Assert.Equal(events, result.ImportedCount);
            // The file itself sits in the test's own memory; what is measured is what the import adds over it.
            Assert.True(input.PeakLiveBytes - baseline < 128 * MiB,
                $"Peak live heap grew by {(input.PeakLiveBytes - baseline) / MiB} MiB.");

            // The bound discriminates (tester finding 6): the materialising import this replaced loaded the whole file
            // into one Ical.Net calendar first, and that parse alone retains more than the bound for this file.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var beforeParse = GC.GetTotalMemory(forceFullCollection: true);
            var whole = Ical.Net.Calendar.Load(file);
            var materialised = GC.GetTotalMemory(forceFullCollection: true) - beforeParse;
            Assert.Equal(events, whole!.Events.Count);
            Assert.True(materialised > 128 * MiB, $"A whole-file parse retained only {materialised / MiB} MiB.");
            GC.KeepAlive(whole);
        }
        finally
        {
            await DeleteCalendarAsync(options, calendarId);
        }
    }

    [SkippableFact]
    public async Task A_cancelled_series_is_removed_with_delete_semantics_in_one_batch_save()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options, rows: 0, series: 0);
        try
        {
            Guid patternId;
            // A fixed clock, handed to the importer too, so the past/future split never depends on when this runs.
            var now = new DateTime(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            await using (var seeding = new OdysseyContext(options))
            {
                var first = now.AddDays(-1).AddHours(-1);
                var pattern = new RecurrencePattern
                {
                    CalendarId = calendarId, Title = "Gone", StartDateTime = first, EndDateTime = first.AddHours(1),
                    Frequency = Odyssey.Context.RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 3,
                    ExternalUid = $"gone-{calendarId:N}@example.com", CreatedByUserId = User, CreatedAt = now, UpdatedAt = now,
                };
                seeding.RecurrencePatterns.Add(pattern);
                for (var o = 0; o < 3; o++)
                {
                    seeding.CalendarEvents.Add(new CalendarEvent
                    {
                        CalendarId = calendarId, Title = "Gone", StartDateTime = first.AddDays(o), EndDateTime = first.AddDays(o).AddHours(1),
                        RecurrencePattern = pattern, CreatedByUserId = User, CreatedAt = now, UpdatedAt = now,
                    });
                }

                await seeding.SaveChangesAsync();
                patternId = pattern.RecurrencePatternId;
            }

            var file = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\n"
                + $"UID:gone-{calendarId:N}@example.com\r\nSTATUS:CANCELLED\r\nSUMMARY:Gone\r\nDTSTART:20300101T090000Z\r\n"
                + "RRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            await using (var context = new OdysseyContext(options))
            {
                var result = await Service(context, new FakeTimeProvider(now)).ImportAsync(
                    calendarId, Stream(file), file.Length, "text/calendar", User, allowRemovals: true, canDelete: true);
                Assert.Equal(1, result.RemovedCount);
            }

            await using var check = new OdysseyContext(options);
            Assert.False(await check.RecurrencePatterns.AnyAsync(p => p.RecurrencePatternId == patternId));
            var left = await check.CalendarEvents.Where(e => e.CalendarId == calendarId).ToListAsync();
            Assert.Equal(2, left.Count);
            Assert.All(left, e => Assert.Null(e.RecurrencePatternId));
        }
        finally
        {
            await DeleteCalendarAsync(options, calendarId);
        }
    }

    [SkippableFact]
    public async Task A_case_variant_uid_updates_the_stored_row_instead_of_duplicating_it()
    {
        // Architect nit 7: the keyed query compares under the column's case-insensitive collation, so the in-memory
        // pick must too; otherwise it loads the row and then creates a second one.
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options, rows: 0, series: 0);
        try
        {
            var stored = $"Case-{calendarId:N}@Example.com";
            await using (var seeding = new OdysseyContext(options))
            {
                var at = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
                seeding.CalendarEvents.Add(new CalendarEvent
                {
                    CalendarId = calendarId, Title = "Before", StartDateTime = at, EndDateTime = at.AddHours(1),
                    ExternalUid = stored, CreatedByUserId = User, CreatedAt = at, UpdatedAt = at,
                });
                await seeding.SaveChangesAsync();
            }

            var file = File(1, _ => stored.ToLowerInvariant());
            await using (var context = new OdysseyContext(options))
            {
                var result = await Import(context, calendarId, file);
                Assert.Equal((0, 1), (result.ImportedCount, result.UpdatedCount));
            }

            await using var check = new OdysseyContext(options);
            var row = Assert.Single(await check.CalendarEvents.Where(e => e.CalendarId == calendarId).ToListAsync());
            Assert.Equal("Imported 0", row.Title);
            Assert.Equal(stored, row.ExternalUid);
        }
        finally
        {
            await DeleteCalendarAsync(options, calendarId);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<DbContextOptions<OdysseyContext>> OptionsAsync(params IInterceptor[] interceptors)
    {
        var connectionString = fixture.RelationalConnectionString;
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), o => o.EnableRetryOnFailure())
            .AddInterceptors(interceptors)
            .Options;
        await using var migrating = new OdysseyContext(options);
        await migrating.Database.MigrateAsync();
        await AttributionUsers.EnsureAsync(migrating, User);
        return options;
    }

    private static CalendarIcsService Service(OdysseyContext context, TimeProvider? clock = null) =>
        new(context, NullLogger<CalendarIcsService>.Instance, new Limits(), new StubJournalLimitsLookup(), clock);

    private static Task<IcsImportResult> Import(OdysseyContext context, Guid calendarId, string file) =>
        Service(context).ImportAsync(calendarId, Stream(file), Encoding.UTF8.GetByteCount(file), "text/calendar", User);

    private static MemoryStream Stream(string file) => new(Encoding.UTF8.GetBytes(file));

    private static string File(int count, Func<int, string> uid)
    {
        var builder = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n");
        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var at = start.AddMinutes(i);
            builder.Append("BEGIN:VEVENT\r\nUID:").Append(uid(i))
                .Append("\r\nSUMMARY:Imported ").Append(i)
                .Append("\r\nDTSTART:").Append(at.ToString("yyyyMMdd'T'HHmmss'Z'"))
                .Append("\r\nDTEND:").Append(at.AddMinutes(30).ToString("yyyyMMdd'T'HHmmss'Z'"))
                .Append("\r\nEND:VEVENT\r\n");
        }

        return builder.Append("END:VCALENDAR\r\n").ToString();
    }

    private static async Task<Guid> SeedCalendarAsync(DbContextOptions<OdysseyContext> options, int rows, int series)
    {
        await using var context = new OdysseyContext(options);
        var calendar = new Calendar { Name = $"Streaming {Guid.NewGuid():N}", CreatedByUserId = User };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();

        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;
        for (var s = 0; s < series; s++)
        {
            var first = start.AddDays(s * 10);
            var pattern = new RecurrencePattern
            {
                CalendarId = calendar.CalendarId, Title = $"Series {s}", StartDateTime = first, EndDateTime = first.AddHours(1),
                Frequency = Odyssey.Context.RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 3, CreatedByUserId = User,
                CreatedAt = now, UpdatedAt = now,
            };
            context.RecurrencePatterns.Add(pattern);
            for (var o = 0; o < 3; o++)
            {
                context.CalendarEvents.Add(new CalendarEvent
                {
                    CalendarId = calendar.CalendarId, Title = pattern.Title, StartDateTime = first.AddDays(o),
                    EndDateTime = first.AddDays(o).AddHours(1), RecurrencePattern = pattern, CreatedByUserId = User,
                    CreatedAt = now, UpdatedAt = now,
                });
            }
        }

        await context.SaveChangesAsync();
        for (var batch = 0; batch < rows; batch += 5000)
        {
            await using var seeding = new OdysseyContext(options);
            seeding.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = batch; i < Math.Min(rows, batch + 5000); i++)
            {
                seeding.CalendarEvents.Add(new CalendarEvent
                {
                    CalendarId = calendar.CalendarId, Title = $"Row {calendar.CalendarId:N} {i}",
                    Description = "A description long enough to make the row worth streaming rather than buffering.",
                    StartDateTime = start.AddMinutes(i), EndDateTime = start.AddMinutes(i + 30), CreatedByUserId = User,
                    CreatedAt = now, UpdatedAt = now,
                });
            }

            await seeding.SaveChangesAsync();
        }

        return calendar.CalendarId;
    }

    // Shared database: the next test's unfiltered aggregate counts every row, so each test removes its own.
    private static async Task DeleteCalendarAsync(DbContextOptions<OdysseyContext> options, Guid calendarId)
    {
        await using var context = new OdysseyContext(options);
        await context.CalendarEvents.Where(e => e.CalendarId == calendarId).ExecuteDeleteAsync();
        await context.RecurrencePatterns.Where(p => p.CalendarId == calendarId).ExecuteDeleteAsync();
        await context.Calendars.Where(c => c.CalendarId == calendarId).ExecuteDeleteAsync();
    }

    private sealed class Limits(int? importEvents = null) : IImportExportLimitsLookup
    {
        public Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImportExportLimits(
                null, null, 64 * MiB, 64 * MiB,
                null, importEvents, 10240 * MiB, 10240 * MiB,
                null, null, 64 * MiB, 64 * MiB,
                null, null, 64 * MiB, 64 * MiB,
                // The occurrence budget is the shipped default, so a stub can never mask a cap the real lookup applies.
                200_000, SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences, 92, 200, 100, 64 * MiB,
                IsDegraded: false));
    }

    private sealed class TransactionCounter : DbTransactionInterceptor
    {
        private int started;

        public int Started => started;

        public void Reset() => Interlocked.Exchange(ref started, 0);

        public override InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            Interlocked.Increment(ref started);
            return result;
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref started);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Counts the SELECTs that reach the server — the unit AC 19 is written in.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int selects;

        public int Selects => selects;

        public void Reset() => Interlocked.Exchange(ref selects, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return ValueTask.FromResult(result);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref selects);
            }
        }
    }

    /// <summary>An output that samples the live heap every few thousand writes.</summary>
    private sealed class SamplingStream(int sampleEvery) : Stream
    {
        private int writes;

        public long PeakLiveBytes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++writes % sampleEvery == 0)
            {
                PeakLiveBytes = Math.Max(PeakLiveBytes, GC.GetTotalMemory(forceFullCollection: true));
            }

            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().Wait();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>An input that samples the live heap every megabyte read.</summary>
    private sealed class SamplingReadStream(Stream inner, int sampleEveryBytes) : Stream
    {
        private long read;
        private long nextSample = sampleEveryBytes;

        public long PeakLiveBytes { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            read += count;
            if (read >= nextSample)
            {
                nextSample += sampleEveryBytes;
                PeakLiveBytes = Math.Max(PeakLiveBytes, GC.GetTotalMemory(forceFullCollection: true));
            }

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().Result;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
