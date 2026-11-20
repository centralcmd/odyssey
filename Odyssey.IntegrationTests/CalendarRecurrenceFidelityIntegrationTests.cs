using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Journal;
using Odyssey.Core.Locations;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using ContextFrequency = Odyssey.Context.RecurrenceFrequency;
using ContextDays = Odyssey.Context.DaysOfWeekFlags;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #519 against real MariaDB, where the fast tiers cannot look: the migration's keys, cascade and CHECKs (AC 16),
/// the window predicate and the offset-segment sort translated to SQL (AC 26), the series lock under concurrency —
/// a series edit racing occurrence writes never leaves a key that is both a row and an exclusion and never surfaces as
/// a 500 (AC 7, AC 19) — the per-statement lock wait, a forced deadlock retried by the shared strategy (AC 27b),
/// two imports over the same series (AC 27), and the backfill leaving every calendar's events unchanged (AC 12).
/// </summary>
[Collection(MariaDbTimingCollection.Name)]
public class CalendarRecurrenceFidelityIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_recurrence_fidelity";
    private const string User = "calendar-recurrence-it";
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ AC 16: the schema

    [SkippableFact]
    public async Task The_key_indexes_the_cascade_and_the_all_day_checks_hold_on_mariadb()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        await using var context = new OdysseyContext(options);
        var (calendarId, patternId) = await SeedKeyedSeriesAsync(options, count: 3);
        var key = await context.CalendarEvents.Where(e => e.RecurrencePatternId == patternId)
            .OrderBy(e => e.OccurrenceKey).Select(e => e.OccurrenceKey!.Value).FirstAsync();

        var duplicateRow = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO CalendarEvents (CalendarEventId, CalendarId, Title, StartDateTime, EndDateTime, IsAllDay, RecurrencePatternId,
                OccurrenceKey, IsOverride, CreatedAt, UpdatedAt)
            VALUES ({Guid.NewGuid()}, {calendarId}, 'dup', {key}, {key.AddHours(1)}, 0, {patternId}, {key}, 0, {Now}, {Now})
            """));
        Assert.Equal(1062, duplicateRow.Number);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO RecurrenceExclusions (RecurrenceExclusionId, RecurrencePatternId, OccurrenceKey, CreatedAt)
            VALUES ({Guid.NewGuid()}, {patternId}, {key.AddYears(1)}, {Now})
            """);
        var duplicateExclusion = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO RecurrenceExclusions (RecurrenceExclusionId, RecurrencePatternId, OccurrenceKey, CreatedAt)
            VALUES ({Guid.NewGuid()}, {patternId}, {key.AddYears(1)}, {Now})
            """));
        Assert.Equal(1062, duplicateExclusion.Number);

        var zonedAllDay = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO CalendarEvents (CalendarEventId, CalendarId, Title, StartDateTime, EndDateTime, IsAllDay, TimeZoneId,
                IsOverride, CreatedAt, UpdatedAt)
            VALUES ({Guid.NewGuid()}, {calendarId}, 'all day', {Now.Date}, {Now.Date.AddDays(1)}, 1, 'Europe/Oslo', 0, {Now}, {Now})
            """));
        Assert.Equal(4025, zonedAllDay.Number); // ER_CONSTRAINT_FAILED
        await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE RecurrencePatterns SET IsAllDay = 1, TimeZoneId = 'Europe/Oslo' WHERE RecurrencePatternId = {patternId}"));

        // CASCADE: a series row deleted underneath the application takes its exclusions with it.
        await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM RecurrencePatterns WHERE RecurrencePatternId = {patternId}");
        Assert.Equal(0, await context.RecurrenceExclusions.CountAsync(x => x.RecurrencePatternId == patternId));
    }

    // ------------------------------------------------------------------ AC 26: the window in SQL

    [SkippableFact]
    public async Task A_mix_of_floating_and_zoned_rows_pages_identically_in_sql_under_every_view_zone()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options);
        await using (var seeding = new OdysseyContext(options))
        {
            var oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
            for (var i = 0; i < 1000; i++)
            {
                // Across the Oslo change on 29 March 2026, alternating floating and zoned.
                var wall = new DateTime(2026, 3, 22, 0, 0, 0).AddMinutes(i * 17);
                var zoned = i % 2 == 0;
                var start = zoned ? CalendarTimeZones.ToUtc(wall, oslo) : wall;
                seeding.CalendarEvents.Add(new ContextCalendarEvent
                {
                    CalendarId = calendarId, Title = $"Row {i}", StartDateTime = start, EndDateTime = start.AddMinutes(10),
                    TimeZoneId = zoned ? "Europe/Oslo" : null, CreatedAt = Now, UpdatedAt = Now,
                });
            }

            await seeding.SaveChangesAsync();
        }

        var from = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        // The paged window contains the Oslo change on 29 March (PR #528 review T3), so the Oslo view sorts by a CASE over
        // two offset segments in SQL rather than by the single-offset shortcut.
        Assert.Equal(2, CalendarWindow.Segments(from, to, TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo"))!.Count);
        foreach (var zone in new[] { "UTC", "Europe/Oslo", "America/New_York" })
        {
            await using var context = new OdysseyContext(options);
            var service = new CalendarEventService(context, new StubJournalLimitsLookup());
            var paged = new List<ExistingCalendarEvent>();
            PagedResult<ExistingCalendarEvent> page;
            var offset = 0;
            do
            {
                page = await service.ListAsync(new CalendarEventsQueryParams
                {
                    From = from, To = to, CalendarIds = [calendarId], ViewTimeZoneId = zone, Offset = offset, Limit = 200,
                });
                paged.AddRange(page.Items);
                offset += 200;
            }
            while (offset < page.TotalCount);

            Assert.Equal(1000, page.TotalCount);
            Assert.Equal(1000, paged.Select(p => p.CalendarEventId).Distinct().Count());
            var view = TimeZoneInfo.FindSystemTimeZoneById(zone);
            var instants = paged.Select(p => p.StartUtc ?? CalendarTimeZones.ToUtc(p.StartDateTime, view)).ToList();
            Assert.Equal(instants.Order(), instants);
        }
    }

    // ------------------------------------------------------------------ AC 7, AC 19: the series lock

    [SkippableFact]
    public async Task A_series_edit_racing_occurrence_writes_never_leaves_a_key_as_both_a_row_and_an_exclusion()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var (calendarId, patternId) = await SeedKeyedSeriesAsync(options, count: 12);
        List<Guid> rowIds;
        await using (var reading = new OdysseyContext(options))
        {
            rowIds = await reading.CalendarEvents.Where(e => e.RecurrencePatternId == patternId)
                .OrderBy(e => e.OccurrenceKey).Select(e => e.CalendarEventId).ToListAsync();
        }

        var edits = new List<Task<bool>>();
        var deletes = new List<Task<bool>>();
        var overrides = new List<Task<bool>>();
        for (var round = 0; round < 6; round++)
        {
            var title = $"Round {round}";
            var victim = rowIds[round];
            edits.Add(Expect409OrSuccess(async () =>
            {
                await using var context = new OdysseyContext(options);
                await Patterns(context).Update(patternId, Rule(calendarId) with { Title = title }, User, claims: LocationClaims.Trusted);
            }));
            deletes.Add(Expect409OrSuccess(async () =>
            {
                await using var context = new OdysseyContext(options);
                await Events(context).Delete(victim, userId: User);
            }));
            overrides.Add(Expect409OrSuccess(async () =>
            {
                await using var context = new OdysseyContext(options);
                await Events(context).Update(rowIds[6 + round], new NewCalendarEvent
                {
                    CalendarId = calendarId, Title = "Edited", StartDateTime = Wall(2026, 2, 2, 11).AddDays(7 * (6 + round)),
                    EndDateTime = Wall(2026, 2, 2, 12).AddDays(7 * (6 + round)),
                }, User, claims: LocationClaims.Trusted);
            }));
        }

        await Task.WhenAll([.. edits, .. deletes, .. overrides]);

        await using var check = new OdysseyContext(options);
        var rowKeys = await check.CalendarEvents.Where(e => e.RecurrencePatternId == patternId).Select(e => e.OccurrenceKey).ToListAsync();
        var excluded = await check.RecurrenceExclusions.Where(x => x.RecurrencePatternId == patternId).Select(x => (DateTime?)x.OccurrenceKey).ToListAsync();
        Assert.Empty(rowKeys.Intersect(excluded));
        // A series edit keeps every exception, so each occurrence write that went through is still there.
        Assert.Equal(deletes.Count(t => t.Result), excluded.Count);
        Assert.Equal(overrides.Count(t => t.Result),
            await check.CalendarEvents.CountAsync(e => e.RecurrencePatternId == patternId && e.IsOverride));
        Assert.True(edits.Any(t => t.Result) && deletes.Any(t => t.Result));
    }

    [SkippableFact]
    public async Task A_stale_version_and_changed_consent_are_both_409s_on_mariadb_and_write_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var (calendarId, patternId) = await SeedKeyedSeriesAsync(options, count: 4);
        Guid version;
        Guid editedRow;
        await using (var context = new OdysseyContext(options))
        {
            version = await context.RecurrencePatterns.Where(p => p.RecurrencePatternId == patternId).Select(p => p.Version).SingleAsync();
            editedRow = await context.CalendarEvents.Where(e => e.RecurrencePatternId == patternId).OrderBy(e => e.OccurrenceKey)
                .Select(e => e.CalendarEventId).Skip(1).FirstAsync();
            await Events(context).Update(editedRow, new NewCalendarEvent
            {
                CalendarId = calendarId, Title = "Override", StartDateTime = Wall(2026, 2, 9, 9), EndDateTime = Wall(2026, 2, 9, 10),
            }, User, claims: LocationClaims.Trusted);
        }

        await using (var context = new OdysseyContext(options))
        {
            var drops = await Assert.ThrowsAsync<CalendarSeriesConflictException>(() => Patterns(context).Update(patternId,
                Rule(calendarId) with { DaysOfWeek = Odyssey.Dtos.Journal.DaysOfWeekFlags.Friday, ExpectedVersion = version, ConfirmedDrops = new RecurrenceConfirmedDrops() },
                User, claims: LocationClaims.Trusted));
            Assert.Equal(CalendarSeriesConflictException.DropsChanged, drops.Code);
        }

        await using (var context = new OdysseyContext(options))
        {
            await Patterns(context).Update(patternId, Rule(calendarId) with { Title = "Someone else" }, User, claims: LocationClaims.Trusted);
            var stale = await Assert.ThrowsAsync<CalendarSeriesConflictException>(() => Patterns(context).Update(patternId,
                Rule(calendarId) with { Title = "Mine", ExpectedVersion = version }, User, claims: LocationClaims.Trusted));
            Assert.Equal(CalendarSeriesConflictException.VersionConflict, stale.Code);
        }

        await using var check = new OdysseyContext(options);
        Assert.Equal("Someone else", await check.RecurrencePatterns.Where(p => p.RecurrencePatternId == patternId).Select(p => p.Title).SingleAsync());
        Assert.Equal("Override", await check.CalendarEvents.Where(e => e.CalendarEventId == editedRow).Select(e => e.Title).SingleAsync());
    }

    [SkippableFact]
    public async Task A_series_held_by_another_transaction_is_a_409_busy_after_the_bounded_lock_waits()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var (calendarId, patternId) = await SeedKeyedSeriesAsync(options, count: 2);

        await using var holder = new MySqlConnection(fixture.ConnectionStringFor(Database));
        await holder.OpenAsync();
        await using var held = await holder.BeginTransactionAsync();
        await using (var command = new MySqlCommand(
            $"SELECT RecurrencePatternId FROM RecurrencePatterns WHERE RecurrencePatternId = '{patternId}' FOR UPDATE", holder, held))
        {
            await command.ExecuteScalarAsync();
        }

        var watch = Stopwatch.StartNew();
        await using (var context = new OdysseyContext(options))
        {
            var busy = await Assert.ThrowsAsync<CalendarSeriesConflictException>(() =>
                Patterns(context).Update(patternId, Rule(calendarId) with { Title = "Blocked" }, User, claims: LocationClaims.Trusted));
            Assert.Equal(CalendarSeriesConflictException.Busy, busy.Code);
        }

        // Three attempts of a 5 s statement wait, plus the strategy's short backoff: far below the server's 50 s default.
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40));
        await held.RollbackAsync();

        // The session setting did not leak into the pool: a plain connection still reads the server default.
        await using var probe = new MySqlConnection(fixture.ConnectionStringFor(Database));
        await probe.OpenAsync();
        await using var read = new MySqlCommand("SELECT @@SESSION.innodb_lock_wait_timeout", probe);
        Assert.NotEqual(CalendarSeriesTransactions.LockWaitSeconds, Convert.ToInt32(await read.ExecuteScalarAsync()));
    }

    [SkippableFact]
    public async Task A_forced_deadlock_is_retried_by_the_shared_strategy()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var (_, first) = await SeedKeyedSeriesAsync(options, count: 1);
        var (_, second) = await SeedKeyedSeriesAsync(options, count: 1);
        var bothLockedOne = new Barrier(2);
        var attempts = new int[2];

        async Task RunAsync(int index, Guid a, Guid b)
        {
            await using var context = new OdysseyContext(options);
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                var attempt = Interlocked.Increment(ref attempts[index]);
                await using var transaction = await context.Database.BeginTransactionAsync();
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT RecurrencePatternId FROM RecurrencePatterns WHERE RecurrencePatternId = {a} FOR UPDATE");
                if (attempt == 1)
                {
                    bothLockedOne.SignalAndWait(TimeSpan.FromSeconds(30));
                }

                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT RecurrencePatternId FROM RecurrencePatterns WHERE RecurrencePatternId = {b} FOR UPDATE");
                await transaction.CommitAsync();
            });
        }

        await Task.WhenAll(Task.Run(() => RunAsync(0, first, second)), Task.Run(() => RunAsync(1, second, first)));

        // One of the two was the deadlock victim (1213) and succeeded on its retry.
        Assert.Equal(3, attempts.Sum());
    }

    [SkippableFact]
    public async Task A_batch_the_strategy_retries_after_a_deadlock_reloads_its_places_and_counts_them_once()
    {
        // PR #528 review A1: 1205 and 1213 are transient for every context, so the execution strategy can re-run a batch
        // whose first run created a place. The re-run must not reuse that rolled-back place, nor count it twice.
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var deadlock = new FirstInsertDeadlock("INSERT INTO `CalendarEvents`");
        var options = await OptionsAsync(deadlock);
        var calendarId = await SeedCalendarAsync(options);
        var place = $"Brand new cafe {Guid.NewGuid():N}";
        var text = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:retried@example.com\r\n"
            + $"DTSTART:20300107T090000Z\r\nDTEND:20300107T100000Z\r\nSUMMARY:Coffee\r\nLOCATION:{place}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        await using (var context = new OdysseyContext(options))
        {
            var result = await Ics(context).ImportAsync(
                calendarId, Stream(text), text.Length, "text/calendar", User, claims: LocationClaims.Trusted);

            Assert.True(deadlock.Fired);
            Assert.Equal((1, 1, 1), (result.ImportedCount, result.PlacesLinked, result.PlacesCreated));
        }

        await using var check = new OdysseyContext(options);
        var location = Assert.Single(await check.Locations.Where(l => l.Name == place).ToListAsync());
        Assert.Equal(location.LocationId, (await check.CalendarEvents.SingleAsync(e => e.CalendarId == calendarId)).LocationId);
    }

    // ------------------------------------------------------------------ AC 27: concurrent imports

    [SkippableFact]
    public async Task Two_concurrent_imports_touching_the_same_fifty_series_both_finish_or_one_is_busy_never_a_500()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options);
        var file = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n");
        for (var i = 0; i < 50; i++)
        {
            file.Append($"BEGIN:VEVENT\r\nUID:concurrent-{calendarId:N}-{i}\r\nDTSTART:20300107T090000Z\r\nDTEND:20300107T100000Z\r\n")
                .Append($"SUMMARY:Series {i}\r\nRRULE:FREQ=WEEKLY;COUNT=4\r\nEND:VEVENT\r\n");
        }

        var text = file.Append("END:VCALENDAR\r\n").ToString();
        await using (var first = new OdysseyContext(options))
        {
            Assert.Equal(50, (await Ics(first).ImportAsync(calendarId, Stream(text), text.Length, "text/calendar", User)).ImportedCount);
        }

        var changed = text.Replace("SUMMARY:Series", "SUMMARY:Renamed", StringComparison.Ordinal);
        async Task<string> ImportOnceAsync(string body)
        {
            await using var context = new OdysseyContext(options);
            try
            {
                await Ics(context).ImportAsync(calendarId, Stream(body), body.Length, "text/calendar", User);
                return "ok";
            }
            catch (DomainException ex) when (ex.StatusCode == 409)
            {
                return "busy";
            }
        }

        var results = await Task.WhenAll(Task.Run(() => ImportOnceAsync(text)), Task.Run(() => ImportOnceAsync(changed)));

        Assert.All(results, r => Assert.Contains(r, new[] { "ok", "busy" }));
        await using var check = new OdysseyContext(options);
        Assert.Equal(50, await check.RecurrencePatterns.CountAsync(p => p.CalendarId == calendarId));
        Assert.Equal(200, await check.CalendarEvents.CountAsync(e => e.CalendarId == calendarId));
        Assert.Equal(0, await check.CalendarEvents.Where(e => e.CalendarId == calendarId)
            .GroupBy(e => new { e.RecurrencePatternId, e.OccurrenceKey }).CountAsync(g => g.Count() > 1));
    }

    // ------------------------------------------------------------------ AC 12: the backfill

    [SkippableFact]
    public async Task The_backfill_leaves_every_calendars_events_unchanged_and_a_second_run_changes_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options);
        var otherCalendarId = await SeedCalendarAsync(options);
        Guid patternId;
        await using (var seeding = new OdysseyContext(options))
        {
            var start = Wall(2025, 12, 1, 9); // past and future rows around Now
            var pattern = new RecurrencePattern
            {
                RecurrencePatternId = Guid.NewGuid(), CalendarId = calendarId, Title = "Legacy", StartDateTime = start,
                EndDateTime = start.AddHours(1), Frequency = ContextFrequency.Weekly, Interval = 1, DaysOfWeek = ContextDays.Monday,
                OccurrenceCount = 10, CreatedAt = Now, UpdatedAt = Now, Version = Guid.NewGuid(),
            };
            seeding.RecurrencePatterns.Add(pattern);
            patternId = pattern.RecurrencePatternId;
            for (var i = 0; i < 10; i++)
            {
                if (i == 7)
                {
                    continue; // deleted occurrence (future)
                }

                var at = start.AddDays(7 * i);
                seeding.CalendarEvents.Add(new ContextCalendarEvent
                {
                    CalendarId = i == 6 ? otherCalendarId : calendarId,                   // moved occurrence
                    Title = i == 9 ? "Edited" : "Legacy",                                 // edited occurrence (future)
                    StartDateTime = i == 8 ? at.AddHours(2) : at,                         // rescheduled occurrence
                    EndDateTime = (i == 8 ? at.AddHours(2) : at).AddHours(1),
                    RecurrencePatternId = pattern.RecurrencePatternId, CreatedAt = Now.AddMinutes(i), UpdatedAt = Now,
                });
                if (i == 9)
                {
                    seeding.CalendarEvents.Add(new ContextCalendarEvent                   // duplicate start
                    {
                        CalendarId = calendarId, Title = "Legacy", StartDateTime = at, EndDateTime = at.AddHours(1),
                        RecurrencePatternId = pattern.RecurrencePatternId, CreatedAt = Now.AddHours(1), UpdatedAt = Now,
                    });
                }
            }

            await seeding.SaveChangesAsync();
        }

        var before = await SnapshotAsync(options, calendarId, otherCalendarId);
        var services = new ServiceCollection();
        services.AddDbContext<OdysseyContext>(o => o.UseMySql(
            fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.ConnectionStringFor(Database)), OdysseyDatabaseRetry.Configure));
        await using var provider = services.BuildServiceProvider();
        var job = new RecurrenceKeyBackfillJob(
            provider.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now), NullLogger<RecurrenceKeyBackfillJob>.Instance);

        var tally = new RecurrenceKeyBackfillJob.Tally();
        await job.KeyPendingAsync(tally, default);
        var after = await SnapshotAsync(options, calendarId, otherCalendarId);
        var again = new RecurrenceKeyBackfillJob.Tally();
        await job.KeyPendingAsync(again, default);
        var afterSecond = await SnapshotAsync(options, calendarId, otherCalendarId);

        Assert.True(tally.Keyed >= 1);
        Assert.Equal(2, tally.Detached); // the rescheduled row and the duplicate
        Assert.Equal(before, after);
        Assert.Equal(after, afterSecond);
        Assert.Equal(0, again.Keyed);
        await using var check = new OdysseyContext(options);
        var exclusions = await check.RecurrenceExclusions.Where(x => x.RecurrencePatternId == patternId)
            .Select(x => x.OccurrenceKey).OrderBy(k => k).ToListAsync();
        Assert.Equal([Wall(2025, 12, 1, 9).AddDays(49), Wall(2025, 12, 1, 9).AddDays(56)], exclusions);
        Assert.True(await check.CalendarEvents.Where(e => e.RecurrencePatternId == patternId && e.Title == "Edited")
            .Select(e => e.IsOverride).SingleAsync());
    }

    // ------------------------------------------------------------------ helpers

    private static DateTime Wall(int y, int m, int d, int h) => new(y, m, d, h, 0, 0, DateTimeKind.Unspecified);

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private static NewRecurrencePattern Rule(Guid calendarId) => new()
    {
        CalendarId = calendarId, Title = "Standup", StartDateTime = Wall(2026, 2, 2, 9), EndDateTime = Wall(2026, 2, 2, 10),
        Frequency = Odyssey.Dtos.Journal.RecurrenceFrequency.Weekly, DaysOfWeek = Odyssey.Dtos.Journal.DaysOfWeekFlags.Monday,
        OccurrenceCount = 12,
    };

    private static RecurrencePatternService Patterns(OdysseyContext context) =>
        new(context, new StubJournalLimitsLookup(), new FakeTimeProvider(Now));

    private static CalendarEventService Events(OdysseyContext context) =>
        new(context, new StubJournalLimitsLookup(), new FakeTimeProvider(Now));

    private static CalendarIcsService Ics(OdysseyContext context) =>
        new(context, NullLogger<CalendarIcsService>.Instance, new ImportLimits(), new StubJournalLimitsLookup(), new FakeTimeProvider(Now));

    // True when the write went through; false for the loser of a race, which gets a 409 and never a 500.
    private static async Task<bool> Expect409OrSuccess(Func<Task> work)
    {
        try
        {
            await work();
            return true;
        }
        catch (DomainException ex) when (ex.StatusCode == 409)
        {
            return false;
        }
    }

    private async Task<DbContextOptions<OdysseyContext>> OptionsAsync(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        // A fresh copy of the migrated schema per test, so one test's series never reach another's backfill.
        var connectionString = await fixture.CreateMigratedDatabaseAsync(Database);
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), OdysseyDatabaseRetry.Configure)
            .AddInterceptors(interceptors)
            .Options;
        await using var migrating = new OdysseyContext(options);
        await AttributionUsers.EnsureAsync(migrating, User);
        return options;
    }

    private static async Task<Guid> SeedCalendarAsync(DbContextOptions<OdysseyContext> options)
    {
        await using var context = new OdysseyContext(options);
        var calendar = new Odyssey.Context.Calendar { Name = $"Fidelity {Guid.NewGuid():N}", CreatedByUserId = User };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        return calendar.CalendarId;
    }

    private static async Task<(Guid CalendarId, Guid PatternId)> SeedKeyedSeriesAsync(DbContextOptions<OdysseyContext> options, int count)
    {
        var calendarId = await SeedCalendarAsync(options);
        await using var context = new OdysseyContext(options);
        var created = await Patterns(context).Create(Rule(calendarId) with { OccurrenceCount = count }, User, claims: LocationClaims.Trusted);
        return (calendarId, created.RecurrencePatternId);
    }

    // Each calendar's GET /api/calendar-events result, less the fields #519 added and the series membership (AC 12).
    private static async Task<List<string>> SnapshotAsync(DbContextOptions<OdysseyContext> options, params Guid[] calendarIds)
    {
        await using var context = new OdysseyContext(options);
        var page = await new CalendarEventService(context, new StubJournalLimitsLookup()).ListAsync(new CalendarEventsQueryParams
        {
            From = new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc), To = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            CalendarIds = calendarIds, Limit = 200,
        });
        var second = await new CalendarEventService(context, new StubJournalLimitsLookup()).ListAsync(new CalendarEventsQueryParams
        {
            From = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), To = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            CalendarIds = calendarIds, Limit = 200,
        });
        return page.Items.Concat(second.Items)
            .Select(e => $"{e.CalendarEventId}|{e.CalendarId}|{e.Title}|{e.StartDateTime:O}|{e.EndDateTime:O}|{e.IsAllDay}|{e.TimeZoneId}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // Fails the first command whose text contains the marker with a deadlock (1213), as InnoDB would, then lets every
    // later one through: what the shared strategy sees when a batch loses a deadlock once.
    private sealed class FirstInsertDeadlock(string marker) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int fired;

        public bool Fired => fired == 1;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal) && Interlocked.CompareExchange(ref fired, 1, 0) == 0)
            {
                throw (MySqlException)Activator.CreateInstance(
                    typeof(MySqlException), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, [MySqlErrorCode.LockDeadlock, "Deadlock found when trying to get lock (injected)"], null)!;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ImportLimits : IImportExportLimitsLookup
    {
        public Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImportExportLimits(
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                200_000, SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences, 92, 200, 100, 64L * 1024 * 1024,
                IsDegraded: false));
    }
}
