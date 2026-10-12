using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The smart-tag scope editor (issue #308). Parameters, state and the save path; the markup is in
/// <c>EditSmartTagScopeDialog.razor</c>.
/// </summary>
public partial class EditSmartTagScopeDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful save so the host can refresh and announce it.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>The contract as loaded — the source of every field the PUT carries forward.</summary>
    [Parameter, EditorRequired] public ExistingContract Contract { get; set; } = default!;

    private OdsScopeRange _scope;
    private bool _tried;
    private string? _serverStartError;
    private string? _serverEndError;

    protected override void OnInitialized() => _scope = OdsSmartTagScope.Of(Contract);

    private (string? Start, string? End) Errors => OdsSmartTagScope.Validate(_scope);

    // Bounds errors show on submit; the order error shows as soon as it stands (the design system's
    // EditSmartTagScopeModal). A server rejection shows until the field it names is edited.
    private string? ShownStartError => _serverStartError ?? (_tried ? Errors.Start : null);

    private string? ShownEndError => _serverEndError
        ?? (_tried || Errors.End == SmartTagScopeRules.InvertedMessage ? Errors.End : null);

    private bool Unchanged => _scope.SameAs(OdsSmartTagScope.Of(Contract));

    private bool SubmitBlocked => Unchanged || ShownStartError is not null || ShownEndError is not null;

    private void OnScopeChanged(OdsScopeRange next)
    {
        if (next.Start != _scope.Start)
            _serverStartError = null;
        if (next.End != _scope.End)
            _serverEndError = null;
        _scope = next;
    }

    private async Task<bool> SaveAsync()
    {
        _tried = true;
        if (Errors.Start is not null || Errors.End is not null)
            return false;

        var c = Contract;
        var update = new UpdateContract
        {
            Name = c.Name,
            Type = c.Type,
            Description = c.Description,
            // PUT is a full replacement: every field this dialog does not edit is carried forward.
            ReferenceNumber = c.ReferenceNumber,
            StartDate = c.StartDate,
            EndDate = c.EndDate,
            CompletionDate = c.CompletionDate,
            // The two fields this dialog exists to write, as UTC-midnight calendar days.
            SmartTagScopeStart = OdsSmartTagScope.ToWire(_scope.Start),
            SmartTagScopeEnd = OdsSmartTagScope.ToWire(_scope.End),
            IsArchived = c.Archived is not null,
            IsPaused = c.Paused is not null,
            Ready = c.Ready,
            Signed = c.Signed,
        };

        return (await Contracts.UpdateAsync(c.ContractId, update)).ToastOrFields(
            Snackbar, "Unable to save the smart-tag scope", AssignFieldError, "Smart-tag scope saved.");
    }

    private bool AssignFieldError(string field, string message)
    {
        if (field.Equals(nameof(UpdateContract.SmartTagScopeStart), StringComparison.OrdinalIgnoreCase))
        {
            _serverStartError = message;
            return true;
        }
        if (field.Equals(nameof(UpdateContract.SmartTagScopeEnd), StringComparison.OrdinalIgnoreCase))
        {
            _serverEndError = message;
            return true;
        }
        return false;
    }
}
