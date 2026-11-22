using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Client.Pages.Attachments;

namespace Odyssey.Client.Pages.Finance;

public partial class ContractsCard
{
    // ── Create ──────────────────────────────────────────────────────────────────
    private Guid _createKey;
    private bool _createOpen;

    private void AddClicked()
    {
        if (!_canCreate)
        {
            return;
        }

        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    private async Task OnContractCreated()
    {
        await LoadContracts();
        await LoadSummary();
    }

    // ── Edit mode (design-system update: the create dialog reused in edit mode, not an inline panel) ──
    private ExistingContract? _editContract;
    private Guid _editContractKey;
    private bool _editContractOpen;

    private async Task EditClicked(ContractListItem c)
    {
        if (!_canUpdate)
        {
            return;
        }

        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var detail))
        {
            return;
        }

        _editContract = detail;
        _editContractKey = Guid.NewGuid();
        _editContractOpen = true;
    }

    private Task OnContractEdited() =>
        _editContract is { } contract ? ReloadContract(contract.ContractId) : Task.CompletedTask;

    // ── Smart-tag scope (issue #308): its own dialog, opened from the Smart tags section ──────────
    private ExistingContract? _scopeContract;
    private Guid _scopeContractKey;
    private bool _scopeContractOpen;

    private async Task EditScopeClicked(ContractListItem c)
    {
        if (!_canUpdate)
        {
            return;
        }

        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var detail))
        {
            return;
        }

        _scopeContract = detail;
        _scopeContractKey = Guid.NewGuid();
        _scopeContractOpen = true;
    }

    // The dialog's own success toast says "Smart-tag scope saved."; a second announcement here would
    // read the same sentence twice.
    private Task OnScopeEdited() =>
        _scopeContract is { } contract ? ReloadContract(contract.ContractId) : Task.CompletedTask;

    // ── Archive / unarchive (PUT with IsArchived; archived contracts stay in the list, dimmed,
    //    hidden by default, and drop out of the active summary counts) ──────────────────────
    private async Task ToggleArchive(ContractListItem c)
    {
        if (!_canUpdate)
        {
            return;
        }

        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var d))
        {
            return;
        }

        var archiving = c.Archived is null;
        var update = new UpdateContract
        {
            Name = d.Name,
            Type = d.Type,
            Description = d.Description,
            // PUT is a full replacement: omitted, the reference number would be cleared (issue #181).
            ReferenceNumber = d.ReferenceNumber,
            StartDate = d.StartDate,
            EndDate = d.EndDate,
            CompletionDate = d.CompletionDate,
            // Full replacement: an omitted scope OPENS the smart-tag match (issue #308).
            SmartTagScopeStart = d.SmartTagScopeStart,
            SmartTagScopeEnd = d.SmartTagScopeEnd,
            IsArchived = archiving,
            // PUT is a full replacement, so an omitted flag RESUMES: archiving without carrying the
            // pause stamp forward would silently clear it. The two stamps are orthogonal in storage
            // and only ordered in presentation — archiving a paused contract keeps both.
            IsPaused = d.Paused is not null,
            // Same rule, and the one this action would otherwise BREAK (issue #145 §5.2): omitting
            // these clears both signature stamps, flips a signed contract to Draft and drops it out
            // of the run rate — for a reader who only clicked Archive.
            Ready = d.Ready,
            Signed = d.Signed,
        };

        if ((await Contracts.UpdateAsync(c.ContractId, update)).Toast(Snackbar,
                archiving ? "Unable to archive contract" : "Unable to restore contract",
                archiving ? "Contract archived." : "Contract restored."))
        {
            await ReloadContract(c.ContractId);
        }
    }

    // ── Pause / resume (PUT with IsPaused; a paused contract stays listed at full brightness, stays
    //    fully editable and keeps its price history — it only leaves the money: the run rate, its
    //    by-type split and the next charges) ──────────────────────────────────────────────────────
    private async Task TogglePause(ContractListItem c)
    {
        if (!_canUpdate)
        {
            return;
        }

        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var d))
        {
            return;
        }

        var pausing = d.Paused is null;
        var update = new UpdateContract
        {
            Name = d.Name,
            Type = d.Type,
            Description = d.Description,
            // PUT is a full replacement: omitted, the reference number would be cleared (issue #181).
            ReferenceNumber = d.ReferenceNumber,
            StartDate = d.StartDate,
            EndDate = d.EndDate,
            CompletionDate = d.CompletionDate,
            // Full replacement: an omitted scope OPENS the smart-tag match (issue #308).
            SmartTagScopeStart = d.SmartTagScopeStart,
            SmartTagScopeEnd = d.SmartTagScopeEnd,
            // Carried forward for the same reason archiving carries the pause stamp: a full
            // replacement that omitted this would restore a contract the reader only meant to resume.
            IsArchived = d.Archived is not null,
            IsPaused = pausing,
            // And the signature stamps for the same reason again (issue #145 §5.2): a pause that
            // cleared them would unsign the contract the reader only meant to suspend.
            Ready = d.Ready,
            Signed = d.Signed,
        };

        if ((await Contracts.UpdateAsync(c.ContractId, update)).Toast(Snackbar,
                pausing ? "Unable to pause contract" : "Unable to resume contract",
                pausing ? "Contract paused." : "Contract resumed."))
        {
            await ReloadContract(c.ContractId);
        }
    }

    // ── Signature lifecycle (issue #145) ─────────────────────────────────────────────────
    //
    // The one-click path, in lifecycle order and one step at a time: Mark ready for signature →
    // Mark signed → Unsign. All three ride the SAME full-replacement PUT the archive and pause
    // actions do; the date fields in the create/edit dialog are the other way in, for backdating a
    // paper contract signed last month.

    /// <summary>
    /// Marks the contract ready for signature. IDEMPOTENT the way a pause is: a repeated mark keeps
    /// the ORIGINAL stamp, so "ready since" never resets — which matters because that date is what
    /// the header's "Awaiting signature" row counts from.
    /// </summary>
    private Task MarkReady(ContractListItem c) =>
        WriteSignature(
            c,
            (d, now) => (d.Ready ?? now, d.Signed),
            "Unable to mark the contract ready", "Contract marked ready for signature.");

    /// <summary>
    /// Marks the contract signed by all parties.
    ///
    /// <para>
    /// It stamps <c>Ready</c> too when that is missing, because the server refuses a <c>Signed</c>
    /// without one (<c>contract_signed_requires_ready</c>) and a ONE-CLICK action must not be able to
    /// compose a write the server will reject. An existing ready date is kept, so signing never
    /// rewrites when the contract was sent out.
    /// </para>
    /// </summary>
    private Task MarkSigned(ContractListItem c) =>
        WriteSignature(
            c,
            (d, now) => (d.Ready ?? now, d.Signed ?? now),
            "Unable to mark the contract signed", "Contract marked signed.");

    /// <summary>
    /// Clears BOTH stamps, returning the contract to <c>Draft</c>. Never refused, in any state — a
    /// guard on the way out is how a row gets stranded, which is the rule the pause guard already
    /// states.
    /// </summary>
    private Task Unsign(ContractListItem c) =>
        WriteSignature(
            c,
            (_, _) => (null, null),
            "Unable to clear the signature dates", "Signature dates cleared.");

    /// <summary>
    /// The shared body of the three actions above: load the detail, rebuild the WHOLE record with the
    /// new pair of stamps, PUT it.
    ///
    /// <para>
    /// The rebuild carries every other field explicitly — <c>IsArchived</c>, <c>IsPaused</c> and the
    /// dates — for the reason the DTO documents: <c>PUT</c> is a full replacement, so an omitted
    /// field is a cleared field. Writing one stamp here and forgetting the archive flag would
    /// silently restore a contract the reader only meant to sign.
    /// </para>
    /// </summary>
    private async Task WriteSignature(
        ContractListItem c,
        Func<ExistingContract, DateTime, (DateTime? Ready, DateTime? Signed)> next,
        string failure, string success)
    {
        if (!_canUpdate)
        {
            return;
        }

        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var d))
        {
            return;
        }

        var (ready, signed) = next(d, DateTime.UtcNow);
        var update = new UpdateContract
        {
            Name = d.Name,
            Type = d.Type,
            Description = d.Description,
            // PUT is a full replacement: omitted, the reference number would be cleared (issue #181).
            ReferenceNumber = d.ReferenceNumber,
            StartDate = d.StartDate,
            EndDate = d.EndDate,
            CompletionDate = d.CompletionDate,
            // Full replacement: an omitted scope OPENS the smart-tag match (issue #308).
            SmartTagScopeStart = d.SmartTagScopeStart,
            SmartTagScopeEnd = d.SmartTagScopeEnd,
            IsArchived = d.Archived is not null,
            IsPaused = d.Paused is not null,
            Ready = ready,
            Signed = signed,
        };

        if ((await Contracts.UpdateAsync(c.ContractId, update)).Toast(Snackbar, failure, success))
        {
            await ReloadContract(c.ContractId);
        }
    }

    // ── Delete (permanent; the API hard-deletes the contract + its party/file links) ──────
    private async Task ConfirmDelete(ContractListItem c)
    {
        if (!_canDelete)
        {
            return;
        }

        var confirmed = await _confirm.AskAsync("Delete contract", $"Permanently delete '{c.Name}' and all its party and document links? This cannot be undone.", "Delete");

        if (confirmed && (await Contracts.DeleteAsync(c.ContractId)).Toast(Snackbar, "Delete failed", "Contract deleted."))
        {
            _contracts.Remove(c);
            _details.Remove(c.ContractId);
            if (_expandedId == c.ContractId)
            {
                _expandedId = null;
            }

            if (_editContract?.ContractId == c.ContractId)
            { _editContract = null; _editContractOpen = false; }
            await LoadSummary();
            StateHasChanged();
        }
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), OdsCopied.Id("Contract"));

    // ── Add/edit-party dialog ────────────────────────────────────────────────────
    // One dialog serves both (issue #121's new PUT): _editingParty is what switches it between
    // "New party" and "Edit party".
    private ExistingContract? _partyContract;
    private ExistingContractParty? _editingParty;
    private Guid _partyKey;
    private bool _partyOpen;

    private Task AddParty(Guid contractId) => OpenPartyDialog(contractId, party: null);

    /// <summary>
    /// Opens the Terms section's create dialog on an expanded record. The card is expanded first when
    /// it is not already: the dialog writes into a section the reader has to be able to see the result
    /// in, and a create that lands in a collapsed body reads as nothing having happened.
    /// </summary>
    /// <remarks>
    /// On a collapsed card the section does not exist yet — <c>ContractDetailView</c> renders only
    /// once the detail load has landed — so the open is DEFERRED to the render that brings it into
    /// existence rather than raced against it with a yield.
    /// </remarks>
    private async Task AddTerm(Guid contractId)
    {
        if (!IsExpanded(contractId))
        {
            await ToggleExpand(contractId);
        }

        // A fresh token each time, so clicking "New term" twice on the same record opens the dialog
        // twice rather than being swallowed as an unchanged parameter.
        _newTermRequest = (contractId, Guid.NewGuid());
        StateHasChanged();
    }

    /// <summary>
    /// The outstanding "New term" request: which record asked, and a token identifying the ask.
    /// </summary>
    /// <remarks>
    /// The request travels DOWN as a parameter rather than through an <c>@ref</c> to the expanded
    /// body. A ref is rebound on the next render, so immediately after expanding record B it still
    /// points at record A's section — and where B's detail was already cached, <c>ToggleExpand</c>
    /// returns without yielding at all, so no render has happened in between. Opening through the ref
    /// there would put the dialog on the wrong contract and silently drop the click. The token is
    /// handed only to the row whose id matches, so the section that receives it IS that contract's
    /// section, by construction rather than by timing.
    /// </remarks>
    private (Guid ContractId, Guid Token)? _newTermRequest;

    /// <summary>The token for this record, or null when the outstanding request is not its own.</summary>
    private Guid? NewTermRequestFor(Guid contractId) =>
        _newTermRequest is { } request && request.ContractId == contractId ? request.Token : null;

    /// <summary>
    /// Opens the Events section's create dialog on an expanded record. Same shape as
    /// <see cref="AddTerm"/>: the card is expanded first when it is not already, because the dialog
    /// writes into a section the reader has to be able to see the result in, and a create that lands
    /// in a collapsed body reads as nothing having happened.
    /// </summary>
    private async Task AddEvent(Guid contractId)
    {
        if (!IsExpanded(contractId))
        {
            await ToggleExpand(contractId);
        }

        // A fresh token each time, so clicking "New event" twice on the same record opens the dialog
        // twice rather than being swallowed as an unchanged parameter.
        _newEventRequest = (contractId, Guid.NewGuid());
        StateHasChanged();
    }

    /// <summary>
    /// The outstanding "New event" request: which record asked, and a token identifying the ask. The
    /// reasoning is <see cref="_newTermRequest"/>'s, unchanged — a ref to the expanded body is rebound
    /// on the next render, so it can still point at the previous record's section at the moment the
    /// click is handled.
    /// </summary>
    private (Guid ContractId, Guid Token)? _newEventRequest;

    /// <summary>The token for this record, or null when the outstanding request is not its own.</summary>
    private Guid? NewEventRequestFor(Guid contractId) =>
        _newEventRequest is { } request && request.ContractId == contractId ? request.Token : null;

    private Task EditParty(Guid contractId, ExistingContractParty party) => OpenPartyDialog(contractId, party);

    private async Task OpenPartyDialog(Guid contractId, ExistingContractParty? party)
    {
        if (!_canUpdate)
        {
            return;
        }

        await EnsureDetail(contractId);
        if (!_details.TryGetValue(contractId, out var d))
        {
            return;
        }

        _expandedId = contractId;
        _partyContract = d;
        _editingParty = party;
        _partyKey = Guid.NewGuid();
        _partyOpen = true;
    }

    /// <summary>
    /// Routes a child's line into the page's own <c>OdsLiveAnnouncer</c>. The announcer is mounted
    /// once, on this page, so the party tiles and the dialog raise their text rather than each owning
    /// a live region — two regions on one page race each other.
    /// </summary>
    private void Announce(string message)
    {
        _announce = message;
        StateHasChanged();
    }

    // ── Upload / attach dialog ───────────────────────────────────────────────────
    private ExistingContract? _uploadContract;
    private Guid _uploadKey;
    private bool _uploadOpen;

    /// <summary>
    /// This surface's own, tighter product limit — kept from the retired ContractUploadDialog. The
    /// effective cap is the smaller of it and the instance-wide one; a surface may tighten, never loosen.
    /// </summary>
    private const int ContractSurfaceMaxMegabytes = 25;

    // The attach needs contracts.update + files.read (the server's two policies); files.create only
    // adds the "Upload new" tab, which the dialog decides for itself.
    private bool CanAttachDocuments => _canUpdate && _canDownloadFiles;

    private async Task<bool> AttachContractFileAsync(AttachDocumentItem item)
    {
        if (_uploadContract is null)
        {
            return false;
        }

        return (await Contracts.AttachFileAsync(_uploadContract.ContractId, AttachDocumentRequests.Contract(item)))
            .Toast(Snackbar, $"Couldn’t attach “{item.Name}”");
    }

    private async Task AttachDocument(Guid contractId)
    {
        if (!CanAttachDocuments)
        {
            return;
        }

        await EnsureDetail(contractId);
        if (!_details.TryGetValue(contractId, out var d))
        {
            return;
        }

        _expandedId = contractId;
        _uploadContract = d;
        _uploadKey = Guid.NewGuid();
        _uploadOpen = true;
    }
}
