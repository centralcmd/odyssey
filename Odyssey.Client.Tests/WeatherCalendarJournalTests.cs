using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Weather on journal entries, agenda rows and the event dialog (issue #363): the words and names, the list loader's
/// request discipline, the infinite list's window signal, the dialog section's states and claims, and the
/// markup-free source.
/// </summary>
[Collection(nameof(WeatherWidgetSeam))]
public class WeatherCalendarJournalTests : IDisposable
{
    private static readonly WeatherPointRef Oslo = new() { WeatherPointId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), Name = "Oslo" };
    private static readonly DateOnly Day = new(2026, 10, 20);
    private static readonly DateTime UtcNow = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    public WeatherCalendarJournalTests() => WeatherRetry.CoolDown = TimeSpan.FromSeconds(10);

    public void Dispose() => WeatherRetry.CoolDown = TimeSpan.FromSeconds(10);

    private static WeatherDaySummary DayWith(bool observed, string symbol = "rain") => new()
    {
        Date = Day,
        Forecast = new WeatherDayForecast { SymbolCode = symbol, MinTempC = 4, MaxTempC = 9, PrecipitationMm = 6.2m, CoverageHours = 24, IssuedAtUtc = UtcNow.AddDays(-4) },
        Observed = observed ? new WeatherDayObserved { MinTempC = 3.6m, MaxTempC = 8.4m, MeanTempC = 6.1m, PrecipitationMm = 7.4m } : null,
    };

    private static JournalEntryWeatherItem JournalItem(Guid id, WeatherStatus status = WeatherStatus.Ok, bool observed = true) => new()
    {
        JournalEntryId = id,
        Status = status,
        Point = Oslo,
        ResolvedFrom = WeatherResolvedFrom.Household,
        Date = Day,
        Day = status == WeatherStatus.Ok ? DayWith(observed) : null,
    };

    private static BunitContext Context(params string[] claims)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new Claims(claims));
        return ctx;
    }

    private sealed class Claims(IReadOnlyList<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [.. permissions.Select(p => new Claim(PermissionClaims.Type, p))], "test"))));
    }

    // ── Journal disclosure (AC 3, 4, 6) ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, "Weather: Rain, high 8°, low 4°, 7.4 mm of rain, measured")]
    [InlineData(false, "Weather: Rain, high 9°, low 4°, 6.2 mm of rain, forecast")]
    public async Task The_disclosure_is_named_by_its_visible_text_and_only_its_state_changes(bool observed, string expected)
    {
        await using var ctx = Context();
        var cut = ctx.Render<JournalEntryCard>(p => p
            .Add(c => c.Entry, Entry())
            .Add(c => c.Id, "je-1")
            .Add(c => c.Weather, JournalItem(Guid.NewGuid(), observed: observed))
            .Add(c => c.WeatherAttribution, WeatherAttribution.MetNorway));

        var button = cut.Find(".jec-weather .odc-collapsible-trigger");
        Assert.Equal(expected, VisibleName(button));
        Assert.Null(button.GetAttribute("aria-label"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));

        button.Click();
        button = cut.Find(".jec-weather .odc-collapsible-trigger");
        Assert.Equal("true", button.GetAttribute("aria-expanded"));
        Assert.Equal(expected, VisibleName(button));
        Assert.NotNull(cut.Find(".wx-jw-body .wx-attr a"));
        Assert.Empty(cut.FindAll(".wx-jw [role=region], .wx-jw [role=heading]"));
    }

    [Fact]
    public async Task The_disclosure_is_its_own_row_below_the_meta_row_not_inside_it()
    {
        await using var ctx = Context();
        var cut = ctx.Render<JournalEntryCard>(p => p.Add(c => c.Entry, Entry()).Add(c => c.Weather, JournalItem(Guid.NewGuid())));

        var meta = cut.Find(".jec-meta");
        Assert.Empty(meta.QuerySelectorAll(".wx-jw"));
        Assert.Equal("jec-weather", meta.NextElementSibling!.ClassName);
    }

    [Theory]
    [InlineData(WeatherStatus.NoPlace)]
    [InlineData(WeatherStatus.NoData)]
    [InlineData(WeatherStatus.NotConfigured)]
    [InlineData(WeatherStatus.WeatherDisabled)]
    public async Task A_non_Ok_item_renders_nothing_on_the_card(WeatherStatus status)
    {
        await using var ctx = Context();
        var cut = ctx.Render<JournalEntryCard>(p => p.Add(c => c.Entry, Entry()).Add(c => c.Weather, JournalItem(Guid.NewGuid(), status)));

        Assert.Empty(cut.FindAll(".jec-weather, .wx-jw"));
        Assert.DoesNotContain("Weather", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_focused_disclosure_that_goes_away_sends_focus_to_the_menu_then_the_article()
    {
        await using var ctx = Context();
        var weatherJs = ctx.JSInterop.SetupModule("./js/weather-hours.js");
        weatherJs.Setup<bool>("containsFocus", _ => true).SetResult(true);
        var focusJs = ctx.JSInterop.SetupModule("./js/focus-return.js");
        focusJs.SetupVoid("focusFirst", _ => true).SetVoidResult();
        var id = Guid.NewGuid();
        var cut = ctx.Render<JournalEntryCard>(p => p.Add(c => c.Entry, Entry()).Add(c => c.Id, "je-1").Add(c => c.Weather, JournalItem(id)));

        cut.Render(p => p.Add(c => c.Weather, JournalItem(id, WeatherStatus.NoData)));

        cut.WaitForAssertion(() =>
        {
            var call = Assert.Single(focusJs.Invocations, i => i.Identifier == "focusFirst");
            Assert.Equal(["#je-1 .je-cardmenu button", "#je-1"], call.Arguments.Cast<string?>());
        });
        Assert.Equal("-1", cut.Find("article").GetAttribute("tabindex"));
    }

    [Fact]
    public void Snow_is_precipitation_not_rain_and_a_dry_day_says_so()
    {
        Assert.EndsWith("6.2 mm of precipitation, forecast", WeatherEventText.JournalLine(DayWith(false, "snow")), StringComparison.Ordinal);
        var dry = DayWith(false) with { Forecast = DayWith(false).Forecast! with { PrecipitationMm = 0 } };
        Assert.EndsWith("no rain, forecast", WeatherEventText.JournalLine(dry), StringComparison.Ordinal);
    }

    // ── Where (AC 5) ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WeatherResolvedFrom.Linked, 4, "Weather at Oslo, 4 km from Cabin.")]
    [InlineData(WeatherResolvedFrom.Linked, 0, "Weather at Oslo, less than 1 km from Cabin.")]
    [InlineData(WeatherResolvedFrom.Linked, null, "Weather at Oslo. The distance from Cabin is unknown.")]
    [InlineData(WeatherResolvedFrom.Preferred, null, "Weather at Oslo, your default weather point. This entry has no place.")]
    [InlineData(WeatherResolvedFrom.Household, null, "Weather at Oslo, the household default. This entry has no place.")]
    public void The_where_sentence_follows_resolvedFrom(WeatherResolvedFrom from, int? km, string expected) =>
        Assert.Equal(expected, WeatherEventText.Where(from, Oslo, new WeatherLinkedLocationRef { Name = "Cabin" }, km, "entry"));

    // ── Agenda (AC 6, 7) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_past_timed_summary_reads_as_forecast_with_a_true_minus_and_a_glyph_named_Weather()
    {
        using var ctx = Context();
        var start = UtcNow.AddDays(-1);
        var cut = ctx.Render<WeatherEventSummaryText>(p => p
            .Add(c => c.Item, new CalendarEventWeatherItem
            {
                Status = WeatherStatus.Ok,
                Summary = new WeatherEventSummary { Basis = WeatherSummaryBasis.Hourly, SymbolCode = "partlycloudy_day", MinTempC = -2, MaxTempC = 3, PrecipitationMm = 0.8m, PrecipitationHours = 6 },
            })
            .Add(c => c.Start, start).Add(c => c.End, start.AddHours(1)).Add(c => c.UtcNow, UtcNow));

        Assert.Equal("−2° to 3° · 0.8 mm in 6 h · as forecast", Normalize(cut.Find(".wx-evs-t").TextContent));
        Assert.StartsWith("Weather: ", cut.Find("[role=img]").GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".sr-only"));
    }

    [Theory]
    [InlineData(WeatherStatus.NoData)]
    [InlineData(WeatherStatus.OutOfRange)]
    public void A_non_Ok_agenda_item_renders_nothing(WeatherStatus status)
    {
        using var ctx = Context();
        var cut = ctx.Render<WeatherEventSummaryText>(p => p.Add(c => c.Item, new CalendarEventWeatherItem { Status = status }));
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    // ── The list loader (AC 1, 8, 9, 14, 25) ──────────────────────────────────────────────────────

    private sealed class CountingFetch
    {
        public List<IReadOnlyCollection<Guid>> Calls { get; } = [];

        public Func<IReadOnlyCollection<Guid>, HttpStatusCode> Status { get; set; } = _ => HttpStatusCode.OK;

        /// <summary>The ids the server knows; any other is omitted from the answer.</summary>
        public HashSet<Guid>? Known { get; set; }

        public Task<ApiResult<WeatherBatchPage<JournalEntryWeatherItem>>> FetchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        {
            Calls.Add([.. ids]);
            var status = Status(ids);
            return Task.FromResult(status == HttpStatusCode.OK
                ? ApiResult<WeatherBatchPage<JournalEntryWeatherItem>>.Success(
                    new([.. ids.Where(id => Known?.Contains(id) ?? true).Select(id => JournalItem(id))], WeatherAttribution.MetNorway), status)
                : ApiResult<WeatherBatchPage<JournalEntryWeatherItem>>.Failure(status, new ApiProblem { Title = "x" }));
        }
    }

    private static WeatherBatchLoader<JournalEntryWeatherItem> Loader(CountingFetch fetch, TimeSpan? debounce = null)
    {
        var loader = new WeatherBatchLoader<JournalEntryWeatherItem>(fetch.FetchAsync, i => i.JournalEntryId, () => Task.CompletedTask,
            NullLogger.Instance, "journal", debounce ?? TimeSpan.Zero);
        loader.Reset(canRead: true);
        return loader;
    }

    private static List<Guid> Ids(int n) => [.. Enumerable.Range(0, n).Select(_ => Guid.NewGuid())];

    [Fact]
    public async Task A_window_is_one_request_and_a_reveal_asks_only_for_the_new_ids()
    {
        var fetch = new CountingFetch();
        using var loader = Loader(fetch);
        var ids = Ids(50);

        await loader.ShowAsync(ids.Take(25));
        await loader.ShowAsync(ids);
        await loader.ShowAsync(ids.Skip(10).Take(20)); // a filter: everything rendered is already known

        Assert.Equal(2, fetch.Calls.Count);
        Assert.Equal(ids.Take(25), fetch.Calls[0]);
        Assert.Equal(ids.Skip(25), fetch.Calls[1]);
        Assert.NotNull(loader.Get(ids[3]));
    }

    [Fact]
    public async Task An_omitted_id_is_not_asked_about_again_and_a_save_re_asks_for_exactly_that_id()
    {
        var ids = Ids(3);
        var fetch = new CountingFetch { Known = [ids[0], ids[1]] };
        using var loader = Loader(fetch);

        await loader.ShowAsync(ids);
        await loader.ShowAsync(ids);
        await loader.RefreshAsync(ids[1]);

        Assert.Equal(2, fetch.Calls.Count);
        Assert.Equal([ids[1]], fetch.Calls[1]);
    }

    [Fact]
    public async Task A_refresh_keeps_the_old_answer_on_screen_until_the_new_one_lands()
    {
        var fetch = new CountingFetch();
        using var loader = Loader(fetch, TimeSpan.FromMilliseconds(100));
        var ids = Ids(2);
        await loader.ShowAsync(ids);

        var refresh = loader.RefreshAsync(ids[0]);
        Assert.NotNull(loader.Get(ids[0]));
        await refresh;

        Assert.Equal([ids[0]], fetch.Calls[1]);
        Assert.NotNull(loader.Get(ids[0]));
    }

    [Fact]
    public async Task Changes_inside_the_debounce_collapse_into_one_request()
    {
        var fetch = new CountingFetch();
        using var loader = Loader(fetch, TimeSpan.FromMilliseconds(300));
        var ids = Ids(3);

        var a = loader.ShowAsync(ids.Take(1));
        var b = loader.ShowAsync(ids.Take(2));
        var c = loader.ShowAsync(ids);
        await Task.WhenAll(a, b, c);

        Assert.Equal([ids], fetch.Calls);
    }

    [Fact]
    public async Task A_403_clears_everything_and_stops_further_requests()
    {
        var fetch = new CountingFetch();
        using var loader = Loader(fetch);
        var ids = Ids(2);
        await loader.ShowAsync(ids.Take(1));

        fetch.Status = _ => HttpStatusCode.Forbidden;
        await loader.ShowAsync(ids);
        await loader.ShowAsync(Ids(1));

        Assert.True(loader.Stopped);
        Assert.Null(loader.Get(ids[0]));
        Assert.Equal(2, fetch.Calls.Count);
    }

    [Fact]
    public async Task A_429_retries_once_after_the_cool_down_and_not_after_the_page_leaves()
    {
        WeatherRetry.CoolDown = TimeSpan.FromMilliseconds(50);
        var fetch = new CountingFetch { Status = _ => HttpStatusCode.TooManyRequests };
        using (var loader = Loader(fetch))
        {
            await loader.ShowAsync(Ids(1));
        }

        Assert.Equal(2, fetch.Calls.Count);

        WeatherRetry.CoolDown = TimeSpan.FromSeconds(5);
        var leaving = new CountingFetch { Status = _ => HttpStatusCode.TooManyRequests };
        var gone = Loader(leaving);
        var pending = gone.ShowAsync(Ids(1));
        gone.Dispose();
        await pending;

        Assert.Single(leaving.Calls);
    }

    [Fact]
    public async Task Without_weather_read_nothing_is_ever_requested()
    {
        var fetch = new CountingFetch();
        using var loader = Loader(fetch);
        loader.Reset(canRead: false);

        await loader.ShowAsync(Ids(5));

        Assert.Empty(fetch.Calls);
    }

    // ── OdsInfiniteList.OnShownChanged (AC 22, 25) ────────────────────────────────────────────────

    [Fact]
    public void The_window_signal_fires_on_a_change_of_rendered_keys_only()
    {
        using var ctx = Context();
        var items = Enumerable.Range(0, 60).Select(i => $"item-{i}").ToList();
        var fired = new List<IReadOnlyList<string>>();
        var cut = ctx.Render<OdsInfiniteList<string>>(p => p
            .Add(l => l.Items, items)
            .Add(l => l.BatchSize, 25)
            .Add(l => l.ItemKey, s => s)
            .Add(l => l.RenderItem, s => b => b.AddContent(0, s))
            .Add(l => l.OnShownChanged, shown => fired.Add(shown)));

        Assert.Single(fired);
        Assert.Equal(25, fired[0].Count);

        cut.Render(p => p.Add(l => l.Items, [.. items])); // a new instance, the same rendered keys
        Assert.Single(fired);

        cut.Find(".odc-infinite-pill").Click();
        Assert.Equal(2, fired.Count);
        Assert.Equal(50, fired[1].Count);
    }

    // ── The event dialog section (AC 2, 10, 11, 12, 13, 15, 24, 26) ──────────────────────────────

    private static readonly Guid EventId = Guid.NewGuid();

    private static (BunitContext Ctx, Mock<IWeatherApiClient> Weather) SectionContext(
        ApiResult<CalendarEventWeatherResponse> answer, params string[] claims)
    {
        var ctx = Context(claims.Length == 0 ? [PermissionClaims.WeatherRead] : claims);
        var weather = new Mock<IWeatherApiClient>();
        weather.Setup(w => w.CalendarEventAsync(EventId, null, It.IsAny<CancellationToken>())).ReturnsAsync(answer);
        ctx.Services.AddSingleton(weather.Object);
        return (ctx, weather);
    }

    private static ApiResult<CalendarEventWeatherResponse> Answer(WeatherStatus status, bool allDay = false) =>
        ApiResult<CalendarEventWeatherResponse>.Success(new CalendarEventWeatherResponse
        {
            Status = status,
            Point = Oslo,
            ResolvedFrom = WeatherResolvedFrom.Preferred,
            IsAllDay = allDay,
            Steps = status == WeatherStatus.Ok && !allDay
                ? [new WeatherStep { TimeUtc = UtcNow.AddDays(-1), PeriodHours = 1, SymbolCode = "rain", AirTemperatureC = 7, IssuedAtUtc = UtcNow.AddDays(-2) }]
                : [],
            Days = status == WeatherStatus.Ok && allDay ? [DayWith(true)] : [],
        }, HttpStatusCode.OK);

    private static IRenderedComponent<CalendarEventWeatherSection> Section(BunitContext ctx, bool dirty = false, string title = "Hike",
        EventCallback? onNavigate = null) =>
        ctx.Render<CalendarEventWeatherSection>(p => p
            .Add(s => s.EventId, EventId)
            .Add(s => s.EventTitle, title)
            .Add(s => s.Start, UtcNow.AddDays(-1))
            .Add(s => s.End, UtcNow.AddDays(-1).AddHours(2))
            .Add(s => s.Dirty, dirty)
            .Add(s => s.UtcNow, UtcNow)
            .Add(s => s.OnNavigate, onNavigate ?? EventCallback.Empty));

    [Fact]
    public void Without_weather_read_the_section_renders_nothing_and_asks_for_nothing()
    {
        var (ctx, weather) = SectionContext(Answer(WeatherStatus.Ok), PermissionClaims.CalendarRead);
        using var _ = ctx;
        var cut = Section(ctx);

        Assert.Equal(string.Empty, cut.Markup.Trim());
        weather.VerifyNoOtherCalls();
    }

    [Fact]
    public void An_Ok_timed_event_is_a_group_named_Weather_with_a_captioned_table_and_no_heading()
    {
        var (ctx, weather) = SectionContext(Answer(WeatherStatus.Ok));
        using var _ = ctx;
        var cut = Section(ctx, title: "<img src=x onerror=alert(1)>");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("table.wx-hours-table")));
        var group = cut.Find("[role=group]");
        Assert.Equal("Weather", cut.Find($"#{group.GetAttribute("aria-labelledby")}").TextContent.Trim());
        Assert.Empty(cut.FindAll("h1, h2, h3, h4, h5, h6, [role=heading]"));
        Assert.Equal("Hourly weather for <img src=x onerror=alert(1)>", cut.Find("caption").TextContent);
        Assert.Empty(cut.FindAll("img"));
        Assert.Contains("As forecast, issued", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Hourly weather is never measured.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Weather at Oslo, your default weather point. This event has no place.", cut.Markup, StringComparison.Ordinal);
        Assert.NotNull(cut.Find(".wx-attr a"));
        weather.Verify(w => w.CalendarEventAsync(EventId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void An_all_day_event_shows_static_day_tiles_preferring_the_measured_half()
    {
        var (ctx, _) = SectionContext(Answer(WeatherStatus.Ok, allDay: true));
        using var __ = ctx;
        var cut = Section(ctx);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".wx-day-static")));
        Assert.Empty(cut.FindAll(".wx-days-static button"));
        Assert.Equal("measured", cut.Find(".wx-day-static .wx-day-partial").TextContent.Trim());
    }

    public static TheoryData<WeatherStatus, string, string> States => new()
    {
        { WeatherStatus.NoPlace, "No weather point covers this event’s place, and no default is set.", "status" },
        { WeatherStatus.NoData, "No weather is stored for Oslo on this date.", "status" },
        { WeatherStatus.NotConfigured, "Weather isn’t set up yet.", "status" },
        { WeatherStatus.OutOfRange, "The forecast reaches about 10 days ahead. Check back closer to the date.", "status" },
        { WeatherStatus.WeatherDisabled, "Oslo’s forecast is turned off.", "status" },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Each_state_renders_its_fixed_copy_with_its_role_and_no_snackbar(WeatherStatus status, string copy, string role)
    {
        var (ctx, _) = SectionContext(Answer(status));
        using var __ = ctx;
        var snackbar = ctx.Services.GetRequiredService<ISnackbar>();
        var cut = Section(ctx);

        cut.WaitForAssertion(() => Assert.Equal(copy, cut.Find(".wx-ev-msg p").TextContent));
        Assert.Equal(role, cut.Find(".wx-ev-msg").GetAttribute("role"));
        Assert.Empty(snackbar.ShownSnackbars);
        Assert.Empty(cut.FindAll("a.wx-ev-link"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "You no longer have access to weather.", "alert", false)]
    [InlineData(HttpStatusCode.NotFound, "This event no longer exists.", "alert", false)]
    [InlineData(HttpStatusCode.InternalServerError, "Weather couldn’t be loaded. The event is unaffected.", "alert", true)]
    [InlineData(HttpStatusCode.TooManyRequests, "Weather is resting for a moment. You can try again in a few seconds.", "status", true)]
    public void Each_failure_renders_its_fixed_copy_never_the_server_text(HttpStatusCode code, string copy, string role, bool retry)
    {
        var (ctx, _) = SectionContext(ApiResult<CalendarEventWeatherResponse>.Failure(code, new ApiProblem { Title = "SERVER TEXT", Detail = "SERVER DETAIL" }));
        using var __ = ctx;
        var cut = Section(ctx);

        cut.WaitForAssertion(() => Assert.Equal(copy, cut.Find(".wx-ev-msg p").TextContent));
        Assert.Equal(role, cut.Find(".wx-ev-msg").GetAttribute("role"));
        Assert.DoesNotContain("SERVER", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(retry, cut.FindAll("button").Any(b => b.TextContent.Contains("Try again", StringComparison.Ordinal)));
        if (code == HttpStatusCode.TooManyRequests)
            Assert.Equal("true", cut.FindAll("button").Single(b => b.TextContent.Contains("Try again", StringComparison.Ordinal)).GetAttribute("aria-disabled"));
    }

    [Fact]
    public void Each_action_renders_only_with_the_claim_its_destination_enforces()
    {
        var (ctx, _) = SectionContext(Answer(WeatherStatus.WeatherDisabled), PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead);
        using var __ = ctx;
        var cut = Section(ctx);
        cut.WaitForAssertion(() => Assert.Equal("Open in Weather points", cut.Find("a.wx-ev-link").TextContent));
        Assert.Equal($"/weather-points?open={Oslo.WeatherPointId:D}", cut.Find("a.wx-ev-link").GetAttribute("href"));

        var (admin, _) = SectionContext(Answer(WeatherStatus.NotConfigured), PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead);
        using var ___ = admin;
        var noSettings = Section(admin);
        noSettings.WaitForAssertion(() => Assert.Equal("Weather isn’t set up yet.", noSettings.Find(".wx-ev-msg p").TextContent));
        Assert.Empty(noSettings.FindAll("a.wx-ev-link"));

        var (history, _) = SectionContext(Answer(WeatherStatus.NoData), PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead, PermissionClaims.WeatherPointsUpdate);
        using var ____ = history;
        var hint = Section(history);
        hint.WaitForAssertion(() => Assert.Contains("Turn on historical data for this point", hint.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plain_click_closes_the_dialog_and_a_modified_click_leaves_it_open()
    {
        var closed = 0;
        var (ctx, _) = SectionContext(Answer(WeatherStatus.NoPlace), PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead);
        using var __ = ctx;
        var cut = Section(ctx, onNavigate: EventCallback.Factory.Create(this, () => closed++));
        cut.WaitForAssertion(() => cut.Find("a.wx-ev-link"));

        await cut.Find("a.wx-ev-link").ClickAsync(new MouseEventArgs { Button = 0, CtrlKey = true });
        Assert.Equal(0, closed);
        await cut.Find("a.wx-ev-link").ClickAsync(new MouseEventArgs { Button = 0 });
        Assert.Equal(1, closed);
    }

    [Fact]
    public void While_dirty_the_status_line_says_save_and_actions_are_text_with_no_new_request()
    {
        var (ctx, weather) = SectionContext(Answer(WeatherStatus.NoPlace), PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead);
        using var __ = ctx;
        var cut = Section(ctx);
        cut.WaitForAssertion(() => cut.Find("a.wx-ev-link"));
        var status = cut.Find(".wx-ev-dirty");
        Assert.Equal("status", status.GetAttribute("role"));
        Assert.Equal(string.Empty, status.TextContent);

        cut.Render(p => p.Add(s => s.Dirty, true));

        Assert.Equal("Save to update the weather.", cut.Find(".wx-ev-dirty").TextContent);
        Assert.Empty(cut.FindAll("a.wx-ev-link"));
        Assert.Equal("Save or cancel first to open Weather points.", cut.Find(".wx-ev-hold").TextContent);
        weather.Verify(w => w.CalendarEventAsync(EventId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Try_again_moves_focus_to_the_section_label_and_reads_again()
    {
        var (ctx, weather) = SectionContext(ApiResult<CalendarEventWeatherResponse>.Failure(HttpStatusCode.InternalServerError, new ApiProblem()));
        using var __ = ctx;
        var module = ctx.JSInterop.SetupModule("./js/weather-hours.js");
        module.SetupVoid("focusById", _ => true).SetVoidResult();
        var cut = Section(ctx);
        cut.WaitForAssertion(() => cut.Find(".wx-ev-msg button"));
        var label = cut.Find(".odc-sectiondivider-l");
        Assert.Equal("-1", label.GetAttribute("tabindex"));

        cut.Find(".wx-ev-msg button").Click();

        cut.WaitForAssertion(() => Assert.Contains(module.Invocations, i => i.Identifier == "focusById" && (string)i.Arguments[0]! == label.Id));
        weather.Verify(w => w.CalendarEventAsync(EventId, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── Guards ────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] Markup = ["MarkupString", "innerHTML", "AddMarkupContent"];

    /// <summary>
    /// Untrusted strings (an event title, a place or point name) render as encoded text only (issue #363 §9.2): every
    /// file under Pages/Weather and the five pages this issue changed. The scan set must contain each named file, so a
    /// moved file cannot silently leave it.
    /// </summary>
    [Fact]
    public void No_weather_surface_builds_markup_from_a_string()
    {
        var weatherDir = Path.Combine(ClientSource.Root, "Pages", "Weather");
        var named = new[]
        {
            Path.Combine(weatherDir, "WeatherDayHours.razor"),
            Path.Combine(weatherDir, "WeatherForecastBody.razor"),
            Path.Combine(ClientSource.Root, "Pages", "Calendar", "CalendarEventDialog.razor"),
            Path.Combine(ClientSource.Root, "Pages", "Journal", "JournalEntryCard.razor"),
            Path.Combine(ClientSource.Root, "Pages", "Calendar", "CalendarPage.razor"),
        };
        var files = Directory.GetFiles(weatherDir, "*.razor*").Concat(Directory.GetFiles(weatherDir, "*.cs"))
            .Concat(named).Concat(named.Select(f => f + ".cs").Where(File.Exists)).Distinct().ToList();

        Assert.NotEmpty(files);
        Assert.All(named, f => Assert.Contains(f, files));
        Assert.All(named, f => Assert.True(File.Exists(f), f));
        foreach (var name in new[] { "JournalEntryWeather.razor", "WeatherEventSummaryText.razor", "CalendarEventWeatherSection.razor", "WeatherStepTable.razor" })
            Assert.Contains(Path.Combine(weatherDir, name), files);

        var hits = files
            .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, i)))
            .SelectMany(x => Markup.Where(m => x.line.Contains(m, StringComparison.Ordinal)).Select(m => $"{Path.GetFileName(x.file)}:{x.i + 1} uses {m}"))
            .ToList();
        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    /// <summary>AC 16: only <see cref="WeatherVisuals"/> (and the history measures) format a temperature.</summary>
    [Fact]
    public void No_new_weather_component_formats_a_temperature_itself()
    {
        var weatherDir = Path.Combine(ClientSource.Root, "Pages", "Weather");
        var files = new[]
        {
            "JournalEntryWeather.razor", "WeatherEventSummaryText.razor", "CalendarEventWeatherSection.razor", "CalendarEventWeatherSection.razor.cs",
            "WeatherWhereLine.razor", "WeatherDayTile.razor", "WeatherStepTable.razor", "WeatherEventText.cs", "WeatherBatchLoader.cs",
        }.Select(f => Path.Combine(weatherDir, f)).ToList();
        Assert.All(files, f => Assert.True(File.Exists(f), f));

        var hits = files.Where(f => Regex.IsMatch(StripComments(File.ReadAllText(f)), "°")).Select(Path.GetFileName).ToList();
        Assert.True(hits.Count == 0, string.Join(", ", hits));
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"@\*.*?\*@", "", RegexOptions.Singleline), @"^\s*//.*$", "", RegexOptions.Multiline);

    /// <summary>The text a screen reader takes as the button's name: everything but the aria-hidden glyphs.</summary>
    private static string VisibleName(AngleSharp.Dom.IElement element)
    {
        var clone = (AngleSharp.Dom.IElement)element.Clone(deep: true);
        foreach (var hidden in clone.QuerySelectorAll("[aria-hidden=true]").ToList())
            hidden.Remove();
        return Normalize(clone.TextContent);
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static JournalEntrySummary Entry() => new()
    {
        JournalEntryId = Guid.NewGuid(),
        Title = "Hike",
        Content = "Text",
        EntryDate = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc),
        AttachmentCount = 0,
    };
}
