using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Moq;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="ImportCalendarDialog"/> (issue #517 §3 Import flow, §6, §7). Test names carry the acceptance criterion
/// they prove. The page's half of the hand-off (the toast and the refresh it owns) is
/// <see cref="CalendarPageIcsTests"/>.
/// </summary>
public class ImportCalendarDialogTests
{
    static ImportCalendarDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private const long Megabyte = 1L << 20;

    internal sealed class Harness : IAsyncDisposable
    {
        public CalendarIcsTestContext Context { get; } = new();

        public IRenderedComponent<RefreshHost> Cut { get; private set; } = default!;

        public List<bool> OpenChanges { get; } = [];

        public int Changed { get; private set; }

        public List<Task<ApiResult<IcsImportResult>>> HandedOff { get; } = [];

        public bool CanDelete { get; set; } = true;

        public bool ImportPending { get; set; }

        public bool Open { get; set; } = true;

        /// <summary>False removes the dialog from the tree, as a navigation away from the page does.</summary>
        public bool Mounted { get; set; } = true;

        public Harness Start()
        {
            Cut = Context.Ctx.Render<RefreshHost>(p => p.AddChildContent(Rebuild));
            Cut.WaitForElement("#" + ImportCalendarDialog.ImportButtonId);
            return this;
        }

        private Dictionary<string, object?> Parameters() => new()
        {
            [nameof(ImportCalendarDialog.Open)] = Open,
            [nameof(ImportCalendarDialog.OpenChanged)] = EventCallback.Factory.Create<bool>(this, v => OpenChanges.Add(v)),
            [nameof(ImportCalendarDialog.Calendars)] = (IReadOnlyList<ExistingCalendar>)[Personal],
            [nameof(ImportCalendarDialog.DefaultCalendarId)] = CalendarId,
            [nameof(ImportCalendarDialog.CanDelete)] = CanDelete,
            [nameof(ImportCalendarDialog.ImportPending)] = ImportPending,
            [nameof(ImportCalendarDialog.OnChanged)] = EventCallback.Factory.Create(this, () => Changed++),
            [nameof(ImportCalendarDialog.OnBackgrounded)] =
                EventCallback.Factory.Create<Task<ApiResult<IcsImportResult>>>(this, t => HandedOff.Add(t)),
        };

        /// <summary>Closes and reopens the dialog, as the page does with its <c>Open</c> flag.</summary>
        public void Reopen()
        {
            Open = false;
            Rerender();
            Cut.WaitForAssertion(() => Assert.Empty(Cut.FindAll(".cal-imp-dialog")));
            Open = true;
            Rerender();
            Cut.WaitForElement("#" + ImportCalendarDialog.ImportButtonId);
        }

        public void Rerender() => Cut.InvokeAsync(Cut.Instance.RefreshAsync).GetAwaiter().GetResult();

        private void Rebuild(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenComponent<MudBlazor.MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudBlazor.MudPopoverProvider>(10);
            b.CloseComponent();
            if (!Mounted)
            {
                return;
            }

            b.OpenComponent<ImportCalendarDialog>(20);
            foreach (var (name, value) in Parameters())
            {
                b.AddComponentParameter(30, name, value);
            }

            b.CloseComponent();
        }

        public Task Pick(string name = "calendar.ics", long size = 4_096) =>
            Cut.InvokeAsync(() => Cut.FindComponent<OdsFileUpload>().Instance.FilesChanged.InvokeAsync(
                [new OdsUploadFile { Uid = Guid.NewGuid().ToString(), Name = name, Kind = "Document", SizeBytes = size, Source = new FakeBrowserFile(name, size, "text/calendar") }]));

        public void ClickImport() => Cut.Find("#" + ImportCalendarDialog.ImportButtonId).Click();

        public IElement Footer(string label) => Cut.FindAll(".ods-modal-foot button").Single(b => Label(b) == label);

        public bool HasFooter(string label) => Cut.FindAll(".ods-modal-foot button").Any(b => Label(b) == label);

        public static string Label(IElement button)
        {
            var icons = string.Concat(button.QuerySelectorAll(".material-icons").Select(i => i.TextContent));
            return (icons.Length == 0 ? button.TextContent : button.TextContent.Replace(icons, string.Empty)).Trim();
        }

        /// <summary>The import dialog's own announcer (the first polite region in it).</summary>
        public string Announced => Cut.FindAll(".cal-imp-dialog div.sr-only[aria-live=polite]").First().TextContent;

        public string FooterStatus => Cut.Find("#" + ImportCalendarDialog.FootStatusId).TextContent;

        public IElement? Alert => Cut.FindAll("#" + ImportCalendarDialog.AlertId).FirstOrDefault();

        public ImportCalendarDialog.Phase Phase => Cut.FindComponent<ImportCalendarDialog>().Instance.CurrentPhase;

        public FakeNativeTransfer Transfer => Context.Transfer;

        public bool ConfirmOpen => Cut.FindAll(".cal-imp-stop-dialog").Count > 0;

        /// <summary>Escape inside the import dialog, as the <c>odsDialogEscape</c> watch reports it.</summary>
        public Task Escape() => Cut.InvokeAsync(Cut.FindComponent<ImportCalendarDialog>().Instance.OnDialogEscape);

        /// <summary>
        /// MudDialog's own Escape (and scrim) path for the dialog carrying <paramref name="cssClass"/>: it raises its
        /// <c>VisibleChanged(false)</c>, exactly as its key interceptor does on Escape.
        /// </summary>
        public Task MudEscape(string cssClass) =>
            Cut.InvokeAsync(() => Cut.FindComponents<MudBlazor.MudDialog>()
                .First(d => d.Instance.VisibleChanged.HasDelegate
                    && (d.Instance.Class ?? string.Empty).Contains(cssClass, StringComparison.Ordinal))
                .Instance.VisibleChanged.InvokeAsync(false));

        /// <summary>Polls a condition that changes without a render (a toast from a disposed dialog).</summary>
        public static async Task Eventually(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(condition());
        }

        public bool FocusedById(string id) =>
            Context.Ctx.JSInterop.Invocations["odsFocusById"].Any(i => Equals(i.Arguments[0], id));

        public IReadOnlyList<JSRuntimeInvocation> Js(string identifier) => [.. Context.Ctx.JSInterop.Invocations[identifier]];

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static IcsImportResult Imported(int imported = 3, int updated = 1, int removed = 0, params string[] removedTitles) => new()
    {
        ImportedCount = imported,
        UpdatedCount = updated,
        RemovedCount = removed,
        RemovedSampleTitles = removedTitles,
        Skipped = [new IcsImportSkipGroup { Reason = "Cancelled in the file.", Count = 1, SampleTitles = ["Old standup"] }],
    };

    private static async Task<Harness> UploadingAsync(Action<Harness>? configure = null)
    {
        var h = new Harness();
        configure?.Invoke(h);
        h.Start();
        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Uploading, h.Phase));
        return h;
    }

    // ── Compose ─────────────────────────────────────────────────────────────

    [Fact] // AC 16
    public async Task The_hint_states_the_formatted_effective_limit()
    {
        await using var h = new Harness();
        h.Start();

        Assert.Contains("iCalendar (.ics) · one file · up to 2 GB", h.Cut.Find($"#{ImportCalendarDialog.UploadId}").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC 16: the limits failed to load — the cache's fallback is the 10 GB bound
    public async Task Without_the_limits_the_hint_and_the_size_error_say_10_GB()
    {
        await using var h = new Harness();
        h.Context.CurrentLimits = Odyssey.Client.Services.ImportLimitsCache.Fallback;
        h.Start();

        Assert.Contains("up to 10 GB", h.Cut.Find($"#{ImportCalendarDialog.UploadId}").TextContent, StringComparison.Ordinal);
        await h.Pick(size: 11L * 1024 * Megabyte);
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Contains("That file is larger than the 10 GB limit.", h.Cut.Markup, StringComparison.Ordinal));
        Assert.Empty(h.Transfer.Uploads);
    }

    [Fact] // AC 16: the size error carries the formatted effective limit
    public async Task A_file_over_the_limit_is_refused_with_the_formatted_limit()
    {
        await using var h = new Harness();
        h.Start();
        await h.Pick(size: 3L * 1024 * Megabyte);

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Contains("That file is larger than the 2 GB limit.", h.Cut.Markup, StringComparison.Ordinal));
        Assert.Empty(h.Transfer.Uploads);
        Assert.Contains(h.Js("odsFocusFirstIn"), i => Equals(i.Arguments[0], ImportCalendarDialog.UploadId));
    }

    [Fact] // AC 9
    public async Task A_file_that_does_not_end_in_ics_is_refused_before_any_request()
    {
        await using var h = new Harness();
        h.Start();
        await h.Pick("calendar.txt");

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Contains("That isn't a .ics file.", h.Cut.Markup, StringComparison.Ordinal));
        Assert.Empty(h.Transfer.Uploads);
        h.Context.Calendar.Verify(c => c.ImportAsync(It.IsAny<Guid>(), It.IsAny<ApiUpload>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(h.Js("odsFocusFirstIn"), i => Equals(i.Arguments[0], ImportCalendarDialog.UploadId));
    }

    [Fact] // AC 10: offered only to a calendar.delete holder, unticked
    public async Task The_remove_option_renders_only_for_a_delete_holder_and_unticked()
    {
        await using (var without = new Harness { CanDelete = false })
        {
            without.Start();
            Assert.Empty(without.Cut.FindAll("#" + ImportCalendarDialog.RemoveId));
            Assert.DoesNotContain("Removing events is never automatic", without.Cut.Markup, StringComparison.Ordinal);
        }

        await using var h = new Harness();
        h.Start();
        var box = h.Cut.Find("#" + ImportCalendarDialog.RemoveId);
        Assert.False(box.HasAttribute("checked"));
        Assert.Contains("Allow this import to remove events or discard edits", h.Cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Removing events is never automatic: tick the option below to remove events this file marks as cancelled.",
            h.Cut.Markup, StringComparison.Ordinal);
        Assert.Equal(ImportCalendarDialog.RemoveHelpId, box.GetAttribute("aria-describedby"));
    }

    [Fact] // AC 10: ticking shows the Status-live warning
    public async Task Ticking_the_remove_option_shows_the_status_warning()
    {
        await using var h = new Harness();
        h.Start();

        // The live region is there, empty, before the box is ticked — it is filled, not created (WCAG 4.1.3).
        var region = h.Cut.Find("#" + ImportCalendarDialog.RemoveWarningId);
        Assert.Equal("status", region.GetAttribute("role"));
        Assert.Equal("polite", region.GetAttribute("aria-live"));
        Assert.Equal(string.Empty, region.TextContent.Trim());

        h.Cut.Find("#" + ImportCalendarDialog.RemoveId).Change(true);

        region = h.Cut.Find("#" + ImportCalendarDialog.RemoveWarningId);
        Assert.Contains("This import can delete events.", region.TextContent, StringComparison.Ordinal);
        Assert.Null(region.QuerySelector(".mud-alert")!.GetAttribute("role"));

        h.Cut.Find("#" + ImportCalendarDialog.RemoveId).Change(false);
        Assert.Single(h.Cut.FindAll("#" + ImportCalendarDialog.RemoveWarningId));
        Assert.Equal(string.Empty, h.Cut.Find("#" + ImportCalendarDialog.RemoveWarningId).TextContent.Trim());
    }

    [Fact] // AC 10: it resets on a file change
    public async Task The_remove_option_resets_on_a_file_change()
    {
        await using var h = new Harness();
        h.Start();
        h.Cut.Find("#" + ImportCalendarDialog.RemoveId).Change(true);

        await h.Pick("other.ics");

        Assert.False(h.Cut.Find("#" + ImportCalendarDialog.RemoveId).HasAttribute("checked"));
    }

    [Fact] // AC 10 + AC 7 (dialog half): the field travels with the upload, then resets after the attempt
    public async Task The_remove_option_is_sent_then_reset_after_the_attempt()
    {
        await using var h = new Harness();
        h.Start();
        await h.Pick();
        h.Cut.Find("#" + ImportCalendarDialog.RemoveId).Change(true);

        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        Assert.Equal([new KeyValuePair<string, string>("allowRemovals", "true")], h.Transfer.Uploads[0].Fields);
        Assert.Equal($"http://api.test/api/calendars/{CalendarId}/ics", h.Transfer.Uploads[0].Url);

        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Fail<IcsImportResult>(HttpStatusCode.BadRequest));
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Compose, h.Phase));
        Assert.False(h.Cut.Find("#" + ImportCalendarDialog.RemoveId).HasAttribute("checked"));
    }

    [Fact] // AC 10: without the option the field is not sent at all
    public async Task Without_the_option_no_field_is_sent()
    {
        await using var h = await UploadingAsync();

        Assert.Null(h.Transfer.Uploads[0].Fields);
    }

    [Fact] // AC 10: it resets on Import another file and on close/reopen
    public async Task The_remove_option_resets_on_import_another_file_and_on_reopen()
    {
        await using var h = new Harness();
        h.Start();
        await h.Pick();
        h.Cut.Find("#" + ImportCalendarDialog.RemoveId).Change(true);
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Ok(Imported()));
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));

        h.Footer("Import another file").Click();
        Assert.False(h.Cut.Find("#" + ImportCalendarDialog.RemoveId).HasAttribute("checked"));

        h.Cut.Find("#" + ImportCalendarDialog.RemoveId).Change(true);
        h.Reopen();
        Assert.False(h.Cut.Find("#" + ImportCalendarDialog.RemoveId).HasAttribute("checked"));
    }

    // ── Result ──────────────────────────────────────────────────────────────

    [Fact] // AC 11
    public async Task Two_removed_events_show_the_removed_tile_and_group_announce_and_refresh()
    {
        await using var h = await UploadingAsync();
        h.Transfer.CompleteUpload();

        h.Transfer.Respond(Ok(Imported(imported: 0, updated: 0, removed: 2, "Team offsite", "Dentist")));

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));
        var tile = h.Cut.Find(".cal-imp-stat.rm");
        Assert.Contains("2", tile.QuerySelector(".cal-imp-stat-n")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("removed", tile.QuerySelector(".cal-imp-stat-l")!.TextContent);
        Assert.Equal("delete_sweep", tile.QuerySelector(".material-icons")!.TextContent);
        Assert.Equal("true", tile.QuerySelector(".material-icons")!.GetAttribute("aria-hidden"));
        Assert.NotNull(h.Cut.Find(".cal-imp-stats.cal-imp-stats--four"));
        Assert.Contains("Removed events", h.Cut.Find(".cal-imp-result").TextContent, StringComparison.Ordinal);

        var group = h.Cut.Find(".cal-imp-skip.rm");
        group.QuerySelector("button")!.Click();
        Assert.Contains("Team offsite", h.Cut.Find(".cal-imp-skip.rm").TextContent, StringComparison.Ordinal);
        Assert.Contains("Dentist", h.Cut.Find(".cal-imp-skip.rm").TextContent, StringComparison.Ordinal);

        h.Cut.WaitForAssertion(() => Assert.Equal("0 imported, 0 updated, 1 skipped, 2 removed.", h.Announced));
        Assert.Equal(1, h.Changed);
    }

    [Fact] // AC 11: "+n more" beyond the sampled titles
    public async Task More_removed_events_than_titles_say_how_many_more()
    {
        await using var h = await UploadingAsync();
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Ok(Imported(removed: 5, removedTitles: ["One", "Two"])));
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));

        h.Cut.Find(".cal-imp-skip.rm button").Click();

        Assert.Contains("+3 more", h.Cut.Find(".cal-imp-skip.rm").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC 11: with none removed, neither the tile nor the group appears
    public async Task No_removed_events_show_neither_the_tile_nor_the_group()
    {
        await using var h = await UploadingAsync();
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Ok(Imported()));

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));
        Assert.Empty(h.Cut.FindAll(".cal-imp-stat.rm"));
        Assert.Empty(h.Cut.FindAll(".cal-imp-stats--four"));
        Assert.DoesNotContain("Removed events", h.Cut.Markup, StringComparison.Ordinal);
        h.Cut.WaitForAssertion(() => Assert.Equal("3 imported, 1 updated, 1 skipped.", h.Announced));
    }

    // ── Failures ────────────────────────────────────────────────────────────

    [Fact] // AC 12
    public async Task A_failure_puts_the_text_in_the_focused_alert_and_empties_the_live_region()
    {
        await using var h = await UploadingAsync();
        h.Transfer.Respond(Fail<IcsImportResult>(HttpStatusCode.BadRequest, "The file is not a calendar."));

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Alert));
        Assert.Contains("The file is not a calendar.", h.Alert!.TextContent, StringComparison.Ordinal);
        Assert.Equal("alert", h.Alert.QuerySelector(".mud-alert")!.GetAttribute("role"));
        Assert.Equal("-1", h.Alert.GetAttribute("tabindex"));
        h.Cut.WaitForAssertion(() => Assert.Contains(h.Context.Ctx.JSInterop.Invocations, i => i.Identifier.EndsWith("focus", StringComparison.Ordinal)));
        h.Cut.WaitForAssertion(() => Assert.Equal(string.Empty, h.Announced));
        // Refused before the body was sent: nothing can have changed, so no refresh.
        Assert.Equal(0, h.Changed);
    }

    [Fact] // AC 13
    public async Task A_partially_applied_import_shows_the_partial_sentence_and_refreshes()
    {
        await using var h = await UploadingAsync();
        h.Transfer.CompleteUpload();

        h.Transfer.Respond(Fail<IcsImportResult>(HttpStatusCode.InternalServerError, "The import stopped before the end of the file.",
            CalendarImportCopy.PartiallyAppliedCode, new Dictionary<string, object> { ["committedEvents"] = 1000 }));

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Alert));
        Assert.Contains("The import stopped before the end of the file. 1000 events from this file were already imported or removed "
            + "before the error. Importing the same file again is safe and completes it.", h.Alert!.TextContent, StringComparison.Ordinal);
        Assert.Equal(1, h.Changed);
    }

    [Fact] // AC 13: status 0 after UploadComplete
    public async Task A_lost_connection_after_the_upload_shows_the_connection_lost_sentence_and_refreshes()
    {
        await using var h = await UploadingAsync();
        h.Transfer.CompleteUpload();

        h.Transfer.Respond(ApiResult<IcsImportResult>.Failure(new HttpRequestException("The connection was lost.")), jsonBody: false);

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Alert));
        Assert.Contains(CalendarImportCopy.ConnectionLost, h.Alert!.TextContent, StringComparison.Ordinal);
        Assert.Equal(1, h.Changed);
    }

    [Fact] // AC 13: a proxy's non-JSON 504 page after UploadComplete
    public async Task A_non_json_504_after_the_upload_shows_the_connection_lost_sentence_and_refreshes()
    {
        await using var h = await UploadingAsync();
        h.Transfer.CompleteUpload();

        h.Transfer.Respond(ApiResult<IcsImportResult>.Failure(HttpStatusCode.GatewayTimeout, new ApiProblem { Status = 504 }), jsonBody: false);

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Alert));
        Assert.Contains(CalendarImportCopy.ConnectionLost, h.Alert!.TextContent, StringComparison.Ordinal);
        Assert.Equal(1, h.Changed);
    }

    [Fact] // AC 13 / §3 step 7: any other failure after the upload shows the server's text and refreshes
    public async Task Another_failure_after_the_upload_shows_the_server_text_and_refreshes()
    {
        await using var h = await UploadingAsync();
        h.Transfer.CompleteUpload();

        h.Transfer.Respond(Fail<IcsImportResult>(HttpStatusCode.ServiceUnavailable, "The server is busy."));

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Alert));
        Assert.Contains("The server is busy.", h.Alert!.TextContent, StringComparison.Ordinal);
        Assert.Equal(1, h.Changed);
    }

    // ── Typed fallback ──────────────────────────────────────────────────────

    [Fact] // AC 8
    public async Task Without_a_native_handle_a_file_over_512_MB_shows_the_focused_alert()
    {
        await using var h = new Harness();
        h.Transfer.Holds = false;
        h.Start();
        await h.Pick(size: 600 * Megabyte);

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Alert));
        Assert.Contains("This browser could not stream the file, and files over 512 MB cannot be read into the page. "
            + "Choose the file again with Browse, then import.", h.Alert!.TextContent, StringComparison.Ordinal);
        Assert.Equal("alert", h.Alert.QuerySelector(".mud-alert")!.GetAttribute("role"));
        h.Context.Calendar.Verify(c => c.ImportAsync(It.IsAny<Guid>(), It.IsAny<ApiUpload>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // AC 8
    public async Task Without_a_native_handle_a_smaller_file_uses_the_typed_client_in_the_server_phase()
    {
        await using var h = new Harness();
        h.Transfer.Holds = false;
        var pending = new TaskCompletionSource<ApiResult<IcsImportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Context.Calendar.Setup(c => c.ImportAsync(CalendarId, It.IsAny<ApiUpload>(), true, It.IsAny<CancellationToken>())).Returns(pending.Task);
        h.Start();
        await h.Pick(size: 10 * Megabyte);
        h.Cut.Find("#" + ImportCalendarDialog.RemoveId).Change(true);

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Server, h.Phase));
        Assert.False(h.HasFooter("Cancel upload"));
        Assert.True(h.HasFooter("Close"));
        Assert.Equal(CalendarImportCopy.ServerStatus, h.FooterStatus);
        h.Context.Calendar.Verify(c => c.ImportAsync(CalendarId, It.IsAny<ApiUpload>(), true, It.IsAny<CancellationToken>()), Times.Once);

        pending.SetResult(Ok(Imported()));
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));
    }

    // ── Cancel upload ───────────────────────────────────────────────────────

    [Fact] // AC 14a
    public async Task Cancel_upload_and_Escape_open_the_confirmation()
    {
        await using var h = await UploadingAsync();
        Assert.Equal(CalendarImportCopy.UploadingStatus, h.FooterStatus);
        Assert.False(h.HasFooter("Cancel"));
        Assert.Empty(h.Cut.FindAll(".cal-imp-dialog .ods-modal-x"));

        h.Footer("Cancel upload").Click();
        h.Cut.WaitForAssertion(() => Assert.True(h.ConfirmOpen));
        Assert.Contains("Nothing is imported from this file until the upload has finished.", h.Cut.Markup, StringComparison.Ordinal);

        h.Cut.FindAll(".cal-imp-stop-dialog button").Single(b => Harness.Label(b) == "Keep uploading").Click();
        h.Cut.WaitForAssertion(() => Assert.False(h.ConfirmOpen));

        await h.Escape();
        h.Cut.WaitForAssertion(() => Assert.True(h.ConfirmOpen));
        Assert.Equal(0, h.Transfer.AbortCalls);
    }

    [Fact] // AC 14a: Keep uploading returns focus to Cancel upload
    public async Task Keep_uploading_refocuses_cancel_upload()
    {
        await using var h = await UploadingAsync();
        h.Footer("Cancel upload").Click();
        var restoresBefore = h.Js("odsRestoreFocusOwner").Count;

        h.Cut.FindAll(".cal-imp-stop-dialog button").Single(b => Harness.Label(b) == "Keep uploading").Click();

        h.Cut.WaitForAssertion(() => Assert.Contains(h.Js("odsRestoreFocusOwner").Skip(restoresBefore),
            i => Equals(i.Arguments.FirstOrDefault(), ImportCalendarDialog.CancelUploadId)));
        Assert.Equal(ImportCalendarDialog.Phase.Uploading, h.Phase);
    }

    [Fact] // AC 14a
    public async Task Stop_upload_aborts_returns_to_compose_with_the_file_kept_focuses_the_picker_and_announces()
    {
        await using var h = await UploadingAsync();
        h.Footer("Cancel upload").Click();

        h.Cut.Find("#" + ImportCalendarDialog.StopUploadId).Click();

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Compose, h.Phase));
        Assert.Equal(1, h.Transfer.AbortCalls);
        Assert.Equal("calendar.ics", h.Cut.Find(".odc-upload-file-name").GetAttribute("value"));
        h.Cut.WaitForAssertion(() => Assert.Contains(h.Js("odsFocusFirstIn"), i => Equals(i.Arguments[0], ImportCalendarDialog.UploadId)));
        h.Cut.WaitForAssertion(() => Assert.Equal(CalendarImportCopy.Stopped, h.Announced));
        Assert.Null(h.Alert);
        Assert.Equal(0, h.Changed);
    }

    [Fact] // AC 14b
    public async Task An_upload_finishing_while_the_confirmation_is_open_closes_it_focuses_the_status_and_announces()
    {
        await using var h = await UploadingAsync();
        h.Footer("Cancel upload").Click();
        var restoresBefore = h.Js("odsRestoreFocusOwner").Count;

        await h.Cut.InvokeAsync(h.Transfer.CompleteUpload);

        h.Cut.WaitForAssertion(() => Assert.False(h.ConfirmOpen));
        Assert.Equal(ImportCalendarDialog.Phase.Server, h.Phase);
        Assert.Equal("-1", h.Cut.Find("#" + ImportCalendarDialog.FootStatusId).GetAttribute("tabindex"));
        h.Cut.WaitForAssertion(() => Assert.Contains(h.Js("odsRestoreFocusOwner").Skip(restoresBefore),
            i => Equals(i.Arguments.FirstOrDefault(), ImportCalendarDialog.FootStatusId)));
        h.Cut.WaitForAssertion(() => Assert.Equal(CalendarImportCopy.FinishedWhileDeciding, h.Announced));

        // A Stop upload activation that arrives after the signal does nothing.
        var dialog = h.Cut.FindComponent<ImportCalendarDialog>().Instance;
        await h.Cut.InvokeAsync(dialog.StopUploadAsync);
        Assert.Equal(0, h.Transfer.AbortCalls);
        Assert.True(h.Transfer.UploadInFlight);
    }

    [Fact] // AC 14c
    public async Task An_abort_after_the_upload_finished_shows_the_interrupted_wording_and_refreshes()
    {
        await using var h = await UploadingAsync();
        h.Transfer.CompleteUpload();

        // The browser aborted after the whole body had gone (aborted && uploadComplete).
        await h.Transfer.AbortAsync(ImportCalendarDialog.UploadId);

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Alert));
        Assert.Contains(CalendarImportCopy.Interrupted, h.Alert!.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was imported", h.Cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, h.Changed);
    }

    [Fact] // AC 14d
    public async Task After_the_upload_finishes_cancel_upload_is_gone()
    {
        await using var h = await UploadingAsync();

        await h.Cut.InvokeAsync(h.Transfer.CompleteUpload);

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Server, h.Phase));
        Assert.False(h.HasFooter("Cancel upload"));
        Assert.True(h.HasFooter("Close"));
        Assert.Equal(CalendarImportCopy.ServerStatus, h.FooterStatus);
        Assert.Single(h.Cut.FindAll(".cal-imp-dialog .ods-modal-x"));
    }

    // ── Hand-off ────────────────────────────────────────────────────────────

    [Fact] // AC 15a (dialog half): Close in the server phase hands the pending import off
    public async Task Closing_in_the_server_phase_hands_the_import_off_and_applies_nothing()
    {
        await using var h = await UploadingAsync();
        await h.Cut.InvokeAsync(h.Transfer.CompleteUpload);

        h.Footer("Close").Click();

        h.Cut.WaitForAssertion(() => Assert.Single(h.HandedOff));
        Assert.Contains(false, h.OpenChanges);
        Assert.False(h.HandedOff[0].IsCompleted);

        h.Transfer.Respond(Ok(Imported()));
        var outcome = await h.HandedOff[0];
        Assert.True(outcome.IsSuccess);
        Assert.Equal(0, h.Changed); // the page refreshes, never the dialog
        Assert.NotEqual(ImportCalendarDialog.Phase.Result, h.Phase);
    }

    [Fact] // AC 15c + 15d: no stale result after the dialog moved on; reopening while pending shows the note
    public async Task A_reopened_dialog_shows_the_pending_note_and_never_applies_the_old_result()
    {
        await using var h = await UploadingAsync();
        await h.Cut.InvokeAsync(h.Transfer.CompleteUpload);
        h.Footer("Close").Click();
        h.Cut.WaitForAssertion(() => Assert.Single(h.HandedOff));

        h.ImportPending = true;
        h.Reopen();

        Assert.Contains(CalendarImportCopy.PendingNote, h.Cut.Markup, StringComparison.Ordinal);
        Assert.Equal("true", h.Cut.Find("#" + ImportCalendarDialog.ImportButtonId).GetAttribute("aria-disabled"));
        await h.Pick();
        h.ClickImport();
        Assert.Single(h.Transfer.Uploads);

        h.Transfer.Respond(Ok(Imported(removed: 2)));
        await h.HandedOff[0];
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Compose, h.Phase));
        Assert.Empty(h.Cut.FindAll(".cal-imp-result"));
    }

    [Fact] // AC 15e: the typed fallback hands off on Escape (the modal's own close) the same way
    public async Task The_typed_fallback_hands_off_on_close()
    {
        await using var h = new Harness();
        h.Transfer.Holds = false;
        var pending = new TaskCompletionSource<ApiResult<IcsImportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Context.Calendar.Setup(c => c.ImportAsync(CalendarId, It.IsAny<ApiUpload>(), false, It.IsAny<CancellationToken>())).Returns(pending.Task);
        h.Start();
        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Server, h.Phase));

        // The header close button.
        h.Cut.Find(".cal-imp-dialog .ods-modal-x").Click();

        h.Cut.WaitForAssertion(() => Assert.Single(h.HandedOff));
        pending.SetResult(Ok(Imported()));
        Assert.True((await h.HandedOff[0]).IsSuccess);
        Assert.Equal(0, h.Changed);
    }

    [Fact] // §3 step 8: the picked file is forgotten on close
    public async Task Closing_forgets_the_picked_file()
    {
        await using var h = new Harness();
        h.Start();

        h.Footer("Cancel").Click();

        h.Cut.WaitForAssertion(() => Assert.Equal(1, h.Transfer.ForgetCalls));
        Assert.Contains(false, h.OpenChanges);
        Assert.Empty(h.HandedOff);
    }

    // ── Review round: focus, Escape anywhere, the remove warning, disposal ─────

    [Fact] // WCAG 2.4.3 (PR #524 review): Import goes away, so focus moves to the phase line
    public async Task Starting_the_import_moves_focus_to_the_footer_status()
    {
        await using var h = await UploadingAsync();

        h.Cut.WaitForAssertion(() => Assert.True(h.FocusedById(ImportCalendarDialog.FootStatusId)));
        Assert.Equal("-1", h.Cut.Find("#" + ImportCalendarDialog.FootStatusId).GetAttribute("tabindex"));
    }

    [Fact] // WCAG 2.4.3: the typed fallback enters the server phase directly, and focus follows
    public async Task Starting_a_typed_import_moves_focus_to_the_footer_status()
    {
        await using var h = new Harness();
        h.Transfer.Holds = false;
        h.Context.Calendar.Setup(c => c.ImportAsync(CalendarId, It.IsAny<ApiUpload>(), false, It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<ApiResult<IcsImportResult>>().Task);
        h.Start();
        await h.Pick();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.True(h.FocusedById(ImportCalendarDialog.FootStatusId)));
    }

    [Fact] // WCAG 2.4.3: Cancel upload goes away when the body has been sent, and focus follows to the phase line
    public async Task The_upload_finishing_moves_focus_to_the_footer_status()
    {
        await using var h = await UploadingAsync();
        h.Cut.WaitForAssertion(() => Assert.True(h.FocusedById(ImportCalendarDialog.FootStatusId)));
        var before = h.Js("odsFocusById").Count;

        await h.Cut.InvokeAsync(h.Transfer.CompleteUpload);

        h.Cut.WaitForAssertion(() => Assert.Contains(h.Js("odsFocusById").Skip(before), i => Equals(i.Arguments[0], ImportCalendarDialog.FootStatusId)));
        Assert.Equal(ImportCalendarDialog.Phase.Server, h.Phase);
    }

    [Fact] // WCAG 2.1.1 (PR #524 review): Escape is watched on the whole dialog, not only on its body and footer
    public async Task Escape_is_watched_on_the_whole_dialog()
    {
        await using var h = new Harness();
        h.Start();

        h.Cut.WaitForAssertion(() => Assert.NotEmpty(h.Js("odsDialogEscape.watch")));
        var watch = h.Js("odsDialogEscape.watch").First();
        Assert.Equal(ImportCalendarDialog.DialogClass, watch.Arguments[0]);
        Assert.IsType<Microsoft.JSInterop.DotNetObjectReference<ImportCalendarDialog>>(watch.Arguments[1]);
        Assert.Contains(ImportCalendarDialog.DialogClass, h.Cut.Find(".mud-dialog." + ImportCalendarDialog.DialogClass).ClassName, StringComparison.Ordinal);
    }

    [Fact] // WCAG 2.1.1: Escape means "confirm the stop" only while uploading
    public async Task Escape_opens_the_confirmation_only_while_uploading()
    {
        await using var h = new Harness();
        h.Start();
        await h.Escape();
        Assert.False(h.ConfirmOpen);

        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Uploading, h.Phase));
        await h.Escape();
        h.Cut.WaitForAssertion(() => Assert.True(h.ConfirmOpen));
        h.Cut.FindAll(".cal-imp-stop-dialog button").Single(b => Harness.Label(b) == "Keep uploading").Click();
        h.Cut.WaitForAssertion(() => Assert.False(h.ConfirmOpen));

        await h.Cut.InvokeAsync(h.Transfer.CompleteUpload);
        await h.Escape();
        Assert.False(h.ConfirmOpen);
        Assert.Empty(h.HandedOff); // the server phase's Escape is the dialog's own close, not this watch
    }

    [Fact] // AC 14a (test review): Escape inside the confirmation means Keep uploading and refocuses Cancel upload
    public async Task Escape_inside_the_confirmation_keeps_uploading_and_refocuses_cancel_upload()
    {
        await using var h = await UploadingAsync();
        h.Footer("Cancel upload").Click();
        h.Cut.WaitForAssertion(() => Assert.True(h.ConfirmOpen));
        var restoresBefore = h.Js("odsRestoreFocusOwner").Count;

        await h.MudEscape("cal-imp-stop-dialog");

        h.Cut.WaitForAssertion(() => Assert.False(h.ConfirmOpen));
        h.Cut.WaitForAssertion(() => Assert.Contains(h.Js("odsRestoreFocusOwner").Skip(restoresBefore),
            i => Equals(i.Arguments.FirstOrDefault(), ImportCalendarDialog.CancelUploadId)));
        Assert.Equal(ImportCalendarDialog.Phase.Uploading, h.Phase);
        Assert.Equal(0, h.Transfer.AbortCalls);
        Assert.Empty(h.OpenChanges);
    }

    [Fact] // AC 15e (test review): the typed fallback hands off on Escape too
    public async Task The_typed_fallback_hands_off_on_escape()
    {
        await using var h = new Harness();
        h.Transfer.Holds = false;
        var pending = new TaskCompletionSource<ApiResult<IcsImportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Context.Calendar.Setup(c => c.ImportAsync(CalendarId, It.IsAny<ApiUpload>(), false, It.IsAny<CancellationToken>())).Returns(pending.Task);
        h.Start();
        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Server, h.Phase));

        await h.MudEscape(ImportCalendarDialog.DialogClass);

        h.Cut.WaitForAssertion(() => Assert.Single(h.HandedOff));
        Assert.Contains(false, h.OpenChanges);
        pending.SetResult(Ok(Imported()));
        Assert.True((await h.HandedOff[0]).IsSuccess);
        Assert.Equal(0, h.Changed);
    }

    [Fact] // AC 10 (test review): closing the dialog and opening it again leaves the option off
    public async Task The_remove_option_resets_when_the_dialog_closes_and_reopens()
    {
        await using var h = new Harness();
        h.Start();
        h.Cut.Find("#" + ImportCalendarDialog.RemoveId).Change(true);
        Assert.True(h.Cut.Find("#" + ImportCalendarDialog.RemoveId).HasAttribute("checked"));

        h.Footer("Cancel").Click();
        h.Cut.WaitForAssertion(() => Assert.Contains(false, h.OpenChanges));
        h.Reopen();

        Assert.False(h.Cut.Find("#" + ImportCalendarDialog.RemoveId).HasAttribute("checked"));
        Assert.Equal(string.Empty, h.Cut.Find("#" + ImportCalendarDialog.RemoveWarningId).TextContent.Trim());
    }

    [Fact] // AC 7 + PR #524 review: a navigation mid-upload aborts the upload and releases the callback
    public async Task Disposing_mid_upload_aborts_the_upload_and_releases_the_callback()
    {
        await using var h = await UploadingAsync();
        var lifetime = h.Transfer.UploadToken;
        Assert.False(lifetime.IsCancellationRequested);

        h.Mounted = false;
        h.Rerender();

        h.Cut.WaitForAssertion(() => Assert.Equal(1, h.Transfer.AbortCalls));
        // The token NativeTransfer releases the DotNetObjectReference on (NativeTransferTests pins that half).
        Assert.True(lifetime.IsCancellationRequested);
        Assert.True(h.Transfer.ForgetCalls >= 1);
        Assert.Empty(h.Context.Toasts); // stopped before the body finished: nothing ran, so nothing is reported
    }

    [Fact] // PR #524 review: a navigation in the server phase still reports the outcome, through the app-wide snackbar
    public async Task Disposing_in_the_server_phase_still_toasts_the_outcome()
    {
        await using var h = await UploadingAsync();
        await h.Cut.InvokeAsync(h.Transfer.CompleteUpload);
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Server, h.Phase));

        h.Mounted = false;
        h.Rerender();
        Assert.Equal(0, h.Transfer.AbortCalls);
        Assert.Empty(h.Context.Toasts);

        h.Transfer.Respond(Ok(Imported(imported: 4, updated: 0, removed: 1)));

        await Harness.Eventually(() => h.Context.Toasts.Count == 1);
        Assert.Equal("Calendar import finished: 4 imported, 0 updated, 1 skipped, 1 removed.", h.Context.Toasts[0].Message);
        Assert.Empty(h.HandedOff);
    }

    [Fact] // PR #524 review: the server phase's failure after a navigation is a persistent toast
    public async Task Disposing_in_the_server_phase_toasts_a_failure_persistently()
    {
        await using var h = await UploadingAsync();
        await h.Cut.InvokeAsync(h.Transfer.CompleteUpload);

        h.Mounted = false;
        h.Rerender();
        h.Transfer.Respond(Fail<IcsImportResult>(System.Net.HttpStatusCode.InternalServerError, "Broke."));

        await Harness.Eventually(() => h.Context.Toasts.Count == 1);
        Assert.Equal(CalendarImportCopy.HandedOffFailure, h.Context.Toasts[0].Message);
        Assert.True(h.Context.Toasts[0].RequireInteraction);
    }

    [Fact] // WCAG 2.1.1: the watch is a capture listener on the document, scoped to the dialog, and removable
    public void The_escape_watch_script_listens_on_the_whole_dialog()
    {
        var html = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html"));
        var start = html.IndexOf("window.odsDialogEscape = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "window.odsDialogEscape is missing from index.html.");
        var script = html[start..html.IndexOf("window.revokeBlobUrl", start, StringComparison.Ordinal)];

        Assert.Contains("document.addEventListener('keydown', handler, true)", script, StringComparison.Ordinal);
        Assert.Contains("document.removeEventListener('keydown', handler, true)", script, StringComparison.Ordinal);
        Assert.Contains("closest('.mud-dialog-container')", script, StringComparison.Ordinal);
        Assert.Contains("invokeMethodAsync('OnDialogEscape')", script, StringComparison.Ordinal);
    }
}
