using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
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

    // Reference date for the active view: the shown month (month/agenda), the week containing it
    // (week), or the shown day (day). A real date, not forced to the 1st, so week/day keep their day.
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Now);
    private string _view = "month";
    private string _search = string.Empty;
    private bool _searchOpen = true;
    private HashSet<Guid> _visibleCalendarIds = [];

    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;

    // Managing calendars (create / rename / delete calendars) needs any one of the write claims.
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
                    ? $"{start:MMM d} – {end:MMM d, yyyy}"
                    : $"{start:MMM d, yyyy} – {end:MMM d, yyyy}";
            }

            return _anchor.ToString("MMMM yyyy");
        }
    }

    private IReadOnlyCollection<string> _visibleCalendarValues =>
        [.. _visibleCalendarIds.Select(id => id.ToString())];

    private IReadOnlyList<OdsOption> _calendarOptions =>
        [.. _calendars.Select(c => new OdsOption(c.CalendarId.ToString(), c.Name))];

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

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

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
        var from = days[0].ToDateTime(TimeOnly.MinValue);
        var to = days[^1].AddDays(1).ToDateTime(TimeOnly.MinValue);

        // Track failure explicitly: ItemsOrToast falls back to [], which is indistinguishable from a
        // period that genuinely has no events.
        var result = await CalendarApi.ListEventsAsync(from, to);
        _events = result.ItemsOrToast(Snackbar, "calendar events");
        _loadError = !result.IsSuccess;
        RebuildViewModels();

        _isLoading = false;
        StateHasChanged();
    }

    private Task ReloadEventsAsync() => LoadEventsAsync();

    private async Task OnCalendarsChangedAsync()
    {
        await LoadCalendarsAsync();
        RebuildViewModels();
        StateHasChanged();
    }

    // Export: fetch the .ics bytes (the client toasts on 403/404) and save via the shared download
    // interop — the same mechanism every file-download site uses. Guarded against a re-entrant
    // double-click firing concurrent downloads.
    private bool _exporting;

    private async Task ExportCalendarAsync(ExistingCalendar calendar)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            var result = await CalendarApi.ExportAsync(calendar.CalendarId);
            if (result.OrToast(Snackbar, "Unable to export the calendar") is not { } file)
            {
                return;
            }

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/calendar");
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
    private async Task ExportEventDialogAsync()
    {
        if (_eventDialogMode == CalendarEventDialogMode.Series && _eventDialogPatternId is { } patternId)
        {
            await ExportPatternDownloadAsync(patternId);
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

        await ExportEventDownloadAsync(eventId);
    }

    private Task ExportScopeChosenAsync(CalendarEventExportScope scope) => scope switch
    {
        CalendarEventExportScope.Series when _exportScopeEvent?.RecurrencePatternId is { } patternId =>
            ExportPatternDownloadAsync(patternId),
        _ when _exportScopeEvent is { } evt => ExportEventDownloadAsync(evt.CalendarEventId),
        _ => Task.CompletedTask,
    };

    // ExportEventScopeDialog renders stacked on top of the still-open CalendarEventDialog (no
    // close-then-open sequencing), so closing it — via Cancel, Esc, backdrop click, or choosing a
    // scope — would otherwise drop focus to <body> (WCAG 2.4.3). Restore it to the Export button that
    // opened this prompt; every dismissal path funnels through OpenChanged(false).
    private async Task OnExportScopeOpenChangedAsync(bool open)
    {
        _exportScopeOpen = open;
        if (!open)
        {
            try
            {
                await JS.InvokeVoidAsync("odsFocusById", "cal-export-btn");
            }
            catch (Exception)
            {
                // Best-effort focus return.
            }
        }
    }

    private async Task ExportEventDownloadAsync(Guid eventId)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            var result = await CalendarApi.ExportEventAsync(eventId);
            if (result.OrToast(Snackbar, "Unable to export the event") is not { } file)
            {
                return;
            }

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/calendar");
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task ExportPatternDownloadAsync(Guid patternId)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            var result = await CalendarApi.ExportPatternAsync(patternId);
            if (result.OrToast(Snackbar, "Unable to export the series") is not { } file)
            {
                return;
            }

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/calendar");
        }
        finally
        {
            _exporting = false;
        }
    }

    // "Export all as iCalendar" — a direct, no-dialog aggregate export of every event across every
    // calendar (bounded by the server's caps). "Export filtered…" opens ExportCalendarEventsDialog
    // instead, since that scope needs a form (date range / calendars / search).
    private async Task ExportAllEventsAsync()
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            var result = await CalendarApi.ExportAggregateAsync(null, null, null, null);
            // The client returns a result rather than presenting anything; this action has no dialog
            // to show an inline error in, so surface it as a toast here.
            if (result.OrToast(Snackbar, "Unable to export events") is not { } file)
            {
                return;
            }

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/calendar");
        }
        finally
        {
            _exporting = false;
        }
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
            : _events.Where(e =>
                e.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (e.Location?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        _viewModels = [.. filtered.Select(ToVm)];
    }

    private CalendarEventVm ToVm(ExistingCalendarEvent e)
    {
        var calendar = _calendars.FirstOrDefault(c => c.CalendarId == e.CalendarId);
        var swatch = OdsCalendarSwatches.SwatchFor(calendar?.Color);
        return new CalendarEventVm(
            e.CalendarEventId, e.CalendarId, calendar?.Name ?? "Unknown",
            e.Title, e.StartDateTime, e.EndDateTime, e.IsAllDay,
            swatch.Hex, swatch.Fg, e.RecurrencePatternId is not null);
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

    private void OpenEventDialog(Guid eventId)
    {
        _eventDialogMode = CalendarEventDialogMode.Edit;
        _eventDialogEventId = eventId;
        _eventDialogPatternId = null;
        _eventDialogOpen = true;
    }

    private void OpenSeriesDialog(Guid patternId)
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

        var deltaDays = args.ToDate.DayNumber - args.FromDate.DayNumber;
        var update = new NewCalendarEvent
        {
            CalendarId = existing.CalendarId,
            Title = existing.Title,
            Description = existing.Description,
            Location = existing.Location,
            StartDateTime = existing.StartDateTime.AddDays(deltaDays),
            EndDateTime = existing.EndDateTime.AddDays(deltaDays),
            IsAllDay = existing.IsAllDay,
        };

        if ((await CalendarApi.UpdateEventAsync(existing.CalendarEventId, update)).Toast(Snackbar, "Unable to reschedule event", "Event rescheduled."))
        {
            await ReloadEventsAsync();
        }
    }

    // Time-grid drag/resize: set explicit start/end, hydrating the rest from the loaded event so
    // Description/Location/CalendarId survive the PUT (the VM carries none of them).
    private async Task HandleEventChange((Guid Id, DateTime NewStart, DateTime NewEnd) args)
    {
        var existing = _events.FirstOrDefault(e => e.CalendarEventId == args.Id);
        if (existing is null)
        {
            return;
        }

        var update = new NewCalendarEvent
        {
            CalendarId = existing.CalendarId,
            Title = existing.Title,
            Description = existing.Description,
            Location = existing.Location,
            StartDateTime = args.NewStart,
            EndDateTime = args.NewEnd,
            IsAllDay = existing.IsAllDay,
        };

        if ((await CalendarApi.UpdateEventAsync(existing.CalendarEventId, update)).Toast(Snackbar, "Unable to reschedule event", "Event rescheduled."))
        {
            await ReloadEventsAsync();
        }
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
}
