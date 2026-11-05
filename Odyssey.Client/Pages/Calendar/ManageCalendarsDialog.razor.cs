using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

public partial class ManageCalendarsDialog
{
    [Inject] private ICalendarApiClient CalendarApi { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter] public IReadOnlyList<ExistingCalendar> Calendars { get; set; } = [];
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>Export a calendar as an .ics file (per-row download action). Handled by the page.</summary>
    [Parameter] public EventCallback<ExistingCalendar> OnExport { get; set; }

    /// <summary>The Color field's help line.</summary>
    public const string ColorHelp = "Shown on this calendar's events.";

    private OdsTagColorPicker? _colorPicker;
    private bool _colorValid = true;
    private bool _colorShowErrors;
    private string? _colorError;

    private void ResetColorState()
    {
        _colorValid = true;
        _colorShowErrors = false;
        _colorError = null;
    }

    private void OnColorChanged(RowDraft draft, string? value)
    {
        // AllowNone="false" never emits null; keep the last colour if it ever did.
        draft.Color = value ?? draft.Color;
        _colorError = null;
    }

    private void OnColorValidChanged(bool valid)
    {
        _colorValid = valid;
        if (valid)
        {
            _colorShowErrors = false;
        }
    }

    // Custom with an empty or invalid hex blocks submit with the picker's own message, focus on the field.
    private async Task<bool> ColorBlocksSaveAsync()
    {
        if (_colorValid)
        {
            return false;
        }

        _colorShowErrors = true;
        StateHasChanged();
        if (_colorPicker is not null)
        {
            await _colorPicker.FocusErrorAsync();
        }

        return true;
    }

    // The picker only offers what the server accepts, so a 400 on color is a stale client or a race.
    private async Task<bool> ShowServerColorErrorAsync(Odyssey.ApiClient.ApiResult result)
    {
        if (result.Problem?.ErrorFor("color") is not { } colorError)
        {
            return false;
        }

        _colorError = colorError;
        StateHasChanged();
        if (_colorPicker is not null)
        {
            await _colorPicker.FocusErrorAsync();
        }

        return true;
    }

    private Guid? _editingId;
    private RowDraft _editDraft = new();
    private string? _editError;

    private bool _adding;
    private RowDraft _newDraft = new();
    private string? _addError;

    private bool _isSaving;

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);

    private void StartEdit(ExistingCalendar calendar)
    {
        _editingId = calendar.CalendarId;
        _editDraft = new RowDraft { Name = calendar.Name, Description = calendar.Description ?? string.Empty, Color = calendar.Color };
        _editError = null;
        _adding = false;
        ResetColorState();
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editError = null;
        ResetColorState();
    }

    private async Task SaveEditAsync()
    {
        if (_editingId is not { } id || _isSaving)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_editDraft.Name))
        {
            _editError = "Name is required.";
            return;
        }

        if (await ColorBlocksSaveAsync())
        {
            return;
        }

        _isSaving = true;
        try
        {
            var result = await CalendarApi.UpdateCalendarAsync(id, ToNewCalendar(_editDraft));
            if (result.IsSuccess)
            {
                _editingId = null;
                _editError = null;
                await OnChanged.InvokeAsync();
            }
            else if (!await ShowServerColorErrorAsync(result))
            {
                _editError = result.Status == HttpStatusCode.Conflict
                    ? "A calendar with that name already exists."
                    : result.Error;
            }
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task SaveNewAsync()
    {
        if (_isSaving)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_newDraft.Name))
        {
            _addError = "Name is required.";
            return;
        }

        if (await ColorBlocksSaveAsync())
        {
            return;
        }

        _isSaving = true;
        try
        {
            var result = await CalendarApi.CreateCalendarAsync(ToNewCalendar(_newDraft));
            if (result.IsSuccess)
            {
                _newDraft = new RowDraft();
                _adding = false;
                _addError = null;
                await OnChanged.InvokeAsync();
            }
            else if (!await ShowServerColorErrorAsync(result))
            {
                _addError = result.Status == HttpStatusCode.Conflict
                    ? "A calendar with that name already exists."
                    : result.Error;
            }
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task DeleteAsync(ExistingCalendar calendar)
    {
        if ((await CalendarApi.DeleteCalendarAsync(calendar.CalendarId)).Toast(Snackbar, "Unable to delete calendar", "Calendar deleted."))
        {
            if (_editingId == calendar.CalendarId)
            {
                _editingId = null;
            }

            await OnChanged.InvokeAsync();
        }
    }

    private static NewCalendar ToNewCalendar(RowDraft draft) => new()
    {
        Name = draft.Name.Trim(),
        Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim(),
        Color = draft.Color,
    };

    private sealed class RowDraft
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Color { get; set; } = OdsCalendarSwatches.DefaultColor;
    }
}
