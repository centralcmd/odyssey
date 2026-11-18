using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

public partial class CalendarEventDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter] public CalendarEventDialogMode Mode { get; set; } = CalendarEventDialogMode.Create;
    [Parameter] public Guid? EventId { get; set; }
    [Parameter] public Guid? PatternId { get; set; }
    [Parameter] public DateOnly? DefaultDate { get; set; }
    [Parameter] public TimeSpan? DefaultTime { get; set; }
    [Parameter] public IReadOnlyList<ExistingCalendar> Calendars { get; set; } = [];

    /// <summary>Whether the current user may save edits (calendar.update). Create is gated upstream by the page.</summary>
    [Parameter] public bool CanUpdate { get; set; }

    /// <summary>Whether the current user may delete (calendar.delete).</summary>
    [Parameter] public bool CanDelete { get; set; }

    /// <summary><c>locations.read</c> — the Place picker. Without it the picker is hidden and the stored link is kept.</summary>
    [Parameter] public bool CanReadLocations { get; set; }

    /// <summary><c>locations.create</c> — the picker's staged <c>Create place "…"</c> row.</summary>
    [Parameter] public bool CanCreateLocations { get; set; }

    [Parameter] public EventCallback OnSaved { get; set; }
    [Parameter] public EventCallback<Guid> OnEditSeries { get; set; }

    /// <summary>Export clicked (Edit or Series mode; issue #340). The dialog stays open — the page owns
    /// deciding whether the current event is a standalone occurrence (download immediately), a
    /// recurring occurrence (prompt for occurrence-vs-series), or a series (export directly), since it
    /// already has the loaded event list to check <c>RecurrencePatternId</c> without a re-fetch.</summary>
    [Parameter] public EventCallback OnExport { get; set; }

    // A create is only ever opened for a user who holds calendar.create (the page gates it); edit/series
    // saves need calendar.update. When the user can't submit, the dialog is view-only (Close, no Save).
    private bool CanSubmit => Mode == CalendarEventDialogMode.Create || CanUpdate;

    private static readonly IReadOnlyList<OdsSegmentedOption> _repeatOptions =
    [
        new() { Value = "once", Label = "Does not repeat", Icon = "event_available" },
        new() { Value = "repeats", Label = "Repeats", Icon = "event_repeat" },
    ];

    private static readonly IReadOnlyList<OdsOption> _frequencyOptions =
    [
        new("Daily", "Daily"), new("Weekly", "Weekly"), new("Monthly", "Monthly"), new("Yearly", "Yearly"),
    ];

    private static readonly IReadOnlyList<OdsOption> _monthOptions =
    [
        new("1", "January"), new("2", "February"), new("3", "March"), new("4", "April"),
        new("5", "May"), new("6", "June"), new("7", "July"), new("8", "August"),
        new("9", "September"), new("10", "October"), new("11", "November"), new("12", "December"),
    ];

    private static readonly (DaysOfWeekFlags Flag, string Short)[] _dayToggles =
    [
        (DaysOfWeekFlags.Monday, "M"), (DaysOfWeekFlags.Tuesday, "T"), (DaysOfWeekFlags.Wednesday, "W"),
        (DaysOfWeekFlags.Thursday, "T"), (DaysOfWeekFlags.Friday, "F"), (DaysOfWeekFlags.Saturday, "S"), (DaysOfWeekFlags.Sunday, "S"),
    ];

    private CalendarEventDraft _draft = new();
    private OdsLiveAnnouncer? _announcer;

    /// <summary>
    /// Announces through the announcer's own method, never by setting its Message: an identical string (the
    /// same place failing to create twice — the usual retry) renders nothing new and would be silent the
    /// second time (WCAG 4.1.3; accessibility review 2).
    /// </summary>
    private void Announce(string message) => _ = _announcer?.Announce(message);
    private ExistingCalendarEvent? _sourceEvent;

    /// <summary>The fields the weather depends on, as loaded — the Weather section's dirty rule compares against it.</summary>
    private sealed record WeatherInputs(DateTime? StartDate, TimeSpan? StartTime, DateTime? EndDate, TimeSpan? EndTime, bool IsAllDay, LocationPick? Place);

    private WeatherInputs? _loadedWeatherInputs;

    private WeatherInputs CurrentWeatherInputs =>
        new(_draft.StartDate, _draft.StartTime, _draft.EndDate, _draft.EndTime, _draft.IsAllDay, _draft.Place.Pick);

    /// <summary>
    /// The date, time, all-day or place differ from the loaded event, so the weather shown no longer describes the
    /// form (issue #363 §3.4). The place counts only where its field exists (<c>locations.read</c>).
    /// </summary>
    private bool WeatherDirty =>
        _loadedWeatherInputs is { } loaded
        && (CanReadLocations ? loaded != CurrentWeatherInputs : loaded with { Place = null } != CurrentWeatherInputs with { Place = null });
    private bool _isSaving;
    private bool _previousOpen;

    private IReadOnlyList<OdsOption> _calendarOptions => [.. Calendars.Select(CalendarVisuals.Option)];

    private bool RepeatEditable => Mode == CalendarEventDialogMode.Create;

    private bool OccurrenceOfSeries => Mode == CalendarEventDialogMode.Edit && _sourceEvent?.RecurrencePatternId is not null;

    private string DialogIcon => Mode == CalendarEventDialogMode.Series ? "event_repeat" : "event";

    private string DialogTitleText => Mode switch
    {
        CalendarEventDialogMode.Series => "Edit recurring event",
        CalendarEventDialogMode.Edit => "Edit event",
        _ => "New event",
    };

    private string SubmitLabel => Mode switch
    {
        CalendarEventDialogMode.Series => "Save series",
        CalendarEventDialogMode.Edit => "Save changes",
        _ => _draft.Repeats ? "Create series" : "Create event",
    };

    protected override async Task OnParametersSetAsync()
    {
        if (Open && !_previousOpen)
        {
            await LoadAsync();
        }

        _previousOpen = Open;
    }

    private async Task LoadAsync()
    {
        _sourceEvent = null;
        _loadedWeatherInputs = null;

        switch (Mode)
        {
            case CalendarEventDialogMode.Edit when EventId is { } id:
                _sourceEvent = await CalendarApi.GetEventAsync(id);
                _draft = _sourceEvent is not null ? CalendarEventDraft.FromEvent(_sourceEvent) : new CalendarEventDraft();
                _loadedWeatherInputs = CurrentWeatherInputs;
                break;
            case CalendarEventDialogMode.Series when PatternId is { } patternId:
                var pattern = await CalendarApi.GetPatternAsync(patternId);
                _draft = pattern is not null ? CalendarEventDraft.FromPattern(pattern) : new CalendarEventDraft();
                break;
            default:
                _draft = CalendarEventDraft.ForCreate(DefaultDate, DefaultTime, Calendars.FirstOrDefault()?.CalendarId);
                break;
        }

        StateHasChanged();
    }

    private static string UnitFor(RecurrenceFrequency frequency) => frequency switch
    {
        RecurrenceFrequency.Daily => "day",
        RecurrenceFrequency.Weekly => "week",
        RecurrenceFrequency.Monthly => "month",
        _ => "year",
    };

    private void ToggleDay(DaysOfWeekFlags flag)
    {
        _draft.DaysOfWeek ^= flag;
        _draft.DaysError = null;
    }

    private Task CancelAsync() => OpenChanged.InvokeAsync(false);

    private void OnPlaceChanged(LocationPick? pick)
    {
        _draft.Place.Pick = pick;
        _draft.Place.Error = null;
        // A Kartverket address is staged like a new name (issue #383 v6 §3.2a); the create row announces through OnStage.
        if (pick is { IsStagedAddress: true })
            Announce($"{_draft.Place.StagedName} will be added when you save");
    }

    private void OnPlaceStaged(string name) => Announce($"{name} will be added when you save");

    private async Task EditSeriesClicked()
    {
        if (_sourceEvent?.RecurrencePatternId is { } patternId)
        {
            await OpenChanged.InvokeAsync(false);
            await OnEditSeries.InvokeAsync(patternId);
        }
    }

    private async Task SubmitAsync()
    {
        if (_isSaving)
        {
            return;
        }

        _isSaving = true;
        try
        {
            if (await SaveAsync())
            {
                await OnSaved.InvokeAsync();
                await OpenChanged.InvokeAsync(false);
            }
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task<bool> SaveAsync()
    {
        if (!_draft.Validate())
        {
            return false;
        }

        // A staged place is created immediately before the record write; if that fails the save stops and
        // nothing else is written. A retry after a failed record write reuses the created id (issue #383 §3.2, §3.2a).
        if (!await _draft.Place.EnsureCreatedAsync(Locations, CanReadLocations))
        {
            Announce($"Couldn’t add {_draft.Place.StagedName}");
            return false;
        }

        var link = _draft.Place.Link(CanReadLocations);
        var place = _draft.Place;

        if (Mode == CalendarEventDialogMode.Series && PatternId is { } patternId)
        {
            return (await CalendarApi.UpdatePatternAsync(patternId, _draft.ToNewRecurrencePattern(link)))
                .PlacedOrToast(Snackbar, "Unable to update series", "Series updated.", place, link);
        }

        if (Mode == CalendarEventDialogMode.Edit && EventId is { } eventId)
        {
            return (await CalendarApi.UpdateEventAsync(eventId, _draft.ToNewCalendarEvent(link)))
                .PlacedOrToast(Snackbar, "Unable to update event", "Event updated.", place, link);
        }

        if (_draft.Repeats)
        {
            return (await CalendarApi.CreatePatternAsync(_draft.ToNewRecurrencePattern(link)))
                .PlacedOrToast(Snackbar, "Unable to create series", "Series created.", place, link);
        }

        return (await CalendarApi.CreateEventAsync(_draft.ToNewCalendarEvent(link)))
            .PlacedOrToast(Snackbar, "Unable to create event", "Event created.", place, link);
    }

    /// <summary>The delete confirmation's body (issue #383 §3.9): the record goes, its place stays.</summary>
    internal static string DeleteMessage(string noun, string title) =>
        $"Permanently delete the {noun} “{title}”? This cannot be undone. {Odyssey.Client.Pages.Journal.LocationVisuals.RecordDeleteSentence}";

    private async Task<bool> ConfirmDeleteAsync(string noun)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            $"Delete {noun}",
            DeleteMessage(noun, _draft.Title),
            yesText: "Delete", cancelText: "Cancel");
        return confirmed == true;
    }

    private async Task DeleteEventAsync()
    {
        if (EventId is not { } id || !await ConfirmDeleteAsync("event"))
        {
            return;
        }

        if ((await CalendarApi.DeleteEventAsync(id)).Toast(Snackbar, "Unable to delete event", "Event deleted."))
        {
            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
    }

    private async Task DeleteSeriesAsync()
    {
        if (PatternId is not { } id || !await ConfirmDeleteAsync("series"))
        {
            return;
        }

        if ((await CalendarApi.DeletePatternAsync(id)).Toast(Snackbar, "Unable to delete series", "Series deleted."))
        {
            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
    }
}
