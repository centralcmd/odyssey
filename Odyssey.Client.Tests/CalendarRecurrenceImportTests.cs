using System.Text.RegularExpressions;
using Bunit;
using Moq;
using Odyssey.ApiClient;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The import dialog's half of issue #520: the browser zone sent as <c>defaultTimeZoneId</c> on both paths and named in
/// the compose note, the result list of the new counts (announced with the summary, never a file-derived string), the
/// no-permission wording for a gated series, and the plain-text, capped skip titles. Test names carry the acceptance
/// criterion they prove.
/// </summary>
public class CalendarRecurrenceImportTests
{
    static CalendarRecurrenceImportTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static ImportCalendarDialogTests.Harness Harness(string? viewer, bool canDelete = true)
    {
        var h = new ImportCalendarDialogTests.Harness { CanDelete = canDelete };
        h.Context.Ctx.Services.AddSingletonViewer(viewer);
        return h.Start();
    }

    private static IcsImportResult Result(Action<IcsImportResult>? configure = null)
    {
        var r = new IcsImportResult
        {
            ImportedCount = 2,
            UpdatedCount = 1,
            Skipped = [new IcsImportSkipGroup { Reason = "Uses a time zone Odyssey doesn’t know.", Count = 1, SampleTitles = ["Standup on Mars"] }],
        };
        configure?.Invoke(r);
        return r;
    }

    // ── AC 11 ────────────────────────────────────────────────────────────────

    [Fact] // AC 11: the native path sends the browser zone, and the compose note names it
    public async Task The_native_import_sends_the_browser_zone_and_the_note_names_it()
    {
        await using var h = Harness("Europe/Oslo");

        Assert.Contains("Times without a time zone in the file are read as Europe/Oslo.", h.Cut.Find(".cal-imp-zone-note").TextContent, StringComparison.Ordinal);
        await h.Pick();
        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        Assert.Equal([new KeyValuePair<string, string>("defaultTimeZoneId", "Europe/Oslo")], h.Transfer.Uploads[0].Fields);
    }

