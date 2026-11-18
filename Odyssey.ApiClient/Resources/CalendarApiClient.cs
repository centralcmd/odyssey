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

    // ── Recurrence patterns ──────────────────────────────────────────────────
    // The series template. Editing a pattern regenerates its occurrences server-side, which is why
    // the calendar page reloads its window after any pattern write.

    Task<ApiResult> CreatePatternAsync(NewRecurrencePattern pattern, CancellationToken ct = default);

    Task<ApiResult> UpdatePatternAsync(Guid id, NewRecurrencePattern pattern, CancellationToken ct = default);

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

    // Built by hand rather than with PagedQuery: these endpoints take no offset/limit, which PagedQuery would always
    // append.
    private static string AggregateQuery(
        string path, DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search)
    {
        var parts = new List<string>();
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

    private string Absolute(string relative) => new Uri(api.BaseAddress!, relative).ToString();

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

    // ── Recurrence patterns ──────────────────────────────────────────────────

    public Task<ApiResult> CreatePatternAsync(NewRecurrencePattern pattern, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, PatternsBase, pattern, ct);

    public Task<ApiResult> UpdatePatternAsync(Guid id, NewRecurrencePattern pattern, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{PatternsBase}/{id}", pattern, ct);

    public Task<ApiResult> DeletePatternAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{PatternsBase}/{id}", null, ct);
}
