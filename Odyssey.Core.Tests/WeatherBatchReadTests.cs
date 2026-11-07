using Odyssey.Context;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #430 — the event and entry weather batch reads, the shared event-weather rules and the single calendar
/// read's switch to the overlap rule. The batch must answer exactly what the single read answers (except where the
/// single read would first have fetched on demand), never call MET, and compose one summary per event.
/// </summary>
public class WeatherBatchReadTests
{
    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // ── Journal ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_journal_batch_answers_what_the_single_read_answers_in_request_order()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home", tracked: false);
        var cabin = await h.EnableAsync("Cabin", tracked: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var day = new DateOnly(2026, 10, 20);
        await AddDayAsync(h, home.Id, day, observedMax: 8m);
        await AddDayAsync(h, cabin.Id, day, observedMax: 3m);
        var placeless = await AddEntryAsync(h, null, Noon.AddDays(-3));
        var atCabin = await AddEntryAsync(h, cabin.LocationId, Noon.AddDays(-3));
        var noData = await AddEntryAsync(h, null, Noon.AddDays(-30));

        var batch = await h.Batch().JournalEntriesAsync([atCabin, Guid.NewGuid(), placeless, noData], "u1", CancellationToken.None);

        Assert.Equal([atCabin, placeless, noData], batch.Items.Select(i => i.JournalEntryId));
        foreach (var item in batch.Items)
        {
            var single = (await h.Read(h.NewContext()).JournalEntryAsync(item.JournalEntryId, null, "u1", CancellationToken.None))!;
            Assert.Equal(single.Status, item.Status);
            Assert.Equal(single.ResolvedFrom, item.ResolvedFrom);
            Assert.Equal(single.Point, item.Point);
            Assert.Equal(single.Date, item.Date);
            Assert.Equal(single.Day, item.Day);
        }

        Assert.Equal((WeatherResolvedFrom.Linked, 3m), (batch.Items[0].ResolvedFrom!.Value, batch.Items[0].Day!.Observed!.MaxTempC!.Value));
        Assert.Equal((WeatherResolvedFrom.Household, 8m), (batch.Items[1].ResolvedFrom!.Value, batch.Items[1].Day!.Observed!.MaxTempC!.Value));
        Assert.Equal(WeatherStatus.NoData, batch.Items[2].Status);
    }

    [Fact]
    public async Task A_duplicated_id_yields_one_item_and_the_journal_never_reads_OutOfRange()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home", tracked: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var ahead = await AddEntryAsync(h, null, Noon.AddDays(30));

        var batch = await h.Batch().JournalEntriesAsync([ahead, ahead], "u1", CancellationToken.None);

