using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class ContactInsuranceLinksDialog
{
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>The contact the delete was refused for. Named so the user knows which row this is.</summary>
    [Parameter, EditorRequired] public string ContactName { get; set; } = string.Empty;

    /// <summary>The server's 409 payload — kinds and counts always, policies only when the caller may
    /// see them.</summary>
    [Parameter, EditorRequired] public ContactInsuranceLinkBlockers Blockers { get; set; } = new();

    /// <summary>
    /// Whether the caller holds <c>insurance.update</c>. Gates the detach affordance; the server gates
    /// it too, with a 403 — this only keeps the dialog from offering an action it knows will fail.
    /// </summary>
    [Parameter] public bool CanUpdateInsurance { get; set; }

    /// <summary>
    /// Performs the detach-and-delete. Returns what was destroyed on success, or null on failure —
    /// the host owns the API call and its toast, because it also owns the list that has to refresh.
    /// </summary>
    [Parameter, EditorRequired] public Func<Task<DetachedInsuranceLinks?>>? OnDetachAndDelete { get; set; }

    // Non-null once the detach has run: the dialog switches to reporting what the request destroyed.
    // Links removed wholesale in one request is the one operation with a blast radius the ordinary
    // per-policy edit does not have, so the result is shown rather than reduced to a toast.
    private DetachedInsuranceLinks? _result;

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

    internal static string KindLabel(InsuranceLinkKind kind) => kind switch
    {
        InsuranceLinkKind.Insurer => "Insurer",
        InsuranceLinkKind.InsuredContact => "Insured contact",
        _ => "Beneficiary",
    };

    internal static string KindIcon(InsuranceLinkKind kind) => kind switch
    {
        InsuranceLinkKind.Insurer => "groups",
        InsuranceLinkKind.InsuredContact => "person",
        _ => "volunteer_activism",
    };

    internal static string KindNote(InsuranceLinkKind kind) => kind switch
    {
        InsuranceLinkKind.Insurer => "carries cover on the policy",
        InsuranceLinkKind.InsuredContact => "insured under the policy",
        _ => "receives on the policy",
    };
}
