// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a constant, a number or a
// Guid this test generated itself — there is no external input.
#pragma warning disable EF1002

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;
using Xunit.Abstractions;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #430: the batch reads' query budget (AC 8), the clustered reads' row bounds
/// (AC 9, AC 26), the <c>OutOfRange</c> existence check (AC 24), DST date mapping (AC 22), purges (AC 20) and the
/// §10 performance targets — all of which depend on what Pomelo sends to MariaDB, which the EF InMemory provider
/// cannot show.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class WeatherBatchRelationalTests(MariaDbFixture fixture, ITestOutputHelper output)
{
    private const string Database = "odyssey_weather_batch";
    private const string AdminId = "weather-admin";
    private const string UserId = "weather-reader";

    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // ── Query budget (AC 8) ──────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Fifty_journal_ids_over_three_points_stay_within_the_query_budget()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var places = await PointsWithPlacesAsync(3);
        var ids = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            ids.Add(await EntryAsync(places[i % 3], Noon.AddDays(-3).AddHours(i % 24)));
        }

        var capture = new CommandCapture();
        await using var context = NewContext(capture);
        var batch = await Batch(context).JournalEntriesAsync(ids, UserId, CancellationToken.None);

        Assert.Equal(50, batch.Items.Count);
        Assert.All(batch.Items, i => Assert.Equal(WeatherResolvedFrom.Linked, i.ResolvedFrom));
        Assert.True(capture.Commands.Count <= 6 + 3 * 3 + 3, Describe(capture));
    }

    [SkippableFact]
    public async Task Fifty_timed_calendar_ids_over_three_points_stay_within_the_query_budget()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var places = await PointsWithPlacesAsync(3);
        var ids = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            var start = Noon.AddDays(1 + i % 5).AddHours(i % 8);
            ids.Add(await EventAsync(places[i % 3], start, start.AddHours(2)));
        }

        var capture = new CommandCapture();
        await using var context = NewContext(capture);
        var batch = await Batch(context).CalendarEventsAsync(ids, UserId, CancellationToken.None);

        Assert.Equal(50, batch.Items.Count);
        Assert.True(capture.Commands.Count <= 6 + 3 * 3 + 3, Describe(capture));
    }

    // ── Row bounds (AC 9, AC 26) ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Two_events_400_days_apart_read_two_small_windows_not_the_span_between()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var (pointId, placeId) = (await PointsAndPlacesAsync(1)).Single();
        var origin = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedRunsAsync(pointId, origin, runs: 1, runSpacingHours: 0, stepsPerRun: 400 * 24);
        var first = await EventAsync(placeId, origin.AddHours(10), origin.AddHours(11));
        var last = await EventAsync(placeId, origin.AddDays(399).AddHours(10), origin.AddDays(399).AddHours(11));

        var capture = new CommandCapture();
        await using var context = NewContext(capture);
        var batch = await Batch(context).CalendarEventsAsync([first, last], UserId, CancellationToken.None);

        Assert.All(batch.Items, i => Assert.Equal(WeatherStatus.Ok, i.Status));
        Assert.Equal(2, capture.StepQueries);
        Assert.True(capture.StepRows <= 2 * (7 * 24 + 12), $"{capture.StepRows} step rows read");
    }

    [SkippableFact]
    public async Task Fifty_far_apart_events_on_a_four_runs_a_day_point_stay_within_the_worst_case_bound()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var (pointId, placeId) = (await PointsAndPlacesAsync(1)).Single();
        var origin = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedRunsAsync(pointId, origin, runs: 4 * 155, runSpacingHours: 6, stepsPerRun: 36);
        var ids = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            var start = origin.AddDays(1 + 3 * i).AddHours(9);
            ids.Add(await EventAsync(placeId, start, start.AddHours(1)));
        }

        var capture = new CommandCapture();
        await using var context = NewContext(capture);
        var batch = await Batch(context).CalendarEventsAsync(ids, UserId, CancellationToken.None);

        Assert.All(batch.Items, i => Assert.Equal(WeatherStatus.Ok, i.Status));
        Assert.Equal(50, capture.StepQueries);
        Assert.True(capture.StepRows <= 50 * 36 * 4, $"{capture.StepRows} step rows read");
        Assert.True(capture.Commands.Count <= 6 + 3 * 1 + 50, Describe(capture));
    }

    // ── OutOfRange (AC 24) ───────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Five_far_ahead_events_on_one_point_run_the_existence_check_once()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var (empty, emptyPlace) = (await PointsAndPlacesAsync(1)).Single();
        var (stored, storedPlace) = (await PointsAndPlacesAsync(1)).Single();
        await DayAsync(stored, new DateOnly(2026, 10, 1), observedMax: 9m);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await EventAsync(emptyPlace, Noon.AddDays(30 + i), Noon.AddDays(30 + i).AddHours(1)));
        }

        var onStored = await EventAsync(storedPlace, Noon.AddDays(30), Noon.AddDays(30).AddHours(1));

        var capture = new CommandCapture();
        await using var context = NewContext(capture);
        var batch = await Batch(context).CalendarEventsAsync([.. ids, onStored], UserId, CancellationToken.None);

        Assert.All(batch.Items.Take(5), i => Assert.Equal(WeatherStatus.OutOfRange, i.Status));
        Assert.Equal(WeatherStatus.NoData, batch.Items[5].Status);
        Assert.Equal(1, capture.Commands.Count(c => c.Contains("`WeatherDays`", StringComparison.Ordinal)));
        Assert.NotEqual(Guid.Empty, empty);

        await using var single = NewContext();
        var read = (await Read(single).CalendarEventAsync(ids[0], null, UserId, CancellationToken.None))!;
        Assert.Equal(WeatherStatus.OutOfRange, read.Status);
    }

    // ── DST (AC 22) ──────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Entries_either_side_of_a_DST_change_map_to_the_dates_the_single_read_maps_them_to()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var (_, placeId) = (await PointsAndPlacesAsync(1)).Single();
        // 00:30 local on either side of both 2026 changes in Europe/Oslo (29 March and 25 October).
        var instants = new[]
        {
            (Utc: new DateTime(2026, 3, 28, 23, 30, 0, DateTimeKind.Utc), Local: new DateOnly(2026, 3, 29)),
            (Utc: new DateTime(2026, 3, 29, 22, 30, 0, DateTimeKind.Utc), Local: new DateOnly(2026, 3, 30)),
            (Utc: new DateTime(2026, 10, 24, 22, 30, 0, DateTimeKind.Utc), Local: new DateOnly(2026, 10, 25)),
            (Utc: new DateTime(2026, 10, 25, 23, 30, 0, DateTimeKind.Utc), Local: new DateOnly(2026, 10, 26)),
        };
        var ids = new List<Guid>();
        foreach (var (utc, _) in instants)
        {
            ids.Add(await EntryAsync(placeId, utc));
        }

        await using var context = NewContext();
        var batch = await Batch(context).JournalEntriesAsync(ids, UserId, CancellationToken.None);

        Assert.Equal(instants.Select(i => (DateOnly?)i.Local), batch.Items.Select(i => i.Date));
        foreach (var item in batch.Items)
        {
            await using var single = NewContext();
            var read = (await Read(single).JournalEntryAsync(item.JournalEntryId, null, UserId, CancellationToken.None))!;
            Assert.Equal(read.Date, item.Date);
        }
    }

    // ── Purges (AC 20) ───────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_history_purge_drops_the_measured_half_and_a_point_delete_leaves_the_entry_without_a_place_point()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var (pointId, placeId) = (await PointsAndPlacesAsync(1)).Single();
        var date = new DateOnly(2026, 10, 20);
        await DayAsync(pointId, date, observedMax: 8m, forecastMax: 9m);
        var entry = await EntryAsync(placeId, date.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc));

        Assert.NotNull((await BatchItemAsync(entry)).Day!.Observed);

        await using (var context = NewContext())
        {
            await SetAsync(context, pointId, forecast: true, history: false);
        }

        await using (var context = NewContext())
        {
            Assert.True(await Admin(context).PurgeHistoryAsync(pointId, AdminId, CancellationToken.None));
        }

        var afterHistory = await BatchItemAsync(entry);
        Assert.Null(afterHistory.Day!.Observed);
        Assert.NotNull(afterHistory.Day.Forecast);

        await using (var context = NewContext())
        {
            await SetAsync(context, pointId, forecast: false, history: false);
        }

        await using (var context = NewContext())
        {
            Assert.True(await Admin(context).DeleteAsync(pointId, AdminId, CancellationToken.None));
        }

        var afterDelete = await BatchItemAsync(entry);
        Assert.Equal(WeatherStatus.NoPlace, afterDelete.Status);
        Assert.Null(afterDelete.Point);
    }

    // ── Performance (§10) ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The §10 targets measured once: P95 server time over 20 runs of each batch, failing at twice the target so a
    /// regression is caught without making the tier timing-flaky. Journal: 50 entries over 5 points, ≤ 5 clusters
    /// each, target 250 ms. Calendar: 50 timed events over 5 points within 62 days, target 350 ms.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Perf")]
    public async Task The_batch_reads_meet_their_P95_targets()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var places = await PointsAndPlacesAsync(5);
        var origin = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        var entries = new List<Guid>();
        var events = new List<Guid>();
        for (var p = 0; p < places.Count; p++)
        {
            var (pointId, placeId) = places[p];
            await SeedRunsAsync(pointId, origin, runs: 4 * 64, runSpacingHours: 6, stepsPerRun: 36);
            for (var d = 0; d < 62; d++)
            {
                await DayAsync(pointId, DateOnly.FromDateTime(origin.AddDays(d)), observedMax: 8m, forecastMax: 9m);
            }

            for (var i = 0; i < 10; i++)
            {
                entries.Add(await EntryAsync(placeId, origin.AddDays(i * 6).AddHours(10)));
                var start = origin.AddDays(i * 6).AddHours(9);
                events.Add(await EventAsync(placeId, start, start.AddHours(2)));
            }
        }

        var journal = await P95Async(context => Batch(context).JournalEntriesAsync(entries, UserId, CancellationToken.None));
        var calendar = await P95Async(context => Batch(context).CalendarEventsAsync(events, UserId, CancellationToken.None));
        output.WriteLine($"journal batch P95 {journal.TotalMilliseconds:0} ms (target 250); calendar batch P95 {calendar.TotalMilliseconds:0} ms (target 350)");

        Assert.True(journal < TimeSpan.FromMilliseconds(2 * 250), $"journal P95 {journal.TotalMilliseconds:0} ms");
        Assert.True(calendar < TimeSpan.FromMilliseconds(2 * 350), $"calendar P95 {calendar.TotalMilliseconds:0} ms");
    }

    private async Task<TimeSpan> P95Async(Func<OdysseyContext, Task> run)
    {
        var samples = new List<TimeSpan>();
        for (var i = 0; i < 22; i++)
        {
            await using var context = NewContext();
            var watch = Stopwatch.StartNew();
            await run(context);
            watch.Stop();
            if (i >= 2)
            {
                samples.Add(watch.Elapsed);
            }
        }

        samples.Sort();
        return samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private async Task<JournalEntryWeatherItem> BatchItemAsync(Guid entry)
    {
        await using var context = NewContext();
        return Assert.Single((await Batch(context).JournalEntriesAsync([entry], UserId, CancellationToken.None)).Items);
    }

    /// <summary><paramref name="count"/> tracked points, each with one place linked to it; returns the place ids.</summary>
    private async Task<List<Guid>> PointsWithPlacesAsync(int count) =>
        (await PointsAndPlacesAsync(count)).Select(p => p.PlaceId).ToList();

    private async Task<List<(Guid PointId, Guid PlaceId)>> PointsAndPlacesAsync(int count)
    {
        var result = new List<(Guid, Guid)>();
        for (var i = 0; i < count; i++)
        {
            var pointId = await EnabledPointAsync($"Point {i}");
            result.Add((pointId, await PlaceAsync($"Place {i}", pointId)));
        }

        return result;
    }

    private async Task<Guid> EnabledPointAsync(string name)
    {
        await using var context = NewContext();
        var point = await Admin(context).CreateAsync(new NewWeatherPoint
        {
            Name = name + " " + Guid.NewGuid().ToString("N")[..6],
            Latitude = 59.91273m,
            Longitude = 10.75027m,
            AltitudeMeters = 12,
            TimeZoneId = "Europe/Oslo",
            ForecastEnabled = true,
            HistoryEnabled = true,
            IsTracked = true,
        }, AdminId, CancellationToken.None);
        return point.WeatherPointId;
    }

    private async Task<Guid> PlaceAsync(string name, Guid pointId)
    {
        await using var context = NewContext();
        var location = new Location
        {
            LocationId = Guid.NewGuid(),
            Name = name + " " + Guid.NewGuid().ToString("N")[..6],
            Latitude = 59.91273m,
            Longitude = 10.75027m,
            AltitudeMeters = 12,
            TimeZoneId = "Europe/Oslo",
            Source = LocationSource.Manual,
            WeatherPointId = pointId,
            CreatedAt = Noon,
            UpdatedAt = Noon,
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        return location.LocationId;
    }

    private async Task<Guid> EntryAsync(Guid? placeId, DateTime entryDate)
    {
        await using var context = NewContext();
        var id = Guid.NewGuid();
        context.JournalEntries.Add(new JournalEntry
        {
            JournalEntryId = id, ExternalUid = $"urn:uuid:{id}", Title = "Entry", Content = "Text",
            EntryDate = entryDate, LocationId = placeId, CreatedAt = Noon, UpdatedAt = Noon,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> EventAsync(Guid? placeId, DateTime start, DateTime end)
    {
        await using var context = NewContext();
        var id = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        context.Calendars.Add(new Calendar { CalendarId = calendarId, Name = $"Calendar {id:N}", CreatedAt = Noon, UpdatedAt = Noon });
        context.CalendarEvents.Add(new CalendarEvent
        {
            CalendarEventId = id, CalendarId = calendarId, Title = "Event", LocationId = placeId,
            StartDateTime = start, EndDateTime = end,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task DayAsync(Guid pointId, DateOnly date, decimal? observedMax = null, decimal? forecastMax = null)
    {
        await using var context = NewContext();
        context.WeatherDays.Add(new WeatherDay
        {
            WeatherPointId = pointId,
            LocalDate = date,
            ForecastSymbolCode = forecastMax is null ? null : "cloudy",
            ForecastMaxTempC = forecastMax,
            ForecastMinTempC = forecastMax - 5,
            ForecastCoverageHours = forecastMax is null ? null : 24,
            ObservedMaxTempC = observedMax,
            ObservedMinTempC = observedMax - 5,
            ObservedFetchedAtUtc = observedMax is null ? null : Noon,
            UpdatedAtUtc = Noon,
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// <paramref name="runs"/> runs issued <paramref name="runSpacingHours"/> apart from <paramref name="origin"/>, each
    /// carrying <paramref name="stepsPerRun"/> hourly steps from its issue time — written in bulk.
    /// </summary>
    private async Task SeedRunsAsync(Guid pointId, DateTime origin, int runs, int runSpacingHours, int stepsPerRun)
    {
        await using var context = NewContext();
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var start = origin.ToString("yyyy-MM-dd HH:mm:ss");
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherForecastRuns` (`WeatherForecastRunId`, `WeatherPointId`, `SiteVersion`, `Provider`, `ProviderUpdatedAt`, `FetchedAtUtc`, `Latitude`, `Longitude`)
            SELECT UUID(), '{pointId}', 1, 1, DATE_ADD('{start}', INTERVAL seq * {runSpacingHours} HOUR),
                   DATE_ADD('{start}', INTERVAL seq * {runSpacingHours} * 60 + 1 MINUTE), 59.913, 10.750
            FROM seq_0_to_{runs - 1}
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `WeatherForecastSteps` (`WeatherForecastRunId`, `WeatherPointId`, `SiteVersion`, `RunProviderUpdatedAt`, `RunFetchedAtUtc`, `TimeUtc`, `PeriodHours`, `AirTemperatureC`, `PrecipitationMm`, `SymbolCode`)
            SELECT r.`WeatherForecastRunId`, r.`WeatherPointId`, 1, r.`ProviderUpdatedAt`, r.`FetchedAtUtc`,
                   DATE_ADD(r.`ProviderUpdatedAt`, INTERVAL s.seq HOUR), 1, 5.0, 0.1, 'cloudy'
            FROM `WeatherForecastRuns` r CROSS JOIN seq_0_to_{stepsPerRun - 1} s
            WHERE r.`WeatherPointId` = '{pointId}'
            """);
    }

    private static async Task SetAsync(OdysseyContext context, Guid pointId, bool forecast, bool history)
    {
        var current = (await Admin(context).GetAsync(pointId, true, CancellationToken.None))!;
        Assert.NotNull(await Admin(context).UpdateAsync(pointId, new UpdateWeatherPoint
        {
            Name = current.Name,
            Latitude = current.Site.Latitude,
            Longitude = current.Site.Longitude,
            AltitudeMeters = current.Site.AltitudeMeters,
            TimeZoneId = current.Site.TimeZoneId,
            ForecastEnabled = forecast,
            HistoryEnabled = history,
            IsTracked = current.IsTracked,
            SettingsVersion = current.SettingsVersion,
        }, AdminId, true, CancellationToken.None));
    }

    private static WeatherBatchReadService Batch(OdysseyContext context) => new(context, new Settings(), new FixedTime());

    private static WeatherReadService Read(OdysseyContext context) => new(context, Fetch(context), new Settings(), new FixedTime());

    private static WeatherFetchService Fetch(OdysseyContext context) =>
        new(context, new NoForecasts(), new NoObservations(), new Settings(), new NoSecrets(), new FixedTime());

    private static WeatherPointService Admin(OdysseyContext context) =>
        new(context, Fetch(context), new Settings(), new RelationalWeatherHistoryPurger(context), new FixedTime());

    private static string Describe(CommandCapture capture) =>
        $"{capture.Commands.Count} commands:\n" + string.Join("\n---\n", capture.Commands.Select(c => c.Length > 200 ? c[..200] : c));

    private async Task MigratedAsync()
    {
        await fixture.CreateMigratedDatabaseAsync(Database);
        await using var context = NewContext();
        await AttributionUsers.EnsureAsync(context, AdminId);
    }

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString));
        if (interceptor is not null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new OdysseyContext(builder.Options);
    }

    /// <summary>
    /// Records every command, and counts the rows read from every steps query by wrapping its reader — AC 9's
    /// "rows are counted by a reader that counts <c>Read()</c> calls".
    /// </summary>
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public int StepQueries { get; private set; }

        public int StepRows { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM `WeatherForecastSteps`", StringComparison.Ordinal))
            {
                return ValueTask.FromResult(result);
            }

            StepQueries++;
            return ValueTask.FromResult<DbDataReader>(new CountingReader(result, this));
        }
    }

    private sealed class CountingReader(DbDataReader inner, CommandCapture capture) : DbDataReader
    {
        public override bool Read()
        {
            var read = inner.Read();
            if (read)
            {
                capture.StepRows++;
            }

            return read;
        }

        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(cancellationToken);
            if (read)
            {
                capture.StepRows++;
            }

            return read;
        }

        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => inner.IsDBNullAsync(ordinal, cancellationToken);
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
        public override void Close() => inner.Close();
        public override Task CloseAsync() => inner.CloseAsync();
        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Noon);
    }

    private sealed class Settings : IWeatherSettingsLookup
    {
        public Task<WeatherSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WeatherSettings("ops@example.org", 60, 10, null, false));
    }

    private sealed class NoSecrets : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(SecretResult.NotSet);
    }

    /// <summary>Fails the test on any outbound forecast call: none of these reads may fetch.</summary>
    private sealed class NoForecasts : IWeatherForecastSource
    {
        public Task<ForecastFetchResult> FetchAsync(
            WeatherSiteQuery site, string contact, string? ifModifiedSince, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No outbound forecast call is expected here.");
    }

    private sealed class NoObservations : IWeatherObservationSource
    {
        public Task<StationLookupResult> FindStationAsync(WeatherSiteQuery site, WeatherMeasureGroup group, string contact, string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new StationLookupResult(true, null));

        public Task<ObservationFetchResult> FetchDailyAsync(
            IReadOnlyCollection<string> stationIds, IReadOnlyCollection<string> elements, DateOnly from, DateOnly to, string contact, string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new ObservationFetchResult(true, []));
    }
}
