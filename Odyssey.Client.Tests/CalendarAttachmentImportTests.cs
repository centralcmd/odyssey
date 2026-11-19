using System.Net;
using Bunit;
using Odyssey.Client.Pages.Calendar;
using Odyssey.Dtos.Journal;
using Xunit;
using static Odyssey.Client.Tests.CalendarIcsTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The import dialog's half of issue #522 (§3.5, v3 §D): the compose Warning and claim line, the attachment result
/// items and the attachment refusal. Each test names the acceptance criterion it proves.
/// </summary>
public class CalendarAttachmentImportTests
{
    static CalendarAttachmentImportTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private const string UriReason = "Linked (URI) attachments are not imported.";
    private const string UnresolvedReason = "Attached content is not in this file or this instance.";

    private static IcsImportResult WithAttachments() => new()
    {
        ImportedCount = 2,
        UpdatedCount = 1,
        StoredFileCount = 3,
        ReusedFileCount = 1,
        StoredPhotoCount = 1,
        ReusedPhotoCount = 2,
        ReplacedLinkSetCount = 2,
        RemovedLinkCount = 3,
        SkippedAttachmentCount = 5,
        IgnoredOccurrenceAttachmentCount = 1,
        Skipped =
        [
            new IcsImportSkipGroup { Reason = "Event is missing a title (SUMMARY).", Count = 1, SampleTitles = ["Untitled event"] },
            new IcsImportSkipGroup { Reason = UriReason, Count = 3 },
            new IcsImportSkipGroup { Reason = UnresolvedReason, Count = 2 },
        ],
    };

