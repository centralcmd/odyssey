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
    /// failure result rather than a body that would download as a fake <c>.ics</c>.</summary>
    Task<ApiResult<ApiFile>> ExportAsync(Guid calendarId, CancellationToken ct = default);

    /// <summary>Imports an <c>.ics</c> file into an existing calendar (multipart).</summary>
    Task<ApiResult<IcsImportResult>> ImportAsync(Guid calendarId, ApiUpload file, CancellationToken ct = default);

    /// <summary>Exports a single event as a standalone <c>.ics</c> VEVENT (issue #340).</summary>
    Task<ApiResult<ApiFile>> ExportEventAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>Exports a recurring series as one RRULE VEVENT, or its per-occurrence fallback (issue #340).</summary>
    Task<ApiResult<ApiFile>> ExportPatternAsync(Guid patternId, CancellationToken ct = default);

    /// <summary>Exports every matching event across every calendar the caller can read, as one
    /// multi-VEVENT <c>.ics</c> file, optionally filtered by date range / calendar / search term (issue
    /// #340). Omitting <paramref name="from"/>/<paramref name="to"/> and <paramref name="search"/>
    /// exports everything (subject to the server's caps).</summary>
    Task<ApiResult<ApiFile>> ExportAggregateAsync(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search,
        CancellationToken ct = default);

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
    /// The migration-seeded default for <c>CalendarIcsMaxImportMegabytes</c> — the real, effective
    /// cap is dynamic (System Settings, issue #343) and read via <c>IImportLimitsApiClient</c>; this
    /// constant exists solely as <c>ImportLimitsCache.Fallback</c>'s input for when that read fails.
    /// </summary>
    public const long MaxImportBytes = 64L * 1024 * 1024;

    private const string CalendarsBase = "api/calendars";
    private const string EventsBase = "api/calendar-events";
    private const string PatternsBase = "api/recurrence-patterns";

    public Task<ApiResult<List<ExistingCalendar>>> ListCalendarsAsync(CancellationToken ct = default) =>
        api.GetAllAsync<ExistingCalendar>(PagedQuery.For(CalendarsBase).Build(), ct);

    public Task<ApiResult<List<ExistingCalendarEvent>>> ListEventsAsync(DateTime from, DateTime to, CancellationToken ct = default) =>
        api.GetAllAsync<ExistingCalendarEvent>(
            PagedQuery.For(EventsBase).Add("from", from).Add("to", to).Build(), ct);

    public async Task<ExistingCalendarEvent?> GetEventAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingCalendarEvent>($"{EventsBase}/{id}", ct)).Value;

    public async Task<ExistingRecurrencePattern?> GetPatternAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingRecurrencePattern>($"{PatternsBase}/{id}", ct)).Value;

    // No completeness marker: CalendarIcsController.Export is fully-buffered with Content-Length
    // (Content-Length itself already guarantees completeness), not one of the four streamed Goal 8
    // surfaces, and carries no X-Odyssey-Export-Rows header (issue #343 §10 item 6).
    public Task<ApiResult<ApiFile>> ExportAsync(Guid calendarId, CancellationToken ct = default) =>
        api.GetFileAsync($"{CalendarsBase}/{calendarId}/ics", "calendar.ics", ct: ct);

    public Task<ApiResult<IcsImportResult>> ImportAsync(Guid calendarId, ApiUpload file, CancellationToken ct = default) =>
        api.UploadAsync<IcsImportResult>(
            $"{CalendarsBase}/{calendarId}/ics",
            string.IsNullOrWhiteSpace(file.FileName) ? file with { FileName = "calendar.ics" } : file,
            contentTypeOverride: "text/calendar",
            ct: ct);

    public Task<ApiResult<ApiFile>> ExportEventAsync(Guid eventId, CancellationToken ct = default) =>
        api.GetFileAsync($"{EventsBase}/{eventId}/ics", "odyssey-event.ics", ct: ct);

    public Task<ApiResult<ApiFile>> ExportPatternAsync(Guid patternId, CancellationToken ct = default) =>
        api.GetFileAsync($"{PatternsBase}/{patternId}/ics", "odyssey-series.ics", ct: ct);

    public Task<ApiResult<ApiFile>> ExportAggregateAsync(
        DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search,
        CancellationToken ct = default)
    {
        // Built by hand rather than with PagedQuery: this endpoint streams a whole .ics and takes no
        // offset/limit, which PagedQuery would always append.
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

        var url = parts.Count == 0 ? $"{EventsBase}/ics" : $"{EventsBase}/ics?{string.Join('&', parts)}";
        return api.GetFileAsync(url, "calendar-events.ics", completenessMarker: "BEGIN:VEVENT", ct: ct);
    }

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
