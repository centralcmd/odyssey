using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The calendar page's half of issue #517: the summary-first exports E1/E3/E4/E5, their busy guards and toasts, and the
/// single owner of a handed-off import's outcome. The dialogs' halves are <see cref="ImportCalendarDialogTests"/> and
/// <see cref="CalendarExportSurfaceTests"/>. Test names carry the acceptance criterion they prove.
/// </summary>
public class CalendarPageIcsTests
{
    static CalendarPageIcsTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private const string StartedTail =
        "Your browser has started the download — if it does not finish, your browser's downloads list shows why.";

    private static (CalendarIcsTestContext Context, IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut, CalendarPage Page) Render()
    {
        var context = new CalendarIcsTestContext();
        var cut = context.Render<CalendarPage>();
        return (context, cut, cut.FindComponent<CalendarPage>().Instance);
    }

    private static string PageAnnouncer(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindComponent<CalendarPage>().FindAll("div.sr-only[aria-live=polite]").First().TextContent;

    // ── E1 ──────────────────────────────────────────────────────────────────

    [Fact] // AC 2 + AC 3 (the page hands the client's absolute URL to the browser)
    public async Task Export_all_asks_for_the_summary_then_downloads_and_toasts_the_counts()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Counts(12, series: 2)));

        await cut.InvokeAsync(page.ExportAllEventsAsync);

        Assert.Equal(["http://api.test/api/calendar-events/ics"], context.Transfer.Downloads);
        var toast = Assert.Single(context.Toasts);
        Assert.Equal($"Exporting 12 events, including 2 recurring series. {StartedTail}", toast.Message);
        Assert.Equal(Severity.Success, toast.Severity);
        Assert.Equal(10_000, toast.Duration);
        cut.WaitForAssertion(() => Assert.Equal(CalendarIcsExport.Preparing, PageAnnouncer(cut)));
        context.Calendar.Verify(c => c.ExportAggregateAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // §3.1: an empty calendar still exports, and the toast says "no events"
    public async Task An_empty_export_toasts_no_events()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Counts(0)));

        await cut.InvokeAsync(page.ExportAllEventsAsync);

        Assert.Equal($"Exporting no events. {StartedTail}", Assert.Single(context.Toasts).Message);
        Assert.Single(context.Transfer.Downloads);
    }

    [Theory] // AC 2: a refusal is a persistent toast and starts no download
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refused_export_all_toasts_persistently_and_downloads_nothing(HttpStatusCode status)
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<CalendarIcsExportSummary>(status, "Too many events to export."));

        await cut.InvokeAsync(page.ExportAllEventsAsync);

        Assert.Empty(context.Transfer.Downloads);
        var toast = Assert.Single(context.Toasts);
        Assert.Equal("Unable to export events: Too many events to export.", toast.Message);
        Assert.Equal(Severity.Error, toast.Severity);
        Assert.True(toast.RequireInteraction);
    }

    [Fact] // AC 4
    public async Task Two_identical_exports_in_a_row_both_show_their_toast()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Counts(3)));

        await cut.InvokeAsync(page.ExportAllEventsAsync);
        await cut.InvokeAsync(page.ExportAllEventsAsync);

        Assert.Equal(2, context.Toasts.Count);
        Assert.Equal(context.Toasts[0].Message, context.Toasts[1].Message);
        Assert.NotNull(context.Toasts[0].Key);
        Assert.NotEqual(context.Toasts[0].Key, context.Toasts[1].Key);
        Assert.Equal(2, context.Transfer.Downloads.Count);
    }

    [Fact] // AC 5
    public async Task A_second_export_all_while_one_is_prepared_makes_no_second_summary_call_and_says_so()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var summary = new TaskCompletionSource<ApiResult<CalendarIcsExportSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(null, null, null, null, It.IsAny<CancellationToken>()))
            .Returns(summary.Task);

        var first = cut.InvokeAsync(page.ExportAllEventsAsync);
        await cut.InvokeAsync(page.ExportAllEventsAsync);

        cut.WaitForAssertion(() => Assert.Equal(CalendarIcsExport.AlreadyPreparing, PageAnnouncer(cut)));
        context.Calendar.Verify(c => c.SummarizeAggregateExportAsync(null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);

        summary.SetResult(Ok(Counts(1)));
        await first;
        Assert.Single(context.Transfer.Downloads);
    }

    // ── E3 ──────────────────────────────────────────────────────────────────

    [Fact] // AC 2 (E3) + §3.1
    public async Task The_per_calendar_export_toasts_the_calendar_name_and_counts()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeExportAsync(CalendarId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Counts(1)));

        await cut.InvokeAsync(() => page.ExportCalendarAsync(Personal));

        Assert.Equal([$"http://api.test/api/calendars/{CalendarId}/ics"], context.Transfer.Downloads);
        Assert.Equal($"Exporting “Personal”: 1 event. {StartedTail}", Assert.Single(context.Toasts).Message);
    }

    [Fact] // AC 2 (E3)
    public async Task A_refused_calendar_export_toasts_persistently_and_downloads_nothing()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        context.Calendar.Setup(c => c.SummarizeExportAsync(CalendarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<CalendarIcsExportSummary>(HttpStatusCode.NotFound, "Calendar not found."));

        await cut.InvokeAsync(() => page.ExportCalendarAsync(Personal));

        Assert.Empty(context.Transfer.Downloads);
        Assert.True(Assert.Single(context.Toasts).RequireInteraction);
    }

    // ── E4 / E5 ─────────────────────────────────────────────────────────────

    [Fact] // AC 6
    public async Task A_series_export_toasts_the_series_title_passed_by_the_event_dialog()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var patternId = Guid.NewGuid();
        context.Calendar.Setup(c => c.SummarizePatternExportAsync(patternId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Counts(12, series: 1, events: 1)));

        await cut.InvokeAsync(() => page.OpenSeriesDialog(patternId));
        await cut.InvokeAsync(() => page.ExportEventDialogAsync("Book club"));

        Assert.Equal([$"http://api.test/api/recurrence-patterns/{patternId}/ics"], context.Transfer.Downloads);
        Assert.Equal($"Exporting the series “Book club” (12 events). {StartedTail}", Assert.Single(context.Toasts).Message);
    }

    [Fact] // AC 2 (E5): a refused single-event export
    public async Task A_refused_event_export_toasts_persistently_and_downloads_nothing()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var eventId = Guid.NewGuid();
        context.Calendar.Setup(c => c.SummarizeEventExportAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<CalendarIcsExportSummary>(HttpStatusCode.NotFound, "Event not found."));

        await cut.InvokeAsync(() => page.OpenEventDialog(eventId));
        await cut.InvokeAsync(() => page.ExportEventDialogAsync("Dentist"));

        Assert.Empty(context.Transfer.Downloads);
        var toast = Assert.Single(context.Toasts);
        Assert.Equal("Unable to export the event: Event not found.", toast.Message);
        Assert.True(toast.RequireInteraction);
    }

    [Fact] // §3.1: a single event's toast names it
    public async Task A_single_event_export_toasts_its_title()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var eventId = Guid.NewGuid();
        context.Calendar.Setup(c => c.SummarizeEventExportAsync(eventId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Counts(1)));

        await cut.InvokeAsync(() => page.OpenEventDialog(eventId));
        await cut.InvokeAsync(() => page.ExportEventDialogAsync("Dentist"));

        Assert.Equal($"Exporting “Dentist”. {StartedTail}", Assert.Single(context.Toasts).Message);
    }

    [Fact] // AC 2 (E4): closing the scope prompt returns focus to the event dialog's Export
    public async Task Closing_the_scope_prompt_restores_focus_to_export()
    {
        var (context, cut, page) = Render();
        await using var _ = context;

        await cut.InvokeAsync(() => page.OnExportScopeOpenChangedAsync(false));

        Assert.Contains(context.Ctx.JSInterop.Invocations["odsFocusById"], i => Equals(i.Arguments[0], "cal-export-btn"));
    }

    // ── Import hand-off ─────────────────────────────────────────────────────

    private static int Loads(CalendarIcsTestContext context) =>
        context.Calendar.Invocations.Count(i => i.Method.Name == nameof(Odyssey.ApiClient.Resources.ICalendarApiClient.ListEventsAsync));

    [Fact] // AC 15a
    public async Task A_handed_off_success_toasts_the_counts_for_10_s_and_refreshes()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var pending = new TaskCompletionSource<ApiResult<IcsImportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = Loads(context);

        var handling = cut.InvokeAsync(() => page.OnImportBackgroundedAsync(pending.Task));
        pending.SetResult(Ok(new IcsImportResult { ImportedCount = 4, UpdatedCount = 2, RemovedCount = 1 }));
        await handling;

        var toast = Assert.Single(context.Toasts);
        Assert.Equal("Calendar import finished: 4 imported, 2 updated, 0 skipped, 1 removed.", toast.Message);
        Assert.Equal(Severity.Success, toast.Severity);
        Assert.Equal(10_000, toast.Duration);
        Assert.Equal(before + 1, Loads(context));
    }

    [Fact] // AC 15b: a partially applied import
    public async Task A_handed_off_partial_import_toasts_persistently_with_the_partial_sentence_and_refreshes()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var before = Loads(context);

        await cut.InvokeAsync(() => page.OnImportBackgroundedAsync(Task.FromResult(Fail<IcsImportResult>(
            HttpStatusCode.InternalServerError, "The import stopped early.", CalendarImportCopy.PartiallyAppliedCode,
            new Dictionary<string, object> { ["committedEvents"] = 1000 }))));

        var toast = Assert.Single(context.Toasts);
        Assert.Equal("The import stopped early. " + CalendarImportCopy.PartialSentence(1000), toast.Message);
        Assert.True(toast.RequireInteraction);
        Assert.Equal(before + 1, Loads(context));
    }

    [Fact] // AC 15b: any other failure
    public async Task A_handed_off_failure_toasts_persistently_and_refreshes()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var before = Loads(context);

        await cut.InvokeAsync(() => page.OnImportBackgroundedAsync(Task.FromResult(
            ApiResult<IcsImportResult>.Failure(new HttpRequestException("The connection was lost.")))));

        var toast = Assert.Single(context.Toasts);
        Assert.Equal(CalendarImportCopy.HandedOffFailure, toast.Message);
        Assert.Equal(Severity.Error, toast.Severity);
        Assert.True(toast.RequireInteraction);
        Assert.Equal(before + 1, Loads(context));
    }

    [Fact] // AC 15f (implementation note 2): a disposed page still toasts, and skips only the refresh
    public async Task A_disposed_page_still_toasts_but_skips_its_refresh()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var pending = new TaskCompletionSource<ApiResult<IcsImportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handling = cut.InvokeAsync(() => page.OnImportBackgroundedAsync(pending.Task));
        var before = Loads(context);

        page.Dispose();
        pending.SetResult(Ok(new IcsImportResult { ImportedCount = 1 }));
        await handling;

        Assert.Single(context.Toasts);
        Assert.Equal(before, Loads(context));
    }

    [Fact] // AC 15d: the page tells a reopened dialog an import is still running
    public async Task While_a_handed_off_import_runs_the_dialog_is_told_it_is_pending()
    {
        var (context, cut, page) = Render();
        await using var _ = context;
        var pending = new TaskCompletionSource<ApiResult<IcsImportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handling = cut.InvokeAsync(() => page.OnImportBackgroundedAsync(pending.Task));

        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<ImportCalendarDialog>().Instance.ImportPending));
        pending.SetResult(Ok(new IcsImportResult()));
        await handling;
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<ImportCalendarDialog>().Instance.ImportPending));
    }
}
