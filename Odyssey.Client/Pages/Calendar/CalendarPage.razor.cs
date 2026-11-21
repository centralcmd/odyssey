using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Client.Pages.Weather;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Calendar;

public partial class CalendarPage
{
    private const string PageStateKey = "calendar-page";

    private List<ExistingCalendar> _calendars = [];
    private List<ExistingCalendarEvent> _events = [];
    private List<CalendarEventVm> _viewModels = [];
    private bool _isLoading = true;
    private bool _loadError;
    // Latest-request-wins for the list fetch (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();

    // Reference date for the active view: the shown month (month/agenda), the week containing it
    // (week), or the shown day (day). A real date, not forced to the 1st, so week/day keep their day.
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Now);
    private string _view = "month";
    private string _search = string.Empty;
    private bool _searchOpen = true;
    private HashSet<Guid> _visibleCalendarIds = [];

    [Inject] private IServiceProvider Services { get; set; } = default!;

    // The viewer's zone (issue #520 §2): zoned events are drawn in it, and the window is read in it.
    private ViewerTimeZone? _viewerZone;

    private ViewerTimeZone Viewer => _viewerZone ??= ViewerTimeZone.From(Services);

    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;

    // Managing calendars (create / rename / delete calendars) needs any one of the write claims.
    private bool _canReadLocations;
    private bool _canCreateLocations;

    // The attachment fields and the import's claim line (issue #522): a kind is shown and editable only with its read
    // claim, and a file carrying attachments imports only with both kinds' read and create claims.
    private bool _canReadFiles;
    private bool _canReadPhotos;
    private bool _canAddAttachments;
    private bool _canManageCalendars => _canCreate || _canUpdate || _canDelete;

    // Import can create OR update rows depending on UID match, so it needs BOTH claims (mirrors the
    // server's dual-claim gate). Export needs only read — always true on this CalendarRead-gated page.
    private bool _canImport => _canCreate && _canUpdate;

    private EventCallback<(Guid Id, DateOnly ToDate, DateOnly FromDate)> DropCallback =>
        _canUpdate
            ? EventCallback.Factory.Create<(Guid Id, DateOnly ToDate, DateOnly FromDate)>(this, HandleEventDrop)
            : default;

    private bool _eventDialogOpen;
    private CalendarEventDialogMode _eventDialogMode = CalendarEventDialogMode.Create;
    private Guid? _eventDialogEventId;
    private Guid? _eventDialogPatternId;
    private DateOnly? _eventDialogDefaultDate;
    private TimeSpan? _eventDialogDefaultTime;

    private bool _manageCalendarsOpen;
    private bool _importOpen;
    private Guid? _importDefaultCalendarId;

    private bool _exportScopeOpen;
    private ExistingCalendarEvent? _exportScopeEvent;

    private bool _exportBulkOpen;
    private DateTime? _exportBulkFrom;
    private DateTime? _exportBulkTo;

    private static readonly IReadOnlyList<OdsSegmentedOption> _viewOptions =
    [
        new() { Value = "month", Label = "Month", Icon = "calendar_view_month" },
        new() { Value = "week", Label = "Week", Icon = "calendar_view_week" },
        new() { Value = "day", Label = "Day", Icon = "calendar_view_day" },
        new() { Value = "agenda", Label = "Agenda", Icon = "view_agenda" },
    ];

    private DateOnly TodayDate => DateOnly.FromDateTime(DateTime.Now);

    // The dates the active view covers — also the event-fetch window. Month/Agenda use the full 6×7
    // month grid (so partial leading/trailing weeks are populated); Week is 7 days from the week start;
    // Day is the single anchor day.
    private IReadOnlyList<DateOnly> CurrentDays => _view switch
    {
        "week" => [.. Enumerable.Range(0, 7).Select(CalendarGridMath.StartOfWeek(_anchor, 1).AddDays)],
        "day" => [_anchor],
        _ => CalendarGridMath.DaysInGrid(_anchor, 1),
    };

    private string PeriodLabel
    {
        get
        {
            if (_view == "day")
            {
                return _anchor.ToString("dddd, MMMM d, yyyy");
            }

            if (_view == "week")
            {
                var start = CalendarGridMath.StartOfWeek(_anchor, 1);
                var end = start.AddDays(6);
                return start.Year == end.Year
                    ? $"{start:MMM d} – {OdsDates.LongUnpadded(end)}"
                    : $"{OdsDates.LongUnpadded(start)} – {OdsDates.LongUnpadded(end)}";
            }

            return _anchor.ToString("MMMM yyyy");
        }
    }

    private IReadOnlyCollection<string> _visibleCalendarValues =>
        [.. _visibleCalendarIds.Select(id => id.ToString())];

    private IReadOnlyList<OdsOption> _calendarOptions =>
        [.. _calendars.Select(CalendarOption)];

    /// <summary>A calendar row: the calendar's own glyph in its own colour, through <see cref="CalendarVisuals"/>.</summary>
    internal static OdsOption CalendarOption(ExistingCalendar calendar) => CalendarVisuals.Option(calendar);

    private IReadOnlySet<Guid>? EffectiveVisibleIds => _visibleCalendarIds.Count == 0 ? null : _visibleCalendarIds;

    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _visibleCalendarIds.Count > 0;

    private IEnumerable<DateOnly> AgendaDays =>
        _viewModels
            .Where(vm => EffectiveVisibleIds is null || EffectiveVisibleIds.Contains(vm.CalendarId))
            .Select(vm => DateOnly.FromDateTime(vm.Start))
            .Distinct()
            .OrderBy(day => day);

    private IEnumerable<CalendarEventVm> AgendaEventsFor(DateOnly day) =>
        _viewModels
            .Where(vm => (EffectiveVisibleIds is null || EffectiveVisibleIds.Contains(vm.CalendarId))
                && CalendarGridMath.CoversDay(vm, day))
            .OrderBy(vm => vm.Start);

    // ── Weather on the agenda (issue #363 §3.3, §3.7) ─────────────────────────
    // Asked only in the agenda view, only for the events it renders after the calendar and search filters, and
    // debounced so stepping through months quickly spends one permit, not one per step. Month, week and day ask
    // for nothing.
    private WeatherBatchLoader<CalendarEventWeatherItem>? _agendaWeatherLoader;

    private WeatherBatchLoader<CalendarEventWeatherItem> AgendaWeather => _agendaWeatherLoader ??= new(
        WeatherBatchReads.CalendarEvents(WeatherApi), item => item.CalendarEventId,
        () => InvokeAsync(StateHasChanged), Log, "agenda", TimeSpan.FromMilliseconds(300));

    /// <summary>The ids whose start row the agenda renders, after the visible-calendar and search filters.</summary>
    internal IReadOnlyList<Guid> AgendaWeatherIds() =>
        _view != "agenda"
            ? []
            : [.. _viewModels.Where(vm => EffectiveVisibleIds is null || EffectiveVisibleIds.Contains(vm.CalendarId)).Select(vm => vm.Id)];

    private void ShowAgendaWeather() => _ = AgendaWeather.ShowAsync(AgendaWeatherIds());

    // A save may have moved the event's time or place: forget its answer so the agenda asks again.
    private async Task OnEventSavedAsync()
    {
        if (_eventDialogMode == CalendarEventDialogMode.Edit && _eventDialogEventId is { } id)
        {
            AgendaWeather.Forget(id);
        }
        else if (_eventDialogMode == CalendarEventDialogMode.Series && _eventDialogPatternId is { } patternId)
        {
            foreach (var e in _events.Where(e => e.RecurrencePatternId == patternId))
            {
                AgendaWeather.Forget(e.CalendarEventId);
            }
        }

        await ReloadEventsAsync();
    }

    private async void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        try
        {
            var user = (await state).User;
            AgendaWeather.Reset(user.HasPermission(PermissionClaims.WeatherRead));
            await InvokeAsync(StateHasChanged);
            await AgendaWeather.ShowAsync(AgendaWeatherIds());
        }
        catch (Exception)
        {
            // The page may already be gone; weather is supplementary and never worth an unhandled exception.
        }
    }

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        AuthenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        await LoadCalendarsAsync();
        await LoadEventsAsync();
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.CalendarCreate);
        _canUpdate = user.HasPermission(PermissionClaims.CalendarUpdate);
        _canDelete = user.HasPermission(PermissionClaims.CalendarDelete);
        _canReadLocations = user.HasPermission(PermissionClaims.LocationsRead);
        _canCreateLocations = _canReadLocations && user.HasPermission(PermissionClaims.LocationsCreate);
        _canReadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canReadPhotos = user.HasPermission(PermissionClaims.PhotosRead);
        _canAddAttachments = _canReadFiles && _canReadPhotos
            && user.HasPermission(PermissionClaims.FilesCreate) && user.HasPermission(PermissionClaims.PhotosCreate);
        AgendaWeather.Reset(user.HasPermission(PermissionClaims.WeatherRead));
    }

    private async Task LoadCalendarsAsync()
    {
        _calendars = (await CalendarApi.ListCalendarsAsync()).ItemsOrToast(Snackbar, "calendars");
    }

    private async Task LoadEventsAsync()
    {
        _isLoading = true;
        StateHasChanged();

        var days = CurrentDays;

        // Track failure explicitly: ItemsOrToast falls back to [], which is indistinguishable from a
        // period that genuinely has no events.
        // The range changes on every prev/next/view switch, so a slow earlier range must not land last.
        // A zone the server's tzdata does not know is retried once in UTC, which the session then keeps (PR #528).
        var response = await _listLoader.RunAsync(ct => Viewer.WithFallbackAsync(ViewZoneField, () =>
        {
            var (from, to) = Window(days, Viewer);
            return CalendarApi.ListEventsAsync(from, to, Viewer.ViewZoneId, ct);
        }));
        if (response.IsSuperseded)
        {
            return;
        }

        var result = response.Value;
        _events = result.ItemsOrToast(Snackbar, "calendar events");
        _loadError = !result.IsSuccess;
        RebuildViewModels();

        _isLoading = false;
        StateHasChanged();
    }

    private Task ReloadEventsAsync() => LoadEventsAsync();

    /// <summary>The field a windowed read's zone is validated as: a 400 keyed on it is the server refusing the zone.</summary>
    internal const string ViewZoneField = "viewTimeZoneId";

    /// <summary>
    /// The window of <paramref name="days"/> as instants (issue #520 v4 §1): the first day's and the day after the last
    /// day's local midnights in the viewer's zone, converted to UTC, read together with that zone as viewTimeZoneId.
    /// </summary>
    internal static (DateTime From, DateTime To) Window(IReadOnlyList<DateOnly> days, ViewerTimeZone viewer) =>
        (CalendarZoneClock.ToUtc(days[0].ToDateTime(TimeOnly.MinValue), viewer.Zone),
            CalendarZoneClock.ToUtc(days[^1].AddDays(1).ToDateTime(TimeOnly.MinValue), viewer.Zone));

    private async Task OnCalendarsChangedAsync()
    {
        await LoadCalendarsAsync();
        RebuildViewModels();
        StateHasChanged();
    }

    // ── Export (issue #517) ──────────────────────────────────────────────────
    // Every export asks the server for a summary first: a refusal (400/403/404/429) is a persistent toast and no
    // download starts. Otherwise the browser downloads the file itself — a calendar export can be gigabytes, far
    // more than the WASM heap holds — and a 10 s toast says what it carries. One export at a time per page.
    private bool _exporting;
    private OdsLiveAnnouncer? _announcer;

    /// <summary>The event dialog's Export button, where focus returns after an export prompt closes.</summary>
    private const string ExportButtonId = "cal-export-btn";

    private Task AnnounceAsync(string message) => _announcer?.Announce(message) ?? Task.CompletedTask;

    /// <summary>Summary first, then the native download; false when the summary was refused (and toasted).</summary>
    private async Task<bool> DownloadAsync(
        Task<ApiResult<CalendarIcsExportSummary>> summary, string failureLead, Func<string> url,
        Func<CalendarIcsExportSummary, string> toast)
    {
        if ((await summary).OrToast(Snackbar, failureLead, requireInteraction: true) is not { } counts)
        {
            return false;
        }

        await NativeTransfer.DownloadAsync(url());
        CalendarIcsExport.ToastStarted(Snackbar, toast(counts));
        return true;
    }

    /// <summary>
    /// One export that can carry attachments (issue #522 §3.4, v3 §A): what the commit step shows and how the chosen mode
    /// becomes a URL and a toast. <paramref name="Toast"/> takes the attachment part (or empty) and returns the toast.
    /// </summary>
    internal sealed record AttachmentExport(
        string Title, string What, string Noun, CalendarIcsExportSummary Summary, bool Forbidden, string? ReturnFocusToId,
        Func<IcsAttachmentsMode, string> Url, Func<CalendarIcsExportSummary, string, string> Toast);

    private AttachmentExport? _attachmentExport;
    private bool _attachmentExportOpen;

    /// <summary>
    /// A calendar, event or series export (issue #522 §3.4): the include summary first. Nothing attached — the download
    /// starts as before. Attachments — the commit step asks whether to include them. A <c>403</c> for attachments — the
    /// omit summary, then the commit step in its refused form. Any other failure keeps Spec A's refusal toast and renders
    /// no checkbox (v4). False when nothing was started or offered.
    /// </summary>
    private async Task<bool> DownloadWithAttachmentsAsync(
        Func<IcsAttachmentsMode, Task<ApiResult<CalendarIcsExportSummary>>> summarize, string failureLead,
        Func<IcsAttachmentsMode, string> url, Func<CalendarIcsExportSummary, string, string> toast,
        string title, string noun, string? returnFocusToId)
    {
        var included = await summarize(IcsAttachmentsMode.Include);
        var forbidden = included.Status == System.Net.HttpStatusCode.Forbidden
            && string.Equals(included.Problem?.Code, CalendarIcsExport.AttachmentsForbiddenCode, StringComparison.Ordinal);
        if (!forbidden)
        {
            if (included.OrToast(Snackbar, failureLead, requireInteraction: true) is not { } counts)
            {
                return false;
            }

            if (counts.AttachmentCount + counts.PhotoCount == 0)
            {
                await NativeTransfer.DownloadAsync(url(IcsAttachmentsMode.Include));
                CalendarIcsExport.ToastStarted(Snackbar, toast(counts, string.Empty));
                return true;
            }

            OpenAttachmentExport(new AttachmentExport(title, CalendarIcsExport.WhatFile(noun, counts), noun, counts, false, returnFocusToId, url, toast));
            return true;
        }

        if ((await summarize(IcsAttachmentsMode.Omit)).OrToast(Snackbar, failureLead, requireInteraction: true) is not { } omitted)
        {
            return false;
        }

        OpenAttachmentExport(new AttachmentExport(title, CalendarIcsExport.WhatFile(noun, omitted), noun, omitted, true, returnFocusToId, url, toast));
        return true;
    }

    // Include is the server's default: the plain summary and URL are the include ones (#521 §5.4), so only Omit names it.
    private static bool Included(IcsAttachmentsMode mode) => mode == IcsAttachmentsMode.Include;

    private void OpenAttachmentExport(AttachmentExport export)
    {
        _attachmentExport = export;
        _attachmentExportOpen = true;
        StateHasChanged();
    }

    /// <summary>The commit step's choice: the download in that mode and its toast, long ones persistent (AX7).</summary>
    internal async Task AttachmentExportChosenAsync(IcsAttachmentsMode mode)
    {
        if (_attachmentExport is not { } export)
        {
            return;
        }

        await NativeTransfer.DownloadAsync(export.Url(mode));
        CalendarIcsExport.ToastStartedScaled(Snackbar,
            CalendarIcsExport.WithAttachments(part => export.Toast(export.Summary, part), export.Summary, mode));
    }

    /// <summary>E3: the per-calendar export. Manage calendars shows its own busy state and announcements.</summary>
    internal async Task ExportCalendarAsync(ExistingCalendar calendar)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            await DownloadWithAttachmentsAsync(
                mode => Included(mode) ? CalendarApi.SummarizeExportAsync(calendar.CalendarId) : CalendarApi.SummarizeExportAsync(calendar.CalendarId, mode), "Unable to export the calendar",
                mode => Included(mode) ? CalendarApi.ExportUrl(calendar.CalendarId) : CalendarApi.ExportUrl(calendar.CalendarId, mode), (s, part) => CalendarIcsExport.CalendarToast(calendar.Name, s, part),
                $"Export “{calendar.Name}”", "calendar", returnFocusToId: null);
        }
        finally
        {
            _exporting = false;
        }
    }

    private void OpenImportDialog()
    {
        _importDefaultCalendarId = _calendars.FirstOrDefault()?.CalendarId;
        _importOpen = true;
    }

    // Single-event / series export from CalendarEventDialog (issue #340). The page — not the dialog —
    // decides the scope: it already has the loaded event list, so it can tell a standalone event from a
    // recurring occurrence without an extra fetch. Series mode never needs the occurrence/series prompt.
    // The title is the series' own in Series mode and the event's in Edit mode (issue #517 §3.1).
    internal async Task ExportEventDialogAsync(string? title)
    {
        if (_eventDialogMode == CalendarEventDialogMode.Series && _eventDialogPatternId is { } patternId)
        {
            await ExportPatternDownloadAsync(patternId, title);
            return;
        }

        if (_eventDialogMode != CalendarEventDialogMode.Edit || _eventDialogEventId is not { } eventId)
        {
            return;
        }

        var existing = _events.FirstOrDefault(e => e.CalendarEventId == eventId);
        if (existing?.RecurrencePatternId is not null)
        {
            _exportScopeEvent = existing;
            _exportScopeOpen = true;
            return;
        }

        await ExportEventDownloadAsync(eventId, title ?? existing?.Title);
    }

    // E4: the scope dialog stays open (busy) until this settles, then closes either way.
    internal Task ExportScopeChosenAsync(CalendarEventExportScope scope) => scope switch
    {
        CalendarEventExportScope.Series when _exportScopeEvent?.RecurrencePatternId is { } patternId =>
            ExportPatternDownloadAsync(patternId, _exportScopeEvent.Title),
        _ when _exportScopeEvent is { } evt => ExportEventDownloadAsync(evt.CalendarEventId, evt.Title),
        _ => Task.CompletedTask,
    };

    // ExportEventScopeDialog renders stacked on top of the still-open CalendarEventDialog (no
    // close-then-open sequencing), so closing it — via Cancel, Esc, backdrop click, or choosing a
    // scope — would otherwise drop focus to <body> (WCAG 2.4.3). Restore it to the Export button that
    // opened this prompt; every dismissal path funnels through OpenChanged(false).
    internal async Task OnExportScopeOpenChangedAsync(bool open)
    {
        _exportScopeOpen = open;
        // The attachments step opened from this choice and holds focus now (issue #522); it returns focus on close itself.
        if (!open && !_attachmentExportOpen)
        {
            try
            {
                await JS.InvokeVoidAsync("odsFocusById", ExportButtonId);
            }
            catch (Exception)
            {
                // Best-effort focus return.
            }
        }
    }

    private async Task ExportEventDownloadAsync(Guid eventId, string? title)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            await DownloadWithAttachmentsAsync(
                mode => Included(mode) ? CalendarApi.SummarizeEventExportAsync(eventId) : CalendarApi.SummarizeEventExportAsync(eventId, mode), "Unable to export the event",
                mode => Included(mode) ? CalendarApi.ExportEventUrl(eventId) : CalendarApi.ExportEventUrl(eventId, mode), (_, part) => CalendarIcsExport.EventToast(title, part),
                $"Export “{(string.IsNullOrWhiteSpace(title) ? "Untitled event" : title)}”", "event", ExportButtonId);
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task ExportPatternDownloadAsync(Guid patternId, string? title)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            await DownloadWithAttachmentsAsync(
                mode => Included(mode) ? CalendarApi.SummarizePatternExportAsync(patternId) : CalendarApi.SummarizePatternExportAsync(patternId, mode), "Unable to export the series",
                mode => Included(mode) ? CalendarApi.ExportPatternUrl(patternId) : CalendarApi.ExportPatternUrl(patternId, mode), (s, part) => CalendarIcsExport.SeriesToast(title, s, part),
                $"Export the series “{(string.IsNullOrWhiteSpace(title) ? "Untitled event" : title)}”", "series", ExportButtonId);
        }
        finally
        {
            _exporting = false;
        }
    }

    // E1: "Export all as iCalendar" — a direct, no-dialog aggregate export of every event across every
    // calendar (bounded by the server's caps). "Export filtered…" opens ExportCalendarEventsDialog
    // instead, since that scope needs a form (date range / calendars / search). A second activation
    // while one is being prepared only says so (AC 5).
    internal async Task ExportAllEventsAsync()
    {
        if (_exporting)
        {
            await AnnounceAsync(CalendarIcsExport.AlreadyPreparing);
            return;
        }

        _exporting = true;
        try
        {
            _ = AnnounceAsync(CalendarIcsExport.Preparing);
            await DownloadAsync(
                Viewer.WithFallbackAsync(ViewZoneField, () => CalendarApi.SummarizeAggregateExportAsync(null, null, null, null, Viewer.ViewZoneId)),
                "Unable to export events",
                () => CalendarApi.ExportAggregateUrl(null, null, null, null, Viewer.ViewZoneId), CalendarIcsExport.AllToast);
        }
        finally
        {
            _exporting = false;
        }
    }

    // ── Import hand-off (issue #517 §3 step 6) ───────────────────────────────
    // Closing the import dialog while the server imports hands the pending request to the page, which then owns
    // the outcome: one toast (always — the snackbar is app-wide, so a user who navigated away still learns of a
    // partial failure or of removed events) and one refresh (skipped once the page is gone).
    private bool _importPending;
    private bool _disposed;

    internal async Task OnImportBackgroundedAsync(Task<ApiResult<IcsImportResult>> pending)
    {
        _importPending = true;
        StateHasChanged();

        ApiResult<IcsImportResult> outcome;
        try
        {
            outcome = await pending;
        }
        catch (Exception ex)
        {
            outcome = ApiResult<IcsImportResult>.Failure(ex);
        }

        CalendarImportCopy.ToastHandedOff(Snackbar, outcome);
        _importPending = false;
        if (_disposed)
        {
            return;
        }

        await ReloadEventsAsync();
    }

    // Prefills the bulk-export dialog's date range from the period currently on screen (mirrors the
    // window LoadEventsAsync already fetches), same as CD's periodBounds() in the design system mockup.
    private void OpenExportFilteredDialog()
    {
        var days = CurrentDays;
        _exportBulkFrom = days[0].ToDateTime(TimeOnly.MinValue);
        _exportBulkTo = days[^1].AddDays(1).ToDateTime(TimeOnly.MinValue);
        _exportBulkOpen = true;
    }

    private void RebuildViewModels()
    {
        var term = _search.Trim();
        var filtered = string.IsNullOrEmpty(term)
            ? _events
            : _events.Where(e => MatchesSearch(e, term)).ToList();

        _viewModels = [.. filtered.Select(ToVm)];
        ShowAgendaWeather();
    }

    /// <summary>
    /// The calendar's text search (issue #383 §3.4): the title, the details text, and the linked place's
    /// name — after conversion a place name lives only on the link, so searching "Hytta" must still match.
    /// </summary>
    internal static bool MatchesSearch(ExistingCalendarEvent e, string term) =>
        e.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (e.Location?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (e.LocationRef?.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    private CalendarEventVm ToVm(ExistingCalendarEvent e)
    {
        var calendar = _calendars.FirstOrDefault(c => c.CalendarId == e.CalendarId);
        var (bg, fg) = OdsCalendarSwatches.PaintFor(calendar?.Color);
        // A zoned event is drawn — and named — at its instant in the viewer's zone; a floating one at its clock time (§2).
        var (start, end) = CalendarZoneClock.Displayed(e, Viewer.Zone);
        return new CalendarEventVm(
            e.CalendarEventId, e.CalendarId, calendar?.Name ?? "Unknown",
            e.Title, start, end, e.IsAllDay,
            bg, fg, e.RecurrencePatternId is not null,
            e.LocationRef is { } place ? Odyssey.Client.Pages.Journal.LocationVisuals.PlaceName(place) : null,
            HasAttachments: e.AttachmentCount + e.PhotoCount > 0);
    }

    private void GoToday()
    {
        _anchor = DateOnly.FromDateTime(DateTime.Now);
        PersistPageState();
        _ = ReloadEventsAsync();
    }

    private void StepPeriod(int delta)
    {
        _anchor = _view switch
        {
            "week" => _anchor.AddDays(7 * delta),
            "day" => _anchor.AddDays(delta),
            _ => _anchor.AddMonths(delta),
        };
        PersistPageState();
        _ = ReloadEventsAsync();
    }

    private void OnViewChanged(string view)
    {
        _view = view;
        PersistPageState();
        _ = ReloadEventsAsync(); // the fetch window differs per view (month grid vs week vs day)
    }

    private void OnSearchToggled(bool open)
    {
        _searchOpen = open;
        PersistPageState();
    }

    private void OnSearchChanged(string value)
    {
        _search = value ?? string.Empty;
        RebuildViewModels();
        PersistPageState();
    }

    private void OnVisibleCalendarsChanged(IReadOnlyCollection<string> values)
    {
        _visibleCalendarIds = [.. values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];
        PersistPageState();
        ShowAgendaWeather();
    }

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _visibleCalendarIds = [];
        RebuildViewModels();
        PersistPageState();
        await ReloadEventsAsync();
    }

    private void OpenCreateDialog(DateOnly? date, TimeSpan? time = null)
    {
        if (!_canCreate)
        {
            return;
        }

        _eventDialogMode = CalendarEventDialogMode.Create;
        _eventDialogEventId = null;
        _eventDialogPatternId = null;
        _eventDialogDefaultDate = date ?? DateOnly.FromDateTime(DateTime.Now);
        _eventDialogDefaultTime = time;
        _eventDialogOpen = true;
    }

    private void OnSlotClicked((DateOnly Day, TimeSpan Time) slot) => OpenCreateDialog(slot.Day, slot.Time);

    internal void OpenEventDialog(Guid eventId)
    {
        _eventDialogMode = CalendarEventDialogMode.Edit;
        _eventDialogEventId = eventId;
        _eventDialogPatternId = null;
        _eventDialogOpen = true;
    }

    internal void OpenSeriesDialog(Guid patternId)
    {
        _eventDialogMode = CalendarEventDialogMode.Series;
        _eventDialogEventId = null;
        _eventDialogPatternId = patternId;
        _eventDialogOpen = true;
    }

    private async Task HandleEventDrop((Guid Id, DateOnly ToDate, DateOnly FromDate) args)
    {
        var existing = _events.FirstOrDefault(e => e.CalendarEventId == args.Id);
        if (existing is null)
        {
            return;
        }

        await RescheduleAsync(existing, DropUpdate(existing, args.ToDate, args.FromDate));
    }

    /// <summary>
    /// A drag or resize write (issue #520 §3 "Drag and resize", v3 §J): one toast — the stored time when the server moved
    /// it out of a daylight-saving gap, or that the occurrence had become a separate event — and, for a generated
    /// occurrence, the announcement that it is now edited separately. The grid redraws from the server's rows.
    /// </summary>
    private async Task RescheduleAsync(ExistingCalendarEvent existing, NewCalendarEvent update)
    {
        var result = await CalendarApi.UpdateEventWithResultAsync(existing.CalendarEventId, update);
        if (!result.IsSuccess)
        {
            Snackbar.Add($"Unable to reschedule event: {result.Error}", Severity.Error);
            return;
        }

        var saved = result.Value;
        var parts = new List<string>();
        if (saved is { Detached: true })
        {
            parts.Add(CalendarEventCopy.DetachedEdit);
        }

        if (saved is { DetachedCount: > 0 })
        {
            parts.Add(CalendarEventCopy.DetachedNow(saved.DetachedCount));
        }

        var adjusted = AdjustedAfterMove(update, saved);
        if (adjusted is not null)
        {
            parts.Add(adjusted);
        }

        if (parts.Count == 0)
        {
            Snackbar.Add("Event rescheduled.", Severity.Success);
        }
        else
        {
            Snackbar.Add(string.Join(' ', parts), Severity.Info, c => c.RequireInteraction = adjusted is not null, Guid.NewGuid().ToString("N"));
        }

        if (existing is { RecurrencePatternId: not null, IsOverride: false } && saved is not { Detached: true })
        {
            await AnnounceAsync(CalendarEventCopy.MovedOverride);
        }

        AgendaWeather.Forget(existing.CalendarEventId);
        await ReloadEventsAsync();
    }

    /// <summary>The DST toast for a move: the server stored a later wall-clock than the one sent (v3 §F).</summary>
    internal static string? AdjustedAfterMove(NewCalendarEvent sent, ExistingCalendarEvent? saved) =>
        sent.TimeZoneId is { } zone && saved is not null && saved.StartDateTime != sent.StartDateTime
            ? CalendarEventCopy.Adjusted(CalendarZoneClock.Hm(saved.StartDateTime), CalendarZoneClock.Hm(sent.StartDateTime),
                CalendarZoneClock.ShortDate(sent.StartDateTime), zone, repeats: false)
            : null;

    /// <summary>
    /// A month-grid drag: the event moved by whole days, everything else — its place included — kept
    /// (issue #383 §5.2). Static so the rule is tested without the page's JS-driven grid. The days move the event's
    /// OWN wall-clock — in its zone when it has one — so its local time survives a daylight-saving change (#520 §3).
    /// </summary>
    internal static NewCalendarEvent DropUpdate(ExistingCalendarEvent existing, DateOnly toDate, DateOnly fromDate)
    {
        var deltaDays = toDate.DayNumber - fromDate.DayNumber;
        return CalendarWrites.Retimed(existing, existing.StartDateTime.AddDays(deltaDays), existing.EndDateTime.AddDays(deltaDays), AttachmentLinks.Keep);
    }

    /// <summary>
    /// A time-grid drag or resize: an explicit start and end, everything else kept. The grid hands back the drop in the
    /// VIEWER's zone; a zoned event's new wall-clock is taken in its own zone from that (issue #520 §3, v3 §J), and a
    /// floating event's is the dropped clock time itself.
    /// </summary>
    internal static NewCalendarEvent ResizeUpdate(ExistingCalendarEvent existing, DateTime newStart, DateTime newEnd, TimeZoneInfo? viewer = null)
    {
        if (CalendarZoneClock.IsZoned(existing) && Odyssey.Client.Pages.Journal.LocationVisuals.FindZone(existing.TimeZoneId) is { } zone)
        {
            return CalendarWrites.Retimed(existing, CalendarZoneClock.Convert(newStart, viewer ?? TimeZoneInfo.Utc, zone), CalendarZoneClock.Convert(newEnd, viewer ?? TimeZoneInfo.Utc, zone), AttachmentLinks.Keep);
        }

        return CalendarWrites.Retimed(existing, newStart, newEnd, AttachmentLinks.Keep);
    }

    // Time-grid drag/resize: set explicit start/end, hydrating the rest from the loaded event so
    // Description/Location/LocationId/CalendarId survive the PUT (the VM carries none of them).
    private async Task HandleEventChange((Guid Id, DateTime NewStart, DateTime NewEnd) args)
    {
        var existing = _events.FirstOrDefault(e => e.CalendarEventId == args.Id);
        if (existing is null)
        {
            return;
        }

        await RescheduleAsync(existing, ResizeUpdate(existing, args.NewStart, args.NewEnd, Viewer.Zone));
    }

    // ── Page-state persistence ───────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<CalendarPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(CalendarPageState state)
    {
        _view = state.View is "agenda" or "week" or "day" ? state.View : "month";
        _search = state.Search ?? string.Empty;
        _searchOpen = state.SearchOpen;
        _visibleCalendarIds = [.. (state.VisibleCalendarIds ?? [])];
        if (DateOnly.TryParseExact(state.Month, "yyyy-MM-dd", out var parsedAnchor))
        {
            _anchor = parsedAnchor;
        }
    }

    private CalendarPageState BuildPageState() => new()
    {
        View = _view,
        Search = _search,
        SearchOpen = _searchOpen,
        VisibleCalendarIds = [.. _visibleCalendarIds],
        Month = _anchor.ToString("yyyy-MM-dd"),
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private sealed class CalendarPageState
    {
        public string View { get; set; } = "month";
        public string Search { get; set; } = string.Empty;
        public bool SearchOpen { get; set; } = true;
        public List<Guid> VisibleCalendarIds { get; set; } = [];
        public string Month { get; set; } = string.Empty;
    }

    public void Dispose()
    {
        _disposed = true;
        AuthenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        _agendaWeatherLoader?.Dispose();
        _listLoader.Dispose();
    }
}
