using MudBlazor;
using Odyssey.Client.Services;
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

    /// <summary>E1 and E2: the aggregate export. It never carries attachments, so its toast is unchanged (issue #522 AC 8).</summary>
    public static string AllToast(CalendarIcsExportSummary summary) => $"Exporting {What(summary)}. {DownloadStarted}";

    /// <summary>E3: one calendar; <paramref name="attachments"/> is <see cref="AttachmentPart"/> or empty.</summary>
    public static string CalendarToast(string calendarName, CalendarIcsExportSummary summary, string attachments = "") =>
        $"Exporting “{calendarName}”: {What(summary)}{attachments}. {DownloadStarted}";

    /// <summary>E4/E5: one event.</summary>
    public static string EventToast(string? eventTitle, string attachments = "") =>
        $"Exporting “{TitleOrDefault(eventTitle)}”{attachments}. {DownloadStarted}";

    /// <summary>E4/E5: a series.</summary>
    public static string SeriesToast(string? seriesTitle, CalendarIcsExportSummary summary, string attachments = "") =>
        $"Exporting the series “{TitleOrDefault(seriesTitle)}” ({Events(summary.OccurrenceCount)}){attachments}. {DownloadStarted}";

    // ── Attachments (issue #522 §3.4, v3 §A, v4 "Export flow details") ─────────

    /// <summary>The <c>code</c> of a summary or export refused for attachments the caller may not read (#521 §5.4).</summary>
    public const string AttachmentsForbiddenCode = "calendar_ics_export_attachments_forbidden";

    /// <summary>A toast longer than this stays until dismissed (AX7) — the attachment toasts only.</summary>
    public const int PersistentToastLength = 120;

    /// <summary>Added to an export's toast when its attachments were left out.</summary>
    public const string NotIncluded = "Attachments were not included.";

    public const string IncludeAttachments = "Include attachments";

    public const string ExifAdvisory = "Attached photos are exported as originals, including any location data in them.";

    /// <summary>
    /// " with 3 documents and 2 photos (4.1 MB of attachments)" — each kind only when non-zero, in real plurals, the
    /// size covering both (S3); empty when nothing is attached.
    /// </summary>
    public static string AttachmentPart(CalendarIcsExportSummary summary)
    {
        var kinds = Kinds(summary);
        if (kinds.Count == 0)
            return string.Empty;
        var size = ByteSize.Format(summary.AttachmentBytes);
        return size.Length == 0 ? $" with {string.Join(" and ", kinds)}" : $" with {string.Join(" and ", kinds)} ({size} of attachments)";
    }

    /// <summary>"Include attachments (3 documents, 2 photos, 4.1 MB)".</summary>
    public static string IncludeAttachmentsLabel(CalendarIcsExportSummary summary) =>
        $"{IncludeAttachments} ({string.Join(", ", Kinds(summary).Append(ByteSize.Format(summary.AttachmentBytes)).Where(p => p.Length > 0))})";

    public static string AttachmentsForbiddenHelp(string noun) => $"You don't have permission to export this {noun}'s attachments.";

    public static string AttachmentsForbiddenWarning(string noun) =>
        $"This {noun} has attachments you don't have permission to export. You can export it without them.";

    public static string AttachmentsForbiddenAnnouncement(string noun) =>
        $"This {noun} has attachments you can't export. You can export it without them.";

    /// <summary>The commit step's subtitle: "One event, as an iCalendar (.ics) file." or "3 events, …, as …".</summary>
    public static string WhatFile(string noun, CalendarIcsExportSummary summary)
    {
        if (noun == "event")
            return "One event, as an iCalendar (.ics) file.";
        var what = What(summary);
        return $"{char.ToUpperInvariant(what[0])}{what[1..]}, as an iCalendar (.ics) file.";
    }

    /// <summary>
    /// The toast of an export that went through the attachments choice: the attachment part with
    /// <see cref="IcsAttachmentsMode.Include"/>, the plain copy plus <see cref="NotIncluded"/> without.
    /// </summary>
    public static string WithAttachments(Func<string, string> toast, CalendarIcsExportSummary summary, IcsAttachmentsMode mode) =>
        mode == IcsAttachmentsMode.Include ? toast(AttachmentPart(summary)) : $"{toast(string.Empty)} {NotIncluded}";

    /// <summary>Spec A's channel, with the length rule for an attachment toast: over 120 characters it is persistent.</summary>
    public static void ToastStartedScaled(ISnackbar snackbar, string text)
    {
        if (text.Length <= PersistentToastLength)
        {
            ToastStarted(snackbar, text);
            return;
        }

        snackbar.Add(text, Severity.Success, config => config.RequireInteraction = true, Guid.NewGuid().ToString("N"));
    }

    private static List<string> Kinds(CalendarIcsExportSummary summary)
    {
        var kinds = new List<string>();
        if (summary.AttachmentCount > 0)
            kinds.Add($"{summary.AttachmentCount} {(summary.AttachmentCount == 1 ? "document" : "documents")}");
        if (summary.PhotoCount > 0)
            kinds.Add($"{summary.PhotoCount} {(summary.PhotoCount == 1 ? "photo" : "photos")}");
        return kinds;
    }

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
