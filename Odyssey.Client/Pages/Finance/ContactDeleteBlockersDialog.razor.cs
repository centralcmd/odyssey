using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class ContactDeleteBlockersDialog
{
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The contact the delete was refused for. Named so the user knows which row this is.</summary>
    [Parameter, EditorRequired] public string ContactName { get; set; } = string.Empty;

    /// <summary>
    /// The server's 409 payload (issue #157 §5.4) — the contracts naming the contact as a
    /// <c>Beneficiary</c>. A count always; the contract NAMES only when the caller holds
    /// <c>contracts.read</c>. Empty when no contract blocks.
    /// </summary>
    [Parameter] public ContactContractBeneficiaryBlockers ContractBeneficiaries { get; set; } = new();

    /// <summary>
    /// Whether the caller holds <c>contracts.update</c>. Gates the detach affordance; the server gates
    /// it too, with a 403 — this only keeps the dialog from offering an action it knows will fail.
    /// </summary>
    [Parameter] public bool CanUpdateContracts { get; set; }

    /// <summary>
    /// Performs the detach-and-delete. Returns what was destroyed on success, or null on failure —
    /// the host owns the API call and its toast, because it also owns the list that has to refresh.
    /// </summary>
    [Parameter, EditorRequired] public Func<Task<DetachedContactLinks?>>? OnDetachAndDelete { get; set; }

    // Non-null once the detach has run: the dialog switches to reporting what the request destroyed.
    // Links removed wholesale in one request is the one operation with a blast radius the ordinary
    // per-contract edit does not have, so the result is shown rather than reduced to a toast.
    private DetachedContactLinks? _result;

    private bool BlockedByContracts => ContractBeneficiaries.TotalLinks > 0;

    private int TotalBlockingLinks => ContractBeneficiaries.TotalLinks;

    private bool CanDetach => !BlockedByContracts || CanUpdateContracts;

    private async Task<bool> SubmitAsync()
    {
        // Second press, on the result step: just close.
        if (_result is not null)
        {
            return true;
        }

        if (OnDetachAndDelete is null)
        {
            return false;
        }

        var detached = await OnDetachAndDelete();
        if (detached is null)
        {
            // The host has toasted the failure; keep the dialog open so the user can retry or cancel.
            return false;
        }

        _result = detached;
        StateHasChanged();
        return false;
    }
}