        var item = Assert.Single(batch.Items);
        Assert.Equal(WeatherStatus.NoData, item.Status);
    }

    [Fact]
    public async Task A_batch_with_no_resolvable_point_reads_NoPlace_with_nothing_else()
    {
        using var h = new WeatherTestHarness();
        var entry = await AddEntryAsync(h, null, Noon.AddDays(-1));

        var item = Assert.Single((await h.Batch().JournalEntriesAsync([entry], "u1", CancellationToken.None)).Items);

        Assert.Equal(WeatherStatus.NoPlace, item.Status);
        Assert.Null(item.Point);
        Assert.Null(item.Day);
    }

    [Fact]
    public async Task The_batch_never_fetches_even_for_a_future_record_on_an_untracked_point()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home", tracked: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var entry = await AddEntryAsync(h, null, Noon.AddDays(2));
        var ev = await AddEventAsync(h, null, Noon.AddDays(2), Noon.AddDays(2).AddHours(1));
        var before = h.Forecasts.Calls;

        await h.Batch().JournalEntriesAsync([entry], "u1", CancellationToken.None);
        await h.Batch().CalendarEventsAsync([ev], "u1", CancellationToken.None);

        Assert.Equal(before, h.Forecasts.Calls);
    }

    [Fact]
    public async Task The_single_calendar_read_still_fetches_on_demand_exactly_once()
    {
        // AC 29: the overlap switch left the single read's on-demand trigger alone.
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home", tracked: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon, Noon, 72, 7m));
        var ev = await AddEventAsync(h, null, Noon.AddDays(1), Noon.AddDays(1).AddHours(2));
        var before = h.Forecasts.Calls;

        var single = (await h.Read(h.NewContext()).CalendarEventAsync(ev, null, "u1", CancellationToken.None))!;

        Assert.Equal(before + 1, h.Forecasts.Calls);
        Assert.Equal(WeatherStatus.Ok, single.Status);
    }

    // ── Calendar: timed ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_coarse_block_overlapped_by_finer_steps_is_not_counted_twice_in_either_read()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home", tracked: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var start = Noon.AddDays(-1).AddHours(-2); // 10:00 yesterday
        var older = Noon.AddDays(-2);
        var newer = Noon.AddDays(-1).AddHours(-6);
        await AddStepAsync(h, home.Id, older, start.AddHours(-1), 6, 9m, 2.0m); // 09–15, the coarse block
        await AddStepAsync(h, home.Id, newer, start, 1, 6m, 0.2m);
        await AddStepAsync(h, home.Id, newer, start.AddHours(1), 1, 9m, 0.2m);
        await AddStepAsync(h, home.Id, newer, start.AddHours(2), 1, 7m, 0.0m);
        var ev = await AddEventAsync(h, null, start, start.AddHours(3));

        var item = Assert.Single((await h.Batch().CalendarEventsAsync([ev], "u1", CancellationToken.None)).Items);
        var single = (await h.Read(h.NewContext()).CalendarEventAsync(ev, null, "u1", CancellationToken.None))!;

        Assert.Equal(WeatherStatus.Ok, item.Status);
        Assert.Equal((0.4m, 3), (item.Summary!.PrecipitationMm!.Value, item.Summary.PrecipitationHours!.Value));
        Assert.Equal((6m, 9m), (item.Summary.MinTempC!.Value, item.Summary.MaxTempC!.Value));
        Assert.Equal(WeatherSummaryBasis.Hourly, item.Summary.Basis);
        Assert.Equal([start, start.AddHours(1), start.AddHours(2)], single.Steps.Select(s => s.TimeUtc));
        Assert.All(single.Steps, s => Assert.Equal(1, s.PeriodHours));
    }

    [Fact]
    public async Task A_step_straddling_the_start_answers_both_reads()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home", tracked: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var six = Noon.AddDays(-1).AddHours(-6); // 06:00 yesterday
        await AddStepAsync(h, home.Id, six.AddHours(-3), six, 6, null, 1.5m, symbol: "rain", tempMin: 4m, tempMax: 9m);
        var ev = await AddEventAsync(h, null, six.AddHours(1.5), six.AddHours(2.5)); // 07:30–08:30

        var item = Assert.Single((await h.Batch().CalendarEventsAsync([ev], "u1", CancellationToken.None)).Items);
        var single = (await h.Read(h.NewContext()).CalendarEventAsync(ev, null, "u1", CancellationToken.None))!;

        Assert.Equal(WeatherStatus.Ok, item.Status);
        Assert.Equal("rain", item.Summary!.SymbolCode);
        Assert.Equal((4m, 9m), (item.Summary.MinTempC!.Value, item.Summary.MaxTempC!.Value));
        Assert.Equal((1.5m, 6), (item.Summary.PrecipitationMm!.Value, item.Summary.PrecipitationHours!.Value));
        Assert.Equal(WeatherStatus.Ok, single.Status);
        Assert.Equal(six, Assert.Single(single.Steps).TimeUtc);
    }

    [Fact]
    public async Task A_zero_length_event_gets_the_hour_that_covers_its_start()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home", tracked: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var at = Noon.AddDays(-1);
        await AddStepAsync(h, home.Id, at.AddHours(-6), at, 1, 5m, 0m);
        var ev = await AddEventAsync(h, null, at, at);

        var item = Assert.Single((await h.Batch().CalendarEventsAsync([ev], "u1", CancellationToken.None)).Items);
        var single = (await h.Read(h.NewContext()).CalendarEventAsync(ev, null, "u1", CancellationToken.None))!;

        Assert.Equal(WeatherStatus.Ok, item.Status);
        Assert.Equal(at, Assert.Single(single.Steps).TimeUtc);
    }

    // ── Calendar: all-day and OutOfRange ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_all_day_summary_prefers_the_measured_half_of_its_first_day()
    {
        using var h = new WeatherTestHarness();
        var home = await h.EnableAsync("Home", tracked: false);
        h.Settings.Value = h.Settings.Value with { HouseholdDefaultPointId = home.Id };
        var first = new DateOnly(2026, 10, 20);
        await AddDayAsync(h, home.Id, first, observedMax: 8m, forecastMax: 10m);
        await AddDayAsync(h, home.Id, first.AddDays(1), forecastMax: 11m);
        var measured = await AddEventAsync(h, null, Midnight(first), Midnight(first.AddDays(2)), allDay: true);
        var forecastOnly = await AddEventAsync(h, null, Midnight(first.AddDays(1)), Midnight(first.AddDays(2)), allDay: true);

        var items = (await h.Batch().CalendarEventsAsync([measured, forecastOnly], "u1", CancellationToken.None)).Items;

        Assert.Equal((true, 8m, "cloudy"), (items[0].Summary!.Measured, items[0].Summary!.MaxTempC!.Value, items[0].Summary!.SymbolCode));
        Assert.Equal((false, 11m), (items[1].Summary!.Measured, items[1].Summary!.MaxTempC!.Value));
        Assert.All(items, i => Assert.Equal(WeatherSummaryBasis.Daily, i.Summary!.Basis));
    }

    [Fact]
    public async Task Far_ahead_is_OutOfRange_on_a_point_with_nothing_stored_and_NoData_on_one_with_data()
    {
        using var h = new WeatherTestHarness();
        var empty = await h.EnableAsync("Empty", tracked: false);
        var stored = await h.EnableAsync("Stored", tracked: false);
        await AddDayAsync(h, stored.Id, new DateOnly(2026, 10, 1), observedMax: 5m);
        var a = await AddEventAsync(h, empty.LocationId, Noon.AddDays(30), Noon.AddDays(30).AddHours(1));
        var b = await AddEventAsync(h, stored.LocationId, Noon.AddDays(30), Noon.AddDays(30).AddHours(1));

        var items = (await h.Batch().CalendarEventsAsync([a, b], "u1", CancellationToken.None)).Items;
        var single = (await h.Read(h.NewContext()).CalendarEventAsync(a, null, "u1", CancellationToken.None))!;

        Assert.Equal([WeatherStatus.OutOfRange, WeatherStatus.NoData], items.Select(i => i.Status));
        Assert.Equal(WeatherStatus.OutOfRange, single.Status);
        Assert.All(items, i => Assert.Null(i.Summary));
    }

    // ── Clusters and the query ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Clusters_join_near_members_and_split_far_ones_and_never_span_more_than_a_week()
    {
        var d = Noon;
        Assert.Single(WeatherBatchReadService.Clusters([(d, d.AddHours(1)), (d.AddDays(1), d.AddDays(1).AddHours(1))]));
        Assert.Equal(2, WeatherBatchReadService.Clusters([(d, d.AddHours(1)), (d.AddDays(400), d.AddDays(400).AddHours(1))]).Count);
        Assert.Equal(2, WeatherBatchReadService.Clusters(
            Enumerable.Range(0, 9).Select(i => (d.AddDays(i), d.AddDays(i).AddHours(1)))).Count);
        Assert.Single(WeatherBatchReadService.Clusters([(d, d.AddDays(20))]));
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "not-a-guid" }, false)]
    [InlineData(new[] { "{6f1c4a8e-0000-4000-8000-000000000001}" }, false)]
    [InlineData(new[] { "6f1c4a8e000040008000000000000001" }, false)]
    [InlineData(new[] { "6f1c4a8e-0000-4000-8000-000000000001", "6F1C4A8E-0000-4000-8000-000000000001" }, true)]
    public void The_batch_query_accepts_only_the_D_format_and_de_duplicates_parsed_ids(string[] ids, bool valid)
    {
        Assert.Equal(valid, WeatherBatchIds.TryParse(ids, out var parsed));
        if (valid)
        {
            Assert.Single(parsed);
        }
    }

    [Fact]
    public void The_batch_query_refuses_more_than_the_cap()
    {
        Assert.True(WeatherBatchIds.TryParse(Ids(WeatherRules.BatchMaxIds), out _));
        Assert.False(WeatherBatchIds.TryParse(Ids(WeatherRules.BatchMaxIds + 1), out _));
        Assert.False(WeatherBatchIds.TryParse(null, out _));

        static string[] Ids(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static DateTime Midnight(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static async Task<Guid> AddEntryAsync(WeatherTestHarness h, Guid? locationId, DateTime entryDate)
    {
        var id = Guid.NewGuid();
        h.Context.JournalEntries.Add(new JournalEntry
        {
            JournalEntryId = id, ExternalUid = $"urn:uuid:{id}", Title = "Entry", Content = "Text",
            EntryDate = entryDate, LocationId = locationId,
        });
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();
        return id;
    }

    private static async Task<Guid> AddEventAsync(
        WeatherTestHarness h, Guid? locationId, DateTime start, DateTime end, bool allDay = false)
    {
        var id = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        h.Context.Calendars.Add(new Calendar { CalendarId = calendarId, Name = $"Calendar {id:N}" });
        h.Context.CalendarEvents.Add(new CalendarEvent
        {
            CalendarEventId = id, CalendarId = calendarId, Title = "Event", LocationId = locationId,
            StartDateTime = start, EndDateTime = end, IsAllDay = allDay,
        });
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();
        return id;
    }

    private static async Task AddStepAsync(
        WeatherTestHarness h, Guid pointId, DateTime issued, DateTime timeUtc, byte period, decimal? temperature, decimal? precipitation,
        string symbol = "cloudy", decimal? tempMin = null, decimal? tempMax = null)
    {
        var run = new WeatherForecastRun
        {
            WeatherForecastRunId = Guid.NewGuid(), WeatherPointId = pointId, SiteVersion = 1,
            Provider = WeatherProvider.MetNorway, ProviderUpdatedAt = issued, FetchedAtUtc = issued,
        };
        run.Steps.Add(new WeatherForecastStep
        {
            WeatherForecastRunId = run.WeatherForecastRunId, WeatherPointId = pointId, SiteVersion = 1,
            RunProviderUpdatedAt = issued, RunFetchedAtUtc = issued, TimeUtc = timeUtc, PeriodHours = period,
            AirTemperatureC = temperature, PrecipitationMm = precipitation, SymbolCode = symbol, TempMinC = tempMin, TempMaxC = tempMax,
        });
        h.Context.WeatherForecastRuns.Add(run);
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();
    }

    private static async Task AddDayAsync(
        WeatherTestHarness h, Guid pointId, DateOnly date, decimal? observedMax = null, decimal? forecastMax = null)
    {
        h.Context.WeatherDays.Add(new WeatherDay
        {
            WeatherPointId = pointId,
            LocalDate = date,
            ForecastSymbolCode = forecastMax is null ? null : "cloudy",
            ForecastMaxTempC = forecastMax,
            ForecastMinTempC = forecastMax is null ? null : forecastMax - 5,
            ForecastCoverageHours = forecastMax is null ? null : 24,
            ObservedMaxTempC = observedMax,
            ObservedMinTempC = observedMax is null ? null : observedMax - 5,
            ObservedFetchedAtUtc = observedMax is null ? null : Noon,
        });
        await h.Context.SaveChangesAsync();
        h.Context.ChangeTracker.Clear();
    }
}
