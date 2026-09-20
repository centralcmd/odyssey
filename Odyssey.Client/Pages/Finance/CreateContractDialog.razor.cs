using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The "New contract" dialog, reused in edit mode when a <see cref="Contract"/> is supplied.
/// Parameters, state and the save path; the markup is in <c>CreateContractDialog.razor</c>.
/// </summary>
public partial class CreateContractDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>When set, the dialog edits this contract. Null = create mode.</summary>
    [Parameter] public ExistingContract? Contract { get; set; }

    private bool IsEdit => Contract is not null;

    private const string TermMode = "term";
    private const string OneOffMode = "oneoff";

    private static readonly IReadOnlyList<OdsSegmentedOption> _modeOptions =
    [
        new() { Value = TermMode, Label = "Term" },
        new() { Value = OneOffMode, Label = "One-off" },
    ];

    private string _mode = TermMode;
    private string? _name;
    private string _type = string.Empty;
    private string? _description;
    private DateTime? _startDate = DateTime.UtcNow.Date;
    private DateTime? _endDate;
    private DateTime? _completionDate;
    private DateTime? _ready;
    private DateTime? _signed;

    private string? _nameError;
    private string? _typeError;
    private string? _endError;
    private string? _completionError;
    private string? _readyError;
    private string? _signedError;

    protected override void OnInitialized()
    {
        if (Contract is not { } contract)
            return;

        _name           = contract.Name;
        _type           = contract.Type.ToString();
        _description    = contract.Description;
        _mode           = contract.CompletionDate is not null ? OneOffMode : TermMode;
        _startDate      = contract.StartDate;
        _endDate        = contract.EndDate;
        _completionDate = contract.CompletionDate;
        _ready          = contract.Ready;
        _signed         = contract.Signed;
    }

    private void OnModeChanged(string mode)
    {
        _mode = mode;
        _endError = null;
        _completionError = null;
    }

    /// <summary>
    /// The client half of the server's three signature guards (issue #145 §8), in the SAME order the
    /// server runs them so the field this highlights is the field a rejected body would name.
    ///
    /// <para>
    /// The comparison granularities are the server's too, and they differ on purpose: the future
    /// check is at DATE granularity, so a client clock a few minutes ahead does not refuse an
    /// ordinary "signed just now", while "signed before ready" compares the two values as given,
    /// since both come from this one form and there is no clock to be skewed against.
    /// </para>
    ///
    /// <para>
    /// This is a pre-check for the message, not the rule: the server enforces all three regardless,
    /// and a body that got past this still comes back as a 400 naming the same field.
    /// </para>
    /// </summary>
    private void ValidateSignature()
    {
        var today = DateTime.UtcNow.Date;

        if (_ready is { } ready && ready.Date > today)
        {
            _readyError = "A ready date records something that has happened — it cannot be in the future.";
            return;
        }

        if (_signed is { } signedInFuture && signedInFuture.Date > today)
        {
            _signedError = "A signed date records something that has happened — it cannot be in the future.";
            return;
        }

        if (_signed is not null && _ready is null)
        {
            _signedError = "A signed contract needs a ready date too. Set when it was ready for signature, or clear the signed date.";
            return;
        }

        if (_signed is { } signed && _ready is { } readyOn && signed < readyOn)
        {
            _signedError = "A contract cannot be signed before it was ready for signature.";
        }
    }

    private async Task<bool> SaveAsync()
    {
        _nameError = string.IsNullOrWhiteSpace(_name) ? "Give the contract a name." : null;
        _typeError = string.IsNullOrEmpty(_type) ? "Pick a contract type." : null;
        _endError = null;
        _completionError = null;
        _readyError = null;
        _signedError = null;

        if (_mode == OneOffMode)
        {
            _completionError = _completionDate is null ? "Set a completion date." : null;
        }
        else if (_endDate is { } e && _startDate is { } s && e.Date < s.Date)
        {
            _endError = "“Ends” can’t be before “Starts”.";
        }

        // The three signature guards, run identically on create and edit — one helper, the way the
        // server shares one between POST and PUT. Clearing a stamp is never refused. On create both
        // stamps are null by construction (the fields are edit-only), so it is a no-op there; the
        // call stays unconditional because the server runs the same guards on POST regardless.
        ValidateSignature();

        if (_nameError is not null || _typeError is not null || _endError is not null
            || _completionError is not null || _readyError is not null || _signedError is not null)
            return false;

        var oneOff = _mode == OneOffMode;
        var type = Enum.TryParse<ContractType>(_type, out var t) ? t : ContractType.Other;
        var name = _name!.Trim();
        var description = string.IsNullOrWhiteSpace(_description) ? null : _description!.Trim();
        var startDate = oneOff ? null : _startDate;
        var endDate = oneOff ? null : _endDate;
        var completionDate = oneOff ? _completionDate : null;

        if (IsEdit)
        {
            var update = new UpdateContract
            {
                Name = name,
                Type = type,
                Description = description,
                StartDate = startDate,
                EndDate = endDate,
                CompletionDate = completionDate,
                // Archive/restore and pause/resume are separate row actions, and PUT is a full
                // replacement — so both stamps are carried forward from the record as it stands.
                // Omitting either would let an ordinary field edit silently restore or resume it.
                IsArchived = Contract!.Archived is not null,
                IsPaused = Contract.Paused is not null,
                // The dialog is a FULL REPLACEMENT and that includes the two signature stamps: a
                // value sets them, a cleared field clears them. Carried explicitly because an
                // omitted stamp on this write would flip a signed contract back to Draft and drop it
                // out of the run rate (issue #145 §5.2).
                Ready = _ready,
                Signed = _signed,
            };

            return (await Contracts.UpdateAsync(Contract.ContractId, update)).Toast(Snackbar,
                "Unable to update contract", "Contract updated.");
        }

        var contract = new NewContract
        {
            Name = name,
            Type = type,
            Description = description,
            StartDate = startDate,
            EndDate = endDate,
            CompletionDate = completionDate,
            // No signature stamps: a new contract ALWAYS starts as a Draft, which is why create mode
            // does not render the two fields at all. They are set afterwards from the contract
            // itself — the row menu's Mark ready / Mark signed, or this dialog in edit mode. Both
            // properties are optional on NewContract, so omitting them is the null the server reads
            // as "unsigned draft".
        };

        return (await Contracts.CreateAsync(contract)).Toast(Snackbar, "Unable to create contract", "Contract created.");
    }
}
