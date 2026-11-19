using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;
using static Odyssey.Client.Tests.CalendarTimeZoneDialogTests;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #520's unit-level criteria: the one zone helper across daylight-saving changes (AC 15), the zone picker's
/// combobox behaviour (AC 14), and the single announcement channel for the dialog's toasts (AC 23).
/// </summary>
public class CalendarZoneClockTests
{
    static CalendarZoneClockTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // ── AC 15: the zone helper across DST ────────────────────────────────────

    [Theory] // AC 15: an ordinary time, either side of the spring change
    [InlineData(2026, 3, 28, 9, 8)] // CET, UTC+1
    [InlineData(2026, 3, 30, 9, 7)] // CEST, UTC+2
    public void An_ordinary_wall_clock_converts_at_the_offset_of_its_own_date(int y, int m, int d, int hour, int utcHour)
    {
        var utc = CalendarZoneClock.ToUtc(new DateTime(y, m, d, hour, 0, 0), Oslo, out var adjustment);

        Assert.Equal(new DateTime(y, m, d, utcHour, 0, 0, DateTimeKind.Utc), utc);
        Assert.Equal(ZoneAdjustment.None, adjustment);
        Assert.Equal(new DateTime(y, m, d, hour, 0, 0), CalendarZoneClock.ToWall(utc, Oslo));
    }

