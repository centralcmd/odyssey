using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

public partial class ImportCalendarDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter] public IReadOnlyList<ExistingCalendar> Calendars { get; set; } = [];

    /// <summary>Pre-selected target calendar. Optional; falls back to the first calendar.</summary>
    [Parameter] public Guid? DefaultCalendarId { get; set; }

    /// <summary>Fires after an import that created or updated any rows, so the page reloads events.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private string? _calendarValue;
    private IReadOnlyList<OdsUploadFile> _files = [];
    private string? _calendarError;
    private string? _fileError;
    private bool _isImporting;

    private IcsImportResult? _result;
    private string _targetName = string.Empty;
    private string _targetColor = OdsCalendarSwatches.DefaultColor;
    private string? _announce;
    private bool _focusResultPending;
    private ElementReference _resultHeading;

    private readonly HashSet<string> _openReasons = new(StringComparer.Ordinal);

    private IReadOnlyList<OdsOption> _calendarOptions =>
        [.. Calendars.Select(c => new OdsOption(c.CalendarId.ToString(), c.Name) { Icon = "circle", IconColor = c.Color })];

    private int SkippedTotal => _result?.Skipped.Sum(g => g.Count) ?? 0;

    // Effective limit (issue #343 §3/§11) — read from the live IImportLimitsCache, which itself
    // falls back to the shipped default when the load fails, so the dialog still opens and accepts
    // a file at that limit rather than erroring out (fe C4 — the fallback lives in the cache, not
    // here; see ImportLimitsCache.Fallback).
    private int _maxImportMb = ImportLimitsCache.Fallback.CalendarIcsMaxImportMegabytes;
    private long _maxImportBytes = ImportLimitsCache.Fallback.CalendarIcsMaxImportMegabytes * 1024L * 1024;

    protected override void OnParametersSet()
    {
        // Seed the target selection once the dialog opens (default calendar, or the only/first one).
        if (Open && _calendarValue is null && _result is null)
        {
            _calendarValue = (DefaultCalendarId ?? Calendars.FirstOrDefault()?.CalendarId)?.ToString();
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!Open)
        {
            return;
        }

        var limits = await ImportLimits.GetAsync();
        _maxImportMb = limits.CalendarIcsMaxImportMegabytes;
        _maxImportBytes = _maxImportMb * 1024L * 1024;
    }

    private void OnCalendarSelected(string value)
    {
        _calendarValue = value;
        _calendarError = null;
    }

    private void OnFilesChanged(IReadOnlyList<OdsUploadFile> files)
    {
        _files = files;
        _fileError = null;
    }

    private async Task SubmitAsync()
    {
        if (_isImporting)
        {
            return;
        }

        _calendarError = null;
        _fileError = null;

        var calendarId = Guid.TryParse(_calendarValue, out var id) ? id : (Guid?)null;
        var file = _files.FirstOrDefault()?.Source;

        if (calendarId is null)
        {
            _calendarError = "Choose which calendar to import into.";
        }

        if (file is null)
        {
            _fileError = "Choose a .ics file to import.";
        }
        else if (file.Size > _maxImportBytes)
        {
            _fileError = $"That file is larger than the {_maxImportMb} MB limit.";
        }

        if (_calendarError is not null || _fileError is not null)
        {
            return;
        }

        _isImporting = true;
        try
        {
            // Same effective value used for the pre-check above and for OpenReadStream (issue #343 fe C2).
            var outcome = await CalendarApi.ImportAsync(calendarId!.Value, file!.ToApiUpload(_maxImportBytes));
            if (!outcome.IsSuccess)
            {
                _fileError = outcome.Error ?? "The file could not be imported.";
                return;
            }

            var calendar = Calendars.FirstOrDefault(c => c.CalendarId == calendarId);
            _targetName = calendar?.Name ?? "calendar";
            _targetColor = OdsCalendarSwatches.SwatchFor(calendar?.Color).Hex;
            _result = outcome.Value;

            if (_result!.ImportedCount > 0 || _result.UpdatedCount > 0)
            {
                await OnChanged.InvokeAsync();
            }

            _announce = $"{_result.ImportedCount} events imported, {_result.UpdatedCount} updated, {SkippedTotal} skipped.";
            _focusResultPending = true;
        }
        finally
        {
            _isImporting = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Move focus to the result heading once the summary renders (spec §3). The heading lives in
        // MudDialog's (deferred) dialog content, so its ElementReference can be unconfigured on the render
        // that first sets the result — guard on Id so FocusAsync never throws, treating focus as
        // best-effort (the summary is also announced via the live region).
        if (_focusResultPending && !string.IsNullOrEmpty(_resultHeading.Id))
        {
            _focusResultPending = false;
            try
            {
                await _resultHeading.FocusAsync();
            }
            catch (Exception)
            {
                // The element went away between the guard and the focus call — ignore.
            }
        }
    }

    private void ToggleReason(string reason)
    {
        if (!_openReasons.Remove(reason))
        {
            _openReasons.Add(reason);
        }
    }

    private void Reset()
    {
        _result = null;
        _files = [];
        _calendarError = null;
        _fileError = null;
        _announce = null;
        _openReasons.Clear();
    }

    private Task CloseAsync()
    {
        // Reset so a subsequent open starts clean (OnParametersSet re-seeds the target); page owns Open.
        Reset();
        _calendarValue = null;
        return OpenChanged.InvokeAsync(false);
    }
}
