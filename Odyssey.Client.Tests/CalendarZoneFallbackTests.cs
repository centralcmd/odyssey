using System.Net;
using System.Reflection;
using Bunit;
using Moq;
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
/// PR #528 architect review: a zone the browser resolves but the server's tzdata does not is refused with a 400 keyed on
/// the zone field. The calendar then retries once in UTC and keeps UTC for the session, on the page's list, the aggregate
/// exports and the import, rather than failing every load.
/// </summary>
public class CalendarZoneFallbackTests
{
    static CalendarZoneFallbackTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static ApiResult<T> Refused<T>(string key) =>
        ApiResult<T>.Failure(HttpStatusCode.BadRequest, new ApiProblem
        {
            Status = 400, Title = "One or more validation errors occurred.",
            Errors = new Dictionary<string, string[]> { [key] = ["The time zone must be an IANA time zone, e.g. Europe/Oslo."] },
        });

    [Theory] // the refusal is recognised by its key, whatever its case; any other 400 is not a zone refusal
    [InlineData("ViewTimeZoneId", "viewTimeZoneId", true)]
    [InlineData("viewTimeZoneId", "viewTimeZoneId", true)]
    [InlineData("defaultTimeZoneId", "defaultTimeZoneId", true)]
    [InlineData("from", "viewTimeZoneId", false)]
    public void A_refusal_is_recognised_only_by_the_zone_fields_key(string key, string field, bool refused)
    {
        var failure = Refused<object>(key);
        Assert.Equal(refused, ViewerTimeZone.RefusedZone(failure.Status, failure.Problem, field));
        Assert.False(ViewerTimeZone.RefusedZone(HttpStatusCode.InternalServerError, failure.Problem, field));
    }

