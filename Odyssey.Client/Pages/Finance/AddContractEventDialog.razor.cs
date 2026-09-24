using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// New / Edit dialog for one entry in a contract's event log (issue #138).
/// </summary>
/// <remarks>
/// <para>
/// Every rule enforced here MIRRORS the server's and never replaces it: each one is re-run server-side
/// and the server is what actually refuses. What the client buys is that the user meets the refusal
/// before the round trip, on the control responsible for it.
/// </para>
/// <para>
/// <b>The future bound is checked against the clock, not against a constant.</b> Both halves are
/// deliberate: the tolerance is the server's, named once here so the two numbers cannot drift
/// independently, and the comparison is to "now at submit" rather than to a value captured when the
/// dialog opened — a dialog left open for a minute would otherwise start refusing a time it had
/// itself pre-filled.
/// </para>
/// </remarks>
public partial class AddContractEventDialog
{
    /// <summary>
    /// The server's forward tolerance on <c>occurredAt</c> (issue #138 §8.3). A client clock that runs
    /// slightly fast must not have its "now" refused.
    /// </summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(60);

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter, EditorRequired] public ExistingContract Contract { get; set; } = default!;

    /// <summary>The event being edited, or null for the add.</summary>
    [Parameter] public ExistingContractEvent? Event { get; set; }

    /// <summary>Raised after a successful write so the host re-reads the log.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private ContractEventType _type = ContractEventType.Other;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _notes = string.Empty;
    private DateTime? _date;
    private TimeSpan? _time;
    private readonly Dictionary<string, string> _errors = [];
    private bool _isSaving;

    private bool IsEdit => Event is not null;

    /// <summary>
    /// Bounds the date picker. A same-day time later than now is still possible and is caught at
    /// submit — the picker's granularity is a day, so it cannot express the 60-second rule itself.
    /// </summary>
    private DateTime Today => DateTime.UtcNow.Date;

    /// <summary>
    /// The one sentence that stops an eighteen-member list of lifecycle verbs from reading like a
    /// control surface (issue #155 §6.8).
    /// </summary>
    /// <remarks>
    /// It rides the TYPE field's <c>Help</c>, so it lands in that field's <c>aria-describedby</c> and a
    /// screen-reader user meets it too. A caption would not: a misconception a screen reader never
    /// reaches is a misconception kept — and this list now offers <em>Paused</em>, <em>Archived</em>
    /// and <em>Signed</em>, which is exactly what invites the assumption that picking one DOES the
    /// thing.
    /// </remarks>
    private const string NoSideEffects =
        "Recording an event does not change the contract. To pause the contract itself, use Edit on the record.";

    /// <summary>
    /// What the chosen type means, so the field explains itself — followed by
    /// <see cref="NoSideEffects"/> on every member, not just the tempting ones.
    /// </summary>
    private string TypeHelp => $"{TypeMeaning} {NoSideEffects}";

    private string TypeMeaning => _type switch
    {
        ContractEventType.Signed => "The agreement was executed by the parties.",
        ContractEventType.Amended => "A variation or addendum changed the terms.",
        ContractEventType.Renewed => "The agreement was taken up for a further term.",
        ContractEventType.Extended => "The existing term was lengthened.",
        ContractEventType.NoticeGiven => "Notice to end the agreement was served, by either side.",
        // Stated on the row that would otherwise mislead: an event is a record, never a mutation of
        // the contract's derived status (§4.2).
        ContractEventType.Terminated => "The agreement was brought to an end. This does not change the contract's status.",
        ContractEventType.TermChanged => "A term of the agreement (a price, rate, date or clause) was set or changed.",
        ContractEventType.EmailSent => "Correspondence you sent about the agreement. Name the recipient in the title or description.",
        // ── The nine automation members (issue #154) ──────────────────────────────
        ContractEventType.Paused => "The contract was paused. Recording this by hand does not pause anything.",
        ContractEventType.Unpaused => "A paused contract was taken off pause.",
        ContractEventType.Ready => "The contract was marked ready.",
        ContractEventType.Unready => "The ready mark was taken off the contract.",
        ContractEventType.Unsigned => "The contract's signed date was removed.",
        ContractEventType.Archived => "The contract was archived. Archival hides a contract; it does not lock it.",
        ContractEventType.Unarchived => "An archived contract was brought back.",
        ContractEventType.PartyAdded => "Someone joined the agreement.",
        ContractEventType.PartyRemoved => "Someone left the agreement.",
        _ => "The default — anything the other members do not name. The title carries it.",
    };

    /// <summary>Whether the row being edited was recorded by the server (issue #154).</summary>
    private bool IsSystemEvent => Event?.Source == ContractEventSource.System;

    /// <summary>
    /// The load-bearing copy for a recorded row — on the field that TAKES FOCUS, so it is in the
    /// Title field's <c>aria-describedby</c> (issue #155 §6.9). <see langword="null"/> on every other
    /// row, which leaves the ordinary title help in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OdsModal</c>'s <c>Subtitle</c> is a visual slot with no <c>aria-describedby</c> wiring, and
    /// the Title field carries <c>AutoFocus</c> — so a screen-reader user editing a system row would
    /// land on Title and might never hear the subtitle at all. The subtitle keeps the same wording for
    /// sighted users; this line is what carries it programmatically.
    /// </para>
    /// <para>
    /// Both halves matter: where the row came from, and that editing it changes the LOG and nothing
    /// else. The second is not decoration — the <c>PUT</c> is a full replacement, so clearing a
    /// generated description clears it for good, because nothing regenerates it.
    /// </para>
    /// </remarks>
    private string? SystemTitleHelp => IsSystemEvent
        ? $"Recorded automatically when {AutoClause}. Editing changes this log line only; it does not change the contract."
        : null;

    /// <summary>
    /// The clause spliced into <see cref="SystemTitleHelp"/>. Falls back to a type-free phrasing, so a
    /// member this build does not recognise still produces an honest sentence rather than a gap.
    /// </summary>
    private string AutoClause => Event?.Type switch
    {
        ContractEventType.Signed => "the contract was marked signed",
        ContractEventType.TermChanged => "a term of the agreement was changed",
        ContractEventType.Paused => "the contract was paused",
        ContractEventType.Unpaused => "the contract was resumed",
        ContractEventType.Ready => "the contract was marked ready",
        ContractEventType.Unready => "the ready mark was withdrawn",
        ContractEventType.Unsigned => "the signed date was cleared",
        ContractEventType.Archived => "the contract was archived",
        ContractEventType.Unarchived => "the contract was restored",
        ContractEventType.PartyAdded => "a party was added to the agreement",
        ContractEventType.PartyRemoved => "a party was removed from the agreement",
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

        // A new entry pre-fills "now", which is what a user recording something that just happened
        // means — and is inside the tolerance rather than at its edge.
        var now = DateTime.UtcNow;
        _date = now.Date;
        _time = new TimeSpan(now.Hour, now.Minute, 0);
    }

    private void PickType(string value)
    {
        if (Enum.TryParse<ContractEventType>(value, out var next))
        {
            _type = next;
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
        // Midnight on a clearing, so a cleared time reads as "that day" rather than silently keeping
        // the previous one.
        _time = value ?? TimeSpan.Zero;
        _errors.Remove("occurredAt");
    }

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);

    private async Task SubmitAsync()
    {
        if (_isSaving)
            return;

        _errors.Clear();

        // Whitespace-only is rejected as empty, exactly as the server does (§8.1) — a [StringLength]
        // minimum would accept a string of spaces.
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

        if (_errors.Count > 0 || occurredAt is not { } value)
            return;

        _isSaving = true;
        try
        {
            // The body is a FULL replacement on the edit, so the dialog always sends every field — a
            // cleared description or note goes as null and clears (§5.3).
            var description = Blank(_description);
            var notes = Blank(_notes);

            var result = Event is { } existing
                ? await Contracts.UpdateEventAsync(Contract.ContractId, existing.ContractEventId, new UpdateContractEvent
                {
                    Type = _type,
                    Title = title,
                    Description = description,
                    Notes = notes,
                    OccurredAt = value,
                })
                : await Contracts.AddEventAsync(Contract.ContractId, new NewContractEvent
                {
                    Type = _type,
                    Title = title,
                    Description = description,
                    Notes = notes,
                    OccurredAt = value,
                });

            // The inline class is keyed on the FIELD KEY the server returns, never on message text —
            // which is exactly why the service throws the field overload of DomainValidationException.
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

    /// <summary>
    /// The picked date and time as a UTC instant. The two controls are local-kind values from the
    /// browser; the contract is a UTC timestamp, so the kind is pinned here rather than left to
    /// whatever the serializer infers.
    /// </summary>
    private DateTime? OccurredAtUtc() => _date is { } date
        ? DateTime.SpecifyKind(date.Date + (_time ?? TimeSpan.Zero), DateTimeKind.Utc)
        : null;

    /// <summary>A blank optional field is sent as absent, not as a string of spaces.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The server's message for a failure that belongs on one of this dialog's controls, or null to
    /// fall through to the toast. Matched on the problem-details <c>errors</c> key.
    /// </summary>
    private static (string Field, string Message)? InlineErrorFor(ApiProblem? problem)
    {
        if (problem?.Errors is not { } errors)
            return null;

        foreach (var field in (string[])["title", "occurredAt"])
        {
            if (errors.TryGetValue(field, out var messages) && messages.Length > 0)
            {
                return (field, messages[0]);
            }
        }

        return null;
    }
}