    [Fact] // AC 15: a spring gap moves forward by the gap, as the server does
    public void A_spring_gap_wall_clock_moves_forward_by_the_gap()
    {
        var utc = CalendarZoneClock.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0), Oslo, out var adjustment);

        Assert.Equal(ZoneAdjustment.Gap, adjustment);
        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal(new DateTime(2026, 3, 29, 3, 30, 0), CalendarZoneClock.Saved(new DateTime(2026, 3, 29, 2, 30, 0), Oslo));
    }

    [Fact] // AC 15: an autumn duplicate takes the earlier instant
    public void An_autumn_duplicate_takes_the_earlier_instant()
    {
        var utc = CalendarZoneClock.ToUtc(new DateTime(2026, 10, 25, 2, 30, 0), Oslo, out var adjustment);

        Assert.Equal(ZoneAdjustment.Ambiguous, adjustment);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), utc);
    }

    [Fact] // AC 15: a viewer in another zone, across their own DST change (New York changes earlier than Oslo)
    public void A_wall_clock_converts_between_zones_across_their_differing_dst_dates()
    {
        // 9 March 2026: New York is already on EDT (UTC−4), Oslo still on CET (UTC+1) — five hours apart, not six.
        Assert.Equal(new DateTime(2026, 3, 9, 4, 0, 0), CalendarZoneClock.Convert(new DateTime(2026, 3, 9, 9, 0, 0), Oslo, NewYork));
        Assert.Equal(new DateTime(2026, 1, 15, 3, 0, 0), CalendarZoneClock.Convert(new DateTime(2026, 1, 15, 9, 0, 0), Oslo, NewYork));
    }

    [Fact] // AC 15: a floating event is displayed unconverted; a zoned one at its instant in the viewer's zone
    public void Display_converts_zoned_events_only()
    {
        Assert.Equal(new DateTime(2026, 1, 15, 9, 0, 0), CalendarZoneClock.Displayed(Floating(new DateTime(2026, 1, 15, 9, 0, 0)), NewYork).Start);
        Assert.Equal(new DateTime(2026, 1, 15, 3, 0, 0), CalendarZoneClock.Displayed(Zoned(new DateTime(2026, 1, 15, 9, 0, 0), "Europe/Oslo"), NewYork).Start);
    }

    [Fact] // AX3: an option's offset is the one on the event's date, not today's
    public void The_offset_note_is_taken_on_the_events_date()
    {
        Assert.Equal("UTC+01:00", CalendarZoneClock.OffsetOn("Europe/Oslo", new DateTime(2026, 1, 15)));
        Assert.Equal("UTC+02:00", CalendarZoneClock.OffsetOn("Europe/Oslo", new DateTime(2026, 7, 15)));
    }

    // ── AC 14: the combobox ──────────────────────────────────────────────────

    private static (BunitContext Ctx, IRenderedComponent<OdsCombobox> Cut) Picker()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Render<MudPopoverProvider>();
        var options = LocationVisuals.TimeZoneIds
            .Select(id => new OdsOption(id, id) { Note = CalendarZoneClock.OffsetOn(id, new DateTime(2026, 1, 15)), SearchKey = CalendarZoneClock.SearchKey(id) })
            .ToList();
        var cut = ctx.Render<OdsCombobox>(p => p
            .Add(c => c.InputId, "tz")
            .Add(c => c.Options, options)
            .Add(c => c.Value, "Europe/Oslo")
            .Add(c => c.Clearable, false)
            .Add(c => c.AllRows, true)
            .Add(c => c.HomeEndKeys, true)
            .Add(c => c.EmptyTextFor, q => $"No time zone matches “{q}”.")
            .Add(c => c.ResultsText, n => $"{n} {(n == 1 ? "time zone matches" : "time zones match")}"));
        return (ctx, cut);
    }

    private static Task<IEnumerable<OdsOption>> Search(IRenderedComponent<OdsCombobox> cut, string text) =>
        cut.InvokeAsync(() => (Task<IEnumerable<OdsOption>>)typeof(OdsCombobox)
            .GetMethod("Search", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [text, CancellationToken.None])!);

    [Fact] // AC 14: "new york" lists America/New_York by its city, and the match count is announced
    public async Task Typing_a_city_lists_its_zone_and_the_count_is_announced()
    {
        var (ctx, cut) = Picker();
        await using var _ = ctx;

        var rows = (await Search(cut, "new york")).ToList();

        Assert.Equal(["America/New_York"], rows.Select(r => r.Value));
        cut.WaitForAssertion(() => Assert.Equal("1 time zone matches", cut.Find(".odc-combo-count").TextContent));
        Assert.Equal("polite", cut.Find(".odc-combo-count").GetAttribute("aria-live"));
    }

    [Fact] // AC 14: nonsense finds nothing; the popover's own status row speaks the empty state and the count stays quiet
    public async Task Typing_nonsense_finds_nothing_and_leaves_the_count_empty()
    {
        var (ctx, cut) = Picker();
        await using var _ = ctx;

        Assert.Empty(await Search(cut, "xyzzy"));
        cut.WaitForAssertion(() => Assert.Equal(string.Empty, cut.Find(".odc-combo-count").TextContent));
        Assert.Equal("No time zone matches “xyzzy”.", cut.Instance.EmptyTextFor!("xyzzy"));
    }

    [Fact] // AC 14: Home and End reach the first and last row of the whole, uncapped list
    public async Task Home_and_end_target_the_first_and_last_rows_of_the_whole_list()
    {
        var (ctx, cut) = Picker();
        await using var _ = ctx;

        var rows = (await Search(cut, "europe")).ToList();

        Assert.True(rows.Count > 10, "the zone list is not capped at MudAutocomplete's ten");
        Assert.Equal(0, OdsCombobox.HomeEndTarget("Home", rows.Count, null));
        Assert.Equal(rows.Count - 1, OdsCombobox.HomeEndTarget("End", rows.Count, null));
        Assert.Null(OdsCombobox.HomeEndTarget("ArrowDown", rows.Count, null));
        // The highlight is moved through MudAutocomplete's own method; a MudBlazor that renames it fails here.
        Assert.NotNull(typeof(MudAutocomplete<OdsOption>).GetMethod("SelectItemAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, [typeof(int)]));
    }

    [Fact] // AC 14: a non-clearable field never passes up the null MudAutocomplete coerces typed text into
    public async Task Typing_into_a_non_clearable_picker_never_empties_its_value()
    {
        var changes = new List<string?>();
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = ctx.Render<OdsCombobox>(p => p
            .Add(c => c.Options, [new OdsOption("Europe/Oslo", "Europe/Oslo")])
            .Add(c => c.Value, "Europe/Oslo")
            .Add(c => c.Clearable, false)
            .Add(c => c.ValueChanged, v => changes.Add(v)));

        await cut.InvokeAsync(() => (Task)typeof(OdsCombobox)
            .GetMethod("OnSelected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [null])!);

        Assert.Empty(changes);
        Assert.Equal("Europe/Oslo", cut.Instance.Value);
    }

    [Fact] // AC 14 (v3 §E): text naming no option is reported on blur and kept for Save; an exact label picks the option
    public async Task Typed_text_naming_no_option_is_reported_on_blur_and_an_exact_label_is_picked()
    {
        var unmatched = new List<string>();
        var picked = new List<string?>();
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = ctx.Render<OdsCombobox>(p => p
            .Add(c => c.Options, [new OdsOption("Europe/Oslo", "Europe/Oslo"), new OdsOption("UTC", "UTC")])
            .Add(c => c.Value, "Europe/Oslo")
            .Add(c => c.Clearable, false)
            .Add(c => c.ValueChanged, v => picked.Add(v))
            .Add(c => c.OnUnmatchedText, t => unmatched.Add(t)));
        var wrap = cut.Find(".odc-combo-wrap");

        wrap.Input("Atlantis");
        Assert.Equal("Atlantis", cut.Instance.UnmatchedText);
        await wrap.TriggerEventAsync("onfocusout", new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
        Assert.Equal(["Atlantis"], unmatched);

        wrap.Input("utc");
        await wrap.TriggerEventAsync("onfocusout", new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
        Assert.Equal(["UTC"], picked);
        Assert.Null(cut.Instance.UnmatchedText);
    }

    // ── AC 23: one announcement per toast ────────────────────────────────────

    [Fact] // AC 23: the drop toast is in exactly one live region, persistent, and the dialog's announcer does not repeat it
    public async Task The_drop_toast_is_announced_once_by_the_snackbar_and_stays_until_dismissed()
    {
        await using var ctx = PageTestContext.Create();
        ctx.Services.AddMudServices(config => SnackbarDefaults.Configure(config.SnackbarConfiguration));
        var calendar = new Mock<Odyssey.ApiClient.Resources.ICalendarApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        calendar.Setup(c => c.GetPatternAsync(PatternId, It.IsAny<CancellationToken>())).ReturnsAsync(Series(new DateTime(2026, 6, 1, 18, 0, 0), "Europe/Oslo"));
        calendar.Setup(c => c.PreviewPatternAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new RecurrencePatternPreview { Version = LoadedVersion }));
        calendar.Setup(c => c.UpdatePatternWithResultAsync(PatternId, It.IsAny<NewRecurrencePattern>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Updated(overrides: 1));
        ctx.Services.AddSingleton(calendar.Object);
        ctx.Services.AddSingleton(new ViewerTimeZone("Europe/Oslo"));

        var cut = ctx.Render(b =>
        {
            b.OpenComponent<MudSnackbarProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudDialogProvider>(1);
            b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(2);
            b.CloseComponent();
            b.OpenComponent<CalendarEventDialog>(3);
            b.AddComponentParameter(4, nameof(CalendarEventDialog.Open), true);
            b.AddComponentParameter(5, nameof(CalendarEventDialog.Mode), CalendarEventDialogMode.Series);
            b.AddComponentParameter(6, nameof(CalendarEventDialog.PatternId), (Guid?)PatternId);
            b.AddComponentParameter(7, nameof(CalendarEventDialog.Calendars), (IReadOnlyList<ExistingCalendar>)[Personal]);
            b.AddComponentParameter(8, nameof(CalendarEventDialog.CanUpdate), true);
            b.CloseComponent();
        });
        cut.WaitForElement("#" + CalendarEventDialog.SaveButtonId).Click();

        var text = CalendarEventCopy.DropToast(1, 0);
        cut.WaitForAssertion(() => Assert.Contains(text, cut.Markup, StringComparison.Ordinal));
        var live = cut.FindAll("[aria-live], [role=status], [role=alert]")
            .Where(e => e.TextContent.Contains(text, StringComparison.Ordinal))
            .Where(e => !e.QuerySelectorAll("[aria-live], [role=status], [role=alert]").Any(inner => inner.TextContent.Contains(text, StringComparison.Ordinal)))
            .ToList();
        Assert.Single(live);
        Assert.Null(live[0].Closest(".ods-modal-body"));
        var shown = Assert.Single(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
        Assert.Equal(Severity.Warning, shown.Severity);
        // Persistence (RequireInteraction) is pinned against the recorded options in CalendarTimeZoneDialogTests (AC 10).
    }
}
