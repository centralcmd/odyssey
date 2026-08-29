using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
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

    private IReadOnlyCollection<string> _calendarValues => [.. _calendarIds.Select(id => id.ToString())];

    private IReadOnlyList<OdsOption> _calendarOptions =>
        [.. Calendars.Select(c => new OdsOption(c.CalendarId.ToString(), c.Name) { Icon = "circle", IconColor = c.Color })];

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
        try
        {
            var outcome = await CalendarApi.ExportAggregateAsync(from, to, calendarIds, search);
            if (!outcome.IsSuccess || outcome.Value is not { } file)
            {
                SetError(outcome.Error ?? "The events could not be exported.");
                return;
            }

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/calendar");
            Snackbar.Add($"Exported {file.FileName}.", Severity.Success);
            await OpenChanged.InvokeAsync(false);
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
