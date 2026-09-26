using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// New / Edit dialog for one entry in a property's event log (issue #209).
/// </summary>
/// <remarks>
/// Every rule here MIRRORS the server's and never replaces it: the matrix is the shared
/// <see cref="PropertyEventTypeMatrix"/> itself, not a copy, and the future bound uses the server's
/// tolerance against "now at submit". The server still decides; a <c>422</c> it returns is put on the
/// control its <c>errors</c> key names.
/// </remarks>
public partial class AddPropertyEventDialog
{
    /// <summary>The server's forward tolerance on <c>occurredAt</c> (issue #209 §8.3).</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(60);

    private const string NoSideEffects =
        "Recording an event does not change the property. To set its acquired or disposed date, edit the property.";

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter, EditorRequired] public ExistingProperty Property { get; set; } = default!;

    /// <summary>The event being edited, or null for the add.</summary>
    [Parameter] public ExistingPropertyEvent? Event { get; set; }

    /// <summary>Raised after a successful write so the host re-reads the log.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private PropertyEventType _type = PropertyEventType.Other;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _notes = string.Empty;
    private DateTime? _date;
    private TimeSpan? _time;
    private readonly Dictionary<string, string> _errors = [];
    private bool _isSaving;

    private bool IsEdit => Event is not null;

    private bool IsSystemEvent => Event?.Source == ContractEventSource.System;

    private DateTime Today => DateTime.UtcNow.Date;

    /// <summary>The system-only member an edited row already carries, which the PUT may keep.</summary>
    private PropertyEventType? KeepType =>
        Event is { } ev && PropertyEventTypeMatrix.IsSystemOnly(ev.Type) ? ev.Type : null;

    private string TitlePlaceholder => Property.Type == PropertyType.Vehicle
        ? "e.g. Winter tyres on"
        : "e.g. Boiler serviced";

    /// <summary>
    /// What the chosen type means, then — for Valued — that it is not an estimate, then that recording
    /// an event changes nothing about the property. It rides the Type field's help, so a screen-reader
    /// user meets it too.
    /// </summary>
    private string TypeHelp => string.Join(" ", new[]
    {
        TypeMeaning,
        _type == PropertyEventType.Valued ? "This is a log line, not an estimate — it does not change the estimated value." : null,
        NoSideEffects,
    }.Where(part => part is not null));

    private string TypeMeaning => _type switch
    {
        PropertyEventType.Acquired => "Bought, inherited or received.",
        PropertyEventType.Disposed => "Sold, scrapped or written off.",
        PropertyEventType.Valued => "An appraisal or valuation was obtained.",
        PropertyEventType.Maintenance => "Routine upkeep.",
        PropertyEventType.Repair => "A defect was fixed.",
        PropertyEventType.Damage => "An incident, accident, storm or water damage.",
        PropertyEventType.Inspection => "A general inspection or survey.",
        PropertyEventType.InsuranceChanged => "A policy was taken out, renewed or changed.",
        PropertyEventType.Renovation => "A renovation or extension.",
        PropertyEventType.TaxAssessed => "A property-tax assessment.",
        PropertyEventType.TenancyStarted => "Let to a tenant.",
        PropertyEventType.TenancyEnded => "A tenancy came to an end.",
        PropertyEventType.Serviced => "A workshop service.",
        PropertyEventType.TyreChange => "Seasonal or replacement tyres.",
        PropertyEventType.PeriodicInspection => "The statutory roadworthiness test (e.g. EU-kontroll).",
        PropertyEventType.Registered => "Registered, re-registered or deregistered.",
        PropertyEventType.Archived => "The property was archived.",
        PropertyEventType.Unarchived => "The property was restored from the archive.",
        PropertyEventType.AcquisitionDateCleared => "The acquired date was removed.",
        PropertyEventType.DisposalReversed => "The disposed date was removed.",
        _ => "Anything the named types do not cover. The title carries it.",
    };

    /// <summary>
    /// The load-bearing copy for a recorded row, on the field that takes focus so it is in the Title's
    /// <c>aria-describedby</c>. Null on every other row.
    /// </summary>
    private string? SystemTitleHelp => IsSystemEvent
        ? $"Recorded automatically when {AutoClause}. Editing changes this log line only; it does not change the property."
        : null;

    private string AutoClause => Event?.Type switch
    {
        PropertyEventType.Acquired => "the acquired date was set",
        PropertyEventType.Disposed => "the disposed date was set",
        PropertyEventType.Archived => "the property was archived",
        PropertyEventType.Unarchived => "the property was restored from the archive",
        PropertyEventType.AcquisitionDateCleared => "the acquired date was removed",
        PropertyEventType.DisposalReversed => "the disposed date was removed",
        _ => "the application made this change",
    };

    protected override void OnInitialized()
    {
        if (Event is { } ev)
        {
            _type = ev.Type;
            _title = ev.Title;
            _description = ev.Description ?? string.Empty;
            _notes = ev.Notes ?? string.Empty;
            _date = ev.OccurredAt.Date;
            _time = ev.OccurredAt.TimeOfDay;
            return;
        }

        var now = DateTime.UtcNow;
        _date = now.Date;
        _time = new TimeSpan(now.Hour, now.Minute, 0);
    }

    private void PickType(string value)
    {
        if (Enum.TryParse<PropertyEventType>(value, out var next))
        {
            _type = next;
            _errors.Remove("type");
        }
    }

    private void OnTitleChanged(string value)
    {
        _title = value;
        _errors.Remove("title");
    }

    private void OnDateChanged(DateTime? value)
    {
        _date = value;
        _errors.Remove("occurredAt");
    }

    private void OnTimeChanged(TimeSpan? value)
    {
        _time = value ?? TimeSpan.Zero;
        _errors.Remove("occurredAt");
    }

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);

    private async Task SubmitAsync()
    {
        if (_isSaving)
            return;

        _errors.Clear();

        var title = _title.Trim();
        if (title.Length == 0)
        {
            _errors["title"] = "Give this event a title — what happened, in your own words.";
        }

        if (_date is null)
        {
            _errors["occurredAt"] = "Say when this happened.";
        }

        var occurredAt = OccurredAtUtc();
        if (occurredAt is { } when && when > DateTime.UtcNow + FutureTolerance)
        {
            _errors["occurredAt"] = "This can’t be in the future — events record what has already happened.";
        }

        // The picker only offers legal members, so these are unreachable through it; they guard a
        // stale state (e.g. a type picked before the property's list changed) before the round trip.
        var label = OdsTypeRegistries.PropertyEventTypeOf(_type).Label;
        if (!PropertyEventTypeMatrix.IsLegal(Property.Type, _type))
        {
            _errors["type"] = $"{label} can’t be recorded on {(Property.Type == PropertyType.Vehicle ? "a vehicle" : "real estate")}.";
        }
        else if (PropertyEventTypeMatrix.IsSystemOnly(_type) && _type != KeepType)
        {
            _errors["type"] = $"{label} is recorded automatically and can’t be chosen.";
        }

        if (_errors.Count > 0 || occurredAt is not { } value)
            return;

        _isSaving = true;
        try
        {
            var description = Blank(_description);
            var notes = Blank(_notes);

            var written = Event is { } existing
                ? await Properties.UpdateEventAsync(Property.PropertyId, existing.PropertyEventId, new UpdatePropertyEvent
                {
                    Type = _type,
                    Title = title,
                    Description = description,
                    Notes = notes,
                    OccurredAt = value,
                })
                : await Properties.CreateEventAsync(Property.PropertyId, new NewPropertyEvent
                {
                    Type = _type,
                    Title = title,
                    Description = description,
                    Notes = notes,
                    OccurredAt = value,
                });

            // Both writes read the event back; only the outcome matters here, since the host re-reads
            // the log.
            var result = new ApiResult { Status = written.Status, Problem = written.Problem };

            // Keyed on the FIELD the server names, never on message text.
            if (!result.IsSuccess && InlineErrorFor(result.Problem) is { } inline)
            {
                _errors[inline.Field] = inline.Message;
                return;
            }

            var ok = result.Toast(Snackbar,
                IsEdit ? "Unable to update event" : "Unable to add event",
                IsEdit ? "Event updated." : "Event recorded.");
            if (!ok)
                return;

            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>The picked date and time as a UTC instant.</summary>
    private DateTime? OccurredAtUtc() => _date is { } date
        ? DateTime.SpecifyKind(date.Date + (_time ?? TimeSpan.Zero), DateTimeKind.Utc)
        : null;

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The server's message for a failure that belongs on one of this dialog's controls, matched on the
    /// problem-details <c>errors</c> key; null falls through to the toast.
    /// </summary>
    private static (string Field, string Message)? InlineErrorFor(ApiProblem? problem)
    {
        if (problem?.Errors is not { } errors)
            return null;

        foreach (var field in (string[])["type", "title", "occurredAt"])
        {
            if (errors.TryGetValue(field, out var messages) && messages.Length > 0)
            {
                return (field, messages[0]);
            }
        }

        return null;
    }
}
