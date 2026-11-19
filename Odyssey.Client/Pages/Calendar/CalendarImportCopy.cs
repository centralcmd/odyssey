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

    /// <summary>The sentence a partially applied import adds after the server's own text.</summary>
    public static string PartialSentence(int committedEvents) =>
        $"{committedEvents} {(committedEvents == 1 ? "event from this file was" : "events from this file were")} "
        + "already imported or removed before the error. Importing the same file again is safe and completes it.";

    /// <summary>The committed-event count of a partially applied import, or null when the failure is any other.</summary>
    public static int? CommittedEvents(ApiProblem? problem) =>
        string.Equals(problem?.Code, PartiallyAppliedCode, StringComparison.Ordinal)
            ? problem!.TryGetExtensionValue<int>("committedEvents", out var committed) ? committed : 0
            : null;

    public static int Skipped(IcsImportResult result) => result.Skipped.Sum(g => g.Count);

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

    /// <summary>The result announcement: the counts, then the non-zero items (AX6, AC 12) — never a title.</summary>
    public static string Announcement(IcsImportResult result, bool canDelete) =>
        string.Join(' ', new[] { $"{Summary(result)}." }.Concat(ResultItems(result, canDelete)));

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
            : HandedOffFailure;
        snackbar.Add(text, Severity.Error, config => config.RequireInteraction = true, key);
    }
}
