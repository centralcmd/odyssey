using System.Net;
using System.Reflection;
using Bunit;
using Moq;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The calendar page's half of issue #522: the attachment indicator on every event surface (§3.3) and the export flow's
/// attachments choice (§3.4, v3 §A, v4). Each test names the acceptance criterion it proves.
/// </summary>
public class CalendarAttachmentPageTests
{
    static CalendarAttachmentPageTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private const string StartedTail =
        "Your browser has started the download — if it does not finish, your browser's downloads list shows why.";

    private static readonly Guid EventId = CalendarAttachmentDialogTests.EventId;
    private static readonly Guid OtherId = Guid.Parse("522e0000-0000-0000-0000-000000000009");

    private static ExistingCalendarEvent Listed(Guid id, string title, int files, int photos, bool recurring = false) => new()
    {
        CalendarEventId = id, CalendarId = CalendarId, Title = title,
        StartDateTime = new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Unspecified),
        EndDateTime = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Unspecified),
        TimeZoneId = "UTC", IsKeyed = true, AttachmentCount = files, PhotoCount = photos,
        RecurrencePatternId = recurring ? Guid.NewGuid() : null,
    };

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static async Task<(CalendarIcsTestContext Context, IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut, CalendarPage Page)> RenderAsync(
        string view, params ExistingCalendarEvent[] events)
    {
        var context = new CalendarIcsTestContext(
            PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete,
            PermissionClaims.FilesRead, PermissionClaims.PhotosRead);
        context.Calendar.Setup(c => c.ListEventsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(events.ToList()));
        var cut = context.Render<CalendarPage>();
        var page = cut.FindComponent<CalendarPage>().Instance;
        Set(page, "_view", view);
        Set(page, "_anchor", new DateOnly(2026, 6, 4));
        await cut.InvokeAsync(() => (Task)typeof(CalendarPage).GetMethod("LoadEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!);
        return (context, cut, page);
    }

    // ── AC 7: indicators ─────────────────────────────────────────────────────

    [Fact] // AC 7: a month chip with attachments shows the glyph after the recurrence glyph, and its name ends in the suffix
    public async Task A_month_chip_with_attachments_shows_the_glyph_and_the_name_suffix()
    {
        var (context, cut, _) = await RenderAsync("month", Listed(EventId, "Dentist", 1, 0, recurring: true), Listed(OtherId, "Gym", 0, 0));
        await using var _ = context;

        var chips = cut.FindAll("button.odc-cal-chip");
        var with = chips.Single(c => c.GetAttribute("title") == "Dentist");
        var without = chips.Single(c => c.GetAttribute("title") == "Gym");
        Assert.EndsWith(", recurring, has attachments", with.GetAttribute("aria-label"), StringComparison.Ordinal);
        var glyphs = with.QuerySelectorAll(".material-icons").Select(g => g.TextContent).ToList();
        Assert.Equal(["autorenew", "attach_file"], glyphs);
        Assert.Equal("true", with.QuerySelector(".odc-cal-chip-att")!.GetAttribute("aria-hidden"));
        Assert.DoesNotContain("has attachments", without.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Null(without.QuerySelector(".odc-cal-chip-att"));
    }

    [Fact] // AC 7: a photo alone counts; the week grid's block and its name carry the indicator
    public async Task A_week_block_with_a_photo_shows_the_indicator()
    {
        var (context, cut, _) = await RenderAsync("week", Listed(EventId, "Hike", 0, 2), Listed(OtherId, "Gym", 0, 0));
        await using var _ = context;

        var block = cut.FindAll("button.cal-tg-ev").Single(b => b.GetAttribute("title") == "Hike");
        Assert.EndsWith(", has attachments", block.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.NotNull(block.QuerySelector(".odc-cal-chip-att"));
        var plain = cut.FindAll("button.cal-tg-ev").Single(b => b.GetAttribute("title") == "Gym");
        Assert.Null(plain.QuerySelector(".odc-cal-chip-att"));
    }

    [Fact] // AC 7: an agenda row carries the glyph and the suffix as visually hidden text inside the button
    public async Task An_agenda_row_with_attachments_names_them()
    {
        var (context, cut, _) = await RenderAsync("agenda", Listed(EventId, "Dentist", 2, 1), Listed(OtherId, "Gym", 0, 0));
        await using var _ = context;

        var rows = cut.FindAll(".cal-agenda-row");
        var with = rows.Single(r => r.TextContent.Contains("Dentist", StringComparison.Ordinal));
        Assert.NotNull(with.QuerySelector(".odc-cal-chip-att"));
        Assert.Equal(OdsCalendarNames.AttachmentsSuffix, with.QuerySelector(".sr-only:last-child")!.TextContent);
        var without = rows.Single(r => r.TextContent.Contains("Gym", StringComparison.Ordinal));
        Assert.Null(without.QuerySelector(".odc-cal-chip-att"));
        Assert.DoesNotContain("has attachments", without.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC 7: the day popover's row says it too (its name is its text)
    public void A_popover_row_with_attachments_names_them()
    {
        using var ctx = PageTestContext.Create();
        var day = new DateOnly(2026, 6, 4);
        CalendarEventVm Vm(string title, bool att, int hour) =>
            new(Guid.NewGuid(), CalendarId, "Personal", title, day.ToDateTime(new TimeOnly(hour, 0)), day.ToDateTime(new TimeOnly(hour, 30)),
                false, "#0369A1", "#fff", false, HasAttachments: att);
        var events = new List<CalendarEventVm> { Vm("A", true, 8), Vm("B", false, 9), Vm("C", false, 10), Vm("D", false, 11), Vm("E", false, 12) };
        var cut = ctx.Render<OdsCalendarGrid>(p => p
            .Add(g => g.Events, events)
            .Add(g => g.Month, day)
            .Add(g => g.Today, day));

        cut.Find("button.odc-cal-more").Click();

        var rows = cut.FindAll(".odc-cal-daypop-row");
        var a = rows.Single(r => r.TextContent.Contains('A'));
        Assert.EndsWith(OdsCalendarNames.AttachmentsSuffix, a.QuerySelector(".odc-cal-daypop-t")!.TextContent.Trim(), StringComparison.Ordinal);
        Assert.NotNull(a.QuerySelector(".odc-cal-chip-att"));
        Assert.All(rows.Where(r => r != a), r => Assert.DoesNotContain("has attachments", r.TextContent, StringComparison.Ordinal));
    }

    // ── AC 8: export ─────────────────────────────────────────────────────────

    private static CalendarIcsExportSummary WithAttachments(int occurrences, int docs, int photos, long bytes) =>
        Counts(occurrences) with { AttachmentCount = docs, PhotoCount = photos, AttachmentBytes = bytes };

    [Fact] // AC 8: a summary with attachments asks first — the box ticked, the advisory beside it — and the toast carries the part
    public async Task A_calendar_with_attachments_asks_then_exports_them_and_toasts_the_attachment_part()
    {
        var (context, cut, page) = await RenderAsync("month");
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeExportAsync(CalendarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(WithAttachments(3, docs: 2, photos: 1, bytes: 3 * 1024 * 1024)));

        await cut.InvokeAsync(() => page.ExportCalendarAsync(Personal));

        Assert.Empty(context.Transfer.Downloads);
        var box = cut.WaitForElement("#" + ExportAttachmentsDialog.CheckboxId);
        Assert.True(box.HasAttribute("checked"));
        Assert.Null(box.GetAttribute("aria-disabled"));
        Assert.DoesNotContain("is-disabled", cut.Find(".cal-exp-att-check").ClassName, StringComparison.Ordinal);
        Assert.Equal(ExportAttachmentsDialog.AdvisoryId, box.GetAttribute("aria-describedby"));
        Assert.Equal("Include attachments (2 documents, 1 photo, 3 MB)", cut.Find(".cal-exp-att-check .odc-check-label").TextContent);
        Assert.Contains("Attached photos are exported as originals, including any location data in them.",
            cut.Find("#" + ExportAttachmentsDialog.AdvisoryId).TextContent, StringComparison.Ordinal);

        cut.Find("#" + ExportAttachmentsDialog.ExportButtonId).Click();

        cut.WaitForAssertion(() => Assert.Equal([$"http://api.test/api/calendars/{CalendarId}/ics"], context.Transfer.Downloads));
        var toast = Assert.Single(context.Toasts);
        Assert.Equal($"Exporting “Personal”: 3 events with 2 documents and 1 photo (3 MB of attachments). {StartedTail}", toast.Message);
        Assert.True(toast.RequireInteraction); // over 120 characters (AX7)
    }

    [Fact] // AC 8: unticking exports with attachments=omit, the action and toast say so
    public async Task Unticking_exports_without_attachments()
    {
        var (context, cut, page) = await RenderAsync("month");
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeExportAsync(CalendarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(WithAttachments(3, docs: 1, photos: 0, bytes: 2048)));
        context.Calendar.Setup(c => c.ExportUrl(CalendarId, IcsAttachmentsMode.Omit))
            .Returns($"http://api.test/api/calendars/{CalendarId}/ics?attachments=omit");

        await cut.InvokeAsync(() => page.ExportCalendarAsync(Personal));
        cut.WaitForElement("#" + ExportAttachmentsDialog.CheckboxId).Change(false);
        Assert.Equal("Export without attachments", ImportCalendarDialogTests.Harness.Label(cut.Find("#" + ExportAttachmentsDialog.ExportButtonId)));
        cut.Find("#" + ExportAttachmentsDialog.ExportButtonId).Click();

        cut.WaitForAssertion(() => Assert.Equal([$"http://api.test/api/calendars/{CalendarId}/ics?attachments=omit"], context.Transfer.Downloads));
        Assert.Equal($"Exporting “Personal”: 3 events. {StartedTail} Attachments were not included.", Assert.Single(context.Toasts).Message);
    }

    [Fact] // AC 8: a summary 403 for attachments — the refused form, announced once, and an omit export
    public async Task A_forbidden_summary_offers_export_without_attachments()
    {
        var (context, cut, page) = await RenderAsync("month");
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeEventExportAsync(EventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<CalendarIcsExportSummary>(HttpStatusCode.Forbidden, "Refused.", CalendarIcsExport.AttachmentsForbiddenCode));
        context.Calendar.Setup(c => c.SummarizeEventExportAsync(EventId, IcsAttachmentsMode.Omit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Counts(1)));
        context.Calendar.Setup(c => c.ExportEventUrl(EventId, IcsAttachmentsMode.Omit))
            .Returns($"http://api.test/api/calendar-events/{EventId}/ics?attachments=omit");

        await cut.InvokeAsync(() => page.OpenEventDialog(EventId));
        await cut.InvokeAsync(() => page.ExportEventDialogAsync("Dentist"));

        Assert.Empty(context.Toasts);
        var box = cut.WaitForElement("#" + ExportAttachmentsDialog.CheckboxId);
        Assert.False(box.HasAttribute("checked"));
        Assert.Equal("true", box.GetAttribute("aria-disabled"));
        Assert.False(box.HasAttribute("disabled"));
        Assert.Contains("is-disabled", cut.Find(".cal-exp-att-check").ClassName, StringComparison.Ordinal);
        Assert.Equal("You don't have permission to export this event's attachments.", cut.Find("#" + box.GetAttribute("aria-describedby")).TextContent);
        var action = cut.Find("#" + ExportAttachmentsDialog.ExportButtonId);
        Assert.Equal("Export without attachments", ImportCalendarDialogTests.Harness.Label(action));
        Assert.Equal(ExportAttachmentsDialog.WarningId, action.GetAttribute("aria-describedby"));
        Assert.Contains("This event has attachments you don't have permission to export. You can export it without them.",
            cut.Find("#" + ExportAttachmentsDialog.WarningId).TextContent, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".cal-exp-att div.sr-only[aria-live=polite]"),
            r => r.TextContent == "This event has attachments you can't export. You can export it without them."));

        box.Change(true); // swallowed
        Assert.False(cut.Find("#" + ExportAttachmentsDialog.CheckboxId).HasAttribute("checked"));
        cut.Find("#" + ExportAttachmentsDialog.ExportButtonId).Click();

        cut.WaitForAssertion(() => Assert.Equal([$"http://api.test/api/calendar-events/{EventId}/ics?attachments=omit"], context.Transfer.Downloads));
        Assert.Equal($"Exporting “Dentist”. {StartedTail} Attachments were not included.", Assert.Single(context.Toasts).Message);
    }

    [Fact] // AC 8 (v4): a summary failure other than 403 renders no checkbox and keeps Spec A's refusal toast
    public async Task A_non_403_summary_failure_renders_no_checkbox()
    {
        var (context, cut, page) = await RenderAsync("month");
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeExportAsync(CalendarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<CalendarIcsExportSummary>(HttpStatusCode.InternalServerError, "Boom."));

        await cut.InvokeAsync(() => page.ExportCalendarAsync(Personal));

        Assert.Empty(cut.FindAll("#" + ExportAttachmentsDialog.CheckboxId));
        Assert.True(Assert.Single(context.Toasts).RequireInteraction);
    }

    [Fact] // AC 8: nothing attached — no step, and the toast is Spec A's
    public async Task A_series_with_nothing_attached_exports_at_once_with_the_plain_toast()
    {
        var (context, cut, page) = await RenderAsync("month");
        await using var _ = context;
        var patternId = Guid.NewGuid();
        context.Calendar.Setup(c => c.SummarizePatternExportAsync(patternId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Counts(4)));
        Set(page, "_eventDialogMode", CalendarEventDialogMode.Series);
        Set(page, "_eventDialogPatternId", (Guid?)patternId);

        await cut.InvokeAsync(() => page.ExportEventDialogAsync("Choir"));

        Assert.Empty(cut.FindAll("#" + ExportAttachmentsDialog.CheckboxId));
        var toast = Assert.Single(context.Toasts);
        Assert.Equal($"Exporting the series “Choir” (4 events). {StartedTail}", toast.Message);
        Assert.Equal(10_000, toast.Duration);
    }

    [Fact] // AC 8: the aggregate export's toast is unchanged
    public async Task The_aggregate_toast_is_unchanged()
    {
        var (context, cut, page) = await RenderAsync("month");
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(null, null, null, null, "UTC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(WithAttachments(12, docs: 4, photos: 4, bytes: 9_000_000) with { SeriesCount = 2 }));

        await cut.InvokeAsync(page.ExportAllEventsAsync);

        Assert.Empty(cut.FindAll("#" + ExportAttachmentsDialog.CheckboxId));
        var toast = Assert.Single(context.Toasts);
        Assert.Equal($"Exporting 12 events, including 2 recurring series. {StartedTail}", toast.Message);
        Assert.Equal(10_000, toast.Duration);
    }

    [Fact] // AC 8: the attachment part's real plurals, each kind only when non-zero
    public void The_attachment_part_uses_real_plurals()
    {
        Assert.Equal(" with 1 document (2 KB of attachments)", CalendarIcsExport.AttachmentPart(WithAttachments(1, 1, 0, 2048)));
        Assert.Equal(" with 3 photos (1.5 MB of attachments)", CalendarIcsExport.AttachmentPart(WithAttachments(1, 0, 3, 1536 * 1024)));
        Assert.Equal(" with 2 documents and 1 photo (1 KB of attachments)", CalendarIcsExport.AttachmentPart(WithAttachments(1, 2, 1, 1024)));
        Assert.Equal(string.Empty, CalendarIcsExport.AttachmentPart(Counts(1)));
    }

    [Fact] // §3.4: the commit step opens on Cancel, never on the checkbox (v4 "Focus")
    public void The_commit_step_opens_on_cancel()
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages/Calendar/ExportAttachmentsDialog.razor"));
        Assert.Contains("InitialFocus=\"OdsModalInitialFocus.Cancel\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-dialog-cancel=\"true\"", markup, StringComparison.Ordinal);
    }
}
