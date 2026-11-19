using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The calendar import's outcome wording (issue #517 §3 Import flow steps 5–7), shared by the dialog — while it is
/// open — and the page, which owns the outcome once the dialog has handed a pending import off.
/// </summary>
internal static class CalendarImportCopy
{
    /// <summary>The problem code of an import that failed after at least one batch had committed (#516 §5).</summary>
    public const string PartiallyAppliedCode = "calendar_ics_import_partially_applied";

    public const string Importing = "Importing events…";

    public const string Stopped = "Upload stopped. Nothing was imported.";

    public const string FinishedWhileDeciding = "The upload finished while you were deciding; the server is now importing.";

    public const string UploadingStatus = "Uploading — this can take a while for a large file. Keep this tab open.";

    public const string ServerStatus =
        "Importing on the server… You can close this dialog; the result will appear as a notification.";

    public const string Interrupted =
        "The import was interrupted after the upload finished. Some events may already have been imported or removed. "
        + "Importing the same file again is safe.";

    public const string ConnectionLost =
        "The connection was lost while the server was importing. Some events may already have been imported or "
        + "removed. Importing the same file again is safe.";

    public const string HandedOffFailure =
        "The calendar import ended with an error. Some events may already have been imported or removed. Importing "
        + "the same file again is safe.";

    public const string PendingNote = "An import is still running on the server; its result will appear as a notification.";

    /// <summary>The <c>code</c> of an import refused for attachments the caller may not add (issue #521 §5.5).</summary>
    public const string AttachmentsForbiddenCode = "calendar_ics_import_attachments_forbidden";

    /// <summary>The error alert's text for that refusal (issue #522 §3.5): fixed, naming nothing from the file.</summary>
    public const string AttachmentsForbidden =
        "This file has attached documents or photos you don’t have permission to add. Nothing was imported.";

    /// <summary>
    /// The compose Warning's body, after its visible "Note:" prefix (issue #522 v3 §D), in two halves around the
    /// emphasised "replace" the design system bolds.
    /// </summary>
    public const string AttachmentsNoteLead =
        "attached documents and photos in the file are matched to ones already stored, so they are not stored twice. "
        + "For an event that already exists, the documents or photos the file actually brings in";

    public const string AttachmentsNoteReplace = "replace";

    public const string AttachmentsNoteTail =
        "the event’s own. Links to other services, such as Google Drive, are not imported and change nothing.";

    /// <summary>The claim line, for a caller without every read and create claim for documents and photos (v3 §D).</summary>
    public const string AttachmentsClaimLine =
        "Files with attached documents or photos need permission to view and add them; such a file will be refused.";

    /// <summary>The sentence a partially applied import adds after the server's own text.</summary>
    public static string PartialSentence(int committedEvents) =>
        $"{committedEvents} {(committedEvents == 1 ? "event from this file was" : "events from this file were")} "
        + "already imported or removed before the error. Importing the same file again is safe and completes it.";

    /// <summary>The committed-event count of a partially applied import, or null when the failure is any other.</summary>
    public static int? CommittedEvents(ApiProblem? problem) =>
        string.Equals(problem?.Code, PartiallyAppliedCode, StringComparison.Ordinal)
            ? problem!.TryGetExtensionValue<int>("committedEvents", out var committed) ? committed : 0
            : null;

    /// <summary>
    /// The skipped EVENTS. <see cref="IcsImportResult.Skipped"/> also counts every skipped attachment under its reason
    /// (issue #521 §5.5), and those are reported as their own result item instead, so they are taken out here.
    /// </summary>
    public static int Skipped(IcsImportResult result) =>
        Math.Max(0, result.Skipped.Sum(g => g.Count) - result.SkippedAttachmentCount);

    /// <summary>
    /// The skip groups that are attachment reasons: they carry no sample title, while every event skip carries at least
    /// one (the per-reason sample setting is at least 1). Only a result that skipped attachments has any.
    /// </summary>
    public static bool IsAttachmentGroup(IcsImportResult result, IcsImportSkipGroup group) =>
        result.SkippedAttachmentCount > 0 && group.SampleTitles.Count == 0;

    /// <summary>The event skip groups — what "Skipped events, by reason" lists.</summary>
    public static IEnumerable<IcsImportSkipGroup> EventSkipGroups(IcsImportResult result) =>
        result.Skipped.Where(g => g.Count > 0 && !IsAttachmentGroup(result, g));

