using MudBlazor;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The words and the success toast every calendar <c>.ics</c> export shares (issue #517 §3 "Export flow", §3.1).
/// Each export asks the server for a summary first, then lets the browser download the file itself, so a failure
/// after the download starts shows only in the browser's downloads list — the toast says so.
/// </summary>
internal static class CalendarIcsExport
{
    public const string Preparing = "Preparing calendar export…";

    public const string AlreadyPreparing = "An export is already being prepared.";

    private const string DownloadStarted =
        "Your browser has started the download — if it does not finish, your browser's downloads list shows why.";

    /// <summary>How long a started-export toast stays (§6.1: 10 s for success).</summary>
    public static readonly TimeSpan SuccessToastDuration = TimeSpan.FromSeconds(10);

    /// <summary>"no events", "3 events" or "12 events, including 2 recurring series".</summary>
    public static string What(CalendarIcsExportSummary summary)
    {
        if (summary.OccurrenceCount == 0)
        {
            return "no events";
        }

        var what = Events(summary.OccurrenceCount);
        return summary.SeriesCount > 0 ? $"{what}, including {summary.SeriesCount} recurring series" : what;
    }

    /// <summary>E1 and E2: the aggregate export.</summary>
    public static string AllToast(CalendarIcsExportSummary summary) => $"Exporting {What(summary)}. {DownloadStarted}";

    /// <summary>E3: one calendar.</summary>
    public static string CalendarToast(string calendarName, CalendarIcsExportSummary summary) =>
        $"Exporting “{calendarName}”: {What(summary)}. {DownloadStarted}";

    /// <summary>E4/E5: one event.</summary>
    public static string EventToast(string? eventTitle) => $"Exporting “{TitleOrDefault(eventTitle)}”. {DownloadStarted}";

    /// <summary>E4/E5: a series.</summary>
    public static string SeriesToast(string? seriesTitle, CalendarIcsExportSummary summary) =>
        $"Exporting the series “{TitleOrDefault(seriesTitle)}” ({Events(summary.OccurrenceCount)}). {DownloadStarted}";

    /// <summary>
    /// The started-export toast: 10 s, and keyed uniquely, so a second identical export in a row shows its own
    /// toast instead of being swallowed as a duplicate of the first (AC 4).
    /// </summary>
    public static void ToastStarted(ISnackbar snackbar, string text) =>
        snackbar.Add(text, Severity.Success,
            config => config.VisibleStateDuration = (int)SuccessToastDuration.TotalMilliseconds,
            Guid.NewGuid().ToString("N"));

    private static string Events(int count) => $"{count} {(count == 1 ? "event" : "events")}";

    private static string TitleOrDefault(string? title) => string.IsNullOrWhiteSpace(title) ? "Untitled event" : title;
}
