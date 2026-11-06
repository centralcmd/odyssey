using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The hourly read's day mode (issue #414): the rows of one point-local date, the overlap read and the
/// suppression rule, the day's bounds in the point's zone, the range check that runs before any date arithmetic,
/// and the service's own re-check of the query's mode.
/// </summary>
public class WeatherDayHoursTests
{
    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // ── DayRows (pure) ────────────────────────────────────────────────────────────────────────────

    private static WeatherForecastStep Step(DateTime time, byte period) => new()
    {
        TimeUtc = time,
        PeriodHours = period,
        SiteVersion = 1,
        RunProviderUpdatedAt = Noon,
        RunFetchedAtUtc = Noon,
    };

    [Fact]
    public void An_older_runs_block_is_dropped_where_newer_hourly_steps_overlap_it_and_its_first_hour_is_a_gap()
    {
        var day = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
        // An older run's 12:00–18:00 block beside a newer run's hourly steps from 13:00 (issue #414 AC 4).
        var effective = new List<WeatherForecastStep> { Step(day.AddHours(12), 6) };
        effective.AddRange(Enumerable.Range(13, 5).Select(h => Step(day.AddHours(h), 1)));
        effective.Add(Step(day.AddHours(18), 6));

        var rows = WeatherComposer.DayRows(effective, day, day.AddDays(1));

        Assert.DoesNotContain(rows, r => r.TimeUtc == day.AddHours(12));
        Assert.Equal([13, 14, 15, 16, 17, 18], rows.Select(r => r.TimeUtc.Hour));
        AssertNoOverlap(rows);
    }

    [Fact]
    public void A_step_that_starts_before_the_window_and_reaches_into_it_is_a_row()
    {
        var midnight = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
        var rows = WeatherComposer.DayRows([Step(midnight.AddHours(-4), 6), Step(midnight.AddHours(-10), 6)], midnight, midnight.AddDays(1));

        Assert.Equal(midnight.AddHours(-4), Assert.Single(rows).TimeUtc);
    }

    [Fact]
    public void A_finer_step_that_starts_before_a_coarser_one_still_suppresses_it()
    {
        var start = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
        var rows = WeatherComposer.DayRows([Step(start.AddHours(6), 12), Step(start.AddHours(3), 6)], start, start.AddDays(1));

        Assert.Equal(start.AddHours(3), Assert.Single(rows).TimeUtc);
    }

    [Fact]
    public void Rows_never_overlap_for_any_mix_of_one_six_and_twelve_hour_steps()
    {
        var start = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
        var random = new Random(414);
        byte[] periods = [1, 6, 12];
        for (var trial = 0; trial < 500; trial++)
        {
            // One effective step per instant, as EffectiveSteps guarantees; anything else is fair game.
            var effective = Enumerable.Range(-12, 48)
                .Where(_ => random.Next(3) > 0)
                .Select(h => Step(start.AddHours(h), periods[random.Next(periods.Length)]))
                .ToList();

            var rows = WeatherComposer.DayRows(effective, start, start.AddDays(1));

            AssertNoOverlap(rows);
            Assert.All(rows, r => Assert.True(r.TimeUtc < start.AddDays(1) && r.TimeUtc.AddHours(r.PeriodHours) > start));
        }
    }