    /// <summary>An attachment result item; <paramref name="Reasons"/> are the grouped skip reasons under the skipped one.</summary>
    public sealed record AttachmentItem(string Text, IReadOnlyList<(string Reason, int Count)> Reasons);

    /// <summary>
    /// The attachment result items (issue #522 §3.5, v3 §D): fixed text and counts, only the non-zero ones, in real
    /// plurals; the skip reasons are the server's fixed strings with their counts and never a sample title (SX7).
    /// </summary>
    public static IReadOnlyList<AttachmentItem> AttachmentItems(IcsImportResult r)
    {
        var items = new List<AttachmentItem>();
        if (r.StoredFileCount > 0 || r.ReusedFileCount > 0)
        {
            var parts = new List<string>();
            if (r.StoredFileCount > 0)
                parts.Add($"{r.StoredFileCount} {Plural(r.StoredFileCount, "document", "documents")} stored");
            if (r.ReusedFileCount > 0)
                parts.Add($"{r.ReusedFileCount} matched to {Plural(r.ReusedFileCount, "a document", "documents")} already stored");
            items.Add(new($"{string.Join("; ", parts)}.", []));
        }

        if (r.StoredPhotoCount > 0 || r.ReusedPhotoCount > 0)
        {
            var parts = new List<string>();
            if (r.StoredPhotoCount > 0)
                parts.Add($"{r.StoredPhotoCount} {Plural(r.StoredPhotoCount, "photo", "photos")} added to the photo library");
            if (r.ReusedPhotoCount > 0)
                parts.Add($"{r.ReusedPhotoCount} matched to {Plural(r.ReusedPhotoCount, "a photo", "photos")} already stored");
            items.Add(new($"{string.Join("; ", parts)}.", []));
        }

        if (r.ReplacedLinkSetCount > 0)
            items.Add(new($"{r.ReplacedLinkSetCount} {Plural(r.ReplacedLinkSetCount, "event or series had its", "events or series had their")} attachments replaced ({r.RemovedLinkCount} removed).", []));

        if (r.SkippedAttachmentCount > 0)
        {
            var reasons = r.Skipped.Where(g => g.Count > 0 && IsAttachmentGroup(r, g)).Select(g => (g.Reason, g.Count)).ToList();
            items.Add(new($"{r.SkippedAttachmentCount} {Plural(r.SkippedAttachmentCount, "attachment", "attachments")} skipped.", reasons));
        }

        if (r.IgnoredOccurrenceAttachmentCount > 0)
            items.Add(new($"{r.IgnoredOccurrenceAttachmentCount} {Plural(r.IgnoredOccurrenceAttachmentCount, "attachment on a single occurrence was", "attachments on single occurrences were")} ignored. Attachments belong to the series.", []));

        return items;
    }

    /// <summary>The longest a skip-sample title is shown (issue #520 v3 §H): longer ones are cut, with an ellipsis.</summary>
    public const int SampleTitleMaxLength = 120;

    /// <summary>The checkbox for <c>allowRemovals</c> (issue #520 v3 §H, v4 §5; the final name of #517's option).</summary>
    public const string AllowRemovalsLabel = "Allow removals";

    public const string AllowRemovalsHelp =
        "Remove events the file marks as cancelled, and apply series changes that would discard separately edited or deleted occurrences.";

    public const string AllowRemovalsWarning =
        "This import can delete events and discard separately edited or deleted occurrences.";

    public const string RemovalsNote =
        "Removing events is never automatic: tick Allow removals to remove events this file marks as cancelled.";

    /// <summary>The reworded regenerated-series alert (issue #520 FR7, v3 §H).</summary>
    public const string SeriesRegenerated =
        "Updating recurring series replaced their future occurrences. Separately edited or deleted occurrences that still fall on the schedule were kept; the rest are counted below.";

    /// <summary>The compose note's zone line (issue #520 §3, FR15): the zone floating times are read in, or that none is.</summary>
    public static string ZoneNote(string? defaultZone) => defaultZone is null
        ? "Times without a time zone in the file are kept as floating times."
        : $"Times without a time zone in the file are read as {defaultZone}.";

