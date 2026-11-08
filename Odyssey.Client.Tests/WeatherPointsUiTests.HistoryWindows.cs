using System.Net;
using Bunit;
using Moq;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>History windows in the client (issue #437 §11). Same harness as the rest of the Weather points tests.</summary>
public partial class WeatherPointsUiTests
{
    private static ExistingWeatherHistoryWindow Win(string from, string? to, WeatherHistoryWindowStatus status = WeatherHistoryWindowStatus.Complete, int covered = 8, int? fetchable = 8) =>
        new() { From = DateOnly.Parse(from), To = to is null ? null : DateOnly.Parse(to), Status = status, CoveredDays = covered, FetchableDays = fetchable };

    private static ExistingWeatherPoint WithWindows(ExistingWeatherPoint p, params ExistingWeatherHistoryWindow[] windows) => p with
    {
        HasObservationStation = true,
        HistoryWindows = windows,
        HistoryWindowSummary = WeatherHistoryWindowSummary.From([.. windows.Select(w => (w.From, w.To))]),
    };

    private static IRenderedComponent<ProvidersHost> Settings(Bunit.BunitContext ctx, ExistingWeatherPoint point) =>
        ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d.Add(x => x.Open, true).Add(x => x.Point, point)));

    private static async Task SetDateAsync(IRenderedComponent<ProvidersHost> cut, string id, DateOnly? value)
    {
        var field = cut.FindComponents<OdsDateField>().Single(f => f.Instance.Id == id);
        await cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value?.ToDateTime(TimeOnly.MinValue)));
    }

    private static List<string> FromIds(IRenderedComponent<ProvidersHost> cut) =>
        [.. cut.FindComponents<OdsDateField>().Where(f => f.Instance.Label == "From").Select(f => f.Instance.Id!)];

    [Fact]
    public async Task Create_starts_with_one_ongoing_window_a_year_back_and_sends_it()
    {
        var weather = Weather();
        NewWeatherPoint? sent = null;
        weather.Setup(w => w.CreatePointAsync(It.IsAny<NewWeatherPoint>(), It.IsAny<CancellationToken>()))
            .Callback((NewWeatherPoint r, CancellationToken _) => sent = r)
            .ReturnsAsync(Ok(Point("Tromsø", OsloId)));
        await using var ctx = PageContext(weather, AdminClaims);
        // Captured on both sides of the render, so a run that crosses midnight cannot flake.
        var before = DateOnly.FromDateTime(DateTime.Today);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d.Add(x => x.Open, true)));
        var after = DateOnly.FromDateTime(DateTime.Today);

        var box = cut.FindAll(".wx-hw-row input[type=checkbox]").Single();
        Assert.True(box.HasAttribute("checked"));
        cut.FindComponents<OdsCoordinateField>().Single();
        await cut.InvokeAsync(() => cut.FindComponent<OdsCoordinateField>().Instance.LatChanged.InvokeAsync(69.649m));
        await cut.InvokeAsync(() => cut.FindComponent<OdsCoordinateField>().Instance.LngChanged.InvokeAsync(18.955m));
        await cut.InvokeAsync(() => cut.FindComponent<OdsTextInputField>().Instance.ValueChanged.InvokeAsync("Tromsø"));
        await cut.InvokeAsync(() => cut.FindComponent<OdsCombobox>().Instance.ValueChanged.InvokeAsync("Europe/Oslo"));

        await Press(cut, "Create weather point");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        var window = Assert.Single(sent!.HistoryWindows!);
        Assert.Contains(window.From!.Value, new[] { before.AddYears(-1), after.AddYears(-1) });
        Assert.Null(window.To);
    }

    [Fact]
    public async Task Settings_loads_the_windows_from_the_single_read_and_saves_the_full_array_in_display_order()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"), Win("2026-12-27", "2027-01-03", WeatherHistoryWindowStatus.Upcoming));
        var weather = Weather(point);
        UpdateWeatherPoint? sent = null;
        weather.Setup(w => w.UpdatePointAsync(OsloId, It.IsAny<UpdateWeatherPoint>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateWeatherPoint r, CancellationToken _) => sent = r)
            .ReturnsAsync(Ok(new WeatherPointUpdateResult { Point = point }));
        await using var ctx = PageContext(weather, AdminClaims);
        // The list's row carries no windows; the dialog reads them itself.
        var cut = Settings(ctx, point with { HistoryWindows = null });

        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));
        await Press(cut, "New window");
        cut.WaitForAssertion(() => Assert.Equal(3, FromIds(cut).Count));
        var ids = FromIds(cut);
        // An earlier date on the new last row does not move it: rows never re-sort while the dialog is open.
        await SetDateAsync(cut, ids[2], new DateOnly(2026, 2, 10));
        await SetDateAsync(cut, ids[2].Replace("-from", "-to", StringComparison.Ordinal), new DateOnly(2026, 2, 17));
        Assert.Equal(ids, FromIds(cut));

        await Press(cut, "Save");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal([new DateOnly(2026, 7, 4), new DateOnly(2026, 12, 27), new DateOnly(2026, 2, 10)], sent!.HistoryWindows!.Select(w => w.From!.Value));
    }

    [Fact]
    public async Task No_end_date_is_on_every_row_and_unticking_restores_the_typed_end()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"), Win("2026-08-01", "2026-08-07"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".wx-hw-row input[type=checkbox]").Count));

        var second = cut.FindAll(".wx-hw-row input[type=checkbox]")[1];
        await cut.InvokeAsync(() => second.Change(true));
        var to = cut.FindComponents<OdsDateField>().Where(f => f.Instance.Label == "To").ElementAt(1);
        Assert.True(to.Instance.Disabled);
        Assert.Null(to.Instance.Value);
        Assert.Contains("collected every day", cut.Markup);

        await cut.InvokeAsync(() => cut.FindAll(".wx-hw-row input[type=checkbox]")[1].Change(false));
        to = cut.FindComponents<OdsDateField>().Where(f => f.Instance.Label == "To").ElementAt(1);
        Assert.Equal(new DateTime(2026, 8, 7), to.Instance.Value);
    }

    [Fact]
    public async Task Rule_errors_show_on_the_right_row_before_any_request()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2025-01-01", null, WeatherHistoryWindowStatus.Collecting), Win("2026-07-04", "2026-07-18"));
        var weather = Weather(point);
        await using var ctx = PageContext(weather, AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));
        await Press(cut, "New window");
        cut.WaitForAssertion(() => Assert.Equal(3, FromIds(cut).Count));
        await SetDateAsync(cut, FromIds(cut)[2], new DateOnly(2026, 7, 10));

        await Press(cut, "Save");

        cut.WaitForAssertion(() => Assert.Contains("Only the latest window can have no end date. Add an end date here, or remove window 3, which starts later.", cut.Markup));
        Assert.Contains("Window 3: Enter an end date, or tick No end date.", cut.Markup);
        // The row-level error is tied to the row's From, which is marked invalid — on the inner input itself.
        var firstFrom = cut.FindComponents<OdsDatePicker>().Single(p => p.Instance.InputId == FromIds(cut)[0]);
        Assert.True(firstFrom.Instance.AriaInvalid);
        Assert.Contains("-row-err", firstFrom.Instance.AriaDescribedBy ?? "");
        Assert.Single(cut.FindAll($"input#{FromIds(cut)[0]}"));
        weather.Verify(w => w.UpdatePointAsync(It.IsAny<Guid>(), It.IsAny<UpdateWeatherPoint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Server_window_keys_land_on_their_row_and_bad_keys_fall_back_to_the_list()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"), Win("2026-08-01", "2026-08-07"));
        var weather = Weather(point);
        weather.Setup(w => w.UpdatePointAsync(OsloId, It.IsAny<UpdateWeatherPoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<WeatherPointUpdateResult>.Failure(HttpStatusCode.BadRequest, new ApiProblem
            {
                Status = 400,
                Errors = new Dictionary<string, string[]>
                {
                    ["HistoryWindows[1].To"] = ["Choose a date on or before 7 Nov 2028."],
                    ["historywindows[99]"] = ["This window overlaps another window."],
                },
            }));
        await using var ctx = PageContext(weather, AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));

        await Press(cut, "Save");

        cut.WaitForAssertion(() => Assert.Contains("Window 2: Choose a date on or before 7 Nov 2028.", cut.Markup));
        Assert.Contains("This window overlaps another window.", cut.Find(".wx-hw-head").TextContent);
    }

    [Theory]
    [InlineData("HistoryWindows[1].To", 1, WindowField.To)]
    [InlineData("historywindows[0].from", 0, WindowField.From)]
    [InlineData("HistoryWindows[0]", 0, WindowField.Row)]
    [InlineData("HistoryWindows", -1, WindowField.List)]
    [InlineData("HistoryWindows[2]", -1, WindowField.List)]
    [InlineData("HistoryWindows[-1]", -1, WindowField.List)]
    [InlineData("HistoryWindows[99999999999]", -1, WindowField.List)]
    [InlineData("HistoryWindows[0].Other", -1, WindowField.List)]
    [InlineData(null, -1, WindowField.List)]
    public void Server_keys_parse_defensively(string? key, int index, WindowField field) =>
        Assert.Equal((index, field), WeatherHistoryWindows.ParseKey(key, rowCount: 2));

    [Fact]
    public async Task History_off_hides_the_editor_and_keeps_the_stored_windows()
    {
        var point = WithWindows(Point("Cabin", OsloId) with { HistoryEnabled = false }, Win("2026-07-04", "2026-07-18"));
        var weather = Weather(point);
        UpdateWeatherPoint? sent = null;
        weather.Setup(w => w.UpdatePointAsync(OsloId, It.IsAny<UpdateWeatherPoint>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateWeatherPoint r, CancellationToken _) => sent = r)
            .ReturnsAsync(Ok(new WeatherPointUpdateResult { Point = point }));
        await using var ctx = PageContext(weather, AdminClaims);
        var cut = Settings(ctx, point);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".wx-hw-list")));
        await Press(cut, "Save");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        // null is "keep the stored set" on the server; an array would replace it.
        Assert.Null(sent!.HistoryWindows);
    }

    [Fact]
    public async Task History_off_with_a_failed_window_read_saves_without_replacing_the_windows()
    {
        var point = WithWindows(Point("Cabin", OsloId) with { HistoryEnabled = false }, Win("2026-07-04", "2026-07-18"));
        var weather = Weather(point);
        weather.Setup(w => w.PointAsync(OsloId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingWeatherPoint>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));
        UpdateWeatherPoint? sent = null;
        weather.Setup(w => w.UpdatePointAsync(OsloId, It.IsAny<UpdateWeatherPoint>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateWeatherPoint r, CancellationToken _) => sent = r)
            .ReturnsAsync(Ok(new WeatherPointUpdateResult { Point = point }));
        await using var ctx = PageContext(weather, AdminClaims);
        var cut = Settings(ctx, point);

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load this point’s history windows.", cut.Markup));
        await Press(cut, "Save");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Null(sent!.HistoryWindows);
    }

    [Fact]
    public async Task Removing_a_window_shows_the_kept_data_note_and_announces_once()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"), Win("2026-08-01", "2026-08-07"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));
        var region = cut.Find(".wx-hw-shrink-region");
        Assert.Equal("status", region.GetAttribute("role"));
        Assert.Empty(region.TextContent.Trim());

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.GetAttribute("aria-label") == "Remove window 2").Click());

        cut.WaitForAssertion(() => Assert.Contains("Days already stored outside the new windows are kept.", cut.Find(".wx-hw-shrink-region").TextContent));
        Assert.Contains("Window 2 removed, 1 remains.", cut.Markup);
    }

    [Fact]
    public void Shrinks_compares_days_not_rows()
    {
        var loaded = new[] { Win("2026-06-10", "2026-06-17") };
        Assert.True(WeatherHistoryWindows.Shrinks(loaded, [new WeatherWindowRow { From = new(2026, 6, 10), To = new(2026, 6, 16) }]));
        Assert.False(WeatherHistoryWindows.Shrinks(loaded,
            [new WeatherWindowRow { From = new(2026, 6, 1), To = new(2026, 6, 12) }, new WeatherWindowRow { From = new(2026, 6, 13), Open = true }]));
        Assert.False(WeatherHistoryWindows.Shrinks(loaded, [new WeatherWindowRow { From = new(2026, 6, 1), To = new(2026, 6, 30) }]));
        // An incomplete row counts for nothing.
        Assert.True(WeatherHistoryWindows.Shrinks(loaded, [new WeatherWindowRow { From = new(2026, 6, 10) }]));
    }

    [Fact]
    public async Task A_stale_reload_replaces_the_windows_and_says_so()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"));
        var theirs = WithWindows(point with { SettingsVersion = Guid.NewGuid() }, Win("2026-09-01", "2026-09-07"));
        var weather = Weather(point);
        var reads = 0;
        weather.Setup(w => w.PointAsync(OsloId, It.IsAny<CancellationToken>())).ReturnsAsync(() => Ok(++reads == 1 ? point : theirs));
        weather.Setup(w => w.UpdatePointAsync(OsloId, It.IsAny<UpdateWeatherPoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<WeatherPointUpdateResult>.Failure(HttpStatusCode.Conflict, new ApiProblem { Status = 409, Code = "stale" }));
        await using var ctx = PageContext(weather, AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Single(FromIds(cut)));

        await Press(cut, "Save");
        cut.WaitForAssertion(() => Assert.Contains("Reload", cut.Markup));
        await Press(cut, "Reload");

        cut.WaitForAssertion(() => Assert.Equal(new DateTime(2026, 9, 1),
            cut.FindComponents<OdsDateField>().Single(f => f.Instance.Label == "From").Instance.Value));
        Assert.Contains("Windows reloaded from the server. Your window edits were discarded.", cut.FindComponent<OdsLiveAnnouncer>().Instance.Message);
    }

    [Fact]
    public async Task A_failed_window_read_shows_retry_no_editor_and_keeps_save_off()
    {
        var point = Point("Cabin", OsloId);
        var weather = Weather(point);
        var reads = 0;
        weather.Setup(w => w.PointAsync(OsloId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++reads == 1 ? ApiResult<ExistingWeatherPoint>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }) : Ok(point));
        await using var ctx = PageContext(weather, AdminClaims);
        var cut = Settings(ctx, point);

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load this point’s history windows.", cut.Markup));
        Assert.Empty(cut.FindAll(".wx-hw-list"));
        var save = cut.FindAll("button").Single(b => b.TextContent.Contains("Save", StringComparison.Ordinal));
        Assert.Equal("true", save.GetAttribute("aria-disabled"));

        await Press(cut, "Retry");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".wx-hw-list")));
    }

    [Fact]
    public async Task The_record_lists_each_window_with_its_status_in_words_and_paused_wins()
    {
        var point = WithWindows(Point("Cabin", OsloId),
            Win("2025-02-14", "2025-02-23", WeatherHistoryWindowStatus.Unavailable, 0, 0),
            Win("2026-08-10", "2026-08-24", WeatherHistoryWindowStatus.Backfilling, 6, 15));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var section = ctx.Render<WeatherHistoryWindowsSection>(p => p.Add(x => x.Row, point));

        section.WaitForAssertion(() => Assert.Equal(2, section.FindAll(".wx-hws-list li").Count));
        Assert.Contains("14 Feb 2025 to 23 Feb 2025", section.Markup);
        Assert.Contains("Unavailable", section.Markup);
        Assert.Contains("6 of 15 days checked", section.Markup);
        Assert.Contains("2 windows", section.Markup);

        await using var offCtx = PageContext(Weather(point with { HistoryEnabled = false }), AdminClaims);
        var off = offCtx.Render<WeatherHistoryWindowsSection>(p => p.Add(x => x.Row, point with { HistoryEnabled = false }));
        off.WaitForAssertion(() => Assert.Equal(2, off.FindAll(".wx-hws-list li .odc-chip").Count(c => c.TextContent.Contains("Paused", StringComparison.Ordinal))));
        Assert.Contains("Paused — historical data is off.", off.Markup);
    }

    [Fact]
    public async Task The_history_tile_summarises_the_windows()
    {
        await using var ctx = PageContext(Weather(), AdminClaims);
        var home = WithWindows(Point("Oslo", OsloId), Win("2025-03-02", null, WeatherHistoryWindowStatus.Collecting));
        Assert.Contains("From 2 Mar 2025", ctx.Render<WeatherPointTiles>(p => p.Add(x => x.Row, home)).Markup);

        var trips = WithWindows(Point("Cabin", OsloId), Win("2025-02-14", "2025-02-23"), Win("2026-12-27", "2027-01-03"));
        var tile = ctx.Render<WeatherPointTiles>(p => p.Add(x => x.Row, trips)).Markup;
        Assert.Contains("2 windows", tile);
        Assert.Contains("14 Feb 2025 to 3 Jan 2027", tile);
    }

    [Fact]
    public void Weather_client_code_carries_no_history_from_or_window_bound_literals()
    {
        var root = Path.Combine(ClientSource.Root, "Pages", "Weather");
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".cs") || f.EndsWith(".razor")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("HistoryFrom", text, StringComparison.Ordinal);
            Assert.DoesNotContain("730", text, StringComparison.Ordinal);
            Assert.DoesNotContain("1950", text, StringComparison.Ordinal);
            // Windows are travel dates: never into page state, browser storage or a log.
            if (text.Contains("WeatherWindowRow", StringComparison.Ordinal) || text.Contains("HistoryWindows", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("IPageStateService", text, StringComparison.Ordinal);
                Assert.DoesNotContain("localStorage", text, StringComparison.Ordinal);
                Assert.DoesNotContain("sessionStorage", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Console.", text, StringComparison.Ordinal);
                Assert.DoesNotContain("ILogger", text, StringComparison.Ordinal);
            }
        }
    }
}