    [Fact] // AC 11: the typed path sends it too
    public async Task The_typed_import_sends_the_browser_zone()
    {
        await using var h = Harness("Europe/Oslo");
        h.Transfer.Holds = false;
        h.Context.Calendar.Setup(c => c.ImportAsync(CalendarId, It.IsAny<ApiUpload>(), false, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Result()));
        await h.Pick();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => h.Context.Calendar.Verify(
            c => c.ImportAsync(CalendarId, It.IsAny<ApiUpload>(), false, "Europe/Oslo", It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact] // AC 11: without a browser zone the field is omitted and the note says floating times stay floating
    public async Task Without_a_browser_zone_no_field_is_sent_and_the_note_says_floating()
    {
        await using var h = Harness(viewer: null);

        Assert.Contains("Times without a time zone in the file are kept as floating times.", h.Cut.Find(".cal-imp-zone-note").TextContent, StringComparison.Ordinal);
        await h.Pick();
        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        Assert.Null(h.Transfer.Uploads[0].Fields);
    }

    // ── AC 12 ────────────────────────────────────────────────────────────────

    [Fact] // AC 12: the new counts render as list items, only non-zero ones, and are appended to the announcement
    public async Task The_new_counts_render_as_list_items_only_when_non_zero_and_are_announced()
    {
        await using var h = Harness("Europe/Oslo");
        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Ok(Result(r =>
        {
            r.AnySeriesRegenerated = true;
            r.OverridesApplied = 2;
            r.ExclusionsApplied = 1;
            r.DroppedOverrideCount = 1;
            r.DroppedExclusionCount = 2;
            r.AdjustedCount = 1;
        })));

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));
        var items = h.Cut.FindAll("ul.cal-imp-items > li").Select(li => li.TextContent.Trim()).ToList();
        Assert.Equal(
        [
            "2 separately edited occurrences applied.",
            "1 deleted occurrence applied.",
            "1 separately edited and 2 deleted occurrences discarded because their series' schedule changed.",
            "1 occurrence fell in a daylight-saving gap and was moved forward.",
        ], items);
        Assert.Contains(CalendarImportCopy.SeriesRegenerated, h.Cut.Markup, StringComparison.Ordinal);
        h.Cut.WaitForAssertion(() => Assert.Equal(
            "2 imported, 1 updated, 1 skipped. " + string.Join(' ', items), h.Announced));
        Assert.DoesNotContain("Mars", h.Announced, StringComparison.Ordinal);
    }

    [Fact] // AC 12: a result with none of the new counts renders no list
    public void A_result_without_the_new_counts_has_no_items() =>
        Assert.Empty(CalendarImportCopy.ResultItems(Result(), canDelete: true));

    [Fact] // AC 12 (v3 §H): skip-sample titles are plain text, capped at 120 characters
    public void A_skip_sample_title_is_capped_at_120_characters()
    {
        var shown = CalendarImportCopy.SampleTitle(new string('x', 500));

        Assert.Equal(120, shown.Length);
        Assert.EndsWith("…", shown, StringComparison.Ordinal);
        Assert.Equal("<b>Standup</b>", CalendarImportCopy.SampleTitle("<b>Standup</b>"));
    }

    /// <summary>
    /// AC 12 (SX4, N3): the import dialog renders file-derived text only as plain text — no <c>MarkupString</c>, no
    /// <c>innerHTML</c> — and its announcer never receives a sample title.
    /// </summary>
    [Fact]
    public void The_import_dialog_never_renders_markup_or_announces_a_sample_title()
    {
        var root = Path.Combine(ClientSource.Root, "Pages", "Calendar");
        var sources = new[] { "ImportCalendarDialog.razor", "ImportCalendarDialog.razor.cs", "CalendarImportCopy.cs" }
            .Select(f => File.ReadAllText(Path.Combine(root, f)))
            .ToList();

        Assert.All(sources, s => Assert.DoesNotMatch(new Regex(@"MarkupString|innerHTML|AddMarkupContent"), s));
        var announcements = sources.SelectMany(s => Regex.Matches(s, @"Announce\(([^;]*)\);").Select(m => m.Groups[1].Value)).ToList();
        Assert.NotEmpty(announcements);
        Assert.All(announcements, a => Assert.DoesNotContain("SampleTitle", a, StringComparison.Ordinal));
    }

    // ── AC 20 ────────────────────────────────────────────────────────────────

    [Fact] // AC 20: without calendar.delete a gated series is worded with the permission, and there is no checkbox
    public async Task Without_calendar_delete_a_gated_series_names_the_permission_and_no_checkbox_shows()
    {
        await using var h = Harness("Europe/Oslo", canDelete: false);
        Assert.Empty(h.Cut.FindAll("#" + ImportCalendarDialog.RemoveId));
        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Ok(Result(r => r.GatedSeriesCount = 1)));

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));
        Assert.Equal(
            "1 series left unchanged because applying it would remove events or discard edits, which needs permission to delete events.",
            h.Cut.Find("ul.cal-imp-items > li").TextContent.Trim());
        Assert.DoesNotContain("tick", h.Cut.Find("ul.cal-imp-items").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // AC 20: a delete holder reads the plain wording
    public void With_calendar_delete_a_gated_series_reads_plainly() =>
        Assert.Equal(["2 series left unchanged because applying them would remove events or discard edits."],
            CalendarImportCopy.ResultItems(Result(r => r.GatedSeriesCount = 2), canDelete: true));
}

internal static class ViewerRegistration
{
    /// <summary>Overrides the harness's viewer zone (the last registration wins).</summary>
    public static void AddSingletonViewer(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, string? viewer) =>
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, new Odyssey.Client.Services.ViewerTimeZone(viewer));
}
