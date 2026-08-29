using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Services;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Models;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Finance;

public partial class FileAnalysisDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter] public Guid AccountId { get; set; }
    [Parameter] public Guid FileId { get; set; }
    [Parameter] public string FileName { get; set; } = "";
    [Parameter] public string? AccountName { get; set; }
    [Parameter] public long FileSizeBytes { get; set; }

    /// <summary>
    /// The host-resolved initial phase. The host already loaded the account's resumable map, so it
    /// decides whether the dialog opens on the consent gate (fresh analysis), the resume-vs-reanalyze
    /// fork, or straight into loading the saved review. The dialog does not re-discover resumability.
    /// </summary>
    [Parameter] public StartMode Start { get; set; } = StartMode.Consent;

    /// <summary>The resumable job to reopen (Resume) / the count shown on the confirm fork. Host-supplied.</summary>
    [Parameter] public ResumableAnalysisSummary? ResumeSummary { get; set; }

    /// <summary>Raised after the review is finished (imported) so the host can refresh its resumable map.</summary>
    [Parameter] public EventCallback OnResolved { get; set; }

    /// <summary>Which phase the host opens this dialog in. Resume/ReanalyzeConfirm need a <see cref="ResumeSummary"/>.</summary>
    public enum StartMode { Consent, ReanalyzeConfirm, Resume }

    // The phase machine, candidate rows and match/merchant-create rules (issue #373). The dialog owns
    // only the I/O around it.
    private readonly FileAnalysisSession _session = new();

    private FileAnalysisPhase Phase => _session.Phase;

    private string? _errorMessage;

    // Consent gate — analysis sends the complete document to the external AI processor, so the
    // dialog opens on an informed-consent gate before any bytes leave Odyssey. The affirmed text
    // is forwarded to the analyze endpoint and recorded verbatim in the admin audit log.
    private bool _consentChecked;

    private Guid _jobId;
    private bool _isImporting;

    private int _importedCount;
    private int _failedCount;
    private List<ImportFailure> _failures = [];

    private bool IsReview => Phase == FileAnalysisPhase.Review;

    // OdsModal keys its inline MudDialog on `Wide`; flipping it re-teleports the dialog, and when that
    // happens during the resume init continuation (not a user event) the previous instance is orphaned
    // behind the new one. So every phase the resume flow can reach without a user click — ResumeLoading,
    // Review, NoLongerAvailable — holds `Wide` constant; their differing visual widths come from the CSS
    // class (ModalClass) instead, which restyles the teleported dialog in place with no re-teleport.
    private bool IsWideFrame =>
        Phase is FileAnalysisPhase.Review or FileAnalysisPhase.ResumeLoading or FileAnalysisPhase.NoLongerAvailable;

    // Header lead glyph + tint (mirrors the design-system AnalyzeFileModal): the consent gate and the
    // no-longer-available state warn (amber); the reanalyze fork wears the history glyph; every other
    // phase wears the brand-toned scanner.
    private string HeaderIcon => Phase switch
    {
        FileAnalysisPhase.Consent => "shield",
        FileAnalysisPhase.ReanalyzeConfirm => "history",
        FileAnalysisPhase.NoLongerAvailable => "unpublished",
        _ => "document_scanner",
    };

    private OdsModalTone HeaderTone =>
        Phase is FileAnalysisPhase.Consent or FileAnalysisPhase.NoLongerAvailable
            ? OdsModalTone.Warning
            : OdsModalTone.Brand;

    // The pending-candidate count shown while confirming/resuming — from the host's summary, falling
    // back to the loaded job's row count once it's in memory.
    private int _pendingN => ResumeSummary is { } s
        ? (s.PendingCount > 0 ? s.PendingCount : s.CandidateCount)
        : _session.Rows.Count;

    // Dialog-scoped polite live region (the dialog had none): the async resume state is announced
    // here; the load-failure state announces via role="alert" on its own body. The visible text is
    // empty on first paint and only set afterward (OnAfterRender) so assistive tech treats it as a
    // mutation and actually speaks it — content present when a live region is inserted is not announced.
    private string _liveMsg = string.Empty;

    /// <summary>
    /// The re-prompt copy shown after a <c>409 disclosure_changed</c> (issue #439 §5.3c), or null when
    /// the gate has not been re-prompted.
    ///
    /// <para>
    /// Non-dismissable and rendered above the gate. The previous affirmation was given for different
    /// facts, so the checkbox is reset with it — and focus moves here rather than being left on a
    /// checkbox that silently unticked beneath the user.
    /// </para>
    ///
    /// <para>
    /// <strong>Deliberately NOT routed through <see cref="DesiredLiveMsg"/>.</strong> It is rendered in
    /// an <c>OdsAlert Severity="Warning"</c>, which carries <c>role="alert"</c> and therefore announces
    /// itself on insertion; adding the dialog's polite region would speak the same sentence twice (three
    /// times counting the focus move). Worse, this field is never cleared for the life of the dialog, so
    /// putting it at the front of that null-coalescing chain masked <em>every</em> later announcement —
    /// grid actions and phase transitions alike — for the rest of the session (WCAG 4.1.3).
    /// </para>
    /// </summary>
    private string? _disclosureChangedNotice;

    /// <summary>The notice element, so focus can land on the reason rather than on the reset checkbox.</summary>
    private ElementReference _disclosureChangedRef;

    private bool _disclosureChangedNeedsFocus;

    private string DesiredLiveMsg => _session.ActionAnnounce ?? Phase switch
    {
        FileAnalysisPhase.ResumeLoading => $"Opening your saved review for {FileName}.",
        FileAnalysisPhase.Matching => "Matching merchants and categories against your contacts and tags.",
        _ => string.Empty,
    };

    // The NoLongerAvailable step swaps into an already-open dialog with no focusable body control, so
    // focus moves to its heading on entry (tracked so it fires once per entry).
    private ElementReference _unavailableHeading;
    private FileAnalysisPhase? _announcedPhase;

    // Per-phase modal width (overrides layered on the OdsModal frame): review widens to the
    // candidate grid (~1600px), consent to the two-column disclosure (~720px); every other
    // single-column phase keeps the default narrow modal.
    private string ModalClass => Phase switch
    {
        // ResumeLoading shares Review's wide frame so the resume flow opens at the review size and never
        // changes the `Wide` key (see IsWideFrame). NoLongerAvailable keeps that key but narrows itself
        // purely via CSS — a short message, not the full grid.
        FileAnalysisPhase.Review or FileAnalysisPhase.ResumeLoading => "fan-modal",
        FileAnalysisPhase.NoLongerAvailable => "fan-unavailable-modal",
        FileAnalysisPhase.Consent => "fan-consent-modal",
        _ => string.Empty,
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Live region: enter empty, then receive its text as a mutation so assistive tech announces it
        // (content present when a polite region is inserted is not spoken).
        if (_liveMsg != DesiredLiveMsg)
        {
            _liveMsg = DesiredLiveMsg;
            StateHasChanged();
            return; // the resulting re-render re-enters here with the DOM settled
        }

        // A re-prompted consent gate: land the user on the REASON, not on the checkbox that reset
        // underneath them (issue #439 §5.3c AC 55). Fires once per re-prompt.
        if (_disclosureChangedNeedsFocus)
        {
            _disclosureChangedNeedsFocus = false;
            try { await _disclosureChangedRef.FocusAsync(); } catch { /* not focusable yet — non-fatal */ }
            return;
        }

        // Move focus to the NoLongerAvailable heading once on entry so the screen reader reads the new
        // context immediately. (ReanalyzeConfirm is only ever the initial phase, where the modal's own
        // open-focus + dialog title already announce it.)
        if (Phase == FileAnalysisPhase.NoLongerAvailable && _announcedPhase != FileAnalysisPhase.NoLongerAvailable)
        {
            _announcedPhase = FileAnalysisPhase.NoLongerAvailable;
            try { await _unavailableHeading.FocusAsync(); } catch { /* not focusable yet — non-fatal */ }
        }
        else if (Phase != FileAnalysisPhase.NoLongerAvailable)
        {
            _announcedPhase = null;
        }
    }

    // The disclosure the consent panel renders. Starts as the compiled fallback so the gate can never
    // show a partial disclosure, and is replaced by the fetched value; DisclosureResolved is what keeps
    // the affirmation disabled until that happens.
    private FileAnalysisDisclosureDto _disclosure = FileAnalysisDisclosureCache.Fallback;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        // Fetched here rather than in the panel, which stays purely presentational (#373). Stale-while-
        // revalidate, so an admin's change reaches the gate on its next open rather than next session.
        _disclosure = await Disclosures.GetAsync();

        // Resolve the host-decided initial phase synchronously, before the first await, so the dialog
        // never flashes the consent gate while a resume/reanalyze open is in flight.
        if (Start == StartMode.ReanalyzeConfirm)
            _session.SetPhase(FileAnalysisPhase.ReanalyzeConfirm);
        else if (Start == StartMode.Resume)
            _session.SetPhase(FileAnalysisPhase.ResumeLoading);

        // Resolve the inline-create gate (contacts.create) up front so the Merchant combobox
        // renders the right affordance from first paint.
        var user = await AuthState.GetUserAsync();
        _session.CanCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);

        // Reference data is loaded up front so the review phase is ready the moment analysis
        // completes — and, on resume, BEFORE candidate rows are seeded (seeding against unloaded
        // contacts/tags would silently drop merchant/tag prefills). Analysis itself does not
        // run until the user clears the consent gate.
        await Task.WhenAll(LoadContacts(), LoadTags(), LoadCurrencies());

        // Resume: reference data is now loaded, so fetch the saved job and seed the Review rows.
        if (Start == StartMode.Resume)
            await ContinueResumeAsync();
    }

    // The consent gate cleared → record the affirmed consent and start the transfer.
    private async Task ProceedFromConsentAsync()
    {
        if (!_consentChecked)
            return;
        await RunAnalysisAsync();
    }

    /// <summary>
    /// The server refused the transfer because the disclosure changed while this gate was open
    /// (<c>409 disclosure_changed</c>, issue #439 §5.3c).
    ///
    /// <para>
    /// Not an error state: the dialog stays open and usable, nothing the user entered is lost, no job
    /// row exists and no document was sent. What it is, is a fresh consent interaction — so the cache
    /// is dropped and re-fetched, the gate re-renders with the new text, and the affirmation resets,
    /// because the previous tick was given for facts that no longer hold.
    /// </para>
    /// </summary>
    private async Task RepromptForChangedDisclosureAsync()
    {
        Disclosures.Invalidate();
        _disclosure = await Disclosures.GetAsync();

        _consentChecked = false;
        _disclosureChangedNotice =
            "The details of who processes your document changed while this dialog was open. "
            + "Please review them again before continuing.";
        _disclosureChangedNeedsFocus = true;
        _session.SetPhase(FileAnalysisPhase.Consent);
        StateHasChanged();
    }

    // ── Analysis flow ─────────────────────────────────────────────────────────
    private async Task RunAnalysisAsync()
    {
        _session.SetPhase(FileAnalysisPhase.Analyzing);
        StateHasChanged();

        try
        {
            var analyzeRequest = new AnalyzeFileRequest
            {
                ConsentAcknowledged = true,
                // The RENDERED sentence, not a template: the server stores it verbatim on the job, so
                // a past consent stays attributable to the exact wording the user saw even after an
                // admin edits the processor (issue #421 Wave 1).
                ConsentText = FileAnalysisConsent.Compose(_disclosure.Processor),
                ConsentMethod = FileAnalysisConsent.Method,
                // The version of the disclosure THIS gate rendered (issue #439 §5.3c). The server
                // recomputes it from the snapshot the transfer will use and answers 409 if they differ,
                // so a consent affirmed against a cached disclosure can never authorise a transfer to a
                // recipient the user was not told about.
                DisclosureVersion = _disclosure.DisclosureVersion,
            };
            var analyzed = await Accounts.AnalyzeFileAsync(AccountId, FileId, analyzeRequest);

            if (analyzed.Status == HttpStatusCode.ServiceUnavailable)
            {
                _session.SetPhase(FileAnalysisPhase.Disabled);
                return;
            }
            if (analyzed.Status == HttpStatusCode.Conflict)
            {
                await RepromptForChangedDisclosureAsync();
                return;
            }
            if (analyzed.Status == HttpStatusCode.BadRequest)
            {
                _errorMessage = analyzed.Error;
                _session.SetPhase(FileAnalysisPhase.Blocked);
                return;
            }
            if (!analyzed.IsSuccess)
            {
                _errorMessage = analyzed.Error;
                _session.SetPhase(FileAnalysisPhase.Failed);
                return;
            }

            var analyzeResult = analyzed.Value
                ?? throw new InvalidOperationException("Empty response from analyze endpoint.");
            _jobId = analyzeResult.AnalysisJobId;

            _session.Job = (await FileAnalysis.GetJobAsync(_jobId)).Value;
            if (_session.Job is null)
            {
                _errorMessage = "Could not load analysis results.";
                _session.SetPhase(FileAnalysisPhase.Failed);
                return;
            }

            if (_session.Job.Status == FileAnalysisJobStatus.Failed)
            {
                _session.SetPhase(FileAnalysisPhase.Failed);
                return;
            }

            // Extraction done. Nothing extracted ⇒ straight to Empty (no point matching). Otherwise
            // hand off to the match step before opening Review.
            _session.SeedRows();
            if (_session.Rows.Count == 0)
            {
                _session.SetPhase(FileAnalysisPhase.Empty);
                return;
            }

            await RunMatchAsync();
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
            _session.SetPhase(FileAnalysisPhase.Failed);
        }
    }

    // The match step (issue #266): POST the match endpoint, which sends the contact + tag NAMES,
    // persists the resolved ids, and returns the updated job. The client then rebuilds the Review rows
    // from the persisted match data (single source of truth) and opens Review. A match failure/over-cap
    // is conveyed via matchStatus (200, not an error) so Review still opens with raw candidates.
    private async Task RunMatchAsync(bool preserveManual = false)
    {
        _session.SetPhase(FileAnalysisPhase.Matching);
        StateHasChanged();

        try
        {
            var matched = await FileAnalysis.MatchAsync(_jobId);
            if (matched.IsSuccess)
            {
                _session.Job = matched.Value ?? _session.Job;
                // The server conveys match success/failure/skip via matchStatus (200, never an error).
                _session.MatchStatus = _session.Job?.MatchStatus ?? FileAnalysisMatchStatus.Failed;
            }
            else
            {
                // A precondition/transport failure shouldn't lose the extracted candidates — fall back to
                // the manual-link Review with a degraded notice.
                _session.MatchStatus = FileAnalysisMatchStatus.Failed;
            }
        }
        catch
        {
            _session.MatchStatus = FileAnalysisMatchStatus.Failed;
        }

        if (preserveManual)
            _session.ApplyMatchesPreservingManual();
        else
            _session.SeedRows();

        _session.SetPhase(_session.Rows.Count == 0 ? FileAnalysisPhase.Empty : FileAnalysisPhase.Review);
    }

    // Re-match: re-run the match step only (no re-extract, no second consent), refreshing the
    // None/Llm suggestions while preserving any row the reviewer already curated.
    private Task ReMatchAsync() => RunMatchAsync(preserveManual: true);

    private async Task ReanalyzeAsync()
    {
        _session.Rows.Clear();
        _session.Job = null;
        await RunAnalysisAsync();
    }

    // ── Resume flow ───────────────────────────────────────────────────────────
    // Reopen a persisted, still-resumable review without re-sending the document. Loads the saved job
    // via the existing get-by-id (reference data is already loaded), re-validates it's resumable, and
    // seeds the same Review rows as a fresh analysis. A vanished/no-longer-resumable job (deleted, or
    // imported from another tab) lands on NoLongerAvailable with curated text — never a raw error.
    private async Task ContinueResumeAsync()
    {
        var jobId = ResumeSummary?.AnalysisJobId ?? Guid.Empty;
        if (jobId == Guid.Empty)
        {
            _session.SetPhase(FileAnalysisPhase.NoLongerAvailable);
            return;
        }

        // Already in ResumeLoading when entered from OnInitializedAsync (set synchronously to avoid a
        // consent flash); only the reanalyze-fork path needs the transition + repaint here.
        if (Phase != FileAnalysisPhase.ResumeLoading)
        {
            _session.SetPhase(FileAnalysisPhase.ResumeLoading);
            StateHasChanged();
        }

        try
        {
            var loaded = await FileAnalysis.GetJobAsync(jobId);
            if (!loaded.IsSuccess)
            {
                _session.SetPhase(FileAnalysisPhase.NoLongerAvailable);
                return;
            }

            _session.Job = loaded.Value;

            // Re-validate on load — the job must still be resumable (completed, with pending candidates).
            var resumable = _session.Job is { Status: FileAnalysisJobStatus.Completed }
                && _session.Job.Candidates.Any(c => c.ReviewStatus == CandidateTransactionReviewStatus.Pending);
            if (!resumable)
            {
                _session.SetPhase(FileAnalysisPhase.NoLongerAvailable);
                return;
            }

            _jobId = jobId;
            _session.MatchStatus = _session.Job!.MatchStatus;
            _session.SeedRows();
            _session.SetPhase(_session.Rows.Count == 0 ? FileAnalysisPhase.NoLongerAvailable : FileAnalysisPhase.Review);
        }
        catch
        {
            _session.SetPhase(FileAnalysisPhase.NoLongerAvailable);
        }
    }

    // Reanalyze-confirm fork: "Resume review" continues the saved review; "Analyze again" / "Analyze"
    // drop to the consent gate (a new transfer), resetting the affirmed checkbox.
    private Task ResumeFromConfirmAsync() => ContinueResumeAsync();

    private void AnalyzeAgain()
    {
        _consentChecked = false;
        _session.SetPhase(FileAnalysisPhase.Consent);
    }

    // ── Import ───────────────────────────────────────────────────────────────
    private async Task ImportAsync()
    {
        if (_isImporting || _session.SelectedCount == 0)
            return;

        _isImporting = true;
        try
        {
            var imported = await FileAnalysis.ImportAsync(_jobId, _session.BuildImportRequest());
            if (!imported.IsSuccess)
            {
                Snackbar.Add($"Import failed: {imported.Error}", Severity.Error);
                return;
            }

            var result = imported.Value;
            _importedCount = result?.Imported ?? 0;
            _failedCount = result?.Failed ?? 0;
            _failures = result?.Failures ?? [];
            _session.SetImported(AccountName);

            // The review is finished — tell the host to refresh its resumable map so the file's
            // "Review pending" hint clears (no stale indicator).
            await OnResolved.InvokeAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Import error: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isImporting = false;
        }
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);

    private async Task ViewTransactions()
    {
        await OpenChanged.InvokeAsync(false);
        NavigationManager.NavigateTo("/transactions");
    }

    // ── Inline merchant create ───────────────────────────────────────────────
    // The grid already staged the contact optimistically against a temp id; this is the server round
    // trip that either reconciles that id with the real one or rolls the whole thing back.
    //
    // POST /api/contacts carries the Name ONLY (clamped/escaped on the server), never the
    // model-derived OrganizationNumber/Description.
    private async Task CreateContactAsync(FileAnalysisPendingContact pending)
    {
        var (tempId, name) = pending;
        try
        {
            // Quick-create defaults to an Organization with the typed text as its legal name (issue #325 §13).
            var body = new NewContact
            {
                Type = ContactType.Organization,
                Archived = false,
                OrganizationDetails = new OrganizationDetailsDto { LegalName = name },
            };
            var result = await Contacts.CreateAsync(body);
            if (!result.IsSuccess)
            {
                Snackbar.Add($"Couldn’t create “{name}”: {result.Error}", Severity.Error);
                _session.RollbackCreatedContact(tempId);
                StateHasChanged();
                return;
            }

            // The session-wide contact cache is now stale — the next picker must re-fetch.
            ReferenceData.InvalidateContacts();

            // POST /api/contacts returns 201 with an empty body; the new id is in the Location
            // header (CreatedAtRoute), which ApiResult.CreatedId parses.
            if (result.CreatedId is { } id)
            {
                _session.ReconcileCreatedContact(tempId, id, name);
            }
            else
            {
                // Created, but no id in the Location header (unexpected) — refresh the list from the
                // server and re-link the row to the freshly-loaded contact by name.
                await LoadContacts();
                var match = _session.Contacts.FirstOrDefault(c =>
                    string.Equals(c.ResolvedDisplayName, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    _session.ReconcileCreatedContact(tempId, match.ContactId, match.ResolvedDisplayName);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Couldn’t create “{name}”: {ex.Message}", Severity.Error);
            _session.RollbackCreatedContact(tempId);
        }

        StateHasChanged();
    }

    // The grid speaks its discrete outcomes (apply/dismiss a suggestion) on the dialog's live region.
    private void OnGridAnnounce(string message) => _session.Announce(message);

    // ── Data loading ──────────────────────────────────────────────────────────
    private async Task LoadContacts()
    {
        // Served from the session's reference-data cache (issue #372) — a failed load toasts
        // there and yields an empty list, so the merchant column simply doesn't pre-resolve.
        _session.SetContacts(await ReferenceData.ContactsAsync());
    }

    private async Task LoadTags() => _session.SetTags(await ReferenceData.TransactionTagsAsync());

    private async Task LoadCurrencies() =>
        _session.SetCurrencies((await ReferenceData.ActiveCurrenciesAsync()).Select(c => c.CurrencyCode));
}
