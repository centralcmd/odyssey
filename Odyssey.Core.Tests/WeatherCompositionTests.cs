using Odyssey.Context;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The pure rules of issue #348 §3: the effective-step tie-break (AC 28), day composition, the backoff
/// schedule, and which site owns a date or an instant (B1, amendment 3; AC 9, 39, 40, 49).
/// </summary>
public class WeatherCompositionTests
{
    private static readonly DateTime T = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static WeatherForecastStep Step(
        DateTime time, int site = 1, DateTime? issued = null, DateTime? fetched = null, byte period = 1,
        decimal? temperature = 8, decimal? precipitation = null, string? symbol = "cloudy", long id = 0) => new()
    {
        WeatherForecastStepId = id,
        TimeUtc = time,
        SiteVersion = site,
        RunProviderUpdatedAt = issued ?? T,
        RunFetchedAtUtc = fetched ?? T,
        PeriodHours = period,
        AirTemperatureC = temperature,
        PrecipitationMm = precipitation,
        SymbolCode = symbol,
    };

    private static WeatherLocationSite Site(int version, DateTime appliedAt, string zone) => new()
    {
        SiteVersion = version,
        AppliedAt = appliedAt,
        TimeZoneId = zone,
    };

    // ── Effective step ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Effective_step_prefers_the_newer_site_then_the_newer_issue_then_the_later_fetch()
    {
        var steps = new[]
        {
            Step(T, site: 1, issued: T.AddHours(2), temperature: 1),
            Step(T, site: 2, issued: T, fetched: T, temperature: 2),
            Step(T, site: 2, issued: T, fetched: T.AddMinutes(5), temperature: 3),
            Step(T.AddHours(1), site: 1, issued: T, temperature: 4),
            Step(T.AddHours(1), site: 1, issued: T.AddHours(1), temperature: 5),
        };

        var effective = WeatherComposer.EffectiveSteps(steps);

        Assert.Equal(2, effective.Count);
        Assert.Equal(3, effective[0].AirTemperatureC);
        Assert.Equal(5, effective[1].AirTemperatureC);
    }

    [Fact]
    public void Effective_step_is_one_per_instant_even_on_a_full_tie()
    {
        var effective = WeatherComposer.EffectiveSteps([Step(T, id: 1, temperature: 1), Step(T, id: 2, temperature: 2)]);

        Assert.Equal(2, Assert.Single(effective).AirTemperatureC);
    }

    [Fact]
    public void Covering_prefers_the_finest_period()
    {
        var effective = WeatherComposer.EffectiveSteps([
            Step(T, period: 6, temperature: 1),
            Step(T.AddHours(2), period: 1, temperature: 2),
        ]);

        Assert.Equal(2, WeatherComposer.Covering(effective, T.AddHours(2).AddMinutes(30))!.AirTemperatureC);
        Assert.Equal(1, WeatherComposer.Covering(effective, T.AddHours(4))!.AirTemperatureC);
        Assert.Null(WeatherComposer.Covering(effective, T.AddHours(7)));
    }

    // ── Day composition ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Day_precipitation_counts_non_overlapping_periods_only()
    {
        var day = T.Date;
        var effective = WeatherComposer.EffectiveSteps([
            Step(day.AddHours(0), period: 1, precipitation: 1.0m),
            Step(day.AddHours(1), period: 1, precipitation: 1.0m),
            // A 6 h step overlapping the two hourly ones is not added on top of them…
            Step(day.AddHours(1).AddMinutes(30), period: 6, precipitation: 9.0m),
            // …but one starting after the last counted period is.
            Step(day.AddHours(12), period: 6, precipitation: 2.0m),
        ]);

        var half = WeatherComposer.ForecastHalf(effective, day, day.AddDays(1), day.AddHours(12));

        Assert.Equal(4.0m, half!.PrecipitationMm);
    }

    [Fact]
    public void Day_summary_uses_noon_for_the_symbol_and_reports_coverage()
    {
        var day = T.Date;
        var effective = WeatherComposer.EffectiveSteps([
            Step(day.AddHours(6), period: 6, temperature: 4, symbol: "rain"),
            Step(day.AddHours(12), period: 6, temperature: 9, symbol: "partlycloudy_day"),
        ]);

        var half = WeatherComposer.ForecastHalf(effective, day, day.AddDays(1), day.AddHours(12))!;

        Assert.Equal("partlycloudy_day", half.SymbolCode);
        Assert.Equal(4, half.MinTempC);
        Assert.Equal(9, half.MaxTempC);
        Assert.Equal(12, half.CoverageHours);
    }