    [Fact]
    public async Task WithFallback_retries_once_in_utc_and_the_viewer_stays_utc()
    {
        var viewer = new ViewerTimeZone("Europe/Oslo");
        var sent = new List<string>();

        var result = await viewer.WithFallbackAsync("viewTimeZoneId", () =>
        {
            sent.Add(viewer.ViewZoneId);
            return Task.FromResult(sent.Count == 1 ? Refused<int>("viewTimeZoneId") : ApiResult<int>.Success(1, HttpStatusCode.OK));
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(["Europe/Oslo", "UTC"], sent);
        Assert.True(viewer.FellBackToUtc);
        Assert.Equal(TimeZoneInfo.Utc, viewer.Zone);
        Assert.Equal("UTC", viewer.ImportZoneId);
        Assert.Equal("Europe/Oslo", viewer.BrowserZoneId);
    }

    [Fact]
    public async Task WithFallback_tries_utc_only_once()
    {
        var viewer = new ViewerTimeZone("Europe/Oslo");
        var calls = 0;

        var result = await viewer.WithFallbackAsync("viewTimeZoneId", () => { calls++; return Task.FromResult(Refused<int>("viewTimeZoneId")); });

        Assert.False(result.IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact] // round 4: a call whose zone was refused retries even when an overlapping call already switched to UTC
    public async Task WithFallback_retries_a_call_that_sent_the_browser_zone_even_after_another_call_switched()
    {
        var viewer = new ViewerTimeZone("Europe/Oslo");
        var release = new TaskCompletionSource();
        var sent = new List<string>();

        var slow = viewer.WithFallbackAsync("viewTimeZoneId", async () =>
        {
            var zone = viewer.ViewZoneId;
            sent.Add(zone);
            if (zone == "UTC")
                return ApiResult<int>.Success(1, HttpStatusCode.OK);
            await release.Task;
            return Refused<int>("viewTimeZoneId");
        });
        // An overlapping call is refused and switches the session to UTC while the first is still in flight.
        await viewer.WithFallbackAsync("viewTimeZoneId", () =>
            Task.FromResult(viewer.ViewZoneId == "UTC" ? ApiResult<int>.Success(1, HttpStatusCode.OK) : Refused<int>("viewTimeZoneId")));
        release.SetResult();

        var result = await slow;

        Assert.True(result.IsSuccess);
        Assert.Equal(["Europe/Oslo", "UTC"], sent);
    }

    [Fact] // round 4: a call that already sent UTC is not retried
    public async Task WithFallback_does_not_retry_a_call_that_sent_utc()
    {
        var viewer = new ViewerTimeZone("Europe/Oslo");
        viewer.UseUtc();
        var calls = 0;

        await viewer.WithFallbackAsync("viewTimeZoneId", () => { calls++; return Task.FromResult(Refused<int>("viewTimeZoneId")); });

        Assert.Equal(1, calls);
    }

    [Fact] // the page's list: retried in UTC, the window recomputed in UTC, and the next load starts in UTC
    public async Task The_calendar_list_falls_back_to_utc_when_the_server_refuses_the_viewers_zone()
    {
        await using var context = new CalendarIcsTestContext(
            PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete)
        { ViewerZone = "Europe/Oslo" };
        var windows = new List<(DateTime From, string? Zone)>();
        context.Calendar.Setup(c => c.ListEventsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((DateTime from, DateTime _, string? zone, CancellationToken _) => windows.Add((from, zone)))
            .ReturnsAsync((DateTime _, DateTime _, string? zone, CancellationToken _) => zone == "UTC"
                ? Ok(new List<ExistingCalendarEvent> { Zoned(new DateTime(2026, 4, 1, 9, 0, 0), "Europe/Oslo") })
                : Refused<List<ExistingCalendarEvent>>("ViewTimeZoneId"));
        var cut = context.Render<CalendarPage>();
        var page = cut.FindComponent<CalendarPage>().Instance;
        typeof(CalendarPage).GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, "day");
        typeof(CalendarPage).GetField("_anchor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, new DateOnly(2026, 4, 1));
        var load = typeof(CalendarPage).GetMethod("LoadEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)load.Invoke(page, null)!);

        Assert.Equal(
            [(new DateTime(2026, 3, 31, 22, 0, 0), "Europe/Oslo"), (new DateTime(2026, 4, 1, 0, 0, 0), "UTC")],
            windows.Select(w => (w.From, w.Zone)));
        // Drawn in UTC too, so the grid shows what the window asked for.
        Assert.Equal(new DateTime(2026, 4, 1, 7, 0, 0), Assert.Single(cut.FindComponent<OdsCalTimeGrid>().Instance.Events).Start);
        Assert.Empty(context.Toasts);

        await cut.InvokeAsync(() => (Task)load.Invoke(page, null)!);
        Assert.Equal("UTC", windows[^1].Zone);
        Assert.Equal(3, windows.Count);
    }

    [Fact] // the aggregate export: the summary is retried in UTC and the download uses UTC
    public async Task Export_all_falls_back_to_utc_and_downloads_in_utc()
    {
        await using var context = new CalendarIcsTestContext { ViewerZone = "Europe/Oslo" };
        var zones = new List<string?>();
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(null, null, null, null, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((DateTime? _, DateTime? _, IReadOnlyCollection<Guid>? _, string? _, string? zone, CancellationToken _) => zones.Add(zone))
            .ReturnsAsync((DateTime? _, DateTime? _, IReadOnlyCollection<Guid>? _, string? _, string? zone, CancellationToken _) =>
                zone == "UTC" ? Ok(Counts(1)) : Refused<CalendarIcsExportSummary>("viewTimeZoneId"));
        var cut = context.Render<CalendarPage>();

        await cut.InvokeAsync(cut.FindComponent<CalendarPage>().Instance.ExportAllEventsAsync);

        Assert.Equal(["Europe/Oslo", "UTC"], zones);
        context.Calendar.Verify(c => c.ExportAggregateUrl(null, null, null, null, "UTC"), Times.Once);
        Assert.Single(context.Transfer.Downloads);
    }

    [Fact] // the filtered export: the window is recomputed in UTC for the retry and the download
    public async Task The_filtered_export_falls_back_to_utc_with_utc_day_boundaries()
    {
        await using var context = new CalendarIcsTestContext { ViewerZone = "Europe/Oslo" };
        context.Calendar.Setup(c => c.SummarizeAggregateExportAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? _, DateTime? _, IReadOnlyCollection<Guid>? _, string? _, string? zone, CancellationToken _) =>
                zone == "UTC" ? Ok(Counts(1)) : Refused<CalendarIcsExportSummary>("ViewTimeZoneId"));
        var cut = context.Render<ExportCalendarEventsDialog>(new Dictionary<string, object?>
        {
            [nameof(ExportCalendarEventsDialog.Open)] = true,
            [nameof(ExportCalendarEventsDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
            [nameof(ExportCalendarEventsDialog.InitialFrom)] = new DateTime(2026, 4, 1),
            [nameof(ExportCalendarEventsDialog.InitialTo)] = new DateTime(2026, 4, 2),
        });

        cut.FindAll(".cal-exp-dialog button").Single(b => b.TextContent.Contains("Export", StringComparison.Ordinal) && !b.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => context.Calendar.Verify(c => c.ExportAggregateUrl(
            new DateTime(2026, 4, 1), new DateTime(2026, 4, 2), null, null, "UTC"), Times.Once));
        context.Calendar.Verify(c => c.SummarizeAggregateExportAsync(new DateTime(2026, 3, 31, 22, 0, 0), new DateTime(2026, 4, 1, 22, 0, 0),
            null, null, "Europe/Oslo", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact] // the typed import: retried once with defaultTimeZoneId = UTC
    public async Task The_typed_import_falls_back_to_utc_when_the_default_zone_is_refused()
    {
        await using var h = new ImportCalendarDialogTests.Harness();
        h.Context.Ctx.Services.AddSingletonViewer("Europe/Oslo");
        h.Transfer.Holds = false;
        var zones = new List<string?>();
        h.Context.Calendar.Setup(c => c.ImportAsync(CalendarId, It.IsAny<ApiUpload>(), false, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, ApiUpload _, bool _, string? zone, CancellationToken _) => zones.Add(zone))
            .ReturnsAsync((Guid _, ApiUpload _, bool _, string? zone, CancellationToken _) =>
                zone == "UTC" ? Ok(new IcsImportResult { ImportedCount = 1 }) : Refused<IcsImportResult>("defaultTimeZoneId"));
        h.Start();
        await h.Pick();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));
        Assert.Equal(["Europe/Oslo", "UTC"], zones);
    }

    [Fact] // round 4: once the session has fallen back (normally on the page's first load), the native upload sends UTC first
    public async Task The_native_import_sends_utc_at_once_when_the_session_already_fell_back()
    {
        await using var h = new ImportCalendarDialogTests.Harness();
        var viewer = new ViewerTimeZone("Europe/Oslo");
        viewer.UseUtc();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(h.Context.Ctx.Services, viewer);
        h.Start();
        await h.Pick();

        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        Assert.Equal([new KeyValuePair<string, string>("defaultTimeZoneId", "UTC")], h.Transfer.Uploads[0].Fields);
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Refused<IcsImportResult>("defaultTimeZoneId"));

        // Refused even in UTC: no second upload of the whole file, the failure is shown.
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Compose, h.Phase));
        Assert.Single(h.Transfer.Uploads);
    }

    [Fact] // the native import: the file is sent once more with defaultTimeZoneId = UTC
    public async Task The_native_import_falls_back_to_utc_when_the_default_zone_is_refused()
    {
        await using var h = new ImportCalendarDialogTests.Harness();
        h.Context.Ctx.Services.AddSingletonViewer("Europe/Oslo");
        h.Start();
        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Refused<IcsImportResult>("defaultTimeZoneId"));

        h.Cut.WaitForAssertion(() => Assert.Equal(2, h.Transfer.Uploads.Count));
        Assert.Equal([new KeyValuePair<string, string>("defaultTimeZoneId", "Europe/Oslo")], h.Transfer.Uploads[0].Fields);
        Assert.Equal([new KeyValuePair<string, string>("defaultTimeZoneId", "UTC")], h.Transfer.Uploads[1].Fields);
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Ok(new IcsImportResult { ImportedCount = 1 }));
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));
    }
}
