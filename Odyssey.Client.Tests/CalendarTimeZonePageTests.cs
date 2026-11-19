using System.Net;
using System.Reflection;
using Bunit;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;
using static Odyssey.Client.Tests.CalendarTimeZoneDialogTests;

namespace Odyssey.Client.Tests;

/// <summary>
/// The calendar page's half of issue #520: zoned events drawn in the viewer's zone in the grid, the time grid, the
/// agenda and the popover (and named at that time); floating ones unconverted; the window read as instants with the
/// viewer's zone; drag and resize computed in the event's own zone; and the read-only viewer's page. Test names carry
/// the acceptance criterion they prove.
/// </summary>
public class CalendarTimeZonePageTests
{
    static CalendarTimeZonePageTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private sealed class PageHarness(CalendarIcsTestContext context, IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) : IAsyncDisposable
    {
        public CalendarIcsTestContext Context { get; } = context;

        public IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut { get; } = cut;

        public CalendarPage Page => Cut.FindComponent<CalendarPage>().Instance;

        public List<(DateTime From, DateTime To, string? Zone)> Windows { get; } = [];

        public List<NewCalendarEvent> Puts { get; } = [];

        public string PageAnnounced => Cut.FindComponent<CalendarPage>().FindAll("div.sr-only[aria-live=polite]").First().TextContent;

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static async Task<PageHarness> RenderAsync(string viewer, string view, DateOnly anchor, params ExistingCalendarEvent[] events)
    {
        var context = new CalendarIcsTestContext(
            PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete)
        { ViewerZone = viewer };
        var cut = context.Render<CalendarPage>();
        var h = new PageHarness(context, cut);
        context.Calendar.Setup(c => c.ListEventsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((DateTime from, DateTime to, string? zone, CancellationToken _) => h.Windows.Add((from, to, zone)))
            .ReturnsAsync(Ok(events.ToList()));
        context.Calendar.Setup(c => c.UpdateEventWithResultAsync(EventId, It.IsAny<NewCalendarEvent>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, NewCalendarEvent body, CancellationToken _) => h.Puts.Add(body))
            .ReturnsAsync((Guid _, NewCalendarEvent body, CancellationToken _) => Ok(Echo(body) with { IsOverride = true }));
        var page = h.Page;
        Set(page, "_canUpdate", true);
        Set(page, "_view", view);
        Set(page, "_anchor", anchor);
        await cut.InvokeAsync(() => (Task)typeof(CalendarPage).GetMethod("LoadEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!);
        return h;
    }

    // ── AC 3: zoned display ──────────────────────────────────────────────────

    [Fact] // AC 3: a 09:00 Europe/Oslo event is at 03:00 for a New York viewer in winter — grid chip, its name, and the popover's source
    public async Task A_zoned_event_is_drawn_and_named_at_the_viewers_time_in_the_month_grid()
    {
        await using var h = await RenderAsync("America/New_York", "month", new DateOnly(2026, 1, 15),
            Zoned(new DateTime(2026, 1, 15, 9, 0, 0), "Europe/Oslo"));

        var vm = Assert.Single(h.Cut.FindComponent<OdsCalendarGrid>().Instance.Events);
        Assert.Equal(new DateTime(2026, 1, 15, 3, 0, 0), vm.Start);
        Assert.Contains(h.Cut.FindAll("[aria-label]"), e => e.GetAttribute("aria-label")!.StartsWith("Standup, 03:00", StringComparison.Ordinal));
    }

    [Fact] // AC 3: the time grid (week) and its accessible name
    public async Task A_zoned_event_is_drawn_and_named_at_the_viewers_time_in_the_time_grid()
    {
        await using var h = await RenderAsync("America/New_York", "week", new DateOnly(2026, 1, 15),
            Zoned(new DateTime(2026, 1, 15, 9, 0, 0), "Europe/Oslo"));

        Assert.Equal(new DateTime(2026, 1, 15, 3, 0, 0), Assert.Single(h.Cut.FindComponent<OdsCalTimeGrid>().Instance.Events).Start);
        Assert.Contains(h.Cut.FindAll("[aria-label]"), e => e.GetAttribute("aria-label")!.StartsWith("Standup, 03:00 to 04:00", StringComparison.Ordinal));
    }

    [Fact] // AC 3: the agenda row
    public async Task A_zoned_event_is_listed_at_the_viewers_time_in_the_agenda()
    {
        await using var h = await RenderAsync("America/New_York", "agenda", new DateOnly(2026, 1, 15),
            Zoned(new DateTime(2026, 1, 15, 9, 0, 0), "Europe/Oslo"));

        var row = h.Cut.Find(".cal-agenda-row");
        Assert.Equal("03:00", row.QuerySelector(".odc-cal-daypop-time")!.TextContent.Trim());
    }

    [Fact] // AC 2: a floating event keeps its stored clock time for every viewer
    public async Task A_floating_event_is_drawn_at_its_stored_clock_time_for_any_viewer()
    {
        await using var h = await RenderAsync("America/New_York", "month", new DateOnly(2026, 1, 15),
            Floating(new DateTime(2026, 1, 15, 9, 0, 0)));

        Assert.Equal(new DateTime(2026, 1, 15, 9, 0, 0), Assert.Single(h.Cut.FindComponent<OdsCalendarGrid>().Instance.Events).Start);
    }

    // ── AC 22: windows as instants ───────────────────────────────────────────

    [Fact] // AC 22: the window is the viewer's local day boundaries as UTC instants, with the viewer's zone
    public async Task The_window_is_read_as_utc_instants_of_the_viewers_day_boundaries_with_their_zone()
    {
        await using var h = await RenderAsync("Europe/Oslo", "day", new DateOnly(2026, 4, 1));

        var window = Assert.Single(h.Windows);
        Assert.Equal(new DateTime(2026, 3, 31, 22, 0, 0, DateTimeKind.Utc), window.From);
        Assert.Equal(new DateTime(2026, 4, 1, 22, 0, 0, DateTimeKind.Utc), window.To);
        Assert.Equal(DateTimeKind.Utc, window.From.Kind);
        Assert.Equal("Europe/Oslo", window.Zone);
    }

    [Fact] // AC 22: a 23:30 America/New_York event is on the next day for an Oslo viewer, at the edge of the window
    public async Task A_late_new_york_event_lands_on_the_oslo_viewers_next_day()
    {
        await using var h = await RenderAsync("Europe/Oslo", "agenda", new DateOnly(2026, 3, 1),
            Zoned(new DateTime(2026, 3, 31, 23, 30, 0), "America/New_York"));

        var vm = Assert.Single(h.Cut.FindComponent<CalendarPage>().FindAll(".cal-agenda-row"));
        Assert.Contains("Wednesday, April 1", h.Cut.Markup, StringComparison.Ordinal);
        Assert.Equal("05:30", vm.QuerySelector(".odc-cal-daypop-time")!.TextContent.Trim());
    }

    [Fact] // AC 22: without a browser zone the window and the display are both UTC
    public void Without_a_browser_zone_the_window_is_utc()
    {
        var (from, to) = CalendarPage.Window([new DateOnly(2026, 4, 1)], new ViewerTimeZone(null));

        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc), to);
        Assert.Equal("UTC", new ViewerTimeZone(null).ViewZoneId);
    }

    [Fact] // AC 22: the filtered export reads its dates on the same terms
    public async Task The_filtered_export_sends_the_viewers_day_boundaries_as_instants_with_their_zone()
    {
        await using var context = new CalendarIcsTestContext { ViewerZone = "Europe/Oslo" };
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Counts(1)));
        var cut = context.Render<ExportCalendarEventsDialog>(new Dictionary<string, object?>
        {
            [nameof(ExportCalendarEventsDialog.Open)] = true,
            [nameof(ExportCalendarEventsDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
            [nameof(ExportCalendarEventsDialog.InitialFrom)] = new DateTime(2026, 4, 1),
            [nameof(ExportCalendarEventsDialog.InitialTo)] = new DateTime(2026, 4, 2),
        });

        cut.FindAll(".cal-exp-dialog button").Single(b => b.TextContent.Contains("Export", StringComparison.Ordinal) && !b.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => context.Calendar.Verify(c => c.SummarizeAggregateExportAsync(
            new DateTime(2026, 3, 31, 22, 0, 0), new DateTime(2026, 4, 1, 22, 0, 0), null, null, "Europe/Oslo", It.IsAny<CancellationToken>()), Times.Once));
    }

    // ── AC 6: drag and resize ────────────────────────────────────────────────

    [Fact] // AC 6: dragging a zoned occurrence across the spring change keeps its local time and makes it an override
    public async Task Dragging_a_zoned_occurrence_across_dst_keeps_its_local_time_and_announces_the_override()
    {
        var friday = Zoned(new DateTime(2026, 3, 27, 9, 0, 0), "Europe/Oslo", e => e.RecurrencePatternId = PatternId);
        await using var h = await RenderAsync("Europe/Oslo", "month", new DateOnly(2026, 3, 15), friday);

        var grid = h.Cut.FindComponent<OdsCalendarGrid>().Instance;
        await h.Cut.InvokeAsync(() => grid.OnEventDrop.InvokeAsync((EventId, new DateOnly(2026, 3, 30), new DateOnly(2026, 3, 27))));

        var put = Assert.Single(h.Puts);
        Assert.Equal((new DateTime(2026, 3, 30, 9, 0, 0), "Europe/Oslo"), (put.StartDateTime, put.TimeZoneId));
        Assert.Equal(DateTimeKind.Unspecified, put.StartDateTime.Kind);
        h.Cut.WaitForAssertion(() => Assert.Equal(CalendarEventCopy.MovedOverride, h.PageAnnounced));
        Assert.Equal(2, h.Windows.Count); // the grid redraws from the server's rows
        Assert.Equal("Event rescheduled.", Assert.Single(h.Context.Toasts).Message);
    }

    [Fact] // AC 6: a time-grid drop in the viewer's zone becomes the wall-clock in the event's own zone
    public async Task A_time_grid_drop_is_converted_into_the_events_own_zone()
    {
        await using var h = await RenderAsync("America/New_York", "week", new DateOnly(2026, 1, 15),
            Zoned(new DateTime(2026, 1, 15, 9, 0, 0), "Europe/Oslo"));

        var grid = h.Cut.FindComponent<OdsCalTimeGrid>().Instance;
        // Dropped at 04:00–05:00 New York time, which is 10:00–11:00 in Oslo.
        await h.Cut.InvokeAsync(() => grid.OnEventChange.InvokeAsync((EventId, new DateTime(2026, 1, 15, 4, 0, 0), new DateTime(2026, 1, 15, 5, 0, 0))));

        var put = Assert.Single(h.Puts);
        Assert.Equal((new DateTime(2026, 1, 15, 10, 0, 0), new DateTime(2026, 1, 15, 11, 0, 0), "Europe/Oslo"),
            (put.StartDateTime, put.EndDateTime, put.TimeZoneId));
        // A standalone event is not an occurrence: nothing to announce.
        Assert.NotEqual(CalendarEventCopy.MovedOverride, h.PageAnnounced);
    }

    [Fact] // AC 6: a floating event moves by the dragged amount, as before, and keeps no zone
    public void A_floating_drag_moves_its_clock_time_and_sends_no_zone()
    {
        var update = CalendarPage.DropUpdate(Floating(new DateTime(2026, 3, 27, 9, 0, 0)), new DateOnly(2026, 3, 30), new DateOnly(2026, 3, 27));

        Assert.Equal(new DateTime(2026, 3, 30, 9, 0, 0), update.StartDateTime);
        Assert.Null(update.TimeZoneId);
        Assert.Equal(DateTimeKind.Utc, update.StartDateTime.Kind);
    }

    [Fact] // AC 6 + v3 §F: the server moved a dragged time out of a gap → the toast says what was stored
    public void A_move_the_server_adjusted_is_reported_from_the_servers_value()
    {
        var sent = CalendarPage.DropUpdate(Zoned(new DateTime(2026, 3, 22, 2, 30, 0), "Europe/Oslo"), new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 22));
        var saved = Echo(sent) with { StartDateTime = new DateTime(2026, 3, 29, 3, 30, 0) };

        Assert.Equal("Saved as 03:30. 02:30 doesn't exist on 29 Mar in Europe/Oslo.", CalendarPage.AdjustedAfterMove(sent, saved));
        Assert.Null(CalendarPage.AdjustedAfterMove(sent, Echo(sent)));
    }

    // ── AC 18: the read-only viewer's page ───────────────────────────────────

    [Fact] // AC 18: a calendar.read-only viewer gets no import
    public async Task A_read_only_viewer_has_no_import()
    {
        await using var context = new CalendarIcsTestContext(PermissionClaims.CalendarRead);
        var cut = context.Render<CalendarPage>();
        var page = cut.FindComponent<CalendarPage>().Instance;
        await cut.InvokeAsync(() => (Task)typeof(CalendarPage).GetMethod("LoadPermissionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!);

        Assert.False((bool)typeof(CalendarPage).GetProperty("_canImport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
    }
}
