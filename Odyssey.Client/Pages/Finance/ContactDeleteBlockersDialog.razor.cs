using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Books;
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
    /// The 409's <c>bookCredits</c> (issue #527 / #571 §3.10): the contact is credited as a book's main author or a
    /// contributor (RESTRICT keys). A count always; titles only for a <c>books.read</c> holder. Non-null switches the
    /// dialog to its books state — there is no detach for a credit; it is removed on the Books page.
    /// </summary>
    [Parameter] public ContactBookCreditBlockers? BookCredits { get; set; }

    /// <summary>The 409 carried a <c>bookCredits</c> member this build could not read: the books state, without numbers.</summary>
    [Parameter] public bool BookCreditsUnreadable { get; set; }

    /// <summary>A detach-and-delete was refused because a credit appeared in between: say that nothing was deleted.</summary>
    [Parameter] public bool DetachRefused { get; set; }

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

    /// <summary>The books state: a book credit blocks, and nothing here can release it.</summary>
    private bool BlockedByBooks => BookCredits is not null || BookCreditsUnreadable;

    /// <summary>Counts the dialog may state; a malformed member falls back to "one or more books".</summary>
    private bool BookCountsKnown => BookCredits is { BookCount: > 0, TotalCredits: > 0 };

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