    /// <summary>A skip-sample title as shown: plain text, at most <see cref="SampleTitleMaxLength"/> characters.</summary>
    public static string SampleTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Untitled event"
        : title.Length <= SampleTitleMaxLength ? title
        : string.Concat(title.AsSpan(0, SampleTitleMaxLength - 1), "…");

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;

    /// <summary>
    /// The result list's items (issue #520 §3 "Result notes", v3 §H, v6 F4): fixed text and counts, only the non-zero
    /// ones, never a value from the file. A gated series is worded for a caller without <c>calendar.delete</c> without
    /// naming a control they cannot see.
    /// </summary>
    public static IReadOnlyList<string> ResultItems(IcsImportResult r, bool canDelete)
    {
        var items = new List<string>();
        if (r.OverridesApplied > 0)
            items.Add($"{r.OverridesApplied} separately edited {Plural(r.OverridesApplied, "occurrence", "occurrences")} applied.");
        if (r.ExclusionsApplied > 0)
            items.Add($"{r.ExclusionsApplied} deleted {Plural(r.ExclusionsApplied, "occurrence", "occurrences")} applied.");
        var dropped = r.DroppedOverrideCount + r.DroppedExclusionCount;
        if (dropped > 0)
        {
            var parts = string.Join(" and ", new[]
            {
                r.DroppedOverrideCount > 0 ? $"{r.DroppedOverrideCount} separately edited" : null,
                r.DroppedExclusionCount > 0 ? $"{r.DroppedExclusionCount} deleted" : null,
            }.Where(p => p is not null));
            items.Add($"{parts} {Plural(dropped, "occurrence", "occurrences")} discarded because {Plural(dropped, "its", "their")} series' schedule changed.");
        }

        if (r.GatedSeriesCount > 0)
        {
            var gated = $"{r.GatedSeriesCount} series left unchanged because applying {Plural(r.GatedSeriesCount, "it", "them")} would remove events or discard edits";
            items.Add(canDelete ? $"{gated}." : $"{gated}, which needs permission to delete events.");
        }

        if (r.AdjustedCount > 0)
            items.Add($"{r.AdjustedCount} {Plural(r.AdjustedCount, "occurrence", "occurrences")} fell in a daylight-saving gap and {Plural(r.AdjustedCount, "was", "were")} moved forward.");

        return items;
    }

    /// <summary>
    /// The result announcement: the counts, then the non-zero items (AX6, AC 12), then the attachment items after Spec B's
    /// (issue #522 §3.5) — never a title, and never a skip reason.
    /// </summary>
    public static string Announcement(IcsImportResult result, bool canDelete) =>
        string.Join(' ', new[] { $"{Summary(result)}." }.Concat(ResultItems(result, canDelete)).Concat(AttachmentItems(result).Select(i => i.Text)));

    /// <summary>"‹i› imported, ‹u› updated, ‹s› skipped[, ‹r› removed]" — counts only, never a title.</summary>
    public static string Summary(IcsImportResult result) =>
        $"{result.ImportedCount} imported, {result.UpdatedCount} updated, {Skipped(result)} skipped"
        + (result.RemovedCount > 0 ? $", {result.RemovedCount} removed" : string.Empty);

    /// <summary>
    /// The one toast for a handed-off import (§3 step 6): 10 s with the counts on success; persistent otherwise —
    /// the partial sentence after the server's text, or the general failure wording. Keyed uniquely, so two imports
    /// that end alike each show theirs.
    /// </summary>
    public static void ToastHandedOff(ISnackbar snackbar, ApiResult<IcsImportResult> outcome)
    {
        var key = Guid.NewGuid().ToString("N");
        if (outcome.IsSuccess && outcome.Value is { } result)
        {
            snackbar.Add($"Calendar import finished: {Summary(result)}.", Severity.Success,
                config => config.VisibleStateDuration = (int)CalendarIcsExport.SuccessToastDuration.TotalMilliseconds, key);
            return;
        }

        var text = CommittedEvents(outcome.Problem) is { } committed
            ? $"{outcome.Error} {PartialSentence(committed)}"
            : string.Equals(outcome.Problem?.Code, AttachmentsForbiddenCode, StringComparison.Ordinal)
                ? AttachmentsForbidden
                : HandedOffFailure;
        snackbar.Add(text, Severity.Error, config => config.RequireInteraction = true, key);
    }
}
