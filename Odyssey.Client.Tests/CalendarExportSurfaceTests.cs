using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Dtos.Journal;
using Odyssey.Testing;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The export surfaces' own halves of issue #517 — the filtered-export dialog (E2), Manage calendars (E3), the
/// occurrence-or-series prompt (E4), the event dialog's Export (E5) and <see cref="OdsIconButton"/>'s busy state —
/// plus the source-lint that keeps byte-returning exports out of the calendar pages (AC 1).
/// </summary>
public class CalendarExportSurfaceTests
{
    static CalendarExportSurfaceTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    // ── AC 1 ────────────────────────────────────────────────────────────────

    private static readonly Regex ByteReturningExport = new(
        @"downloadFileFromBytes|\.Export(Aggregate|Event|Pattern)?Async\(", RegexOptions.Compiled);

    [Fact] // AC 1
    public void No_calendar_page_downloads_bytes_or_calls_a_byte_returning_export()
    {
        var folder = Path.Combine(ClientSource.Root, "Pages", "Calendar");
        var offenders = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return ByteReturningExport.Matches(text).Select(m => $"{ClientSource.Relative(path)}:{SourceLint.LineAt(text, m.Index)} — {m.Value}");
            })
            .ToList();

        Assert.True(offenders.Count == 0,
            "A calendar page still exports through the WASM heap (issue #517 AC 1):\n" + string.Join('\n', offenders));
    }

    [Fact] // AC 1: the lint's pattern matches what it exists to catch
    public void The_lint_pattern_catches_each_byte_returning_call()
    {
        foreach (var call in new[]
                 {
                     "JS.InvokeVoidAsync(\"downloadFileFromBytes\"", "CalendarApi.ExportAsync(id)", "CalendarApi.ExportEventAsync(id)",
                     "CalendarApi.ExportPatternAsync(id)", "CalendarApi.ExportAggregateAsync(null, null, null, null)",
                 })
        {
            Assert.Matches(ByteReturningExport, call);
        }

        Assert.DoesNotMatch(ByteReturningExport, "CalendarApi.SummarizeExportAsync(id)");
        Assert.DoesNotMatch(ByteReturningExport, "CalendarApi.ExportUrl(id)");
    }

    // ── AC 19: OdsIconButton.Busy ───────────────────────────────────────────

    [Fact] // AC 19
    public async Task A_busy_icon_button_shows_a_spinner_is_aria_disabled_ignores_clicks_and_keeps_its_name()
    {
        await using var context = new CalendarIcsTestContext();
        var clicks = 0;
        var cut = context.Ctx.Render<OdsIconButton>(p => p
            .Add(b => b.Icon, Icons.Material.Filled.Download)
            .Add(b => b.AriaLabel, "Export Personal")
            .Add(b => b.Busy, true)
            .Add(b => b.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, () => clicks++)));

        var button = cut.Find("button");
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.Equal("true", button.GetAttribute("aria-busy"));
        Assert.Equal("Export Personal", button.GetAttribute("aria-label"));
        Assert.False(button.HasAttribute("disabled"));
        Assert.NotNull(button.QuerySelector(".odc-iconbtn-spinner[aria-hidden=true]"));
        Assert.Null(button.QuerySelector("svg"));

        button.Click();
        Assert.Equal(0, clicks);
    }

    [Fact] // AC 19: one element in both states, so focus stays on it
    public async Task Turning_busy_and_back_keeps_the_same_button()
    {
        await using var context = new CalendarIcsTestContext();
        var clicks = 0;
        var cut = context.Ctx.Render<OdsIconButton>(p => p
            .Add(b => b.Icon, Icons.Material.Filled.Download)
            .Add(b => b.AriaLabel, "Export Personal")
            .Add(b => b.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, () => clicks++)));
        var before = cut.Find("button");
        Assert.Null(before.GetAttribute("aria-disabled"));
        Assert.NotNull(before.QuerySelector("svg"));

        cut.Render(p => p.Add(b => b.Busy, true));
        cut.Render(p => p.Add(b => b.Busy, false));

        var after = cut.Find("button");
        Assert.Null(after.GetAttribute("aria-busy"));
        after.Click();
        Assert.Equal(1, clicks);
        Assert.Single(cut.FindAll("button"));
    }

    // ── E2: the filtered-export dialog ──────────────────────────────────────

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> FilteredDialog(CalendarIcsTestContext context, List<bool> openChanges) =>
        context.Render<ExportCalendarEventsDialog>(new Dictionary<string, object?>
        {
            [nameof(ExportCalendarEventsDialog.Open)] = true,
            [nameof(ExportCalendarEventsDialog.OpenChanged)] = EventCallback.Factory.Create<bool>(new object(), v => openChanges.Add(v)),
            [nameof(ExportCalendarEventsDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
            [nameof(ExportCalendarEventsDialog.InitialFrom)] = new DateTime(2026, 11, 1),
            [nameof(ExportCalendarEventsDialog.InitialTo)] = new DateTime(2026, 12, 1),
        });

    private static IElement Button(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string scope, string label) =>
        cut.FindAll($"{scope} button").Single(b => b.TextContent.Replace("download", string.Empty).Trim() == label);

    [Fact] // AC 2 (E2): a refusal shows inline, keeps the dialog open and starts no download
    public async Task A_refused_filtered_export_shows_inline_and_keeps_the_dialog_open()
    {
        await using var context = new CalendarIcsTestContext();
        var openChanges = new List<bool>();
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<CalendarIcsExportSummary>(HttpStatusCode.BadRequest, "The export has too many events."));
        var cut = FilteredDialog(context, openChanges);

        Button(cut, ".cal-exp-dialog", "Export").Click();

        cut.WaitForAssertion(() => Assert.Contains("The export has too many events.", cut.Find(".cal-exp-form .mud-alert").TextContent, StringComparison.Ordinal));
        Assert.Empty(context.Transfer.Downloads);
        Assert.Empty(context.Toasts);
        Assert.Empty(openChanges);
    }

    [Fact] // AC 2 + AC 3 (E2): success closes the dialog, downloads the same query natively and toasts
    public async Task A_filtered_export_closes_downloads_and_toasts()
    {
        await using var context = new CalendarIcsTestContext();
        var openChanges = new List<bool>();
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1), null, null, "UTC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Counts(5)));
        var cut = FilteredDialog(context, openChanges);

        Button(cut, ".cal-exp-dialog", "Export").Click();

        cut.WaitForAssertion(() => Assert.Single(context.Transfer.Downloads));
        context.Calendar.Verify(c => c.ExportAggregateUrl(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1), null, null, "UTC"), Times.Once);
        Assert.Equal([false], openChanges);
        Assert.StartsWith("Exporting 5 events.", Assert.Single(context.Toasts).Message, StringComparison.Ordinal);
    }

    [Fact] // E2: Export shows Loading while the summary runs, and the dialog announces it is preparing
    public async Task The_filtered_export_is_busy_and_announced_while_the_summary_runs()
    {
        await using var context = new CalendarIcsTestContext();
        var summary = new TaskCompletionSource<ApiResult<CalendarIcsExportSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(summary.Task);
        var cut = FilteredDialog(context, []);

        Button(cut, ".cal-exp-dialog", "Export").Click();

        cut.WaitForAssertion(() => Assert.Equal(CalendarIcsExport.Preparing, cut.Find(".cal-exp-dialog div.sr-only[aria-live=polite]").TextContent));
        Assert.Equal("true", cut.FindAll(".cal-exp-dialog .ods-modal-foot button").Last().GetAttribute("aria-busy"));
        summary.SetResult(Ok(Counts(1)));
        cut.WaitForAssertion(() => Assert.Single(context.Transfer.Downloads));
    }

    // ── E3: Manage calendars ────────────────────────────────────────────────

    [Fact] // AC 5 (E3)
    public async Task The_per_calendar_export_button_is_busy_while_its_export_is_prepared()
    {
        await using var context = new CalendarIcsTestContext();
        var exporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exports = 0;
        var cut = context.Render<ManageCalendarsDialog>(new Dictionary<string, object?>
        {
            [nameof(ManageCalendarsDialog.Open)] = true,
            [nameof(ManageCalendarsDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
            [nameof(ManageCalendarsDialog.OnExport)] = EventCallback.Factory.Create<ExistingCalendar>(new object(), _ => { exports++; return exporting.Task; }),
        });
        var export = cut.Find("button[aria-label='Export Personal']");

        export.Click();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Export Personal']").GetAttribute("aria-disabled")));
        var busy = cut.Find("button[aria-label='Export Personal']");
        Assert.NotNull(busy.QuerySelector(".odc-iconbtn-spinner"));
        Assert.False(busy.HasAttribute("disabled"));
        cut.WaitForAssertion(() => Assert.Equal(CalendarIcsExport.Preparing, cut.Find(".mud-dialog div.sr-only[aria-live=polite]").TextContent));

        busy.Click();
        Assert.Equal(1, exports);

        exporting.SetResult();
        cut.WaitForAssertion(() => Assert.Null(cut.Find("button[aria-label='Export Personal']").GetAttribute("aria-disabled")));
    }

    // ── E4: the occurrence-or-series prompt ─────────────────────────────────

    [Fact] // AC 2 (E4): it stays open and busy through the summary, then closes on success or refusal alike
    public async Task The_scope_prompt_stays_busy_through_the_summary_then_closes()
    {
        await using var context = new CalendarIcsTestContext();
        var choosing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openChanges = new List<bool>();
        var chosen = new List<CalendarEventExportScope>();
        var cut = context.Render<ExportEventScopeDialog>(new Dictionary<string, object?>
        {
            [nameof(ExportEventScopeDialog.Open)] = true,
            [nameof(ExportEventScopeDialog.OpenChanged)] = EventCallback.Factory.Create<bool>(new object(), v => openChanges.Add(v)),
            [nameof(ExportEventScopeDialog.EventTitle)] = "Book club",
            [nameof(ExportEventScopeDialog.OnChoose)] = EventCallback.Factory.Create<CalendarEventExportScope>(new object(), s => { chosen.Add(s); return choosing.Task; }),
        });

        cut.FindAll(".cal-exp-scope .ods-modal-foot button").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll(".cal-exp-scope .ods-modal-foot button").Last().GetAttribute("aria-busy")));
        var radios = cut.FindAll("[role=radio]");
        Assert.Null(radios[0].GetAttribute("aria-disabled"));
        Assert.Equal("true", radios[1].GetAttribute("aria-disabled"));
        Assert.Empty(cut.FindAll(".cal-exp-scope .ods-modal-x"));
        cut.WaitForAssertion(() => Assert.Equal(CalendarIcsExport.Preparing, cut.Find(".cal-exp-scope div.sr-only[aria-live=polite]").TextContent));
        Assert.Empty(openChanges);

        // A second activation while busy does nothing.
        cut.FindAll(".cal-exp-scope .ods-modal-foot button").Last().Click();
        Assert.Equal([CalendarEventExportScope.Occurrence], chosen);

        choosing.SetResult();
        cut.WaitForAssertion(() => Assert.Equal([false], openChanges));
    }

    // ── E5: the event dialog's Export ───────────────────────────────────────

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> SeriesDialog(CalendarIcsTestContext context, bool busy, List<string?> exported)
    {
        var patternId = Guid.NewGuid();
        context.Calendar.Setup(c => c.GetPatternAsync(patternId, It.IsAny<CancellationToken>())).ReturnsAsync(new ExistingRecurrencePattern
        {
            RecurrencePatternId = patternId,
            CalendarId = CalendarId,
            Title = "Book club",
            Frequency = RecurrenceFrequency.Monthly,
            Interval = 1,
            StartDateTime = new DateTime(2026, 1, 8, 18, 0, 0),
            EndDateTime = new DateTime(2026, 1, 8, 19, 30, 0),
        });
        return context.Render<CalendarEventDialog>(new Dictionary<string, object?>
        {
            [nameof(CalendarEventDialog.Open)] = true,
            [nameof(CalendarEventDialog.Mode)] = CalendarEventDialogMode.Series,
            [nameof(CalendarEventDialog.PatternId)] = patternId,
            [nameof(CalendarEventDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
            [nameof(CalendarEventDialog.CanUpdate)] = true,
            [nameof(CalendarEventDialog.ExportBusy)] = busy,
            [nameof(CalendarEventDialog.OnExport)] = EventCallback.Factory.Create<string?>(new object(), t => exported.Add(t)),
        });
    }

    [Fact] // AC 6 (dialog half): Series mode passes the series' own title
    public async Task Export_in_series_mode_passes_the_series_title_and_announces()
    {
        await using var context = new CalendarIcsTestContext();
        var exported = new List<string?>();
        var cut = SeriesDialog(context, busy: false, exported);
        cut.WaitForElement("#cal-export-btn");

        cut.Find("#cal-export-btn").Click();

        Assert.Equal(["Book club"], exported);
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".mud-dialog div.sr-only[aria-live=polite]"),
            region => region.TextContent == CalendarIcsExport.Preparing));
    }

    [Fact] // AC 5 (E5)
    public async Task A_busy_event_export_is_aria_disabled_shows_a_spinner_keeps_focus_and_ignores_clicks()
    {
        await using var context = new CalendarIcsTestContext();
        var exported = new List<string?>();
        var cut = SeriesDialog(context, busy: true, exported);
        cut.WaitForElement("#cal-export-btn");

        var export = cut.Find("#cal-export-btn");
        Assert.Equal("true", export.GetAttribute("aria-disabled"));
        Assert.Equal("true", export.GetAttribute("aria-busy"));
        Assert.False(export.HasAttribute("disabled"));
        Assert.NotNull(export.QuerySelector(".mud-progress-circular"));

        export.Click();
        Assert.Empty(exported);
    }
}
