using System.Net;
using Bunit;
using Moq;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// History windows (issue #437 §11): the acceptance criteria the first pass left without a bUnit test — focus
/// targets, announcements, the shrink note's undo, point-level precedence, the purge copy and the accessible names.
/// </summary>
public partial class WeatherPointsUiTests
{
    private static WeatherHistoryWindowsEditor Editor(IRenderedComponent<ProvidersHost> cut) =>
        cut.FindComponent<WeatherHistoryWindowsEditor>().Instance;

    private static AngleSharp.Dom.IElement RemoveButton(IRenderedComponent<ProvidersHost> cut, int n) =>
        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == $"Remove window {n}");

    private static string ShrinkRegion(IRenderedComponent<ProvidersHost> cut) => cut.Find(".wx-hw-shrink-region").TextContent.Trim();

    private static string? DialogAnnouncement(IRenderedComponent<ProvidersHost> cut) =>
        cut.FindComponent<OdsLiveAnnouncer>().Instance.Message;

    /// <summary>A label's text as an accessible name computes it: aria-hidden decoration (a check glyph) left out.</summary>
    private static string AccessibleText(AngleSharp.Dom.IElement label)
    {
        var text = label.TextContent;
        foreach (var h in label.QuerySelectorAll("[aria-hidden=true]"))
            text = text.Replace(h.TextContent, "", StringComparison.Ordinal);
        return text.Trim();
    }

    private static bool Focused(BunitContext ctx, string fn, string id) =>
        ctx.JSInterop.Invocations.Any(i => i.Identifier == fn && Equals(i.Arguments[0], id));

    private static string? LastFocusById(BunitContext ctx) =>
        ctx.JSInterop.Invocations.LastOrDefault(i => i.Identifier == "odsFocusById").Arguments?[0] as string;

    private static async Task FillCoordinatesAsync(IRenderedComponent<ProvidersHost> cut)
    {
        await cut.InvokeAsync(() => cut.FindComponent<OdsCoordinateField>().Instance.LatChanged.InvokeAsync(69.649m));
        await cut.InvokeAsync(() => cut.FindComponent<OdsCoordinateField>().Instance.LngChanged.InvokeAsync(18.955m));
        await cut.InvokeAsync(() => cut.FindComponent<OdsTextInputField>().Instance.ValueChanged.InvokeAsync("Tromsø"));
        await cut.InvokeAsync(() => cut.FindComponent<OdsCombobox>().Instance.ValueChanged.InvokeAsync("Europe/Oslo"));
    }

    // ── AC 1: create with history off still sends the one default row ───────────────────────────────

    [Fact]
    public async Task Create_with_history_turned_off_still_sends_exactly_the_one_default_row()
    {
        var weather = Weather();
        NewWeatherPoint? sent = null;
        weather.Setup(w => w.CreatePointAsync(It.IsAny<NewWeatherPoint>(), It.IsAny<CancellationToken>()))
            .Callback((NewWeatherPoint r, CancellationToken _) => sent = r)
            .ReturnsAsync(Ok(Point("Tromsø", OsloId)));
        await using var ctx = PageContext(weather, AdminClaims);
        var before = DateOnly.FromDateTime(DateTime.Today);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d.Add(x => x.Open, true)));
        var after = DateOnly.FromDateTime(DateTime.Today);
        await FillCoordinatesAsync(cut);

        var history = cut.FindComponents<OdsSwitch>().Single(s => s.Instance.AriaLabelledBy?.EndsWith("-hs-l", StringComparison.Ordinal) == true);
        await cut.InvokeAsync(() => history.Instance.CheckedChanged.InvokeAsync(false));
        Assert.Empty(cut.FindAll(".wx-hw-list"));

        await Press(cut, "Create weather point");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.False(sent!.HistoryEnabled);
        var window = Assert.Single(sent.HistoryWindows!);
        Assert.Contains(window.From!.Value, new[] { before.AddYears(-1), after.AddYears(-1) });
        Assert.Null(window.To);
    }

    // ── AC 3 / 13: New window focuses its own From ───────────────────────────────────────────────────

    [Fact]
    public async Task New_window_scrolls_to_and_focuses_the_new_rows_from_field()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Single(FromIds(cut)));

        await Press(cut, "New window");

        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));
        var added = FromIds(cut)[1];
        cut.WaitForAssertion(() => Assert.Equal(added, LastFocusById(ctx)));
        Assert.True(Focused(ctx, "odsScrollIntoViewById", added));
        Assert.Contains("Window 2", cut.FindAll("legend.wx-hw-legend")[1].TextContent);
    }

    // ── AC 4 / 23: focus after removal, two removals in turn ─────────────────────────────────────────

    [Fact]
    public async Task Removing_moves_focus_to_the_next_then_the_previous_remove_then_the_new_window_button()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-06-01", "2026-06-07"), Win("2026-07-04", "2026-07-18"), Win("2026-08-01", "2026-08-07"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(3, FromIds(cut).Count));
        var firstRemove = RemoveButton(cut, 1).Id;
        var thirdRemove = RemoveButton(cut, 3).Id;
        Assert.False(string.IsNullOrEmpty(firstRemove));

        // The middle row: focus to the next row's Remove (window 3, now numbered 2).
        await cut.InvokeAsync(() => RemoveButton(cut, 2).Click());
        cut.WaitForAssertion(() => Assert.Equal(thirdRemove, LastFocusById(ctx)));
        Assert.Equal("Window 2 removed, 2 remain.", DialogAnnouncement(cut));
        Assert.Equal(thirdRemove, RemoveButton(cut, 2).Id);

        // The last row: no next, so the previous row's Remove.
        await cut.InvokeAsync(() => RemoveButton(cut, 2).Click());
        cut.WaitForAssertion(() => Assert.Equal(firstRemove, LastFocusById(ctx)));
        Assert.Equal("Window 2 removed, 1 remains.", DialogAnnouncement(cut));

        // The only row: the New window button, through its wrapper — never the wrapper itself.
        var addHost = cut.Find(".wx-hw-add");
        Assert.NotNull(addHost.QuerySelector("button.odc-add-row"));
        await cut.InvokeAsync(() => RemoveButton(cut, 1).Click());
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i =>
            i.Identifier == "odsFocusFirstIn" && Equals(i.Arguments[0], addHost.Id) && Equals(i.Arguments[1], false)));
        Assert.Equal("Window 1 removed, 0 remain.", DialogAnnouncement(cut));
        Assert.Empty(cut.FindAll(".wx-hw-list"));
    }

    // ── AC 9: the shrink note follows the answer, both ways ──────────────────────────────────────────

    [Fact]
    public async Task Narrowing_a_window_sets_the_kept_data_note_and_restoring_it_clears_the_note()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Single(FromIds(cut)));
        var to = FromIds(cut)[0].Replace("-from", "-to", StringComparison.Ordinal);
        Assert.Empty(ShrinkRegion(cut));

        await SetDateAsync(cut, to, new DateOnly(2026, 7, 10));
        Assert.Contains("Days already stored outside the new windows are kept. Use Delete history to remove them.", ShrinkRegion(cut));

        await SetDateAsync(cut, to, new DateOnly(2026, 7, 18));
        Assert.Empty(ShrinkRegion(cut));

        // Widening is not a shrink either.
        await SetDateAsync(cut, to, new DateOnly(2026, 7, 31));
        Assert.Empty(ShrinkRegion(cut));
    }

    [Fact]
    public async Task Removing_a_window_and_adding_it_back_clears_the_kept_data_note()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"), Win("2026-08-01", "2026-08-07"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));

        await cut.InvokeAsync(() => RemoveButton(cut, 2).Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(ShrinkRegion(cut)));

        await Press(cut, "New window");
        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));
        await SetDateAsync(cut, FromIds(cut)[1], new DateOnly(2026, 8, 1));
        // An incomplete row covers nothing yet.
        Assert.NotEmpty(ShrinkRegion(cut));
        await SetDateAsync(cut, FromIds(cut)[1].Replace("-from", "-to", StringComparison.Ordinal), new DateOnly(2026, 8, 7));
        Assert.Empty(ShrinkRegion(cut));
    }

    [Fact]
    public async Task Create_never_shows_the_kept_data_note()
    {
        await using var ctx = PageContext(Weather(), AdminClaims);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d.Add(x => x.Open, true)));
        cut.WaitForAssertion(() => Assert.Single(FromIds(cut)));

        await cut.InvokeAsync(() => RemoveButton(cut, 1).Click());

        Assert.Empty(ShrinkRegion(cut));
    }

    // ── AC 12: point-level precedence ────────────────────────────────────────────────────────────────

    public static TheoryData<bool, bool, bool, WeatherFetchFailureKind?, string> PrecedenceCases => new()
    {
        // history off outranks everything
        { false, true, false, WeatherFetchFailureKind.Timeout, "Paused — historical data is off." },
        // then a purge in progress
        { true, true, false, WeatherFetchFailureKind.Timeout, "Deleting history." },
        // then no station
        { true, false, false, WeatherFetchFailureKind.Timeout, "No MET station near enough." },
        // then a failure
        { true, false, true, WeatherFetchFailureKind.Timeout, "Timed out. Collection resumes when MET Norway answers again." },
    };

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public async Task The_record_shows_one_paused_line_by_precedence_and_every_chip_reads_paused(
        bool historyOn, bool purging, bool hasStation, WeatherFetchFailureKind? failure, string expected)
    {
        var point = WithWindows(Point("Cabin", OsloId),
            Win("2026-07-04", "2026-07-18"),
            Win("2026-08-10", "2026-08-24", WeatherHistoryWindowStatus.Backfilling, 6, 15)) with
        {
            HistoryEnabled = historyOn,
            PurgeInProgress = purging,
            HasObservationStation = hasStation,
            ObservationFailureKind = failure,
        };
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var section = ctx.Render<WeatherHistoryWindowsSection>(p => p.Add(x => x.Row, point));

        section.WaitForAssertion(() => Assert.Equal(2, section.FindAll(".wx-hws-list li").Count));
        var lines = section.FindAll(".wx-hws-paused");
        Assert.Contains(expected, Assert.Single(lines).TextContent);
        Assert.All(section.FindAll(".wx-hws-list li .odc-chip"), c => Assert.Contains("Paused", c.TextContent));
        // A paused window claims no progress.
        Assert.DoesNotContain("days checked", section.Markup);
    }

    [Fact]
    public async Task A_healthy_point_shows_no_paused_line()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-08-10", "2026-08-24", WeatherHistoryWindowStatus.Backfilling, 6, 15));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var section = ctx.Render<WeatherHistoryWindowsSection>(p => p.Add(x => x.Row, point));

        section.WaitForAssertion(() => Assert.Single(section.FindAll(".wx-hws-list li")));
        Assert.Empty(section.FindAll(".wx-hws-paused"));
        Assert.Contains("Backfilling", section.Markup);
    }

    [Fact]
    public async Task The_record_keeps_the_windows_section_during_a_purge_with_every_window_paused()
    {
        var point = WithWindows(Point("Cabin", OsloId) with { HistoryEnabled = false, PurgeInProgress = true }, Win("2026-07-04", "2026-07-18"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var page = ctx.Render<WeatherPointsPage>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".odc-record")));

        page.Find(".odc-record-trigger").Click();

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".wx-hws-list li")));
        Assert.Contains("Paused", page.Find(".wx-hws-list li .odc-chip").TextContent);
    }

    // ── AC 13 / 22: the error summary's links ────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_links_a_row_error_to_its_from_and_the_list_error_to_the_head()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2025-01-01", null, WeatherHistoryWindowStatus.Collecting), Win("2026-07-04", "2026-07-18"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));
        var firstFrom = FromIds(cut)[0];

        await Press(cut, "Save");

        cut.WaitForAssertion(() => Assert.Contains("Window 1: Only the latest window", cut.Find(".loc-err-summary").TextContent));
        var rowLink = cut.FindAll(".loc-err-summary a").Single(a => a.TextContent.StartsWith("Window 1:", StringComparison.Ordinal));
        Assert.Equal($"#{firstFrom}", rowLink.GetAttribute("href"));
        await cut.InvokeAsync(() => rowLink.Click());
        Assert.Equal(firstFrom, LastFocusById(ctx));

        // Remove both rows: the list's own error is an activatable link to the History windows head.
        await cut.InvokeAsync(() => RemoveButton(cut, 2).Click());
        await cut.InvokeAsync(() => RemoveButton(cut, 1).Click());
        await Press(cut, "Save");

        var head = Editor(cut).HeadId;
        cut.WaitForAssertion(() => Assert.Contains(WeatherHistoryWindows.EmptyMessage, cut.Find(".loc-err-summary").TextContent));
        var listLink = cut.FindAll(".loc-err-summary a").Single(a => a.TextContent.Contains(WeatherHistoryWindows.EmptyMessage, StringComparison.Ordinal));
        Assert.Equal($"#{head}", listLink.GetAttribute("href"));
        Assert.Equal("-1", cut.Find($"#{head}").GetAttribute("tabindex"));
        await cut.InvokeAsync(() => listLink.Click());
        Assert.Equal(head, LastFocusById(ctx));
        Assert.Contains(WeatherHistoryWindows.EmptyMessage, cut.Find(".wx-hw-head").TextContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Adding_or_removing_a_row_clears_every_window_error_so_no_stale_number_survives(bool add)
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"), Win("2026-08-01", "2026-08-07"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));
        // Window 2 overlaps window 1.
        await SetDateAsync(cut, FromIds(cut)[1], new DateOnly(2026, 7, 10));
        await Press(cut, "Save");
        cut.WaitForAssertion(() => Assert.Contains("Window 2: This window overlaps window 1.", cut.Markup));

        if (add)
            await Press(cut, "New window");
        else
            await cut.InvokeAsync(() => RemoveButton(cut, 1).Click());

        cut.WaitForAssertion(() => Assert.DoesNotContain("overlaps window", cut.Markup));
        Assert.DoesNotContain("Window 2:", cut.Markup);
        Assert.Empty(cut.FindAll(".wx-hw-row.error"));
        Assert.Empty(cut.FindAll(".wx-hw-row-err"));
    }

    [Fact]
    public async Task Editing_a_row_clears_only_that_rows_errors()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"), Win("2026-08-01", "2026-08-07"), Win("2026-09-01", "2026-09-07"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(3, FromIds(cut).Count));
        var ids = FromIds(cut);
        await SetDateAsync(cut, ids[1], null);
        await SetDateAsync(cut, ids[2], null);
        await Press(cut, "Save");
        cut.WaitForAssertion(() => Assert.Contains("Window 3: Enter a start date.", cut.Markup));
        Assert.Contains("Window 2: Enter a start date.", cut.Markup);

        await SetDateAsync(cut, ids[1], new DateOnly(2026, 8, 1));

        Assert.DoesNotContain("Window 2: Enter a start date.", cut.Markup);
        Assert.Contains("Window 3: Enter a start date.", cut.Markup);
    }

    // ── AC 16 / 21: names are the visible labels, and the visibility line ────────────────────────────

    [Fact]
    public async Task Each_rows_controls_are_named_by_their_visible_labels_under_a_window_legend()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"), Win("2026-08-01", null, WeatherHistoryWindowStatus.Collecting));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Equal(2, FromIds(cut).Count));

        var rows = cut.FindAll(".wx-hw-list > li > fieldset.wx-hw-row");
        Assert.Equal(2, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            Assert.Equal($"Window {i + 1}", row.QuerySelector("legend")!.TextContent.Trim());
            foreach (var (selector, label) in new[] { ("input[id$='-from']", "From"), ("input[id$='-to']", "To"), ("input[type=checkbox]", "No end date") })
            {
                var input = row.QuerySelector(selector);
                Assert.NotNull(input);
                Assert.False(input!.HasAttribute("aria-label"), $"{label} must be named by its visible label");
                var name = AccessibleText(cut.Find($"label[for='{input.Id}']"));
                Assert.StartsWith(label, name, StringComparison.Ordinal);
                // The number lives in the legend, not in the field's name.
                Assert.DoesNotContain("Window", name, StringComparison.Ordinal);
            }

            var remove = Assert.Single(row.QuerySelectorAll("button"),
                b => b.GetAttribute("aria-label")?.StartsWith("Remove", StringComparison.Ordinal) == true);
            Assert.Equal($"Remove window {i + 1}", remove.GetAttribute("aria-label"));
        }

        var list = cut.Find("ol.wx-hw-list");
        Assert.Equal("History windows", cut.Find($"#{list.GetAttribute("aria-labelledby")}").TextContent.Trim());
        Assert.NotNull(cut.Find($"#{list.GetAttribute("aria-describedby")}"));
        // "collected every day" describes the open row's checkbox, not its disabled To.
        var openBox = rows[1].QuerySelector("input[type=checkbox]")!;
        Assert.Equal("collected every day", cut.Find($"#{openBox.GetAttribute("aria-describedby")}").TextContent.Trim());
        Assert.Null(rows[0].QuerySelector("input[type=checkbox]")!.GetAttribute("aria-describedby"));
    }

    [Fact]
    public async Task The_editor_says_measured_days_are_visible_to_everyone_who_can_see_weather()
    {
        await using var ctx = PageContext(Weather(), AdminClaims);
        var cut = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointDialog>(d => d.Add(x => x.Open, true)));
        cut.WaitForAssertion(() => Assert.Single(FromIds(cut)));

        var list = cut.Find("ol.wx-hw-list");
        var help = cut.Find($"#{list.GetAttribute("aria-describedby")}").TextContent;
        Assert.Contains("Days outside every window are not fetched. Days already stored stay.", help);
        Assert.Contains("Measured days can be seen by everyone who can see weather, so they show when you were here.", help);
    }

    // ── AC 20: the purge dialog's copy ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_history_purge_says_the_windows_are_kept_and_the_point_purge_says_they_go()
    {
        var point = Point("Oslo", OsloId) with { ForecastEnabled = false, HistoryEnabled = false };
        await using var ctx = PageContext(Weather(point), AdminClaims);
        await using var wholeCtx = PageContext(Weather(point), AdminClaims);

        var history = ctx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointPurgeDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Point, point).Add(x => x.Mode, WeatherPurgeMode.HistoryOnly)));
        Assert.Contains("History windows are kept. Remove them in Weather settings if you no longer want them.", history.Markup);

        var whole = wholeCtx.Render<ProvidersHost>(p => p.AddChildContent<WeatherPointPurgeDialog>(d => d
            .Add(x => x.Open, true).Add(x => x.Point, point).Add(x => x.Mode, WeatherPurgeMode.Full).Add(x => x.LinkedLocationCount, 0)));
        Assert.Contains("every position it has used, and its history windows.", whole.Find("ul.wx-effects").TextContent);
        Assert.DoesNotContain("History windows are kept.", whole.Markup);
    }

    // ── AC 24: loading, retry and focus ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_retry_is_busy_while_it_reads_then_renders_the_editor_and_focuses_the_head()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"));
        var weather = Weather(point);
        var second = new TaskCompletionSource<ApiResult<ExistingWeatherPoint>>();
        var reads = 0;
        weather.Setup(w => w.PointAsync(OsloId, It.IsAny<CancellationToken>()))
            .Returns(() => ++reads == 1
                ? Task.FromResult(ApiResult<ExistingWeatherPoint>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }))
                : second.Task);
        await using var ctx = PageContext(weather, AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load this point’s history windows.", cut.Markup));
        Assert.Equal("Couldn’t load this point’s history windows.", DialogAnnouncement(cut));

        var retry = cut.FindAll("button").Last(b => b.TextContent.Contains("Retry", StringComparison.Ordinal));
        var pending = cut.InvokeAsync(() => retry.Click());

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".wx-hw").GetAttribute("aria-busy")));
        Assert.Empty(cut.FindAll(".wx-hw-list"));
        Assert.Equal("true", cut.FindAll("button").Single(b => b.TextContent.Contains("Save", StringComparison.Ordinal)).GetAttribute("aria-disabled"));

        second.SetResult(Ok(point));
        await pending;

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".wx-hw-list")));
        var head = Editor(cut).HeadId;
        cut.WaitForAssertion(() => Assert.Equal(head, LastFocusById(ctx)));
        Assert.Equal("-1", cut.Find($"#{head}").GetAttribute("tabindex"));
        Assert.Null(cut.FindAll("button").Single(b => b.TextContent.Contains("Save", StringComparison.Ordinal)).GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task Settings_opening_does_not_move_focus_to_the_head()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"));
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var cut = Settings(ctx, point);
        cut.WaitForAssertion(() => Assert.Single(FromIds(cut)));

        Assert.False(Focused(ctx, "odsFocusById", Editor(cut).HeadId));
    }

    [Fact]
    public async Task The_record_section_is_busy_while_loading_and_a_successful_retry_focuses_its_heading()
    {
        var point = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"));
        var weather = Weather(point);
        var first = new TaskCompletionSource<ApiResult<ExistingWeatherPoint>>();
        var second = new TaskCompletionSource<ApiResult<ExistingWeatherPoint>>();
        var reads = 0;
        weather.Setup(w => w.PointAsync(OsloId, It.IsAny<CancellationToken>())).Returns(() => ++reads == 1 ? first.Task : second.Task);
        await using var ctx = PageContext(weather, AdminClaims);
        var section = ctx.Render<WeatherHistoryWindowsSection>(p => p.Add(x => x.Row, point));

        Assert.Equal("true", section.Find(".wx-linked-skel").GetAttribute("aria-busy"));
        first.SetResult(ApiResult<ExistingWeatherPoint>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));
        section.WaitForAssertion(() => Assert.Contains("Couldn’t load this point’s history windows.", section.Find("[role=alert]").TextContent));

        var pending = section.InvokeAsync(() => section.FindAll("button").Single(b => b.TextContent.Contains("Retry", StringComparison.Ordinal)).Click());
        section.WaitForAssertion(() => Assert.Equal("true", section.Find(".wx-linked-skel").GetAttribute("aria-busy")));
        second.SetResult(Ok(point));
        await pending;

        section.WaitForAssertion(() => Assert.Single(section.FindAll(".wx-hws-list li")));
        var host = $"wx-hws-{OsloId:N}";
        Assert.NotNull(section.Find($"#{host}"));
        section.WaitForAssertion(() => Assert.True(Focused(ctx, "odsFocusSectionHeading", host)));
    }

    // ── Show all: the label says what pressing does, and no aria-expanded contradicts it ────────────

    [Fact]
    public async Task More_than_twenty_windows_show_the_newest_twenty_and_a_show_all_button_without_aria_expanded()
    {
        var windows = Enumerable.Range(0, 21)
            .Select(i => Win(new DateOnly(2024, 1, 1).AddDays(i * 10).ToString("yyyy-MM-dd"), new DateOnly(2024, 1, 1).AddDays(i * 10 + 2).ToString("yyyy-MM-dd")))
            .ToArray();
        var point = WithWindows(Point("Cabin", OsloId), windows);
        await using var ctx = PageContext(Weather(point), AdminClaims);
        var section = ctx.Render<WeatherHistoryWindowsSection>(p => p.Add(x => x.Row, point));

        section.WaitForAssertion(() => Assert.Equal(20, section.FindAll(".wx-hws-list li").Count));
        Assert.Contains("21 windows", section.Markup);
        // The oldest is the one held back.
        Assert.DoesNotContain(WeatherHistoryWindows.Range(windows[0].From, windows[0].To), section.Find(".wx-hws-list").TextContent);
        var button = section.FindAll("button").Single(b => b.TextContent.Contains("Show all 21", StringComparison.Ordinal));
        Assert.False(button.HasAttribute("aria-expanded"));

        await section.InvokeAsync(() => button.Click());

        Assert.Equal(21, section.FindAll(".wx-hws-list li").Count);
        var less = section.FindAll("button").Single(b => b.TextContent.Contains("Show the newest 20", StringComparison.Ordinal));
        Assert.False(less.HasAttribute("aria-expanded"));
    }

    [Fact]
    public async Task One_window_reads_singular_and_none_reads_the_empty_line()
    {
        var one = WithWindows(Point("Cabin", OsloId), Win("2026-07-04", "2026-07-18"));
        await using var ctx = PageContext(Weather(one), AdminClaims);
        var section = ctx.Render<WeatherHistoryWindowsSection>(p => p.Add(x => x.Row, one));
        section.WaitForAssertion(() => Assert.Single(section.FindAll(".wx-hws-list li")));
        Assert.Contains("1 window", section.Markup);
        Assert.DoesNotContain("1 windows", section.Markup);

        var none = WithWindows(Point("Bergen", BergenId));
        await using var noneCtx = PageContext(Weather(none), AdminClaims);
        var empty = noneCtx.Render<WeatherHistoryWindowsSection>(p => p.Add(x => x.Row, none));
        empty.WaitForAssertion(() => Assert.Contains("No history windows. Add one in Weather settings.", empty.Markup));
    }
}
