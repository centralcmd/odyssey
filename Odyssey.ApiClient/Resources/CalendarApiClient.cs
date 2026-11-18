using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the calendar endpoints (issue #323). Writes to all three resources (calendars,
/// events, recurrence patterns) go through <see cref="IOdysseyApi"/> at the dialog call sites, as
/// every other module's client does. ICS import/export (issue #330) use the transport core's file and
/// multipart helpers, since a <c>text/calendar</c> body is not JSON.
/// </summary>
/// <remarks>
/// Every method returns an <see cref="ApiResult{T}"/> rather than presenting its own errors — the
/// export paths used to raise MudBlazor toasts from inside this client, which is exactly the coupling
/// that kept it out of a shared library. Callers decide between a toast and an inline dialog error.
/// </remarks>
public interface ICalendarApiClient
{
    /// <summary>Lists every calendar (unwindowed — used for the legend, the event dialog's calendar
    /// picker, and Manage Calendars).</summary>
    Task<ApiResult<List<ExistingCalendar>>> ListCalendarsAsync(CancellationToken ct = default);

    /// <summary>Lists events overlapping the [from, to) window (max 92 days, enforced server-side).</summary>
    Task<ApiResult<List<ExistingCalendarEvent>>> ListEventsAsync(DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// Lists the events overlapping the <c>[from, to)</c> instants, matching and ordering floating events by their
    /// wall-clock in <paramref name="viewTimeZoneId"/> (issue #519 v3 §A); null is <c>UTC</c>.
    /// </summary>
    Task<ApiResult<List<ExistingCalendarEvent>>> ListEventsAsync(
        DateTime from, DateTime to, string? viewTimeZoneId, CancellationToken ct = default);

    /// <summary>Loads one event. Null on failure.</summary>
    Task<ExistingCalendarEvent?> GetEventAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads one recurrence pattern (the series template/rule). Null on failure.</summary>
    Task<ExistingRecurrencePattern?> GetPatternAsync(Guid id, CancellationToken ct = default);

    /// <summary>Fetches a calendar's events as an <c>.ics</c> file. A non-success status yields a
    /// failure result rather than a body that would download as a fake <c>.ics</c>. For a non-browser consumer: the
    /// browser summarizes with <see cref="SummarizeExportAsync"/> and downloads <see cref="ExportUrl"/> itself
    /// (issue #516), because a calendar export can be gigabytes.</summary>
    Task<ApiResult<ApiFile>> ExportAsync(Guid calendarId, CancellationToken ct = default);

    /// <summary>Runs the calendar export's checks and reports its counts without writing it (issue #516 §5).</summary>
    Task<ApiResult<CalendarIcsExportSummary>> SummarizeExportAsync(Guid calendarId, CancellationToken ct = default);

    /// <summary>The absolute URL of the calendar export, for the browser to download itself.</summary>
    string ExportUrl(Guid calendarId);

    /// <summary>Imports an <c>.ics</c> file into an existing calendar (multipart). With
    /// <paramref name="allowRemovals"/> an event or series the file marks cancelled is removed, which needs
    /// <c>calendar.delete</c> (issue #516 §8.4).</summary>
    Task<ApiResult<IcsImportResult>> ImportAsync(
        Guid calendarId, ApiUpload file, bool allowRemovals = false, CancellationToken ct = default);

    /// <summary>
    /// Imports an <c>.ics</c> file, reading its floating times in <paramref name="defaultTimeZoneId"/> when one is sent
    /// (issue #519 §5.7). <paramref name="allowRemovals"/> also lets a series change remove stored rows or discard
    /// stored exceptions, which needs <c>calendar.delete</c>.
    /// </summary>
    Task<ApiResult<IcsImportResult>> ImportAsync(
        Guid calendarId, ApiUpload file, bool allowRemovals, string? defaultTimeZoneId, CancellationToken ct = default);

    /// <summary>The absolute URL of a calendar's import, for the browser to upload the picked file itself. The
    /// multipart body carries the file as <c>file</c> and, optionally, <c>allowRemovals=true</c>.</summary>
    string ImportUrl(Guid calendarId);

    /// <summary>Exports a single event as a standalone <c>.ics</c> VEVENT (issue #340).</summary>
    Task<ApiResult<ApiFile>> ExportEventAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>Runs the single-event export's checks and reports its counts (issue #516 §5).</summary>
    Task<ApiResult<CalendarIcsExportSummary>> SummarizeEventExportAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>The absolute URL of the single-event export.</summary>
    string ExportEventUrl(Guid eventId);

    /// <summary>Exports a recurring series as one RRULE VEVENT, or its per-occurrence fallback (issue #340).</summary>
    Task<ApiResult<ApiFile>> ExportPatternAsync(Guid patternId, CancellationToken ct = default);

    /// <summary>Runs the series export's checks and reports its counts (issue #516 §5).</summary>
    Task<ApiResult<CalendarIcsExportSummary>> SummarizePatternExportAsync(Guid patternId, CancellationToken ct = default);

    /// <summary>The absolute URL of the series export.</summary>
    string ExportPatternUrl(Guid patternId);

    // Issue #521 §5.4: the three exports that carry attachments take attachments=include|omit. The defaults keep a test
    // double that implements only the original members compiling; the real client overrides all six.

    /// <summary>The calendar export's summary under <paramref name="attachments"/>.</summary>
    Task<ApiResult<CalendarIcsExportSummary>> SummarizeExportAsync(
        Guid calendarId, IcsAttachmentsMode attachments, CancellationToken ct = default) =>
        attachments == IcsAttachmentsMode.Include ? SummarizeExportAsync(calendarId, ct) : throw new NotSupportedException();

    /// <summary>The calendar export's URL under <paramref name="attachments"/>.</summary>
    string ExportUrl(Guid calendarId, IcsAttachmentsMode attachments) =>
        attachments == IcsAttachmentsMode.Include ? ExportUrl(calendarId) : throw new NotSupportedException();

    /// <summary>The single-event export's summary under <paramref name="attachments"/>.</summary>
    Task<ApiResult<CalendarIcsExportSummary>> SummarizeEventExportAsync(
        Guid eventId, IcsAttachmentsMode attachments, CancellationToken ct = default) =>
        attachments == IcsAttachmentsMode.Include ? SummarizeEventExportAsync(eventId, ct) : throw new NotSupportedException();

    /// <summary>The single-event export's URL under <paramref name="attachments"/>.</summary>
    string ExportEventUrl(Guid eventId, IcsAttachmentsMode attachments) =>
        attachments == IcsAttachmentsMode.Include ? ExportEventUrl(eventId) : throw new NotSupportedException();

    /// <summary>The series export's summary under <paramref name="attachments"/>.</summary>
    Task<ApiResult<CalendarIcsExportSummary>> SummarizePatternExportAsync(
        Guid patternId, IcsAttachmentsMode attachments, CancellationToken ct = default) =>
        attachments == IcsAttachmentsMode.Include ? SummarizePatternExportAsync(patternId, ct) : throw new NotSupportedException();

    /// <summary>The series export's URL under <paramref name="attachments"/>.</summary>
    string ExportPatternUrl(Guid patternId, IcsAttachmentsMode attachments) =>
        attachments == IcsAttachmentsMode.Include ? ExportPatternUrl(patternId) : throw new NotSupportedException();

    /// <summary>
    /// The aggregate export's summary with the window read in <paramref name="viewTimeZoneId"/> (issue #519 v3 §A):
    /// a floating event matches by its wall-clock in that zone. Null is <c>UTC</c>.
    /// </summary>
    Task<ApiResult<CalendarIcsExportSummary>> SummarizeAggregateExportAsync(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search, string? viewTimeZoneId,
        CancellationToken ct = default);

    /// <summary>The absolute URL of the aggregate export with the window read in <paramref name="viewTimeZoneId"/>.</summary>
    string ExportAggregateUrl(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search, string? viewTimeZoneId);

    /// <summary>Exports every matching event across every calendar the caller can read, as one
    /// multi-VEVENT <c>.ics</c> file, optionally filtered by date range / calendar / search term (issue
    /// #340). Omitting <paramref name="from"/>/<paramref name="to"/> and <paramref name="search"/>
    /// exports everything (subject to the server's caps).</summary>
    Task<ApiResult<ApiFile>> ExportAggregateAsync(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search,
        CancellationToken ct = default);

    /// <summary>Runs the aggregate export's checks for the same query and reports its counts (issue #516 §5).</summary>
    Task<ApiResult<CalendarIcsExportSummary>> SummarizeAggregateExportAsync(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search,
        CancellationToken ct = default);

    /// <summary>The absolute URL of the aggregate export for the same query.</summary>
    string ExportAggregateUrl(DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search);

    Task<ApiResult> CreateCalendarAsync(NewCalendar calendar, CancellationToken ct = default);

    /// <summary>Updates a calendar. The API takes the same shape as create; there is no separate
    /// update DTO for this resource.</summary>
    Task<ApiResult> UpdateCalendarAsync(Guid id, NewCalendar calendar, CancellationToken ct = default);

    Task<ApiResult> DeleteCalendarAsync(Guid id, CancellationToken ct = default);

    // ── Events ───────────────────────────────────────────────────────────────

    Task<ApiResult> CreateEventAsync(NewCalendarEvent calendarEvent, CancellationToken ct = default);

    Task<ApiResult> UpdateEventAsync(Guid id, NewCalendarEvent calendarEvent, CancellationToken ct = default);

    Task<ApiResult> DeleteEventAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Deletes one occurrence and reads what keying its legacy series detached (issue #519 v5 §3). The server answers
    /// <c>200</c> with a <see cref="CalendarEventDeleteResult"/>; a <c>null</c> value means an older server's <c>204</c>.
    /// </summary>
    Task<ApiResult<CalendarEventDeleteResult?>> DeleteEventWithResultAsync(Guid id, CancellationToken ct = default);

    // ── Recurrence patterns ──────────────────────────────────────────────────
    // The series template. Editing a pattern regenerates its occurrences server-side, which is why
    // the calendar page reloads its window after any pattern write.

    Task<ApiResult> CreatePatternAsync(NewRecurrencePattern pattern, CancellationToken ct = default);

    Task<ApiResult> UpdatePatternAsync(Guid id, NewRecurrencePattern pattern, CancellationToken ct = default);

    /// <summary>
    /// Updates a series and reads what it dropped (issue #519 §5): send <see cref="NewRecurrencePattern.ExpectedVersion"/>
    /// and <see cref="NewRecurrencePattern.ConfirmedDrops"/> to bind the write to what the user saw; a mismatch is a
    /// <c>409</c> whose <c>code</c> is <c>calendar-series-version-conflict</c> or <c>calendar-series-drops-changed</c>.
    /// </summary>
    Task<ApiResult<RecurrencePatternUpdateResult>> UpdatePatternWithResultAsync(
        Guid id, NewRecurrencePattern pattern, CancellationToken ct = default);

    /// <summary>What a series update with the same body would drop, without saving (issue #519 §5 endpoint 4).</summary>
    Task<ApiResult<RecurrencePatternPreview>> PreviewPatternAsync(Guid id, NewRecurrencePattern pattern, CancellationToken ct = default);

    Task<ApiResult> DeletePatternAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="ICalendarApiClient" />
public sealed class CalendarApiClient(IOdysseyApi api) : ICalendarApiClient
{
    /// <summary>
    /// The shipped default (and ceiling) for <c>CalendarIcsMaxImportMegabytes</c> (issue #516) — the real,
    /// effective cap is dynamic (System Settings, issue #343) and read via <c>IImportLimitsApiClient</c>; this
    /// constant exists solely as <c>ImportLimitsCache.Fallback</c>'s input for when that read fails.
    /// </summary>
    public const long MaxImportBytes = SystemSettingsBounds.CalendarIcsMaxImportMegabytesMax * 1024L * 1024;

    private const string CalendarsBase = "api/calendars";
    private const string EventsBase = "api/calendar-events";
    private const string PatternsBase = "api/recurrence-patterns";
    private const string CompletenessMarker = "BEGIN:VEVENT";

    public Task<ApiResult<List<ExistingCalendar>>> ListCalendarsAsync(CancellationToken ct = default) =>
        api.GetAllAsync<ExistingCalendar>(PagedQuery.For(CalendarsBase).Build(), ct);

    public Task<ApiResult<List<ExistingCalendarEvent>>> ListEventsAsync(DateTime from, DateTime to, CancellationToken ct = default) =>
        api.GetAllAsync<ExistingCalendarEvent>(
            PagedQuery.For(EventsBase).Add("from", from).Add("to", to).Build(), ct);

    public Task<ApiResult<List<ExistingCalendarEvent>>> ListEventsAsync(
        DateTime from, DateTime to, string? viewTimeZoneId, CancellationToken ct = default)
    {
        var query = PagedQuery.For(EventsBase).Add("from", from).Add("to", to);
        if (!string.IsNullOrWhiteSpace(viewTimeZoneId))
        {
            query = query.Add("viewTimeZoneId", viewTimeZoneId);
        }

        return api.GetAllAsync<ExistingCalendarEvent>(query.Build(), ct);
    }

    public async Task<ExistingCalendarEvent?> GetEventAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingCalendarEvent>($"{EventsBase}/{id}", ct)).Value;

    public async Task<ExistingRecurrencePattern?> GetPatternAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingRecurrencePattern>($"{PatternsBase}/{id}", ct)).Value;

    // Every calendar export streams since issue #516 and declares X-Odyssey-Export-Rows, so each download checks its
    // VEVENT count against it: a connection aborted at the byte cap fails rather than saving a short file.
    public Task<ApiResult<ApiFile>> ExportAsync(Guid calendarId, CancellationToken ct = default) =>
        api.GetFileAsync($"{CalendarsBase}/{calendarId}/ics", "calendar.ics", completenessMarker: CompletenessMarker, ct: ct);

    public Task<ApiResult<CalendarIcsExportSummary>> SummarizeExportAsync(Guid calendarId, CancellationToken ct = default) =>
        api.GetAsync<CalendarIcsExportSummary>($"{CalendarsBase}/{calendarId}/ics/summary", ct);

    public string ExportUrl(Guid calendarId) => Absolute($"{CalendarsBase}/{calendarId}/ics");

    public Task<ApiResult<IcsImportResult>> ImportAsync(
        Guid calendarId, ApiUpload file, bool allowRemovals = false, CancellationToken ct = default) =>
        api.UploadAsync<IcsImportResult>(
            $"{CalendarsBase}/{calendarId}/ics",
            string.IsNullOrWhiteSpace(file.FileName) ? file with { FileName = "calendar.ics" } : file,
            contentTypeOverride: "text/calendar",
            ct: ct,
            fields: allowRemovals ? [new KeyValuePair<string, string>("allowRemovals", "true")] : null);

    public Task<ApiResult<IcsImportResult>> ImportAsync(
        Guid calendarId, ApiUpload file, bool allowRemovals, string? defaultTimeZoneId, CancellationToken ct = default)
    {
        var fields = new List<KeyValuePair<string, string>>();
        if (allowRemovals)
        {
            fields.Add(new KeyValuePair<string, string>("allowRemovals", "true"));
        }

        if (!string.IsNullOrWhiteSpace(defaultTimeZoneId))
        {
            fields.Add(new KeyValuePair<string, string>("defaultTimeZoneId", defaultTimeZoneId));
        }

        return api.UploadAsync<IcsImportResult>(
            $"{CalendarsBase}/{calendarId}/ics",
            string.IsNullOrWhiteSpace(file.FileName) ? file with { FileName = "calendar.ics" } : file,
            contentTypeOverride: "text/calendar",
            ct: ct,
            fields: fields.Count > 0 ? fields : null);
    }

    public string ImportUrl(Guid calendarId) => Absolute($"{CalendarsBase}/{calendarId}/ics");

    public Task<ApiResult<ApiFile>> ExportEventAsync(Guid eventId, CancellationToken ct = default) =>
        api.GetFileAsync($"{EventsBase}/{eventId}/ics", "odyssey-event.ics", completenessMarker: CompletenessMarker, ct: ct);

    public Task<ApiResult<CalendarIcsExportSummary>> SummarizeEventExportAsync(Guid eventId, CancellationToken ct = default) =>
        api.GetAsync<CalendarIcsExportSummary>($"{EventsBase}/{eventId}/ics/summary", ct);

    public string ExportEventUrl(Guid eventId) => Absolute($"{EventsBase}/{eventId}/ics");

    public Task<ApiResult<ApiFile>> ExportPatternAsync(Guid patternId, CancellationToken ct = default) =>
        api.GetFileAsync($"{PatternsBase}/{patternId}/ics", "odyssey-series.ics", completenessMarker: CompletenessMarker, ct: ct);

    public Task<ApiResult<CalendarIcsExportSummary>> SummarizePatternExportAsync(Guid patternId, CancellationToken ct = default) =>
        api.GetAsync<CalendarIcsExportSummary>($"{PatternsBase}/{patternId}/ics/summary", ct);

    public string ExportPatternUrl(Guid patternId) => Absolute($"{PatternsBase}/{patternId}/ics");

    private static string AttachmentsQuery(IcsAttachmentsMode attachments) =>
        attachments == IcsAttachmentsMode.Omit ? "?attachments=omit" : string.Empty;

    public Task<ApiResult<CalendarIcsExportSummary>> SummarizeExportAsync(
        Guid calendarId, IcsAttachmentsMode attachments, CancellationToken ct = default) =>
        api.GetAsync<CalendarIcsExportSummary>($"{CalendarsBase}/{calendarId}/ics/summary{AttachmentsQuery(attachments)}", ct);

    public string ExportUrl(Guid calendarId, IcsAttachmentsMode attachments) =>
        Absolute($"{CalendarsBase}/{calendarId}/ics{AttachmentsQuery(attachments)}");

    public Task<ApiResult<CalendarIcsExportSummary>> SummarizeEventExportAsync(
        Guid eventId, IcsAttachmentsMode attachments, CancellationToken ct = default) =>
        api.GetAsync<CalendarIcsExportSummary>($"{EventsBase}/{eventId}/ics/summary{AttachmentsQuery(attachments)}", ct);

    public string ExportEventUrl(Guid eventId, IcsAttachmentsMode attachments) =>
        Absolute($"{EventsBase}/{eventId}/ics{AttachmentsQuery(attachments)}");

    public Task<ApiResult<CalendarIcsExportSummary>> SummarizePatternExportAsync(
        Guid patternId, IcsAttachmentsMode attachments, CancellationToken ct = default) =>
        api.GetAsync<CalendarIcsExportSummary>($"{PatternsBase}/{patternId}/ics/summary{AttachmentsQuery(attachments)}", ct);

    public string ExportPatternUrl(Guid patternId, IcsAttachmentsMode attachments) =>
        Absolute($"{PatternsBase}/{patternId}/ics{AttachmentsQuery(attachments)}");

    public Task<ApiResult<ApiFile>> ExportAggregateAsync(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search,
        CancellationToken ct = default) =>
        api.GetFileAsync(AggregateQuery("ics", from, to, calendarIds, search), "calendar-events.ics",
            completenessMarker: CompletenessMarker, ct: ct);

    public Task<ApiResult<CalendarIcsExportSummary>> SummarizeAggregateExportAsync(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search,
        CancellationToken ct = default) =>
        api.GetAsync<CalendarIcsExportSummary>(AggregateQuery("ics/summary", from, to, calendarIds, search), ct);

    public string ExportAggregateUrl(DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search) =>
        Absolute(AggregateQuery("ics", from, to, calendarIds, search));

    public Task<ApiResult<CalendarIcsExportSummary>> SummarizeAggregateExportAsync(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search, string? viewTimeZoneId,
        CancellationToken ct = default) =>
        api.GetAsync<CalendarIcsExportSummary>(AggregateQuery("ics/summary", from, to, calendarIds, search, viewTimeZoneId), ct);

    public string ExportAggregateUrl(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search, string? viewTimeZoneId) =>
        Absolute(AggregateQuery("ics", from, to, calendarIds, search, viewTimeZoneId));

    // Built by hand rather than with PagedQuery: these endpoints take no offset/limit, which PagedQuery would always
    // append.
    private static string AggregateQuery(
        string path, DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search,
        string? viewTimeZoneId = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(viewTimeZoneId))
        {
            parts.Add($"viewTimeZoneId={Uri.EscapeDataString(viewTimeZoneId)}");
        }

        if (from is { } f)
        {
            parts.Add($"from={Uri.EscapeDataString(f.ToString("o"))}");
        }

        if (to is { } t)
        {
            parts.Add($"to={Uri.EscapeDataString(t.ToString("o"))}");
        }

        if (calendarIds is { Count: > 0 })
        {
            parts.AddRange(calendarIds.Select(id => $"calendarIds={id}"));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            parts.Add($"search={Uri.EscapeDataString(search)}");
        }

        return parts.Count == 0 ? $"{EventsBase}/{path}" : $"{EventsBase}/{path}?{string.Join('&', parts)}";
    }

    // A transfer URL is handed to the browser or another process, so it must be absolute; a consumer whose HttpClient
    // has no BaseAddress gets a message naming the cause instead of a bare NullReferenceException.
    private string Absolute(string relative) =>
        new Uri(
            api.BaseAddress ?? throw new InvalidOperationException(
                "The calendar transfer URLs are absolute, so the API client's HttpClient needs a BaseAddress."),
            relative).ToString();

    public Task<ApiResult> CreateCalendarAsync(NewCalendar calendar, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, CalendarsBase, calendar, ct);

    public Task<ApiResult> UpdateCalendarAsync(Guid id, NewCalendar calendar, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{CalendarsBase}/{id}", calendar, ct);

    public Task<ApiResult> DeleteCalendarAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{CalendarsBase}/{id}", null, ct);

    // ── Events ───────────────────────────────────────────────────────────────

    public Task<ApiResult> CreateEventAsync(NewCalendarEvent calendarEvent, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, EventsBase, calendarEvent, ct);

    public Task<ApiResult> UpdateEventAsync(Guid id, NewCalendarEvent calendarEvent, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{EventsBase}/{id}", calendarEvent, ct);

    public Task<ApiResult> DeleteEventAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{EventsBase}/{id}", null, ct);

    public Task<ApiResult<CalendarEventDeleteResult?>> DeleteEventWithResultAsync(Guid id, CancellationToken ct = default) =>
        api.SendOptionalAsync<CalendarEventDeleteResult>(HttpMethod.Delete, $"{EventsBase}/{id}", null, ct);

    // ── Recurrence patterns ──────────────────────────────────────────────────

    public Task<ApiResult> CreatePatternAsync(NewRecurrencePattern pattern, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, PatternsBase, pattern, ct);

    public Task<ApiResult> UpdatePatternAsync(Guid id, NewRecurrencePattern pattern, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{PatternsBase}/{id}", pattern, ct);

    public Task<ApiResult<RecurrencePatternUpdateResult>> UpdatePatternWithResultAsync(
        Guid id, NewRecurrencePattern pattern, CancellationToken ct = default) =>
        api.SendAsync<RecurrencePatternUpdateResult>(HttpMethod.Put, $"{PatternsBase}/{id}", pattern, ct);

    public Task<ApiResult<RecurrencePatternPreview>> PreviewPatternAsync(
        Guid id, NewRecurrencePattern pattern, CancellationToken ct = default) =>
        api.SendAsync<RecurrencePatternPreview>(HttpMethod.Post, $"{PatternsBase}/{id}/preview", pattern, ct);

    public Task<ApiResult> DeletePatternAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{PatternsBase}/{id}", null, ct);
}