    [Fact]
    public void A_period_straddling_local_midnight_covers_the_next_day_without_counting_its_values_twice()
    {
        // An Oslo winter day: 23:00 UTC to 23:00 UTC. MET's 6 h steps start at 00/06/12/18 UTC, so the
        // 18:00 step of the day before covers the day's first hour.
        var dayStart = new DateTime(2026, 10, 26, 23, 0, 0, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);
        var effective = WeatherComposer.EffectiveSteps([
            Step(new DateTime(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc), period: 6, temperature: 20, precipitation: 9m),
            Step(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc), period: 6, temperature: 5, precipitation: 1m),
            Step(new DateTime(2026, 10, 27, 6, 0, 0, DateTimeKind.Utc), period: 6, temperature: 6, precipitation: 1m),
            Step(new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), period: 6, temperature: 7, precipitation: 1m),
            Step(new DateTime(2026, 10, 27, 18, 0, 0, DateTimeKind.Utc), period: 6, temperature: 4, precipitation: 1m),
        ]);

        var half = WeatherComposer.ForecastHalf(
            effective.Where(s => s.TimeUtc >= dayStart).ToList(), dayStart, dayEnd, dayStart.AddHours(13),
            effective.Where(s => s.TimeUtc.AddHours(s.PeriodHours) > dayStart).ToList())!;

        Assert.Equal(24, half.CoverageHours);
        Assert.Equal(7, half.MaxTempC);
        Assert.Equal(4.0m, half.PrecipitationMm);
    }

    [Fact]
    public void An_empty_day_has_no_forecast_half() =>
        Assert.Null(WeatherComposer.ForecastHalf([], T.Date, T.Date.AddDays(1), T));

    // ── Backoff ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WeatherFetchFailureKind.ProviderError, 1, 15)]
    [InlineData(WeatherFetchFailureKind.Timeout, 2, 30)]
    [InlineData(WeatherFetchFailureKind.ProviderError, 4, 120)]
    [InlineData(WeatherFetchFailureKind.ProviderError, 6, 360)]
    [InlineData(WeatherFetchFailureKind.ProviderError, 40, 360)]
    [InlineData(WeatherFetchFailureKind.ProviderRejected, 1, 360)]
    [InlineData(WeatherFetchFailureKind.RateLimited, 1, 360)]
    public void Backoff_doubles_from_fifteen_minutes_and_caps_at_six_hours(WeatherFetchFailureKind kind, int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), WeatherFetchService.Backoff(kind, failures, null));

    [Fact]
    public void A_longer_retry_after_wins_and_a_shorter_one_does_not()
    {
        Assert.Equal(TimeSpan.FromHours(8), WeatherFetchService.Backoff(WeatherFetchFailureKind.RateLimited, 1, TimeSpan.FromHours(8)));
        Assert.Equal(TimeSpan.FromMinutes(15), WeatherFetchService.Backoff(WeatherFetchFailureKind.ProviderError, 1, TimeSpan.FromMinutes(1)));
    }

    // ── Local dates (B1, amendment 3) ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_apply_day_belongs_to_the_new_site_and_site_one_owns_everything_before_its_first_date()
    {
        var applied = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        var timeline = new WeatherSiteTimeline([
            Site(1, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), "Europe/Oslo"),
            Site(2, applied, "Europe/Oslo"),
        ]);

        Assert.Equal(2, timeline.SiteForDate(new DateOnly(2026, 10, 20)).SiteVersion);
        Assert.Equal(1, timeline.SiteForDate(new DateOnly(2026, 10, 19)).SiteVersion);
        Assert.Equal(1, timeline.SiteForDate(new DateOnly(2020, 5, 1)).SiteVersion);
        Assert.Equal(new DateOnly(2026, 10, 20), timeline.FirstOwnedDate(timeline.Current));
    }

    [Fact]
    public void An_instant_is_dated_in_the_zone_of_the_site_that_was_current_then()
    {
        // Oslo until 2026-10-20 09:00 UTC, then New York. 2026-10-20 02:00 UTC is still Oslo (04:00 local,
        // the 20th); in New York it would have been the 19th.
        var timeline = new WeatherSiteTimeline([
            Site(1, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), "Europe/Oslo"),
            Site(2, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), "America/New_York"),
        ]);

        var instant = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc);

        Assert.Equal(1, timeline.SiteForInstant(instant).SiteVersion);
        Assert.Equal(new DateOnly(2026, 10, 20), timeline.DateForInstant(instant));
        // After the apply, the new zone dates it.
        Assert.Equal(new DateOnly(2026, 10, 20), timeline.DateForInstant(new DateTime(2026, 10, 21, 2, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Day_bounds_follow_the_owning_site_across_a_dst_change()
    {
        var timeline = new WeatherSiteTimeline([Site(1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Europe/Oslo")]);

        // 2026-10-25 is the CEST → CET day in Oslo: 25 hours long.
        var (from, to) = timeline.DayBoundsUtc(new DateOnly(2026, 10, 25));

        Assert.Equal(new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 10, 25, 23, 0, 0, DateTimeKind.Utc), to);
        Assert.Equal(new DateTime(2026, 10, 25, 11, 0, 0, DateTimeKind.Utc), timeline.NoonUtc(new DateOnly(2026, 10, 25)));
    }

    // ── Coordinates ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Weather_rounds_coordinates_to_three_decimals_away_from_zero()
    {
        Assert.Equal(59.913m, WeatherCoordinates.Round(59.91250m));
        Assert.Equal(-10.751m, WeatherCoordinates.Round(-10.75050m));
    }

    [Fact]
    public void A_move_below_the_rounding_is_not_a_move_but_a_zone_change_is()
    {
        var location = new Location { LocationId = Guid.NewGuid(), Name = "Home", Latitude = 59.91273m, Longitude = 10.75027m, AltitudeMeters = 12, TimeZoneId = "Europe/Oslo" };
        var site = new WeatherLocationSite { Latitude = 59.913m, Longitude = 10.750m, AltitudeMeters = 12, TimeZoneId = "Europe/Oslo" };

        Assert.False(WeatherCoordinates.Moved(location, site));
        location.Latitude = 59.91301m;
        Assert.False(WeatherCoordinates.Moved(location, site));
        location.TimeZoneId = "Europe/Stockholm";
        Assert.True(WeatherCoordinates.Moved(location, site));
    }
}