    private static void AssertNoOverlap(IReadOnlyList<WeatherForecastStep> rows)
    {
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.True(rows[i].TimeUtc >= rows[i - 1].TimeUtc.AddHours(rows[i - 1].PeriodHours),
                $"{rows[i - 1].TimeUtc:O} +{rows[i - 1].PeriodHours} h overlaps {rows[i].TimeUtc:O}");
        }
    }

    // ── The service ───────────────────────────────────────────────────────────────────────────────

    private static WeatherHourlyQuery Day(Guid pointId, DateOnly date) =>
        new() { WeatherPointId = pointId, Date = date.ToString("yyyy-MM-dd") };

    private static ForecastFetchResult Payload(DateTime issuedUtc, params (DateTime Time, byte Period)[] steps) =>
        new(WeatherFetchOutcome.Ok,
            new ForecastPayload(issuedUtc, steps.Select(s => new ForecastStepData(
                s.Time, s.Period, 7, 70, 50, 1010, 3, 6, 180, 0.5m, "cloudy", 0.5m, 20, null, null)).ToList()),
            ExpiresUtc: issuedUtc.AddMinutes(30),
            LastModified: issuedUtc.ToString("R"));

    private static async Task<TestPoint> PointWithRunAsync(WeatherTestHarness h, ForecastFetchResult run, string zone = "Europe/Oslo")
    {
        var point = await h.EnableAsync(zone: zone);
        h.Forecasts.Respond = _ => Task.FromResult(run);
        await h.Fetch().FetchForecastAsync(point.Id, WeatherFetchMode.Scheduled, CancellationToken.None);
        return point;
    }

    [Fact]
    public async Task Tomorrow_is_the_whole_point_local_day_with_each_row_in_the_points_clock()
    {
        using var h = new WeatherTestHarness();
        var point = await PointWithRunAsync(h, WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 48, 7));

        var response = await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, new DateOnly(2026, 10, 24)), "u1", CancellationToken.None);

        Assert.Equal(WeatherStatus.Ok, response.Status);
        Assert.Equal(new DateOnly(2026, 10, 24), response.Day!.Date);
        Assert.False(response.Day.IsToday);
        // Oslo is on summer time until 25 October: the 24th runs 22:00Z to 22:00Z.
        Assert.Equal(new DateTime(2026, 10, 23, 22, 0, 0, DateTimeKind.Utc), response.Day.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc), response.Day.ToUtc);
        Assert.Equal(24m, response.Day.LengthHours);
        Assert.Equal(24, response.Steps.Count);
        Assert.All(response.Steps, s => Assert.Equal(TimeSpan.FromHours(2), s.LocalStart!.Value.Offset));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.FromHours(2)), response.Steps[0].LocalStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 1, 0, 0, TimeSpan.FromHours(2)), response.Steps[0].LocalEnd);
    }

    [Fact]
    public async Task Today_starts_at_the_step_covering_now_and_no_row_has_ended()
    {
        using var h = new WeatherTestHarness();
        var point = await PointWithRunAsync(h, WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon.AddHours(-3), 36, 7));
        h.Time.Advance(TimeSpan.FromMinutes(30));
        var now = h.Time.GetUtcNow().UtcDateTime;

        var response = await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, new DateOnly(2026, 10, 23)), "u1", CancellationToken.None);

        Assert.True(response.Day!.IsToday);
        Assert.All(response.Steps, s => Assert.True(s.TimeUtc.AddHours(s.PeriodHours) > now));
        Assert.True(response.Steps[0].TimeUtc <= now);
        Assert.Equal(Noon, response.Steps[0].TimeUtc);
    }

    [Fact]
    public async Task A_day_with_a_dst_change_is_25_hours_and_a_block_across_it_carries_two_offsets()
    {
        using var h = new WeatherTestHarness();
        // Oslo leaves summer time at 01:00Z on 25 October 2026: the 25th runs 22:00Z on the 24th to 23:00Z.
        var dayStart = new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        var point = await PointWithRunAsync(h, Payload(Noon.AddMinutes(-30),
            (dayStart, 1), (dayStart.AddHours(1), 1), (dayStart.AddHours(2), 6), (dayStart.AddHours(8), 6),
            (dayStart.AddHours(14), 6), (dayStart.AddHours(20), 6)));

        var response = await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, new DateOnly(2026, 10, 25)), "u1", CancellationToken.None);

        Assert.Equal(25m, response.Day!.LengthHours);
        var across = response.Steps.Single(s => s.TimeUtc == dayStart.AddHours(2));
        Assert.Equal(TimeSpan.FromHours(2), across.LocalStart!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(1), across.LocalEnd!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(2)), across.LocalStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.FromHours(1)), across.LocalEnd);
    }

    [Fact]
    public async Task A_block_from_the_evening_before_reaches_into_the_day_and_starts_on_the_previous_date()
    {
        using var h = new WeatherTestHarness();
        var tomorrowStart = new DateTime(2026, 10, 23, 22, 0, 0, DateTimeKind.Utc);
        var point = await PointWithRunAsync(h, Payload(Noon.AddMinutes(-30), (tomorrowStart.AddHours(-4), 6), (tomorrowStart.AddHours(2), 6)));

        var response = await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, new DateOnly(2026, 10, 24)), "u1", CancellationToken.None);

        var first = response.Steps[0];
        Assert.Equal(tomorrowStart.AddHours(-4), first.TimeUtc);
        Assert.Equal(new DateOnly(2026, 10, 23), DateOnly.FromDateTime(first.LocalStart!.Value.DateTime));
    }

    [Fact]
    public async Task The_days_bounds_follow_the_points_zone_not_the_servers_or_the_callers()
    {
        using var h = new WeatherTestHarness();
        var point = await PointWithRunAsync(h, WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 48, 7), "Pacific/Auckland");

        // 12:00Z on the 23rd is 01:00 on the 24th in Auckland (+13): that is the point's today.
        var response = await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, new DateOnly(2026, 10, 25)), "u1", CancellationToken.None);

        Assert.Equal(new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc), response.Day!.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 25, 11, 0, 0, DateTimeKind.Utc), response.Day.ToUtc);
        Assert.Equal(TimeSpan.FromHours(13), response.Steps[0].LocalStart!.Value.Offset);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(WeatherRules.DayModeMaxDaysAhead, false)]
    [InlineData(WeatherRules.DayModeMaxDaysAhead + 1, true)]
    public async Task Only_today_through_today_plus_nine_is_in_range(int offset, bool outOfRange)
    {
        using var h = new WeatherTestHarness();
        var point = await PointWithRunAsync(h, WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7));

        var response = await h.Read(h.NewContext()).HourlyAsync(
            Day(point.Id, new DateOnly(2026, 10, 23).AddDays(offset)), "u1", CancellationToken.None);

        Assert.Equal(outOfRange, response.Status == WeatherStatus.OutOfRange);
        Assert.Equal(outOfRange, response.Day!.FromUtc is null);
    }

    [Theory]
    [InlineData("Asia/Tokyo", "0001-01-01")]
    [InlineData("Asia/Tokyo", "9999-12-31")]
    [InlineData("America/Los_Angeles", "0001-01-01")]
    [InlineData("America/Los_Angeles", "9999-12-31")]
    public async Task An_extreme_date_is_OutOfRange_with_no_bounds_and_never_throws(string zone, string date)
    {
        using var h = new WeatherTestHarness();
        var point = await PointWithRunAsync(h, WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 6, 7), zone);
        var calls = h.Forecasts.Calls;

        var response = await h.Read(h.NewContext()).HourlyAsync(
            new WeatherHourlyQuery { WeatherPointId = point.Id, Date = date }, "u1", CancellationToken.None);

        Assert.Equal(WeatherStatus.OutOfRange, response.Status);
        Assert.Null(response.Day!.FromUtc);
        Assert.Null(response.Day.ToUtc);
        Assert.Null(response.Day.LengthHours);
        Assert.Equal(calls, h.Forecasts.Calls);
    }

    [Fact]
    public async Task A_half_hour_dst_shift_gives_a_23_and_a_half_hour_day()
    {
        // Lord Howe Island moves its clocks 30 minutes forward on the first Sunday of October.
        using var h = new WeatherTestHarness(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var point = await h.EnableAsync(zone: "Australia/Lord_Howe");

        var response = await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, new DateOnly(2026, 10, 4)), "u1", CancellationToken.None);

        Assert.Equal(23.5m, response.Day!.LengthHours);
    }

    [Fact]
    public async Task A_point_with_no_site_rows_is_NoData_with_no_bounds_for_any_date()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync();
        await using (var context = h.NewContext())
        {
            context.WeatherPointSites.RemoveRange(await context.WeatherPointSites.Where(s => s.WeatherPointId == point.Id).ToListAsync());
            await context.SaveChangesAsync();
        }

        foreach (var date in new[] { new DateOnly(2026, 10, 24), new DateOnly(1999, 1, 1), DateOnly.MaxValue })
        {
            var response = await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, date), "u1", CancellationToken.None);
            Assert.Equal(WeatherStatus.NoData, response.Status);
            Assert.Null(response.Day!.FromUtc);
        }
    }

    [Theory]
    [InlineData(null, null, null, "fromUtc")]
    [InlineData("2026-10-24", "2026-10-24T00:00:00Z", null, "date")]
    [InlineData(null, "2026-10-24T00:00:00Z", null, "toUtc")]
    [InlineData("2026-13-40", null, null, "date")]
    public async Task The_service_rechecks_the_mode_for_a_caller_that_skipped_model_validation(
        string? date, string? fromUtc, string? toUtc, string key)
    {
        using var h = new WeatherTestHarness();
        var query = new WeatherHourlyQuery
        {
            Date = date,
            FromUtc = fromUtc is null ? null : DateTime.Parse(fromUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            ToUtc = toUtc is null ? null : DateTime.Parse(toUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
        };

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => h.Read().HourlyAsync(query, "u1", CancellationToken.None));
        Assert.Contains(key, error.Errors!.Keys);
    }

    [Fact]
    public async Task A_future_day_fetches_on_demand_like_a_range_read_and_an_out_of_range_one_never_does()
    {
        using var h = new WeatherTestHarness();
        var point = await h.EnableAsync(tracked: false);
        h.Forecasts.Respond = _ => Task.FromResult(WeatherTestHarness.Run(Noon.AddMinutes(-30), Noon, 48, 7));

        await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, new DateOnly(2026, 11, 20)), "u1", CancellationToken.None);
        Assert.Equal(0, h.Forecasts.Calls);

        var response = await h.Read(h.NewContext()).HourlyAsync(Day(point.Id, new DateOnly(2026, 10, 24)), "u1", CancellationToken.None);
        Assert.Equal(1, h.Forecasts.Calls);
        Assert.Equal(WeatherStatus.Ok, response.Status);
    }
}