    private static async Task<ImportCalendarDialogTests.Harness> ImportedAsync(IcsImportResult result)
    {
        var h = new ImportCalendarDialogTests.Harness();
        h.Start();
        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Ok(result));
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportCalendarDialog.Phase.Result, h.Phase));
        return h;
    }

    [Fact] // AC 9: the Warning is visible before the picker, non-live, with a text prefix, and the picker names it
    public async Task The_warning_is_visible_and_referenced_from_the_picker()
    {
        await using var h = new ImportCalendarDialogTests.Harness();
        h.Start();

        var warning = h.Cut.Find("#" + ImportCalendarDialog.AttachWarningId);
        Assert.StartsWith("Note: attached documents and photos in the file are matched", warning.TextContent.Trim(), StringComparison.Ordinal);
        Assert.Contains("replace the event’s own", warning.TextContent, StringComparison.Ordinal);
        Assert.Equal("replace", warning.QuerySelectorAll("strong")[1].TextContent);
        Assert.Null(warning.QuerySelector("[role=alert], [role=status], [aria-live]"));
        Assert.Contains("warning", warning.QuerySelector(".mud-alert")!.ClassName, StringComparison.Ordinal);
        Assert.Equal(ImportCalendarDialog.AttachWarningId, h.Cut.Find(".odc-upload-drop").GetAttribute("aria-describedby"));
        Assert.Empty(h.Cut.FindAll("#" + ImportCalendarDialog.AttachClaimId));
        Assert.True(warning.CompareDocumentPosition(h.Cut.Find(".odc-upload-drop")).HasFlag(AngleSharp.Dom.DocumentPositions.Following));
    }

    [Fact] // AC 9 (S2, v3 §D): without the claims, the claim line shows and the picker names it too
    public async Task Without_the_claims_the_claim_line_shows_and_describes_the_picker()
    {
        await using var h = new ImportCalendarDialogTests.Harness { CanAddAttachments = false };
        h.Start();

        Assert.Equal("Files with attached documents or photos need permission to view and add them; such a file will be refused.",
            h.Cut.Find("#" + ImportCalendarDialog.AttachClaimId).TextContent.Replace("lock", string.Empty).Trim());
        Assert.Equal($"{ImportCalendarDialog.AttachWarningId} {ImportCalendarDialog.AttachClaimId}",
            h.Cut.Find(".odc-upload-drop").GetAttribute("aria-describedby"));
    }

    [Fact] // AC 9: the attachment items show under the focused result heading, after Spec B's, and in the announcement
    public async Task The_result_lists_the_attachment_items_and_announces_them()
    {
        await using var h = await ImportedAsync(WithAttachments());

        var items = h.Cut.FindAll(".cal-imp-items > li.cal-imp-item")
            .Select(li => li.ChildNodes.First().TextContent.Trim()).ToList();
        Assert.Equal(
        [
            "3 documents stored; 1 matched to a document already stored.",
            "1 photo added to the photo library; 2 matched to photos already stored.",
            "2 events or series had their attachments replaced (3 removed).",
            "5 attachments skipped.",
            "1 attachment on a single occurrence was ignored. Attachments belong to the series.",
        ], items);

        var reasons = h.Cut.FindAll(".cal-imp-item-reasons li").Select(li => (
            li.QuerySelector(".cal-imp-item-reason")!.TextContent, li.QuerySelector(".cal-imp-item-count")!.TextContent)).ToList();
        Assert.Equal([(UriReason, "3"), (UnresolvedReason, "2")], reasons);

        // The skipped-events tile and groups count events only; the attachment reasons are not event skips.
        Assert.Equal("1", h.Cut.Find(".cal-imp-stat.skip .cal-imp-stat-n").TextContent);
        Assert.Equal(["Event is missing a title (SUMMARY)."], h.Cut.FindAll(".cal-imp-skip-reason").Select(r => r.TextContent));

        h.Cut.WaitForAssertion(() => Assert.Equal(
            "2 imported, 1 updated, 1 skipped. 3 documents stored; 1 matched to a document already stored. "
            + "1 photo added to the photo library; 2 matched to photos already stored. "
            + "2 events or series had their attachments replaced (3 removed). 5 attachments skipped. "
            + "1 attachment on a single occurrence was ignored. Attachments belong to the series.",
            h.Announced));
    }

    [Fact] // AC 9: only the non-zero items show
    public async Task Only_non_zero_attachment_items_show()
    {
        await using var h = await ImportedAsync(new IcsImportResult { ImportedCount = 1, ReusedPhotoCount = 1 });

        Assert.Equal(["1 matched to a photo already stored."],
            h.Cut.FindAll(".cal-imp-items > li").Select(li => li.TextContent.Trim()));
    }

    [Fact] // AC 9: the attachments refusal shows its fixed copy in the error alert
    public async Task The_attachments_refusal_shows_its_copy()
    {
        await using var h = new ImportCalendarDialogTests.Harness();
        h.Start();
        await h.Pick();
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Single(h.Transfer.Uploads));
        h.Transfer.CompleteUpload();
        h.Transfer.Respond(Fail<IcsImportResult>(HttpStatusCode.Forbidden, "Forbidden.", CalendarImportCopy.AttachmentsForbiddenCode));

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Alert));
        Assert.Contains("This file has attached documents or photos you don’t have permission to add. Nothing was imported.",
            h.Alert!.TextContent, StringComparison.Ordinal);
    }

    [Fact] // SX7 / N3: the reasons are the server's fixed strings and counts only — never a sample title
    public void Attachment_items_carry_no_sample_title()
    {
        var result = WithAttachments();
        result.Skipped = [.. result.Skipped, new IcsImportSkipGroup { Reason = "Something else.", Count = 1, SampleTitles = ["Secret meeting"] }];

        var all = string.Join(' ', CalendarImportCopy.AttachmentItems(result).SelectMany(i => i.Reasons.Select(r => r.Reason).Prepend(i.Text)));
        Assert.DoesNotContain("Secret meeting", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Something else.", all, StringComparison.Ordinal);
    }

    [Fact] // §3.5: a result with no attachment skips keeps Spec B's grouping exactly
    public void Without_attachment_skips_every_group_is_an_event_group()
    {
        var result = new IcsImportResult { Skipped = [new IcsImportSkipGroup { Reason = "r", Count = 2 }] };

        Assert.Equal(2, CalendarImportCopy.Skipped(result));
        Assert.Single(CalendarImportCopy.EventSkipGroups(result));
    }
}
