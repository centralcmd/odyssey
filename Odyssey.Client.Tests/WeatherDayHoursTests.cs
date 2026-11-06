using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The hours of a selected day (issue #415; design system · Weather.jsx WeatherDayHours): day cards as disclosure
/// buttons for one shared panel, the table's rows and gaps, the cache's lifetimes, the stale-response guard, the
/// failure states, the announcements and the markup-free source.
/// </summary>
[Collection(nameof(WeatherWidgetSeam))]
public class WeatherDayHoursTests : IDisposable
{
    private static readonly Guid PointId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly WeatherPointRef Point = new() { WeatherPointId = PointId, Name = "Oslo" };
    private static readonly DateOnly Today = new(2026, 10, 23);
    private static readonly TimeSpan Oslo = TimeSpan.FromHours(2);

    public WeatherDayHoursTests()
    {
        WeatherDayHours.ViewerOffset = static _ => Oslo;
        WeatherForecastBody.RetryCoolDown = TimeSpan.FromSeconds(10);
    }

    public void Dispose()
    {
        WeatherDayHours.ViewerOffset = static utc => TimeZoneInfo.Local.GetUtcOffset(utc);
        WeatherForecastBody.RetryCoolDown = TimeSpan.FromSeconds(10);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    private static WeatherCurrentResponse Current() => new()
    {
        Point = Point,
        Status = WeatherStatus.Ok,
        Step = new WeatherStep { SymbolCode = "cloudy", AirTemperatureC = 8, IssuedAtUtc = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc) },
        Today = new WeatherDaySummary { Date = Today, Forecast = new WeatherDayForecast { SymbolCode = "cloudy", MinTempC = 4, MaxTempC = 11, CoverageHours = 24 } },
    };

    private static IReadOnlyList<WeatherDaySummary> Days(int count = 4) =>
        [.. Enumerable.Range(0, count).Select(i => new WeatherDaySummary
        {
            Date = Today.AddDays(i),
            Forecast = new WeatherDayForecast { SymbolCode = "rain", MinTempC = 3, MaxTempC = 9, PrecipitationMm = 2, CoverageHours = 24 },
        })];

    private static DateTime DayStartUtc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-2);

    private static WeatherStep Step(DateTime utc, int period = 1, decimal? temperature = 7) => new()
    {
        TimeUtc = utc,
        PeriodHours = period,
        LocalStart = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified).Add(Oslo), Oslo),
        LocalEnd = new DateTimeOffset(DateTime.SpecifyKind(utc.AddHours(period), DateTimeKind.Unspecified).Add(Oslo), Oslo),
        SymbolCode = "rain",
        AirTemperatureC = temperature,
        PrecipitationMm = 0.8m,
        PrecipitationProbabilityPct = 70,
        WindSpeedMps = 4,
        WindGustMps = 8,
        WindFromDirectionDeg = 225,
    };

    private static WeatherHourlyResponse Hours(DateOnly date, IEnumerable<WeatherStep> steps, bool isToday = false, WeatherStatus status = WeatherStatus.Ok) => new()
    {
        Point = Point,
        Status = status,
        Day = new WeatherHourlyDay
        {
            Date = date, IsToday = isToday, FromUtc = DayStartUtc(date), ToUtc = DayStartUtc(date.AddDays(1)), LengthHours = 24,
        },
        Steps = [.. steps],
    };

    private static WeatherHourlyResponse FullDay(DateOnly date, bool isToday = false) =>
        Hours(date, Enumerable.Range(isToday ? 12 : 0, isToday ? 12 : 24).Select(h => Step(DayStartUtc(date).AddHours(h))), isToday);

    private static (BunitContext Ctx, Mock<IWeatherApiClient> Weather) Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var weather = new Mock<IWeatherApiClient>();
        return (ctx, weather);
    }

    private static IRenderedComponent<WeatherForecastBody> Body(BunitContext ctx, WeatherPointReads reads, IReadOnlyList<WeatherDaySummary>? days = null) =>
        ctx.Render<WeatherForecastBody>(p => p
            .Add(b => b.Current, Current())
            .Add(b => b.Days, days ?? Days())
            .Add(b => b.Reads, reads));

    private static void Setup(Mock<IWeatherApiClient> weather, DateOnly date, ApiResult<WeatherHourlyResponse> result) =>
        weather.Setup(w => w.HourlyForDayAsync(date, PointId, It.IsAny<CancellationToken>())).ReturnsAsync(result);

    private static IReadOnlyList<AngleSharp.Dom.IElement> Cards(IRenderedComponent<WeatherForecastBody> body) => body.FindAll("button.wx-day-btn");

    // ── Disclosure ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Selecting_opens_one_panel_switching_moves_it_and_reselecting_closes_it_leaving_the_host_mounted()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        Setup(weather, Today.AddDays(1), Ok(FullDay(Today.AddDays(1))));
        Setup(weather, Today.AddDays(2), Ok(FullDay(Today.AddDays(2))));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        var host = body.Find(".wx-hours-host");
        Assert.True(host.HasAttribute("hidden"));
        Assert.All(Cards(body), c => Assert.Equal(host.Id, c.GetAttribute("aria-controls")));

        body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.Equal(24, body.FindAll(".wx-hours-table tbody tr").Count));
        Assert.Single(Cards(body), c => c.GetAttribute("aria-expanded") == "true");
        Assert.Contains("Saturday 24 Oct", body.Find(".wx-hours-h").TextContent);

        body.InvokeAsync(() => Cards(body)[2].Click());
        body.WaitForAssertion(() => Assert.Contains("Sunday 25 Oct", body.Find(".wx-hours-h").TextContent));
        Assert.Equal("true", Cards(body)[2].GetAttribute("aria-expanded"));
        Assert.Single(Cards(body), c => c.GetAttribute("aria-expanded") == "true");

        body.InvokeAsync(() => Cards(body)[2].Click());
        Assert.All(Cards(body), c => Assert.Equal("false", c.GetAttribute("aria-expanded")));
        Assert.True(body.Find(".wx-hours-host").HasAttribute("hidden"));
        Assert.Empty(body.FindAll(".wx-hours"));
    }

    [Fact]
    public void Each_card_names_its_long_date_and_sentence_and_shows_a_chevron()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        var tomorrow = Cards(body)[1];
        Assert.Contains("Saturday 24 Oct", tomorrow.TextContent);
        Assert.Contains("Rain, high 9°, low 3°", tomorrow.TextContent);
        Assert.Null(tomorrow.GetAttribute("title"));
        Assert.NotNull(tomorrow.QuerySelector(".wx-day-chev"));
        Assert.Null(body.Find("ol.wx-days").GetAttribute("tabindex"));
    }

    [Fact]
    public void Today_reads_Now_from_the_response_and_its_time_cells_are_read_once()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        Setup(weather, Today, Ok(FullDay(Today, isToday: true)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[0].Click());
        body.WaitForAssertion(() => Assert.NotEmpty(body.FindAll(".wx-hours-table tbody tr")));

        Assert.Contains("rest of today", body.Find(".wx-hours-h").TextContent);
        var first = body.Find(".wx-hours-table tbody tr");
        Assert.Contains("now", first.ClassName);
        var time = first.QuerySelector("th")!;
        Assert.Equal("true", time.Children[0].GetAttribute("aria-hidden"));
        Assert.Equal("Now · 12:00", time.Children[0].TextContent);
        Assert.Equal("Now, 12:00 to 13:00", time.QuerySelector(".sr-only")!.TextContent);
        var precipitation = first.QuerySelectorAll("td")[2];
        Assert.Equal("true", precipitation.Children[0].GetAttribute("aria-hidden"));
        Assert.Equal("0.8 mm, 70 % chance", precipitation.QuerySelector(".sr-only")!.TextContent);
    }

    [Fact]
    public void Six_hour_blocks_gaps_and_the_horizon_are_written_as_words()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(2);
        var start = DayStartUtc(date);
        // Hourly from 01:00 to 11:00, a run-join gap 11–12, blocks 12–18, then the horizon stops at 18:00.
        var steps = Enumerable.Range(1, 10).Select(h => Step(start.AddHours(h))).Append(Step(start.AddHours(12), 6));
        Setup(weather, date, Ok(Hours(date, steps)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[2].Click());
        body.WaitForAssertion(() => Assert.NotEmpty(body.FindAll(".wx-hours-gap")));

        Assert.Contains("some rows are 6-hour blocks", body.Find(".wx-hours-note").TextContent);
        var gaps = body.FindAll(".wx-hours-gap td").Select(td => td.TextContent).ToList();
        Assert.Equal(["No forecast before 01:00", "No forecast for 11:00–12:00"], gaps);
        Assert.All(body.FindAll(".wx-hours-gap td"), td => Assert.Equal("5", td.GetAttribute("colspan")));
        Assert.Contains(body.FindAll(".wx-hours-table tbody th"), th => th.Children[0].TextContent == "12–18");
        Assert.Equal("Forecast ends at 18:00", body.Find(".wx-hours-end").TextContent);
        // The tail sits outside the scroll wrapper, so it never scrolls away with the table.
        Assert.Null(body.Find(".wx-hours-scroll").QuerySelector(".wx-hours-end"));
    }

    [Fact]
    public void A_null_measure_is_a_dash_read_as_not_available()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(1);
        Setup(weather, date, Ok(Hours(date, [Step(DayStartUtc(date), temperature: null)])));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.NotEmpty(body.FindAll(".wx-hours-table tbody tr")));

        var temperature = body.FindAll(".wx-hours-table tbody td")[1];
        Assert.Equal("–", temperature.Children[0].TextContent);
        Assert.Equal("true", temperature.Children[0].GetAttribute("aria-hidden"));
        Assert.Equal("not available", temperature.QuerySelector(".sr-only")!.TextContent);
    }

    [Fact]
    public void The_heading_names_the_point_when_its_clock_differs_from_the_viewers()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(1);
        Setup(weather, date, Ok(FullDay(date)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.NotEmpty(body.FindAll(".wx-hours-table tbody tr")));
        Assert.DoesNotContain("times at", body.Find(".wx-hours-h").TextContent);

        WeatherDayHours.ViewerOffset = static _ => TimeSpan.FromHours(-5);
        body.InvokeAsync(() => Cards(body)[1].Click());
        body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.Contains("· times at Oslo", body.Find(".wx-hours-h").TextContent));
    }

    // ── Failure states ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Weather is resting for a moment", true)]
    [InlineData(HttpStatusCode.InternalServerError, "The hours couldn’t be loaded.", true)]
    [InlineData(HttpStatusCode.Forbidden, "You no longer have access to weather.", false)]
    [InlineData(HttpStatusCode.NotFound, "This weather point no longer exists.", false)]
    public void A_failure_names_itself_in_an_alert_and_only_a_retryable_one_offers_Try_again(HttpStatusCode status, string text, bool retry)
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(1);
        Setup(weather, date, ApiResult<WeatherHourlyResponse>.Failure(status, new ApiProblem()));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[1].Click());

        body.WaitForAssertion(() => Assert.Contains(text, body.Find(".wx-hours-msg").TextContent));
        Assert.Equal("alert", body.Find(".wx-hours-msg").GetAttribute("role"));
        var buttons = body.Find(".wx-hours-msg").QuerySelectorAll("button");
        Assert.Equal(retry, buttons.Length == 1);
        if (status == HttpStatusCode.TooManyRequests)
            Assert.Equal("true", buttons[0].GetAttribute("aria-disabled"));
        Assert.Equal(4, Cards(body).Count);
        Assert.NotNull(body.Find(".wx-now"));
    }

    [Fact]
    public void Try_again_drops_only_its_own_day_and_asks_again()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(1);
        weather.SetupSequence(w => w.HourlyForDayAsync(date, PointId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<WeatherHourlyResponse>.Failure(HttpStatusCode.InternalServerError, new ApiProblem()))
            .ReturnsAsync(Ok(FullDay(date)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.NotNull(body.Find(".wx-hours-msg button")));
        body.InvokeAsync(() => body.Find(".wx-hours-msg button").Click());

        body.WaitForAssertion(() => Assert.Equal(24, body.FindAll(".wx-hours-table tbody tr").Count));
        weather.Verify(w => w.HourlyForDayAsync(date, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── Stale-response guard ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_answer_that_lands_after_a_newer_selection_is_discarded_and_a_close_during_loading_renders_nothing()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var thursday = Today.AddDays(1);
        var friday = Today.AddDays(2);
        var slow = new TaskCompletionSource<ApiResult<WeatherHourlyResponse>>();
        weather.Setup(w => w.HourlyForDayAsync(thursday, PointId, It.IsAny<CancellationToken>())).Returns(slow.Task);
        Setup(weather, friday, Ok(FullDay(friday)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[1].Click());
        body.InvokeAsync(() => Cards(body)[2].Click());
        body.WaitForAssertion(() => Assert.Contains("Sunday 25 Oct", body.Find(".wx-hours-h").TextContent));
        body.InvokeAsync(() => slow.SetResult(Ok(FullDay(thursday))));
        body.WaitForAssertion(() => Assert.Contains("Sunday 25 Oct", body.Find(".wx-hours-h").TextContent));

        var never = new TaskCompletionSource<ApiResult<WeatherHourlyResponse>>();
        weather.Setup(w => w.HourlyForDayAsync(Today.AddDays(3), PointId, It.IsAny<CancellationToken>())).Returns(never.Task);
        body.InvokeAsync(() => Cards(body)[3].Click());
        body.InvokeAsync(() => Cards(body)[3].Click());
        body.InvokeAsync(() => never.SetResult(Ok(FullDay(Today.AddDays(3)))));
        Assert.Empty(body.FindAll(".wx-hours"));
    }

    [Fact]
    public void A_rerender_that_drops_the_selected_day_closes_the_panel()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(3);
        Setup(weather, date, Ok(FullDay(date)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[3].Click());
        body.WaitForAssertion(() => Assert.NotEmpty(body.FindAll(".wx-hours-table tbody tr")));
        body.Render(p => p.Add(b => b.Days, Days(2)));

        Assert.Empty(body.FindAll(".wx-hours"));
        Assert.True(body.Find(".wx-hours-host").HasAttribute("hidden"));
        Assert.All(Cards(body), c => Assert.Equal("false", c.GetAttribute("aria-expanded")));
    }

    [Fact]
    public void Two_bodies_keep_their_own_selection_and_their_own_ids()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var reads = new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero)));
        Setup(weather, Today.AddDays(1), Ok(FullDay(Today.AddDays(1))));
        var first = Body(ctx, reads);
        var second = Body(ctx, reads);

        Assert.NotEqual(first.Find(".wx-hours-host").Id, second.Find(".wx-hours-host").Id);
        first.InvokeAsync(() => Cards(first)[1].Click());
        first.WaitForAssertion(() => Assert.NotEmpty(first.FindAll(".wx-hours")));
        Assert.Empty(second.FindAll(".wx-hours"));
        Assert.All(Cards(second), c => Assert.Equal(second.Find(".wx-hours-host").Id, c.GetAttribute("aria-controls")));
    }

    // ── Announcements ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_selection_is_announced_once_even_a_repeat_of_the_same_day()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(1);
        Setup(weather, date, Ok(FullDay(date)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.Equal("Tomorrow: 24 rows below the day list.", body.Find("[role=status]").TextContent));
        body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.True(body.Find(".wx-hours-host").HasAttribute("hidden")));
        body.InvokeAsync(() => Cards(body)[1].Click());
        // A repeat of the same text is cleared and set again by Announce(), so it is read twice.
        body.WaitForAssertion(() => Assert.Equal("", body.Find("[role=status]").TextContent), TimeSpan.FromSeconds(2));
        body.WaitForAssertion(() => Assert.Equal("Tomorrow: 24 rows below the day list.", body.Find("[role=status]").TextContent));
    }

    // ── The cache ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_future_day_is_held_until_its_own_start_and_today_for_ten_minutes()
    {
        var weather = new Mock<IWeatherApiClient>();
        var tomorrow = Today.AddDays(1);
        Setup(weather, tomorrow, Ok(FullDay(tomorrow)));
        Setup(weather, Today, Ok(FullDay(Today, isToday: true)));
        // 23:50 Oslo on the 23rd.
        var time = new FakeTime(new DateTimeOffset(2026, 10, 23, 21, 50, 0, TimeSpan.Zero));
        var reads = new WeatherPointReads(weather.Object, time);

        await reads.HourlyForDayAsync(PointId, tomorrow);
        await reads.HourlyForDayAsync(PointId, tomorrow);
        weather.Verify(w => w.HourlyForDayAsync(tomorrow, PointId, It.IsAny<CancellationToken>()), Times.Once);

        // 00:05 Oslo: "tomorrow" is now today, and the answer fetched before midnight is not reused.
        time.Now = new DateTimeOffset(2026, 10, 23, 22, 5, 0, TimeSpan.Zero);
        await reads.HourlyForDayAsync(PointId, tomorrow);
        weather.Verify(w => w.HourlyForDayAsync(tomorrow, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));

        await reads.HourlyForDayAsync(PointId, Today);
        time.Now += TimeSpan.FromMinutes(9);
        await reads.HourlyForDayAsync(PointId, Today);
        weather.Verify(w => w.HourlyForDayAsync(Today, PointId, It.IsAny<CancellationToken>()), Times.Once);
        time.Now += TimeSpan.FromMinutes(2);
        await reads.HourlyForDayAsync(PointId, Today);
        weather.Verify(w => w.HourlyForDayAsync(Today, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task No_day_is_held_longer_than_an_hour()
    {
        var weather = new Mock<IWeatherApiClient>();
        var later = Today.AddDays(5);
        Setup(weather, later, Ok(FullDay(later)));
        var time = new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero));
        var reads = new WeatherPointReads(weather.Object, time);

        await reads.HourlyForDayAsync(PointId, later);
        time.Now += TimeSpan.FromMinutes(61);
        await reads.HourlyForDayAsync(PointId, later);

        weather.Verify(w => w.HourlyForDayAsync(later, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("429")]
    [InlineData("nodata")]
    [InlineData("stale")]
    [InlineData("wrong-date")]
    public async Task Only_a_fresh_Ok_answer_for_the_asked_day_is_held(string kind)
    {
        var weather = new Mock<IWeatherApiClient>();
        var date = Today.AddDays(1);
        Setup(weather, date, kind switch
        {
            "failure" => ApiResult<WeatherHourlyResponse>.Failure(HttpStatusCode.InternalServerError, new ApiProblem()),
            "429" => ApiResult<WeatherHourlyResponse>.Failure(HttpStatusCode.TooManyRequests, new ApiProblem()),
            "nodata" => Ok(Hours(date, [], status: WeatherStatus.NoData)),
            "stale" => Ok(FullDay(date) with { Stale = true }),
            _ => Ok(FullDay(date.AddDays(1))),
        });
        var reads = new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero)));

        await reads.HourlyForDayAsync(PointId, date);
        await reads.HourlyForDayAsync(PointId, date);

        weather.Verify(w => w.HourlyForDayAsync(date, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Forgetting_a_point_drops_its_hours_and_forgetting_a_day_drops_only_that_day()
    {
        var weather = new Mock<IWeatherApiClient>();
        var a = Today.AddDays(1);
        var b = Today.AddDays(2);
        Setup(weather, a, Ok(FullDay(a)));
        Setup(weather, b, Ok(FullDay(b)));
        var reads = new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero)));
        await reads.HourlyForDayAsync(PointId, a);
        await reads.HourlyForDayAsync(PointId, b);

        reads.ForgetDay(PointId, a);
        await reads.HourlyForDayAsync(PointId, a);
        await reads.HourlyForDayAsync(PointId, b);
        weather.Verify(w => w.HourlyForDayAsync(a, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));
        weather.Verify(w => w.HourlyForDayAsync(b, PointId, It.IsAny<CancellationToken>()), Times.Once);

        reads.Forget(PointId);
        await reads.HourlyForDayAsync(PointId, b);
        weather.Verify(w => w.HourlyForDayAsync(b, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── Guards ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_strip_never_shows_a_day_beyond_the_day_modes_reach() =>
        Assert.True(WeatherForecastBody.DaysShown - 1 <= WeatherRules.DayModeMaxDaysAhead);

    private static readonly string[] Forbidden =
        ["innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function", "MarkupString"];

    [Fact]
    public void The_hours_panel_and_its_module_build_no_markup_from_strings()
    {
        var files = new[]
        {
            Path.Combine(ClientSource.Root, "wwwroot", "js", "weather-hours.js"),
            Path.Combine(ClientSource.Root, "Pages", "Weather", "WeatherDayHours.razor"),
            Path.Combine(ClientSource.Root, "Pages", "Weather", "WeatherDayHours.razor.cs"),
            Path.Combine(ClientSource.Root, "Pages", "Weather", "WeatherForecastBody.razor"),
            Path.Combine(ClientSource.Root, "Pages", "Weather", "WeatherForecastBody.razor.cs"),
        };
        Assert.All(files, f => Assert.True(File.Exists(f), f));

        var hits = files
            .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, i)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .SelectMany(x => Forbidden.Where(f => x.line.Contains(f, StringComparison.Ordinal)).Select(f => $"{Path.GetFileName(x.file)}:{x.i + 1} uses {f}"))
            .ToList();
        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }
}
