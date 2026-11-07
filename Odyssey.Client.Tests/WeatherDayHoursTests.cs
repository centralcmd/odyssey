using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Authorization;
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
        WeatherWidget.InteractiveCheck = OperatingSystem.IsBrowser;
        WeatherWidget.LocalNow = static () => DateTime.Now;
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

        // The open card already says which day it is, and the rows read for themselves: no visible title, no notes.
        Assert.Contains("sr-only", body.Find(".wx-hours-h").ClassName);
        Assert.Empty(body.FindAll(".wx-hours-note"));
        var gaps = body.FindAll(".wx-hours-gap td").Select(td => td.TextContent).ToList();
        Assert.Equal(["No forecast before 01:00", "No forecast for 11:00–12:00"], gaps);
        Assert.All(body.FindAll(".wx-hours-gap td"), td => Assert.Equal("5", td.GetAttribute("colspan")));
        Assert.Contains(body.FindAll(".wx-hours-table tbody th"), th => th.Children[0].TextContent == "12–18");
        Assert.Equal("Forecast ends at 18:00", body.Find(".wx-hours-end").TextContent);
        // The tail sits outside the scroll wrapper, so it never scrolls away with the table.
        Assert.Null(body.Find(".wx-hours-scroll").QuerySelector(".wx-hours-end"));
    }

    [Fact]
    public void Blocks_are_cut_at_the_days_midnights_in_the_points_clock_and_keep_their_whole_block_precipitation()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(3);
        var start = DayStartUtc(date);
        // MET's 6-hour grid in Oslo summer time (+2) runs 20–02, 02–08, …: the first block starts the evening before and
        // the last one runs past the next midnight.
        var steps = new[] { -4, 2, 8, 14, 20 }.Select(h => Step(start.AddHours(h), period: 6));
        Setup(weather, date, Ok(Hours(date, steps)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        body.InvokeAsync(() => Cards(body)[3].Click());
        body.WaitForAssertion(() => Assert.Equal(5, body.FindAll(".wx-hours-scroll tbody tr").Count));

        var times = body.FindAll(".wx-hours-table tbody th").Select(th => th.Children[0].TextContent).ToList();
        Assert.Equal(["00–02", "02–08", "08–14", "14–20", "20–24"], times);
        Assert.Equal("00:00 to 02:00", body.FindAll(".wx-hours-table tbody th")[0].QuerySelector(".sr-only")!.TextContent);
        Assert.Equal("20:00 to 24:00", body.FindAll(".wx-hours-table tbody th")[4].QuerySelector(".sr-only")!.TextContent);

        // Only the two cut rows name the whole block their precipitation total belongs to.
        var whole = body.FindAll(".wx-hours-whole").Select(e => e.TextContent).ToList();
        Assert.Equal(["over 20–02", "over 20–02"], whole);
        Assert.Equal("0.8 mm over 20:00 to 02:00, 70 % chance",
            body.FindAll(".wx-hours-table tbody tr")[0].QuerySelectorAll("td")[2].QuerySelector(".sr-only")!.TextContent);
        Assert.Equal("0.8 mm, 70 % chance",
            body.FindAll(".wx-hours-table tbody tr")[1].QuerySelectorAll("td")[2].QuerySelector(".sr-only")!.TextContent);
        Assert.Empty(body.FindAll(".wx-hours-note"));
        // A day cut at both ends is not "partial": no leading gap and no "Forecast ends" line.
        Assert.Empty(body.FindAll(".wx-hours-gap"));
        Assert.Empty(body.FindAll(".wx-hours-end"));
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
    public async Task An_answer_that_lands_after_a_newer_selection_is_discarded_and_a_close_during_loading_renders_nothing()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var saturday = Today.AddDays(1);
        var sunday = Today.AddDays(2);
        var slow = new TaskCompletionSource<ApiResult<WeatherHourlyResponse>>();
        weather.Setup(w => w.HourlyForDayAsync(saturday, PointId, It.IsAny<CancellationToken>())).Returns(slow.Task);
        Setup(weather, sunday, Ok(FullDay(sunday)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        await body.InvokeAsync(() => Cards(body)[1].Click());
        await body.InvokeAsync(() => Cards(body)[2].Click());
        body.WaitForAssertion(() => Assert.Equal(24, body.FindAll(".wx-hours-scroll tbody tr").Count));

        // Saturday's answer lands last, with three rows: it must not replace Sunday's twenty-four.
        await body.InvokeAsync(() => slow.SetResult(Ok(Hours(saturday, Enumerable.Range(0, 3).Select(h => Step(DayStartUtc(saturday).AddHours(h)))))));
        // The late answer's continuation runs on the renderer after SetResult returns: let it run before asserting.
        await Task.Delay(200);
        // Re-render, so the assertion reads the body's state rather than only the last frame it chose to draw.
        body.Render();
        Assert.Contains("Sunday 25 Oct", body.Find(".wx-hours-h").TextContent);
        Assert.Equal(24, body.FindAll(".wx-hours-scroll tbody tr").Count);
        Assert.Equal("true", Cards(body)[2].GetAttribute("aria-expanded"));

        // Open a day whose answer is still in flight, close it, then let the answer land: nothing renders.
        var never = new TaskCompletionSource<ApiResult<WeatherHourlyResponse>>();
        weather.Setup(w => w.HourlyForDayAsync(Today.AddDays(3), PointId, It.IsAny<CancellationToken>())).Returns(never.Task);
        await body.InvokeAsync(() => Cards(body)[3].Click());
        Assert.NotEmpty(body.FindAll(".wx-hours"));
        await body.InvokeAsync(() => Cards(body)[3].Click());
        await body.InvokeAsync(() => never.SetResult(Ok(FullDay(Today.AddDays(3)))));
        await Task.Delay(200);
        body.Render();
        Assert.Empty(body.FindAll(".wx-hours"));
        Assert.True(body.Find(".wx-hours-host").HasAttribute("hidden"));
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

    // ── Focus and Escape ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Escape_closes_the_panel_and_returns_focus_to_the_card_that_opened_it()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var module = ctx.JSInterop.SetupModule("./js/weather-hours.js");
        module.Mode = JSRuntimeMode.Loose;
        var date = Today.AddDays(1);
        Setup(weather, date, Ok(FullDay(date)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        await body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.NotEmpty(body.FindAll(".wx-hours-scroll")));
        // The panel's Escape listener is attached in the browser; it calls back into the body.
        body.WaitForAssertion(() => module.VerifyInvoke("attachEscape"));

        await body.InvokeAsync(() => body.Instance.CloseFromEscape());

        Assert.True(body.Find(".wx-hours-host").HasAttribute("hidden"));
        Assert.All(Cards(body), c => Assert.Equal("false", c.GetAttribute("aria-expanded")));
        body.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase)));
        module.VerifyInvoke("detachEscape");
    }

    [Fact]
    public async Task Try_again_moves_focus_to_the_panel_heading_before_the_skeleton_replaces_the_error()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var module = ctx.JSInterop.SetupModule("./js/weather-hours.js");
        module.Mode = JSRuntimeMode.Loose;
        var date = Today.AddDays(1);
        var second = new TaskCompletionSource<ApiResult<WeatherHourlyResponse>>();
        weather.SetupSequence(w => w.HourlyForDayAsync(date, PointId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<WeatherHourlyResponse>.Failure(HttpStatusCode.InternalServerError, new ApiProblem()))
            .Returns(second.Task);
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        await body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.NotNull(body.Find(".wx-hours-msg button")));
        await body.InvokeAsync(() => body.Find(".wx-hours-msg button").Click());

        // The heading is visually hidden, so focus goes to the panel itself, which shows the ring.
        var panel = body.Find(".wx-hours");
        Assert.Equal("-1", panel.GetAttribute("tabindex"));
        body.WaitForAssertion(() => Assert.Equal(panel.Id, module.VerifyInvoke("focusById").Arguments[0]));
        Assert.Null(body.Find(".wx-hours-h").GetAttribute("tabindex"));
        Assert.Equal("true", body.Find(".wx-hours").GetAttribute("aria-busy"));
    }

    [Fact]
    public async Task A_vanished_day_with_focus_in_the_panel_sends_focus_to_the_first_card()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var module = ctx.JSInterop.SetupModule("./js/weather-hours.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<bool>("containsFocus", _ => true).SetResult(true);
        var date = Today.AddDays(3);
        Setup(weather, date, Ok(FullDay(date)));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        await body.InvokeAsync(() => Cards(body)[3].Click());
        body.WaitForAssertion(() => Assert.NotEmpty(body.FindAll(".wx-hours-scroll")));
        var focusCallsBefore = ctx.JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));

        body.Render(p => p.Add(b => b.Days, Days(2)));

        module.VerifyInvoke("containsFocus");
        body.WaitForAssertion(() => Assert.True(
            ctx.JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase)) > focusCallsBefore));
        Assert.True(body.Find(".wx-hours-host").HasAttribute("hidden"));
    }

    [Fact]
    public async Task A_failure_speaks_through_its_alert_alone_and_never_the_polite_region()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(1);
        Setup(weather, date, ApiResult<WeatherHourlyResponse>.Failure(HttpStatusCode.InternalServerError, new ApiProblem()));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        await body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.Equal("alert", body.Find(".wx-hours-msg").GetAttribute("role")));
        await Task.Delay(300);

        Assert.Equal("", body.Find("[role=status]").TextContent);
    }

    // ── The 429 cool-down ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task After_a_429_Try_again_stays_focusable_but_unavailable_until_the_cool_down_ends()
    {
        WeatherForecastBody.RetryCoolDown = TimeSpan.FromMilliseconds(200);
        var (ctx, weather) = Context();
        using var _ = ctx;
        var date = Today.AddDays(1);
        Setup(weather, date, ApiResult<WeatherHourlyResponse>.Failure(HttpStatusCode.TooManyRequests, new ApiProblem()));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        await body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.Equal("true", body.Find(".wx-hours-msg button").GetAttribute("aria-disabled")));
        Assert.False(body.Find(".wx-hours-msg button").HasAttribute("disabled"));

        // A click during the cool-down asks nothing.
        await body.InvokeAsync(() => body.Find(".wx-hours-msg button").Click());
        weather.Verify(w => w.HourlyForDayAsync(date, PointId, It.IsAny<CancellationToken>()), Times.Once);

        body.WaitForAssertion(() => Assert.Null(body.Find(".wx-hours-msg button").GetAttribute("aria-disabled")), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Switching_day_or_closing_cancels_a_running_cool_down()
    {
        WeatherForecastBody.RetryCoolDown = TimeSpan.FromMinutes(5);
        var (ctx, weather) = Context();
        using var _ = ctx;
        var limited = Today.AddDays(1);
        var other = Today.AddDays(2);
        Setup(weather, limited, ApiResult<WeatherHourlyResponse>.Failure(HttpStatusCode.TooManyRequests, new ApiProblem()));
        Setup(weather, other, ApiResult<WeatherHourlyResponse>.Failure(HttpStatusCode.InternalServerError, new ApiProblem()));
        var body = Body(ctx, new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero))));

        await body.InvokeAsync(() => Cards(body)[1].Click());
        body.WaitForAssertion(() => Assert.Equal("true", body.Find(".wx-hours-msg button").GetAttribute("aria-disabled")));

        // Another day's error offers Try again at once: the five-minute pause belonged to the day that was refused.
        await body.InvokeAsync(() => Cards(body)[2].Click());
        body.WaitForAssertion(() => Assert.Contains("couldn’t be loaded", body.Find(".wx-hours-msg").TextContent));
        Assert.Null(body.Find(".wx-hours-msg button").GetAttribute("aria-disabled"));
    }

    // ── Hosts ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class Claims(IReadOnlyList<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [.. permissions.Select(p => new Claim(PermissionClaims.Type, p))], "test"))));
    }

    [Fact]
    public void On_the_dashboard_card_the_panel_heading_sits_one_level_below_the_cards_h2()
    {
        WeatherWidget.InteractiveCheck = static () => true;
        WeatherWidget.LocalNow = static () => new DateTime(2026, 10, 23, 10, 0, 0);
        var (ctx, weather) = Context();
        using var _ = ctx;
        weather.Setup(w => w.CurrentAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Current()));
        weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new WeatherDailyResponse { Point = Point, Status = WeatherStatus.Ok, Days = Days() }));
        weather.Setup(w => w.OptionsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPointOptions()));
        weather.Setup(w => w.PreferencesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new WeatherPreferences()));
        var date = Today.AddDays(1);
        Setup(weather, date, Ok(FullDay(date)));
        var auth = new Claims([PermissionClaims.WeatherRead]);
        ctx.Services.AddSingleton(weather.Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(auth);
        ctx.Services.AddSingleton<Odyssey.Client.Services.IWeatherPointOptionsCache>(new Odyssey.Client.Services.WeatherPointOptionsCache(weather.Object, auth));

        var card = ctx.Render<WeatherWidget>();
        card.WaitForAssertion(() => Assert.NotEmpty(card.FindAll("button.wx-day-btn")));
        card.InvokeAsync(() => card.FindAll("button.wx-day-btn")[1].Click());

        card.WaitForAssertion(() => Assert.Equal(24, card.FindAll(".wx-hours-scroll tbody tr").Count));
        Assert.NotNull(card.Find("h2.wx-ttl"));
        Assert.Equal("3", card.Find(".wx-hours-h").GetAttribute("aria-level"));
        weather.Verify(w => w.HourlyForDayAsync(date, PointId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void In_a_record_section_the_panel_heading_sits_one_level_below_the_sections_label()
    {
        var (ctx, weather) = Context();
        using var _ = ctx;
        weather.Setup(w => w.CurrentAsync(PointId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Current()));
        weather.Setup(w => w.DailyAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), PointId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new WeatherDailyResponse { Point = Point, Status = WeatherStatus.Ok, Days = Days() }));
        var date = Today.AddDays(1);
        Setup(weather, date, Ok(FullDay(date)));
        var reads = new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero)));

        var section = ctx.Render<WeatherForecastSection>(p => p
            .Add(s => s.WeatherPointId, PointId).Add(s => s.PointName, "Oslo").Add(s => s.Reads, reads));
        section.WaitForAssertion(() => Assert.NotEmpty(section.FindAll("button.wx-day-btn")));
        section.InvokeAsync(() => section.FindAll("button.wx-day-btn")[1].Click());

        section.WaitForAssertion(() => Assert.Equal(24, section.FindAll(".wx-hours-scroll tbody tr").Count));
        var label = section.Find("[role=heading]:not(.wx-hours-h)");
        Assert.Equal("3", label.GetAttribute("aria-level"));
        Assert.Equal("4", section.Find(".wx-hours-h").GetAttribute("aria-level"));
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

        // Today, inside its first row (12:00–13:00 Oslo is 10:00–11:00Z), so only the ten minutes bind.
        time.Now = new DateTimeOffset(2026, 10, 23, 10, 5, 0, TimeSpan.Zero);
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
    [InlineData("empty")]
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
            "empty" => Ok(Hours(date, [])),
            _ => Ok(FullDay(date.AddDays(1))),
        });
        var reads = new WeatherPointReads(weather.Object, new FakeTime(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero)));

        await reads.HourlyForDayAsync(PointId, date);
        await reads.HourlyForDayAsync(PointId, date);

        weather.Verify(w => w.HourlyForDayAsync(date, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Todays_answer_expires_when_its_Now_row_ends_even_inside_the_ten_minutes()
    {
        var weather = new Mock<IWeatherApiClient>();
        // Today's first row runs 12:00–13:00 Oslo (10:00–11:00Z); the answer arrives at 10:55Z.
        Setup(weather, Today, Ok(FullDay(Today, isToday: true)));
        var time = new FakeTime(new DateTimeOffset(2026, 10, 23, 10, 55, 0, TimeSpan.Zero));
        var reads = new WeatherPointReads(weather.Object, time);

        await reads.HourlyForDayAsync(PointId, Today);
        time.Now = new DateTimeOffset(2026, 10, 23, 11, 1, 0, TimeSpan.Zero);
        await reads.HourlyForDayAsync(PointId, Today);

        weather.Verify(w => w.HourlyForDayAsync(Today, PointId, It.IsAny<CancellationToken>()), Times.Exactly(2));
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
