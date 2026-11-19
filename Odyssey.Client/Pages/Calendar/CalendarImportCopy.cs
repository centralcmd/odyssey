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
