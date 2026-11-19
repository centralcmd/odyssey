using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

public partial class ExportCalendarEventsDialog
{
    private const int MaxSpanDays = 92;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter] public IReadOnlyList<ExistingCalendar> Calendars { get; set; } = [];

    /// <summary>Prefill: the page's currently visible date range.</summary>
    [Parameter] public DateTime? InitialFrom { get; set; }
    [Parameter] public DateTime? InitialTo { get; set; }

    /// <summary>Prefill: the page's active calendar filter.</summary>
    [Parameter] public IReadOnlyCollection<Guid> InitialCalendarIds { get; set; } = [];

    /// <summary>Prefill: the page's active search term.</summary>
    [Parameter] public string? InitialSearch { get; set; }

    [Inject] private IServiceProvider Services { get; set; } = default!;

    private static readonly IReadOnlyList<OdsSegmentedOption> _scopeOptions =
    [
        new() { Value = "all", Label = "All events", Icon = "calendar_month" },
        new() { Value = "filtered", Label = "Filtered", Icon = "filter_list" },
    ];

    private string _scope = "filtered";
    private DateTime? _from;
    private DateTime? _to;
    private HashSet<Guid> _calendarIds = [];
    private string _search = string.Empty;
    private string? _error;
    private bool _busy;
    private bool _wasOpen;
    private bool _focusErrorPending;
    private ElementReference _errorAlert;
    private OdsLiveAnnouncer? _announcer;

    private IReadOnlyCollection<string> _calendarValues => [.. _calendarIds.Select(id => id.ToString())];

    private IReadOnlyList<OdsOption> _calendarOptions =>
        [.. Calendars.Select(CalendarVisuals.Option)];

    protected override void OnParametersSet()
    {
        // Every open reseeds from the page's current state (closed→open edge only — OnParametersSet
        // reruns on every re-render while Open stays true, e.g. after the user edits a field).
        if (Open && !_wasOpen)
        {
            _scope = "filtered";
            _from = InitialFrom;
            _to = InitialTo;
            _calendarIds = [.. InitialCalendarIds];
            _search = InitialSearch ?? string.Empty;
            _error = null;
        }

        _wasOpen = Open;
    }

    private void OnScopeChanged(string value)
    {
        _scope = value;
        _error = null;
    }

    private void OnCalendarsChanged(IReadOnlyCollection<string> values)
    {
        _calendarIds = [.. values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];
    }

    private async Task SubmitAsync()
    {
        if (_busy)
        {
            return;
        }

        _error = null;

        DateTime? from = null;
        DateTime? to = null;
        IReadOnlyCollection<Guid>? calendarIds = null;
        string? search = null;
        var viewer = Odyssey.Client.Services.ViewerTimeZone.From(Services);

        if (_scope == "filtered")
        {
            if (_from is null != _to is null)
            {
                SetError("Choose both a start and an end date, or clear both to export without a date range.");
                return;
            }

            if (_from is { } f && _to is { } t)
            {
                if (t < f)
                {
                    SetError("The start date is after the end date.");
                    return;
                }

                if ((t - f).TotalDays > MaxSpanDays)
                {
                    SetError($"That date range spans more than {MaxSpanDays} days. Choose a shorter range.");
                    return;
                }
            }

            from = _from;
            to = _to;
            calendarIds = _calendarIds.Count > 0 ? _calendarIds : null;
            search = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim();
        }

        _busy = true;
        _ = _announcer?.Announce(CalendarIcsExport.Preparing);
        try
        {
            // Summary first (issue #517 E2): its refusal is shown here, and no download starts.
            // The dates are the viewer's days: sent as the instants of their local midnights, with the viewer's zone, so a
            // zoned event is in the export exactly when the grid shows it in the range (issue #520 v4 §1, AC 22). A zone the
            // server does not know is retried once in UTC, which the session keeps (PR #528); the download uses the same.
            DateTime? Instant(DateTime? day) => day is { } d ? CalendarZoneClock.ToUtc(d.Date, viewer.Zone) : null;
            var summary = await viewer.WithFallbackAsync(CalendarPage.ViewZoneField,
                () => CalendarApi.SummarizeAggregateExportAsync(Instant(from), Instant(to), calendarIds, search, viewer.ViewZoneId));
            if (!summary.IsSuccess || summary.Value is not { } counts)
            {
                SetError(summary.Error ?? "The events could not be exported.");
                return;
            }

            await OpenChanged.InvokeAsync(false);
            await NativeTransfer.DownloadAsync(CalendarApi.ExportAggregateUrl(Instant(from), Instant(to), calendarIds, search, viewer.ViewZoneId));
            CalendarIcsExport.ToastStarted(Snackbar, CalendarIcsExport.AllToast(counts));
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetError(string message)
    {
        _error = message;
        _focusErrorPending = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Move focus to the error alert when validation/submission fails (WCAG 3.3.1) — the dialog
        // stays open so the user can fix and retry, mirroring ImportCalendarDialog's result-heading
        // focus pattern. tabindex="-1" on the wrapper makes an otherwise non-interactive div focusable.
        if (_focusErrorPending && !string.IsNullOrEmpty(_errorAlert.Id))
        {
            _focusErrorPending = false;
            try
            {
                await _errorAlert.FocusAsync();
            }
            catch (Exception)
            {
                // The element went away between the guard and the focus call — ignore.
            }
        }
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);
}
