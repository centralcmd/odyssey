using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
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
    /// already has the loaded event list to check <c>RecurrencePatternId</c> without a re-fetch. It carries the title
    /// the export toast names: the series' own in Series mode, the event's in Edit mode (issue #517 §3.1).</summary>
    [Parameter] public EventCallback<string?> OnExport { get; set; }

    /// <summary>An export is being prepared (issue #517 E5): Export is aria-disabled, shows a spinner, keeps focus
    /// and ignores clicks.</summary>
    [Parameter] public bool ExportBusy { get; set; }

    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    internal const string TimeZoneInputId = "cal-ev-tz";
    internal const string TimeZoneHelpId = "cal-ev-tz-help";
    internal const string TwoZoneLineId = "cal-ev-twozone";
    internal const string StartHintId = "cal-ev-start-dst";
    internal const string EndHintId = "cal-ev-end-dst";
    internal const string SaveButtonId = "cal-ev-save";
    internal const string AlertId = "cal-ev-alert";
    internal const string ConfirmHeadingId = "cal-ev-confirm-h";

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
    private ExistingRecurrencePattern? _sourcePattern;

    /// <summary>The fields the weather depends on, as loaded — the Weather section's dirty rule compares against it. A
    /// zone change moves the instant, so it counts (issue #520 v3 §K).</summary>
    private sealed record WeatherInputs(DateTime? StartDate, TimeSpan? StartTime, DateTime? EndDate, TimeSpan? EndTime, bool IsAllDay, LocationPick? Place, string? TimeZoneId);

    private WeatherInputs? _loadedWeatherInputs;

    private WeatherInputs CurrentWeatherInputs =>
        new(_draft.StartDate, _draft.StartTime, _draft.EndDate, _draft.EndTime, _draft.IsAllDay, _draft.Place.Pick, _draft.WriteZone);

    /// <summary>
    /// The date, time, all-day, zone or place differ from the loaded event, so the weather shown no longer describes the
    /// form (issue #363 §3.4). The place counts only where its field exists (<c>locations.read</c>).
    /// </summary>
    private bool WeatherDirty =>
        _loadedWeatherInputs is { } loaded
        && (CanReadLocations ? loaded != CurrentWeatherInputs : loaded with { Place = null } != CurrentWeatherInputs with { Place = null });
    private bool _isSaving;
    private bool _previousOpen;
    private (CalendarEventDialogMode Mode, Guid? EventId, Guid? PatternId) _previousTarget;

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

    // ── Time zones (issue #520 §3) ───────────────────────────────────────────

    private ViewerTimeZone _viewer = ViewerTimeZone.Browser;

    // The zone as loaded: null for a floating event, a new one, or an all-day one.
    private string? _storedZone;

    // The zone was changed in this session — the help line then says where the clock time now lands (FR13).
    private bool _zoneChanged;

    // The zone picker, asked on Save for text typed into it that names no zone: free text never becomes a value (§3, v3 §E).
    private OdsCombobox? _zoneCombo;

    private static readonly Func<string, string> ZoneEmptyText = query => $"No time zone matches “{query}”.";

    private static readonly Func<int, string> ZoneCountText = n => $"{n} {(n == 1 ? "time zone matches" : "time zones match")}";

    private bool OriginallyFloating =>
        Mode != CalendarEventDialogMode.Create && _storedZone is null
        && !(Mode == CalendarEventDialogMode.Series ? _sourcePattern?.IsAllDay ?? false : _sourceEvent?.IsAllDay ?? false);

    /// <summary>An occurrence of a floating series: the zone is read-only text, since a series never mixes (v4 §4).</summary>
    private bool ZoneReadOnly => OccurrenceOfSeries && _storedZone is null;

    /// <summary>"No time zone (floating)" is offered only for an event or series that is floating now (v4 §4).</summary>
    private bool AllowFloating => OriginallyFloating && !OccurrenceOfSeries;

    private TimeZoneInfo? EventZone => _draft.IsAllDay ? null : LocationVisuals.FindZone(_draft.TimeZoneId);

    private string? ZoneFieldValue => _draft.TimeZoneId ?? (AllowFloating ? CalendarZoneClock.FloatingValue : null);

    private IReadOnlyList<OdsOption>? _zoneOptions;
    private (DateTime? Date, string? Stored, bool Floating) _zoneOptionsKey;

    /// <summary>
    /// The zone picker's rows (issue #520 §3): "No time zone (floating)" for a floating event, a stored zone this browser
    /// does not know (kept, FR14), then every allow-listed zone with its UTC offset ON the event's start date (AX3) and a
    /// search key holding its city.
    /// </summary>
    internal IReadOnlyList<OdsOption> ZoneOptions
    {
        get
        {
            var key = (_draft.StartDate?.Date, _storedZone, AllowFloating);
            if (_zoneOptions is not null && key == _zoneOptionsKey)
                return _zoneOptions;

            var date = _draft.StartDate ?? DateTime.UtcNow.Date;
            var options = new List<OdsOption>();
            if (AllowFloating)
                options.Add(new OdsOption(CalendarZoneClock.FloatingValue, CalendarZoneClock.FloatingLabel) { SearchKey = "floating none" });
            if (_storedZone is { } stored && !LocationVisuals.TimeZoneIds.Contains(stored))
                options.Add(new OdsOption(stored, $"{stored} (not recognised here)"));
            options.AddRange(LocationVisuals.TimeZoneIds.Select(id =>
                new OdsOption(id, id) { Note = CalendarZoneClock.OffsetOn(id, date), SearchKey = CalendarZoneClock.SearchKey(id) }));
            _zoneOptionsKey = key;
            return _zoneOptions = options;
        }
    }

    private (DateTime Start, DateTime End)? ViewerTimes =>
        EventZone is { } zone && _draft.StartWall is { } start && _draft.EndWall is { } end
            ? (CalendarZoneClock.Convert(start, zone, _viewer.Zone), CalendarZoneClock.Convert(end, zone, _viewer.Zone))
            : null;

    /// <summary>Shown when the event's zone is not the viewer's (§3 "Times in two zones").</summary>
    private bool ShowTwoZone => EventZone is not null && !string.Equals(_draft.TimeZoneId, _viewer.ViewZoneId, StringComparison.Ordinal);

    /// <summary>"Europe/Oslo · 09:00–10:00 · your time 03:00–04:00", with the viewer's date when it differs (AX3).</summary>
    internal string? TwoZoneText
    {
        get
        {
            if (!ShowTwoZone || ViewerTimes is not { } viewer || _draft.StartWall is not { } start || _draft.EndWall is not { } end)
                return null;
            var text = $"{_draft.TimeZoneId} · {CalendarZoneClock.Hm(start)}–{CalendarZoneClock.Hm(end)} · your time "
                + $"{CalendarZoneClock.Hm(viewer.Start)}–{CalendarZoneClock.Hm(viewer.End)}";
            return viewer.Start.Date != start.Date ? $"{text} ({CalendarZoneClock.DayLabel(viewer.Start)})" : text;
        }
    }

    private string? DstHint(DateTime? wall)
    {
        if (EventZone is not { } zone || wall is not { } value)
            return null;
        CalendarZoneClock.ToUtc(value, zone, out var adjustment);
        return adjustment switch
        {
            ZoneAdjustment.Gap => CalendarEventCopy.GapHint(_draft.TimeZoneId!, CalendarZoneClock.Hm(CalendarZoneClock.Saved(value, zone))),
            ZoneAdjustment.Ambiguous => CalendarEventCopy.AmbiguousHint(_draft.TimeZoneId!),
            _ => null,
        };
    }

    internal string? StartHint => DstHint(_draft.StartWall);

    internal string? EndHint => DstHint(_draft.EndWall);

    private string? TimeDescribedBy(string? hint, string hintId) =>
        string.Join(' ', new[] { TwoZoneText is null ? null : TwoZoneLineId, hint is null ? null : hintId }.Where(id => id is not null))
            is { Length: > 0 } ids ? ids : null;

    /// <summary>The zone field's help line (§3, v4 §4).</summary>
    internal string? ZoneHelp
    {
        get
        {
            if (_draft.TimeZoneId is not { } zone)
                return CalendarEventCopy.FloatingHelp;

            var lines = new List<string>();
            if (_zoneChanged && _draft.StartWall is { } start && ViewerTimes is { } viewer)
                lines.Add(CalendarEventCopy.ZoneChanged(CalendarZoneClock.Hm(start), zone, CalendarZoneClock.Hm(viewer.Start), CalendarZoneClock.DayLabel(viewer.Start)));
            else if (Mode == CalendarEventDialogMode.Create && zone == DefaultZone)
                lines.Add(CalendarEventCopy.DefaultZoneHelp(DefaultZone));

            if (Mode == CalendarEventDialogMode.Series && OriginallyFloating)
                lines.Add(CalendarEventCopy.SeriesReadAs(zone));

            if (OccurrenceOfSeries)
            {
                if (!string.Equals(zone, _storedZone, StringComparison.Ordinal))
                    lines.Add(CalendarEventCopy.OccurrenceBecomesOverride);
                lines.Add(CalendarEventCopy.OccurrenceOtherViewers);
            }

            return lines.Count == 0 ? null : string.Join(' ', lines);
        }
    }

    private string ZoneDescribedBy =>
        string.Join(' ', new[] { ZoneHelp is null ? null : TimeZoneHelpId, _draft.TimeZoneError is null ? null : (ZoneHelp is null ? TimeZoneHelpId : $"{TimeZoneHelpId}-error") }
            .Where(id => id is not null).Distinct());

    private string ReadOnlyWhen
    {
        get
        {
            if (_draft.StartDate is not { } startDate)
                return string.Empty;
            if (_draft.IsAllDay)
            {
                return _draft.EndDate is { } endDate && endDate.Date != startDate.Date
                    ? $"{CalendarZoneClock.DayLabel(startDate)} – {CalendarZoneClock.DayLabel(endDate)} · All day"
                    : $"{CalendarZoneClock.DayLabel(startDate)} · All day";
            }

            if (ViewerTimes is { } viewer)
                return $"{CalendarZoneClock.DayLabel(viewer.Start)}, {CalendarZoneClock.Hm(viewer.Start)}–{CalendarZoneClock.Hm(viewer.End)} your time";

            return _draft.StartWall is { } start && _draft.EndWall is { } end
                ? $"{CalendarZoneClock.DayLabel(start)}, {CalendarZoneClock.Hm(start)}–{CalendarZoneClock.Hm(end)}"
                : string.Empty;
        }
    }

    /// <summary>The read-only header's zone line — the same text as the two-zone line, so the two cannot drift (FR17).</summary>
    internal string? ReadOnlyZone =>
        _draft.IsAllDay ? null : TwoZoneText ?? _draft.TimeZoneId ?? CalendarZoneClock.FloatingLabel;

    /// <summary>A new event's zone: the browser's, else <c>UTC</c> (AC 1).</summary>
    private string DefaultZone => _viewer.ViewZoneId;

    /// <summary>Free text that matches no zone leaves the field invalid, on blur (the picker reports it) and on Save (v3 §E).</summary>
    private void CheckZoneText()
    {
        if (_zoneCombo?.UnmatchedText is not null)
            _draft.TimeZoneError = CalendarEventCopy.ZoneError;
    }

    /// <summary>
    /// Blur with unmatched text (v3 §E): the field error, announced once as it appears — a visible error alone is not a
    /// status message (WCAG 4.1.3, PR #528 accessibility review). A repeat while it is still shown says nothing new.
    /// </summary>
    private void OnZoneUnmatched(string _)
    {
        if (_draft.TimeZoneError is not null)
            return;

        _draft.TimeZoneError = CalendarEventCopy.ZoneError;
        Announce(CalendarEventCopy.ZoneError);
    }

    private void OnZoneChanged(string? value)
    {
        if (value is null)
            return; // the field is not clearable; a clear would otherwise read as "floating" (v2 N5)

        var next = value == CalendarZoneClock.FloatingValue ? null : value;
        _draft.TimeZoneError = null;
        if (string.Equals(next, _draft.TimeZoneId, StringComparison.Ordinal))
            return;

        _draft.TimeZoneId = next;
        _zoneChanged = next is not null;
        // Changing the zone keeps the entered clock time; the instant moves, so say where it lands, once (FR13).
        if (next is not null && _draft.StartWall is { } start && ViewerTimes is { } viewer)
            Announce(CalendarEventCopy.ZoneChanged(CalendarZoneClock.Hm(start), next, CalendarZoneClock.Hm(viewer.Start), CalendarZoneClock.DayLabel(viewer.Start)));
    }

    // The DST hints are announced once, when each first appears (AX2).
    private string _lastHintKey = string.Empty;
    private int _announcerWaits;

    private void AnnounceNewHints()
    {
        var hints = new[] { StartHint, EndHint };
        var key = string.Join('|', hints);
        // The announcer lives in the dialog's portaled content, which can mount a render after this component's: wait
        // for it rather than spend the hint on nothing.
        if (key == _lastHintKey || _announcer is null)
            return;

        var fresh = hints.FirstOrDefault(h => h is not null && !_lastHintKey.Contains(h, StringComparison.Ordinal));
        _lastHintKey = key;
        if (fresh is not null)
            Announce(fresh);
    }

    // ── Exceptions (issue #520 §3 "Cues") ────────────────────────────────────

    /// <summary>"Edited separately" — an override, in every mode; never before its series is keyed (v5 nit).</summary>
    internal bool ShowEditedChip => Mode == CalendarEventDialogMode.Edit && _sourceEvent is { IsOverride: true, IsKeyed: true };

    internal string? ExclusionLine =>
        Mode == CalendarEventDialogMode.Series && _sourcePattern is { ExclusionCount: > 0 } pattern
            ? CalendarEventCopy.Exclusions(pattern.ExclusionCount)
            : null;

    // ── Series save: preview and confirm (§3, v3 §A–§D, v5 B1) ────────────────

    /// <summary>The confirm step: the counts the user is asked to accept, and the body they were computed for.</summary>
    internal sealed record ConfirmStep(int Overrides, int Exclusions, int Detached, bool Again, NewRecurrencePattern Body);

    private ConfirmStep? _confirm;

    /// <summary>The series' version when the dialog opened (v5 B1): what every series PUT sends as expectedVersion.</summary>
    private Guid _loadedVersion;

    /// <summary>Set while the version-conflict alert shows: the series' current version, for Keep editing (v3 §C).</summary>
    private Guid? _conflictVersion;

    private (Severity Severity, string Text)? _formAlert;

    // A preview 403: the claim was revoked mid-session; Save stays unavailable (v4 §7).
    private bool _forbidden;
    private bool _previewing;

    private bool SaveUnavailable => _isSaving || _previewing || _forbidden || _conflictVersion is not null;

    private bool _focusConfirm;
    private bool _focusSave;
    private bool _focusAlert;
    private bool _focusZone;

    protected override async Task OnParametersSetAsync()
    {
        // "Edit series…" closes and reopens the dialog in one page render, so Open never reads false here: a changed
        // mode or record reloads too, or the series form would show the occurrence's draft and save it with no loaded
        // version (found while building issue #520).
        var target = (Mode, EventId, PatternId);
        if (Open && (!_previousOpen || target != _previousTarget))
        {
            await LoadAsync();
        }

        _previousOpen = Open;
        _previousTarget = target;
    }

    private async Task LoadAsync()
    {
        _viewer = ViewerTimeZone.From(Services);
        _sourceEvent = null;
        _sourcePattern = null;
        _seriesTitle = null;
        _loadedWeatherInputs = null;
        _storedZone = null;
        _zoneChanged = false;
        _zoneOptions = null;
        _confirm = null;
        _conflictVersion = null;
        _formAlert = null;
        _forbidden = false;
        _lastHintKey = string.Empty;
        _announcerWaits = 0;

        switch (Mode)
        {
            case CalendarEventDialogMode.Edit when EventId is { } id:
                _sourceEvent = await CalendarApi.GetEventAsync(id);
                _draft = _sourceEvent is not null ? CalendarEventDraft.FromEvent(_sourceEvent, DefaultZone) : new CalendarEventDraft();
                _storedZone = _sourceEvent is { IsAllDay: false } loaded ? loaded.TimeZoneId : null;
                _loadedWeatherInputs = CurrentWeatherInputs;
                break;
            case CalendarEventDialogMode.Series when PatternId is { } patternId:
                await LoadPatternAsync(patternId);
                break;
            default:
                _draft = CalendarEventDraft.ForCreate(DefaultDate, DefaultTime, Calendars.FirstOrDefault()?.CalendarId, DefaultZone);
                break;
        }

        StateHasChanged();
    }

    private async Task LoadPatternAsync(Guid patternId)
    {
        var pattern = await CalendarApi.GetPatternAsync(patternId);
        _sourcePattern = pattern;
        _seriesTitle = pattern?.Title;
        _draft = pattern is not null ? CalendarEventDraft.FromPattern(pattern, DefaultZone) : new CalendarEventDraft();
        _storedZone = pattern is { IsAllDay: false } loaded ? loaded.TimeZoneId : null;
        _loadedVersion = pattern?.Version ?? Guid.Empty;
        _zoneOptions = null;
        _zoneChanged = false;
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

    // The series' title as loaded, not as edited: the export writes what is stored.
    private string? _seriesTitle;

    /// <summary>
    /// Export (issue #517 E5). A standalone event or a series starts its summary at once, so this dialog says it is
    /// preparing; a recurring occurrence first asks occurrence-or-series, and that prompt says it instead.
    /// </summary>
    private async Task ExportAsync()
    {
        if (ExportBusy)
        {
            return;
        }

        var direct = Mode == CalendarEventDialogMode.Series || _sourceEvent?.RecurrencePatternId is null;
        if (direct)
        {
            Announce(CalendarIcsExport.Preparing);
        }

        await OnExport.InvokeAsync(Mode == CalendarEventDialogMode.Series ? _seriesTitle : _sourceEvent?.Title);
    }

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
        if (SaveUnavailable)
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

    /// <summary>Validates, creates a staged place, then writes. True when the dialog should close.</summary>
    private async Task<bool> SaveAsync()
    {
        if (!_draft.IsAllDay && !ZoneReadOnly)
        {
            CheckZoneText();
        }

        if (!_draft.Validate(checkRule: Mode != CalendarEventDialogMode.Edit))
        {
            _focusZone = _draft.TimeZoneError is not null && !_draft.IsAllDay;
            return false;
        }

        // A staged place is created immediately before the record write; if that fails the save stops and
        // nothing else is written. A retry after a failed record write reuses the created id (issue #383 §3.2, §3.2a).
        if (!await _draft.Place.EnsureCreatedAsync(Locations, CanReadLocations))
        {
            Announce($"Couldn’t add {_draft.Place.StagedName}");
            return false;
        }

        _formAlert = null;
        var link = _draft.Place.Link(CanReadLocations);
        var place = _draft.Place;

        if (Mode == CalendarEventDialogMode.Series && PatternId is { } patternId)
        {
            _isSaving = false;
            return await PreviewSeriesAsync(patternId, _draft.ToNewRecurrencePattern(link), link, again: false);
        }

        if (Mode == CalendarEventDialogMode.Edit && EventId is { } eventId)
        {
            var updated = await CalendarApi.UpdateEventWithResultAsync(eventId, _draft.ToNewCalendarEvent(link));
            return await EventWrittenAsync(updated, "Unable to update event", "Event updated.", place, link);
        }

        if (_draft.Repeats)
        {
            var series = await CalendarApi.CreatePatternWithResultAsync(_draft.ToNewRecurrencePattern(link));
            if (!series.IsSuccess)
            {
                return await WriteFailedAsync(series.Status, series.Problem, series.Error, "Unable to create series", place, link);
            }

            ToastSaved("Series created.", [], series.Value?.StartDateTime, repeats: true);
            return true;
        }

        var created = await CalendarApi.CreateEventWithResultAsync(_draft.ToNewCalendarEvent(link));
        return await EventWrittenAsync(created, "Unable to create event", "Event created.", place, link);
    }

    /// <summary>An event write's outcome: its toast — the detached report and the DST adjustment (v6 F2, v3 §F) — or its failure.</summary>
    private async Task<bool> EventWrittenAsync(
        ApiResult<ExistingCalendarEvent> result, string failureLead, string successMessage, PlaceDraft place, LocationLink link)
    {
        if (!result.IsSuccess)
        {
            return await WriteFailedAsync(result.Status, result.Problem, result.Error, failureLead, place, link);
        }

        place.Error = null;
        var parts = new List<string>();
        if (result.Value is { Detached: true })
            parts.Add(CalendarEventCopy.DetachedEdit);
        if (result.Value is { DetachedCount: > 0 } detached)
            parts.Add(CalendarEventCopy.DetachedNow(detached.DetachedCount));
        ToastSaved(successMessage, parts, result.Value?.StartDateTime, repeats: false);
        return true;
    }

    /// <summary>
    /// One toast per save: the plain success message, or — when there is something to report — an Info toast carrying
    /// it in place of that message. A DST adjustment keeps it on screen until dismissed (v4 §6). The snackbar is the one
    /// channel that speaks it (v6 F1, <see cref="SnackbarAnnouncement"/>).
    /// </summary>
    private void ToastSaved(string successMessage, List<string> parts, DateTime? stored, bool repeats, Severity reportSeverity = Severity.Info, bool persistent = false)
    {
        if (AdjustedText(stored, repeats) is { } adjusted)
        {
            parts.Add(adjusted);
            persistent = true;
        }

        if (parts.Count == 0)
        {
            Snackbar.Add(successMessage, Severity.Success);
            return;
        }

        var text = string.Join(' ', parts);
        Snackbar.Add(text, reportSeverity, config => config.RequireInteraction = persistent, Guid.NewGuid().ToString("N"));
        if (SnackbarAnnouncement.Spoken(text) is { } spoken)
            Announce(spoken);
    }

    /// <summary>"Saved as 03:30. 02:30 doesn't exist on 29 Mar in Europe/Oslo." — from the server's stored value (v3 §F).</summary>
    private string? AdjustedText(DateTime? stored, bool repeats)
    {
        if (stored is not { } saved || _draft.WriteZone is not { } zone || _draft.StartWall is not { } entered || saved == entered)
            return null;
        return CalendarEventCopy.Adjusted(CalendarZoneClock.Hm(saved), CalendarZoneClock.Hm(entered), CalendarZoneClock.ShortDate(entered), zone, repeats);
    }

    /// <summary>
    /// A refused write: a gone record closes the dialog, a refused zone marks its field, a busy series says so, and
    /// anything else takes the place-aware toast path (issue #520 §7).
    /// </summary>
    private async Task<bool> WriteFailedAsync(
        HttpStatusCode status, ApiProblem? problem, string? error, string failureLead, PlaceDraft place, LocationLink link)
    {
        if (status == HttpStatusCode.NotFound && Mode != CalendarEventDialogMode.Create)
        {
            await CloseGoneAsync();
            return false;
        }

        if (problem?.ErrorFor("timeZoneId") is { } zoneError)
        {
            _draft.TimeZoneError = zoneError;
            _focusZone = true;
            return false;
        }

        if (string.Equals(problem?.Code, CalendarSeriesCodes.Busy, StringComparison.Ordinal))
        {
            ShowAlert(Severity.Warning, CalendarEventCopy.Busy);
            return false;
        }

        ApiResult.Failure(status, problem ?? new ApiProblem { Detail = error }).PlacedOrToast(Snackbar, failureLead, string.Empty, place, link);
        return false;
    }

    /// <summary>A record that has gone: a persistent toast as the dialog closes, focus to the page heading (v4 §6).</summary>
    private async Task CloseGoneAsync()
    {
        Snackbar.Add(CalendarEventCopy.NotFound, Severity.Error, config => config.RequireInteraction = true, Guid.NewGuid().ToString("N"));
        await OpenChanged.InvokeAsync(false);
        await OnSaved.InvokeAsync();
        try
        {
            await JS.InvokeVoidAsync("odsFocusChainOrHeading", Array.Empty<string>());
        }
        catch (Exception)
        {
            // Best-effort focus.
        }
    }

    private void ShowAlert(Severity severity, string text)
    {
        _confirm = null;
        _formAlert = (severity, text);
        _focusAlert = true;
    }

    /// <summary>
    /// The preview before a series save (§3 step 1): a failure never falls through to the PUT (v3 §B); a version other
    /// than the one loaded is a conflict with no confirm step (v5 B1); drops above 0 open the confirm step; none save.
    /// </summary>
    private async Task<bool> PreviewSeriesAsync(Guid patternId, NewRecurrencePattern body, LocationLink link, bool again)
    {
        _previewing = true;
        StateHasChanged();
        ApiResult<RecurrencePatternPreview> preview;
        try
        {
            preview = await CalendarApi.PreviewPatternAsync(patternId, body);
        }
        finally
        {
            _previewing = false;
        }

        if (!preview.IsSuccess || preview.Value is not { } counts)
        {
            if (preview.Status == HttpStatusCode.NotFound)
            {
                await CloseGoneAsync();
                return false;
            }

            if (preview.Status == HttpStatusCode.Forbidden)
            {
                _forbidden = true;
                ShowAlert(Severity.Error, CalendarEventCopy.Forbidden);
                return false;
            }

            ShowAlert(Severity.Error, preview.Status == HttpStatusCode.TooManyRequests ? CalendarEventCopy.RateLimited : CalendarEventCopy.PreviewFailed);
            return false;
        }

        if (counts.Version != _loadedVersion)
        {
            ShowConflict(counts.Version);
            return false;
        }

        if (counts.DroppedOverrideCount + counts.DroppedExclusionCount > 0)
        {
            _confirm = new ConfirmStep(counts.DroppedOverrideCount, counts.DroppedExclusionCount, counts.DetachedCount, again, body);
            _focusConfirm = true;
            return false;
        }

        return await PutSeriesAsync(patternId, body, link, 0, 0);
    }

    /// <summary>The series PUT, bound to the loaded version and the confirmed counts (v3 §A, v5 B1).</summary>
    private async Task<bool> PutSeriesAsync(Guid patternId, NewRecurrencePattern body, LocationLink link, int overrides, int exclusions)
    {
        _isSaving = true;
        StateHasChanged();
        ApiResult<RecurrencePatternUpdateResult> result;
        try
        {
            result = await CalendarApi.UpdatePatternWithResultAsync(patternId, CalendarWrites.Bound(body, _loadedVersion, overrides, exclusions));
        }
        finally
        {
            _isSaving = false;
        }

        if (result.IsSuccess && result.Value is { } done)
        {
            _confirm = null;
            ReportSeriesSaved(done);
            return true;
        }

        switch (result.Problem?.Code)
        {
            case CalendarSeriesCodes.DropsChanged:
                // Nothing was written: preview again and re-confirm with the new counts (v3 §A, v4 §6).
                return await PreviewSeriesAsync(patternId, body, link, again: true);
            case CalendarSeriesCodes.VersionConflict:
                // A series deleted meanwhile reads back as nothing: it has gone, not changed (PR #528 frontend review).
                if (await CalendarApi.GetPatternAsync(patternId) is not { } current)
                {
                    await CloseGoneAsync();
                    return false;
                }

                ShowConflict(current.Version);
                return false;
        }

        _confirm = null;
        return await WriteFailedAsync(result.Status, result.Problem, result.Error, "Unable to update series", _draft.Place, link);
    }

    /// <summary>
    /// The series save's one toast (§3 step 4, v6 F5): a persistent Warning with the actual drops in place of "Series
    /// updated.", plus any detached rows, the DST adjustment and the rows moved out of a gap.
    /// </summary>
    private void ReportSeriesSaved(RecurrencePatternUpdateResult result)
    {
        var parts = new List<string>();
        var drops = result.DroppedOverrideCount + result.DroppedExclusionCount > 0;
        if (drops)
            parts.Add(CalendarEventCopy.DropToast(result.DroppedOverrideCount, result.DroppedExclusionCount));
        if (result.DetachedCount > 0)
            parts.Add(CalendarEventCopy.DetachedNow(result.DetachedCount));
        var adjusted = AdjustedText(result.Pattern.StartDateTime, repeats: true);
        if (adjusted is not null)
            parts.Add(adjusted);
        if (result.AdjustedCount > 0 && adjusted is null)
            parts.Add(CalendarEventCopy.AdjustedCount(result.AdjustedCount));

        ToastSaved("Series updated.", parts, stored: null, repeats: true,
            drops ? Severity.Warning : Severity.Info, persistent: drops || adjusted is not null);
    }

    private void ShowConflict(Guid currentVersion)
    {
        _confirm = null;
        _formAlert = null;
        _conflictVersion = currentVersion;
        _focusAlert = true;
    }

    private async Task SaveAndDiscardAsync()
    {
        if (_confirm is not { } step || _isSaving || PatternId is not { } patternId)
        {
            return;
        }

        if (await PutSeriesAsync(patternId, step.Body, _draft.Place.Link(CanReadLocations), step.Overrides, step.Exclusions))
        {
            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
    }

    /// <summary>Back to editing — the button, or Escape inside the step: the step closes, focus returns to Save (v3 §D).</summary>
    private void BackToEditing()
    {
        if (_isSaving)
        {
            return;
        }

        _confirm = null;
        _focusSave = true;
    }

    private void OnConfirmKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            BackToEditing();
        }
    }

    /// <summary>Reload series: discards the draft and loads the series as it is now (v3 §C).</summary>
    private async Task ReloadSeriesAsync()
    {
        if (PatternId is not { } patternId)
        {
            return;
        }

        await LoadPatternAsync(patternId);
        _conflictVersion = null;
        _focusSave = true;
    }

    /// <summary>Keep editing: the draft stays, and the next Save previews against the current version (v3 §C).</summary>
    private void KeepEditing()
    {
        if (_conflictVersion is { } current)
        {
            _loadedVersion = current;
        }

        _conflictVersion = null;
        _focusSave = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!Open)
        {
            return;
        }

        AnnounceNewHints();
        if (_announcer is null && _announcerWaits++ < 3)
        {
            // The portaled body has not mounted yet: render once more so a hint present on open is still announced.
            _ = InvokeAsync(StateHasChanged);
        }

        var target = _focusConfirm ? ConfirmHeadingId : _focusAlert ? AlertId : _focusZone ? TimeZoneInputId : _focusSave ? SaveButtonId : null;
        _focusConfirm = _focusAlert = _focusZone = _focusSave = false;
        if (target is not null)
        {
            _focusTarget = target;
            _focusTries = 0;
        }

        if (_focusTarget is not { } pending)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", pending);
        }
        catch (Exception)
        {
            // Best-effort focus.
        }

        // The alert and the confirm step render in the dialog's portaled body, which can land a render after this
        // component's: focus again on the next render, once, so the move is not lost to that ordering.
        if (_focusTries++ < 1)
        {
            _ = InvokeAsync(StateHasChanged);
        }
        else
        {
            _focusTarget = null;
        }
    }

    private string? _focusTarget;
    private int _focusTries;

    /// <summary>The delete confirmation's body (issue #383 §3.9): the record goes, its place stays.</summary>
    internal static string DeleteMessage(string noun, string title) =>
        $"Permanently delete the {noun} “{title}”? This cannot be undone. {Odyssey.Client.Pages.Journal.LocationVisuals.RecordDeleteSentence}";

    /// <summary>
    /// An occurrence's delete confirmation (issue #520 FR6): the event sentence, then what an exclusion keeps. Every write
    /// keys a legacy series first, so the sentence holds for a series the backfill has not reached too (v4 §3).
    /// </summary>
    internal static string DeleteOccurrenceMessage(string title) =>
        $"{DeleteMessage("event", title)} {CalendarEventCopy.DeleteOccurrenceSentence}";

    private async Task<bool> ConfirmDeleteAsync(string noun)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            $"Delete {noun}",
            noun == "event" && OccurrenceOfSeries ? DeleteOccurrenceMessage(_draft.Title) : DeleteMessage(noun, _draft.Title),
            yesText: "Delete", cancelText: "Cancel");
        return confirmed == true;
    }

    private async Task DeleteEventAsync()
    {
        if (EventId is not { } id || !await ConfirmDeleteAsync("event"))
        {
            return;
        }

        var result = await CalendarApi.DeleteEventWithResultAsync(id);
        if (!result.IsSuccess)
        {
            if (result.Status == HttpStatusCode.NotFound)
            {
                await CloseGoneAsync();
                return;
            }

            if (string.Equals(result.Problem?.Code, CalendarSeriesCodes.Busy, StringComparison.Ordinal))
            {
                ShowAlert(Severity.Warning, CalendarEventCopy.Busy);
                return;
            }

            Snackbar.Add($"Unable to delete event: {result.Error}", Severity.Error);
            return;
        }

        var parts = new List<string>();
        if (result.Value is { Detached: true })
            parts.Add(CalendarEventCopy.DetachedDelete);
        if (result.Value is { DetachedCount: > 0 } report)
            parts.Add(CalendarEventCopy.DetachedNow(report.DetachedCount));
        if (parts.Count == 0)
            Snackbar.Add("Event deleted.", Severity.Success);
        else
            Snackbar.Add(string.Join(' ', parts), Severity.Info, _ => { }, Guid.NewGuid().ToString("N"));

        await OnSaved.InvokeAsync();
        await OpenChanged.InvokeAsync(false);
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

/// <summary>The <c>code</c> of a series write's <c>409</c> (issue #519 §9; #520 v3 §A table).</summary>
internal static class CalendarSeriesCodes
{
    public const string VersionConflict = "calendar-series-version-conflict";
    public const string DropsChanged = "calendar-series-drops-changed";
    public const string Busy = "calendar-series-busy";
}
