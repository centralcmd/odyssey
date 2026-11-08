using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Services;
using Odyssey.Client.Authorization;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Models;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Finance;

public partial class FileAnalysisDialog : IDisposable
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
    /// fork, straight into loading the saved review, or onto a queued job's progress or result. The
    /// dialog does not re-discover resumability.
    /// </summary>
    [Parameter] public StartMode Start { get; set; } = StartMode.Consent;

    /// <summary>The resumable job to reopen (Resume) / the count shown on the confirm fork. Host-supplied.</summary>
    [Parameter] public ResumableAnalysisSummary? ResumeSummary { get; set; }

    /// <summary>Raised after the review is finished (imported) so the host can refresh its resumable map.</summary>
    [Parameter] public EventCallback OnResolved { get; set; }

    /// <summary>
    /// The account-file link this dialog analyzes. The first poll of any job checks the job's <c>AccountFileId</c>
    /// against it and drops a job for another file (issue #445 §5, S7). <see cref="Guid.Empty"/> skips the check.
    /// </summary>
    [Parameter] public Guid AccountFileId { get; set; }

    /// <summary>The job a <see cref="StartMode.Progress"/> or <see cref="StartMode.Result"/> open follows.</summary>
    [Parameter] public Guid? JobId { get; set; }

    /// <summary>
    /// The file's latest job's sent status, from the host's summary: when it is <c>Yes</c> or <c>Possibly</c>,
    /// every entry to consent opens with the second-transfer alert (issue #445 §7).
    /// </summary>
    [Parameter] public FileAnalysisDocumentSent? PriorDocumentSent { get; set; }

    /// <summary>Analyze was chosen on a row whose job is still open: say so while showing its progress.</summary>
    [Parameter] public bool AnnounceAlreadyRunning { get; set; }

    /// <summary>The row's stable menu-trigger id: Close returns focus there for every entry point (A6).</summary>
    [Parameter] public string? ReturnFocusToId { get; set; }

    /// <summary>
    /// Which phase the host opens this dialog in. Resume/ReanalyzeConfirm need a <see cref="ResumeSummary"/>;
    /// Progress (an open job) and Result (a failed or stopped one) need a <see cref="JobId"/>.
    /// </summary>
    public enum StartMode { Consent, ReanalyzeConfirm, Resume, Progress, Result }

    /// <summary>The interactivity check, as a seam for bUnit (the <c>Preferences.InteractiveCheck</c> pattern).</summary>
    internal static Func<bool> InteractiveCheck = OperatingSystem.IsBrowser;

    // The phase machine, candidate rows and match/merchant-create rules (issue #373). The dialog owns
    // only the I/O around it.
    private readonly FileAnalysisSession _session = new();

    private FileAnalysisPhase Phase => _session.Phase;

    // Consent gate — analysis sends the complete document to the external AI processor, so the
    // dialog opens on an informed-consent gate before any bytes leave Odyssey. The affirmed text
    // is forwarded to the analyze endpoint and recorded verbatim in the admin audit log.
    private bool _consentChecked;

    private Guid _jobId;
    private bool _isImporting;

    private int _importedCount;
    private int _failedCount;
    private List<ImportFailure> _failures = [];

    // ── The queued job (issue #445) ──
    // Whether this dialog did NOT start the job (View progress, View result, a 409 attach): such a job reaches
    // review without the match step, which is a transfer the attaching user never started (S1).
    private bool _attached;
    private bool _attachNote;
    private FileAnalysisJobStatus? _jobStatus;
    private bool _transferStarted;
    private DateTimeOffset? _firstObservedOpen;
    private bool _takingLong;
    private bool _hiccup;
    private int _unknownStatusPolls;
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _submitCts;
    private bool _slowStart;

    // The terminal state's client-owned copy and the facts its sent line states. No server text is ever held.
    private FileAnalysisTerminalCopy? _terminal;
    private FileAnalysisDocumentSent _terminalSent;
    private string? _terminalProcessor;
    private long? _tooLargeLimit;
    private bool _tooLarge;

    // The second-transfer alert's subject: the latest job's sent status.
    private FileAnalysisDocumentSent? _priorSent;
    private ElementReference _priorRef;
    private bool ShowPriorAlert => FileAnalysisJobCopy.MayHaveBeenSent(_priorSent);

    // One focusable heading per phase, focused once rendered (A1). Ids are per instance.
    private ElementReference _phaseHeading;
    private FileAnalysisPhase? _focusedPhase;
    private bool _initialFocusSettled;
    private readonly string _idBase = $"fan-{Guid.NewGuid():N}";
    private string HeadingId => $"{_idBase}-h";
    private string BodyId => $"{_idBase}-body";
    private string SentId => $"{_idBase}-sent";

    // The STAMPED processor names a job's transfer; today's disclosure only a transfer yet to happen.
    private string JobProcessor => _session.Job?.ProcessorInForce is { Length: > 0 } stamped ? stamped : FileAnalysisJobCopy.FallbackProcessor;
    private string NextProcessor => string.IsNullOrWhiteSpace(_disclosure.Processor) ? FileAnalysisJobCopy.FallbackProcessor : _disclosure.Processor;
    private string ProgressProcessor => _session.Job?.ProcessorInForce is { Length: > 0 } stamped ? stamped : NextProcessor;

    private bool IsReview => Phase == FileAnalysisPhase.Review;

    private bool IsConsentBody => Phase is FileAnalysisPhase.Consent or FileAnalysisPhase.Submitting;

    // OdsModal keys its inline MudDialog on `Wide`; flipping it re-teleports the dialog, and when that
    // happens during the resume init continuation (not a user event) the previous instance is orphaned
    // behind the new one. So every phase the resume flow can reach without a user click — ResumeLoading,
    // Review, NoLongerAvailable — holds `Wide` constant; their differing visual widths come from the CSS
    // class (ModalClass) instead, which restyles the teleported dialog in place with no re-teleport.
    private bool IsWideFrame =>
        Phase is FileAnalysisPhase.Review or FileAnalysisPhase.ResumeLoading or FileAnalysisPhase.NoLongerAvailable;

    // Header lead glyph + tint (mirrors the design-system AnalyzeFileModal).
    private string HeaderIcon => Phase switch
    {
        FileAnalysisPhase.Consent or FileAnalysisPhase.Submitting => "shield",
        FileAnalysisPhase.ReanalyzeConfirm => "history",
        FileAnalysisPhase.NoLongerAvailable => "unpublished",
        FileAnalysisPhase.Disabled or FileAnalysisPhase.Cancelled => "block",
        FileAnalysisPhase.Blocked when _tooLarge => "block",
        _ => "document_scanner",
    };

    private OdsModalTone HeaderTone =>
        Phase is FileAnalysisPhase.Consent or FileAnalysisPhase.Submitting or FileAnalysisPhase.NoLongerAvailable
            or FileAnalysisPhase.Disabled or FileAnalysisPhase.Cancelled
        || (Phase == FileAnalysisPhase.Blocked && _tooLarge)
            ? OdsModalTone.Warning
            : OdsModalTone.Brand;

    // The pending-candidate count shown while confirming/resuming — from the host's summary, falling
    // back to the loaded job's row count once it's in memory.
    private int _pendingN => ResumeSummary is { } s
        ? (s.PendingCount > 0 ? s.PendingCount : s.CandidateCount)
        : _session.Rows.Count;

    // Dialog-scoped polite live region. The visible text is empty on first paint and only set afterward
    // (OnAfterRender) so assistive tech treats it as a mutation and actually speaks it.
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

    /// <summary>
    /// The polite message is a function of (phase, job status, sub-state) — never of a poll — so it changes only
    /// when that tuple does and an earlier message never masks a later one (issue #445 §6.3).
    /// </summary>
    private string DesiredLiveMsg => _session.ActionAnnounce ?? Phase switch
    {
        FileAnalysisPhase.ResumeLoading => $"Opening your saved review for {FileName}.",
        FileAnalysisPhase.Submitting when _slowStart => FileAnalysisJobCopy.StartingAnalysis,
        FileAnalysisPhase.Analyzing when _takingLong => FileAnalysisJobCopy.TakingLonger,
        FileAnalysisPhase.Analyzing when _attachNote => FileAnalysisJobCopy.AlreadyRunning,
        FileAnalysisPhase.Analyzing when _jobStatus == FileAnalysisJobStatus.Queued => "Analysis queued.",
        FileAnalysisPhase.Analyzing when _jobStatus == FileAnalysisJobStatus.Running && !_transferStarted => $"Preparing to send to {ProgressProcessor}.",
        FileAnalysisPhase.Analyzing when _jobStatus == FileAnalysisJobStatus.Running => $"Sending to {ProgressProcessor} and extracting transactions.",
        FileAnalysisPhase.Matching => "Matching merchants and categories against your contacts and tags.",
        // The one summary line, announced once on entering Review (issue #434). Mutually exclusive with
        // the problem alerts, which announce themselves (role="alert").
        FileAnalysisPhase.Review when _session.MatchProblem == FileAnalysisMatchProblem.None
                                      && _session.MatchStatus == FileAnalysisMatchStatus.Completed
                                      && _session.Job is { } job => FileAnalysisDisclosureText.MatchSummary(job) ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>
    /// The gate may be affirmed only against a resolved disclosure that can be described (issue #434):
    /// an incomplete one reuses the not-resolved presentation and keeps the affirmation disabled.
    /// </summary>
    private bool DisclosureUsable => Disclosures.IsResolved && !FileAnalysisDisclosureText.IsIncomplete(_disclosure);

    // A 429 on analyze (issue #434): the wait the server named, and whether the notice is showing.
    private bool _analyzeRateLimited;
    private TimeSpan? _analyzeRetryAfter;

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
        FileAnalysisPhase.Consent or FileAnalysisPhase.Submitting => "fan-consent-modal",
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

        await MoveFocusAsync();
    }

    /// <summary>
    /// Focus on a phase change (issue #445 §6.2): once the new phase has rendered, to its heading — unless a
    /// higher-precedence notice holds it. On consent the order is the disclosure-changed notice, the
    /// second-transfer alert, the 429 notice (which focuses itself), then the heading. Submitting keeps focus on
    /// the busy send button. The phase the dialog OPENS on is left to the modal's own open focus, except that a
    /// consent gate opening with the second-transfer alert lands on the alert.
    /// </summary>
    private async Task MoveFocusAsync()
    {
        if (_disclosureChangedNeedsFocus)
        {
            _disclosureChangedNeedsFocus = false;
            _focusedPhase = Phase;
            try { await _disclosureChangedRef.FocusAsync(); } catch { /* not focusable yet — non-fatal */ }
            return;
        }

        if (_focusedPhase == Phase)
            return;

        var initial = !_initialFocusSettled;
        _initialFocusSettled = true;
        _focusedPhase = Phase;

        if (Phase == FileAnalysisPhase.Submitting)
            return;

        if (Phase == FileAnalysisPhase.Consent)
        {
            if (ShowPriorAlert)
                await TryFocusAsync(_priorRef);
            else if (!_analyzeRateLimited && !initial)
                await TryFocusAsync(_phaseHeading);
            return;
        }

        if (!initial)
            await TryFocusAsync(_phaseHeading);
    }

    private static async Task TryFocusAsync(ElementReference target)
    {
        try { await target.FocusAsync(); } catch { /* not focusable yet — non-fatal */ }
    }

    // The disclosure the consent panel renders. Starts as the compiled fallback so the gate can never
    // show a partial disclosure, and is replaced by the fetched value; DisclosureResolved is what keeps
    // the affirmation disabled until that happens.
    private FileAnalysisDisclosureDto _disclosure = FileAnalysisDisclosureCache.Fallback;

    protected override async Task OnInitializedAsync()
    {
        _priorSent = PriorDocumentSent;

        // Resolve the host-decided initial phase synchronously, before the first await, so the dialog
        // never flashes the consent gate while a resume/reanalyze/progress open is in flight.
        if (Start == StartMode.ReanalyzeConfirm)
            _session.SetPhase(FileAnalysisPhase.ReanalyzeConfirm);
        else if (Start == StartMode.Resume)
            _session.SetPhase(FileAnalysisPhase.ResumeLoading);
        else if (Start is StartMode.Progress or StartMode.Result)
        {
            _attached = true;
            _attachNote = AnnounceAlreadyRunning;
            _session.SetPhase(FileAnalysisPhase.Analyzing);
        }

        if (!InteractiveCheck())
            return;

        // Fetched here rather than in the panel, which stays purely presentational (#373). Stale-while-
        // revalidate, so an admin's change reaches the gate on its next open rather than next session.
        _disclosure = await Disclosures.GetAsync();

        // Resolve the inline-create gate (contacts.create) up front so the Merchant combobox
        // renders the right affordance from first paint.
        var user = await AuthState.GetUserAsync();
        _session.CanCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
        _session.CanCreateTag = user.HasPermission(PermissionClaims.TransactionTagsCreate);

        // Reference data is loaded up front so the review phase is ready the moment analysis
        // completes — and, on resume, BEFORE candidate rows are seeded (seeding against unloaded
        // contacts/tags would silently drop merchant/tag prefills). Analysis itself does not
        // run until the user clears the consent gate.
        await Task.WhenAll(LoadContacts(), LoadTags(), LoadCurrencies());

        // Resume: reference data is now loaded, so fetch the saved job and seed the Review rows.
        if (Start == StartMode.Resume)
            await ContinueResumeAsync();
        else if (Start is StartMode.Progress or StartMode.Result)
        {
            if (JobId is { } id && id != Guid.Empty)
                StartPolling(id);
            else
                ShowTerminal(FileAnalysisJobCopy.PostRefused);
        }
    }

    // The consent gate cleared → record the affirmed consent and start the transfer.
    private async Task ProceedFromConsentAsync()
    {
        // Submitting: the busy button ignores a second activation, so a double click sends nothing (AC 1).
        if (!_consentChecked || Phase == FileAnalysisPhase.Submitting)
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
    // The POST only enqueues (issue #444): a 202 starts the poller; every refusal maps to client-owned copy by
    // status and code, never to the server's text.
    private async Task RunAnalysisAsync()
    {
        _session.SetPhase(FileAnalysisPhase.Submitting);
        _slowStart = false;
        _submitCts?.Cancel();
        _submitCts = new CancellationTokenSource();
        _ = AnnounceSlowStartAsync(_submitCts.Token);
        StateHasChanged();

        ApiResult<AnalyzeFileResponse> analyzed;
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
            analyzed = await Accounts.AnalyzeFileAsync(AccountId, FileId, analyzeRequest);
        }
        catch
        {
            analyzed = default;
        }
        finally
        {
            _submitCts?.Cancel();
            _slowStart = false;
        }

        _analyzeRateLimited = false;
        if (analyzed.IsSuccess && analyzed.Status != default)
        {
            if (analyzed.Value is not { } accepted || accepted.AnalysisJobId == Guid.Empty)
            {
                ShowTerminal(FileAnalysisJobCopy.PostRefused);
                return;
            }

            _attached = false;
            _attachNote = false;
            _jobStatus = FileAnalysisJobStatus.Queued;
            _transferStarted = false;
            _session.Job = null;
            _session.SetPhase(FileAnalysisPhase.Analyzing);
            StartPolling(accepted.AnalysisJobId);
            return;
        }

        switch (analyzed.Status)
        {
            case HttpStatusCode.Conflict when analyzed.Problem.IsFileAnalysisInProgress():
                // Attach to the open job: nothing new is sent, the tick is discarded (S9), no auto-match (S1).
                _consentChecked = false;
                if (!analyzed.Problem.TryGetAnalysisJobId(out var openId))
                {
                    ShowTerminal(FileAnalysisJobCopy.PostRefused);
                    return;
                }

                _attached = true;
                _attachNote = true;
                _jobStatus = null;
                _session.Job = null;
                _session.SetPhase(FileAnalysisPhase.Analyzing);
                StartPolling(openId);
                return;
            case HttpStatusCode.Conflict when analyzed.Problem.IsFileAnalysisDisclosureChanged():
                await RepromptForChangedDisclosureAsync();
                return;
            case HttpStatusCode.ServiceUnavailable when analyzed.Problem.IsFileAnalysisFeatureDisabled():
                _session.SetPhase(FileAnalysisPhase.Disabled);
                return;
            case HttpStatusCode.ServiceUnavailable
                when analyzed.Problem?.Code == FileAnalysisCodes.ConfigurationUnavailable:
                ShowTerminal(FileAnalysisJobCopy.PostConfigurationUnavailable);
                return;
            case HttpStatusCode.ServiceUnavailable:
                // No code (a proxy during a redeploy) is never the disabled state (R2-S N5).
                ShowTerminal(FileAnalysisJobCopy.PostUnavailable);
                return;
            case HttpStatusCode.UnprocessableEntity when analyzed.Problem.IsFileAnalysisTooLarge():
                _tooLarge = true;
                _tooLargeLimit = analyzed.Problem.TryGetAnalysisSizeLimit(out var limit) ? limit : null;
                _session.SetTooLarge();
                return;
            case HttpStatusCode.TooManyRequests:
                // Nothing was sent. Back to the gate, which keeps the affirmation and shows the wait.
                _analyzeRateLimited = true;
                _analyzeRetryAfter = analyzed.RetryAfter;
                _session.SetPhase(FileAnalysisPhase.Consent);
                return;
            case HttpStatusCode.BadRequest:
                _tooLarge = false;
                _session.SetPhase(FileAnalysisPhase.Blocked);
                return;
            case default(HttpStatusCode):
                // Never reached the server, or never answered: a job may have been enqueued (R2-S N1), so the
                // copy says nothing about whether anything was sent, and the map refreshes on close.
                ShowTerminal(FileAnalysisJobCopy.PostTimeout);
                return;
            default:
                ShowTerminal(FileAnalysisJobCopy.PostRefused);
                return;
        }
    }

    // "Starting analysis…" only once the POST has taken longer than 500 ms (AC 1).
    private async Task AnnounceSlowStartAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), Time, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || Phase != FileAnalysisPhase.Submitting)
                return;
            _slowStart = true;
            StateHasChanged();
        });
    }

    private void ShowTerminal(FileAnalysisTerminalCopy copy, FileAnalysisDocumentSent sent = FileAnalysisDocumentSent.No,
        string? processor = null, FileAnalysisPhase phase = FileAnalysisPhase.Failed)
    {
        StopPolling();
        _terminal = copy;
        _terminalSent = sent;
        _terminalProcessor = processor;
        _hiccup = false;
        _session.SetPhase(phase);
    }

    // ── Polling (issue #445 §5) ──────────────────────────────────────────────
    // One CancellationTokenSource per poll session, cancelled on close, on a new start and on dispose. The delay
    // goes through the injected TimeProvider, so tests advance a clock instead of sleeping.
    private void StartPolling(Guid jobId)
    {
        StopPolling();
        _jobId = jobId;
        _hiccup = false;
        _unknownStatusPolls = 0;
        _firstObservedOpen = null;
        _takingLong = false;
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(jobId, _pollCts.Token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private async Task PollLoopAsync(Guid jobId, CancellationToken ct)
    {
        var first = true;
        var transient = 0;
        while (!ct.IsCancellationRequested)
        {
            ApiResult<ExistingFileAnalysisJob> polled;
            try
            {
                polled = await FileAnalysis.GetJobAsync(jobId, ct);
            }
            catch
            {
                polled = default;
            }

            if (ct.IsCancellationRequested)
                return;

            TimeSpan? next = null;
            await InvokeAsync(async () =>
            {
                if (ct.IsCancellationRequested || jobId != _jobId)
                    return;
                next = await ApplyPollAsync(polled, first, () => ++transient, () => transient = 0);
                first = false;
                StateHasChanged();
            });

            if (next is not { } delay)
                return;

            try
            {
                await Task.Delay(delay, Time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Applies one poll; returns the wait before the next one, or <c>null</c> when polling ends.</summary>
    private async Task<TimeSpan?> ApplyPollAsync(
        ApiResult<ExistingFileAnalysisJob> polled, bool first, Func<int> countTransient, Action resetTransient)
    {
        if (polled.IsSuccess && polled.Value is { } job)
        {
            if (job.Id != _jobId)
                return FileAnalysisPollPolicy.Interval(polled.RetryAfterDelta);

            // A job for another file is dropped, never shown (S7).
            if (first && AccountFileId != Guid.Empty && job.AccountFileId != AccountFileId)
            {
                ShowTerminal(FileAnalysisJobCopy.PostRefused);
                return null;
            }

            resetTransient();
            _hiccup = false;
            _session.Job = job;

            switch (job.Status)
            {
                case FileAnalysisJobStatus.New or FileAnalysisJobStatus.Queued or FileAnalysisJobStatus.Running:
                    ObserveOpen(job);
                    return FileAnalysisPollPolicy.Interval(polled.RetryAfterDelta);

                case FileAnalysisJobStatus.Completed:
                    StopPolling();
                    await CompleteAsync(job);
                    return null;

                case FileAnalysisJobStatus.Failed:
                    ShowTerminal(FileAnalysisJobCopy.ForFailed(job.FailureCode), job.DocumentSent, job.ProcessorInForce);
                    return null;

                case FileAnalysisJobStatus.Cancelled:
                    ShowTerminal(FileAnalysisJobCopy.ForCancelled(job.FailureCode), job.DocumentSent, job.ProcessorInForce,
                        FileAnalysisPhase.Cancelled);
                    return null;

                default:
                    // An unknown status is non-terminal for at most three polls (S7).
                    if (++_unknownStatusPolls >= FileAnalysisPollPolicy.UnknownStatusPolls)
                    {
                        ShowTerminal(FileAnalysisJobCopy.ForFailed(null), job.DocumentSent, job.ProcessorInForce);
                        return null;
                    }

                    return FileAnalysisPollPolicy.Interval(polled.RetryAfterDelta);
            }
        }

        switch (polled.Status)
        {
            case HttpStatusCode.NotFound:
                StopPolling();
                _session.SetPhase(FileAnalysisPhase.NoLongerAvailable);
                return null;
            case HttpStatusCode.Forbidden:
                ShowTerminal(FileAnalysisJobCopy.PollForbidden);
                return null;
            case HttpStatusCode.Unauthorized:
                // The app's UnauthorizedHandler already sends the user to sign in.
                StopPolling();
                return null;
            case HttpStatusCode.TooManyRequests:
                // Waits as told, and is not a transient failure (R2-F N7).
                return FileAnalysisPollPolicy.Interval(polled.RetryAfterDelta ?? polled.RetryAfter);
            case default(HttpStatusCode):
            case >= HttpStatusCode.InternalServerError:
                // A network error or any 5xx — a 503 included — is transient: keep polling, back off.
                var failures = countTransient();
                if (failures >= FileAnalysisPollPolicy.HiccupAfter)
                    _hiccup = true;
                return FileAnalysisPollPolicy.Backoff(failures);
            default:
                ShowTerminal(FileAnalysisJobCopy.PostRefused);
                return null;
        }
    }

    private void ObserveOpen(ExistingFileAnalysisJob job)
    {
        // The attach note gives way once the job moves on from the status it was attached at.
        if (_jobStatus is { } previous && previous != job.Status)
            _attachNote = false;

        _jobStatus = job.Status == FileAnalysisJobStatus.New ? FileAnalysisJobStatus.Queued : job.Status;
        _transferStarted = job.TransferStartedAt is not null;

        // "Taking longer" counts from when THIS dialog first saw the job open, on the client clock (R2-F N4).
        var now = Time.GetUtcNow();
        _firstObservedOpen ??= now;
        if (!_takingLong && now - _firstObservedOpen.Value >= FileAnalysisPollPolicy.TakingLongerAfter)
            _takingLong = true;

        if (Phase != FileAnalysisPhase.Analyzing)
            _session.SetPhase(FileAnalysisPhase.Analyzing);
    }

    /// <summary>
    /// Extraction finished. A job this dialog started goes on to the match step, as before; an attached one opens
    /// its review UNMATCHED — matching is a transfer the attaching user never started (S1, F3).
    /// </summary>
    private async Task CompleteAsync(ExistingFileAnalysisJob job)
    {
        _jobStatus = job.Status;
        if (_attached)
        {
            _session.MatchStatus = job.MatchStatus;
            _session.SeedRows();
            _session.SetPhase(_session.Rows.Count == 0 ? FileAnalysisPhase.Empty : FileAnalysisPhase.Review);
            return;
        }

        _session.SeedRows();
        if (_session.Rows.Count == 0)
        {
            _session.SetPhase(FileAnalysisPhase.Empty);
            return;
        }

        await RunMatchAsync();
    }

    // The match step (issue #266): POST the match endpoint, which sends the contact + tag NAMES,
    // persists the resolved ids, and returns the updated job. The client then rebuilds the Review rows
    // from the persisted match data (single source of truth) and opens Review. A match failure/over-cap
    // is conveyed via matchStatus (200, not an error) so Review still opens with raw candidates.
    private async Task RunMatchAsync(bool preserveManual = false)
    {
        _session.SetPhase(FileAnalysisPhase.Matching);
        StateHasChanged();

        _session.MatchProblem = FileAnalysisMatchProblem.None;
        _session.MatchRetryAfter = null;

        try
        {
            var matched = await FileAnalysis.MatchAsync(_jobId);
            if (!matched.IsSuccess && matched.Problem.IsFileAnalysisDestinationChanged())
            {
                // Only this code: any other 409 keeps the generic failure handling below.
                _session.MatchProblem = FileAnalysisMatchProblem.DestinationChanged;
                _session.MatchStatus = FileAnalysisMatchStatus.Failed;
            }
            else if (!matched.IsSuccess && matched.Status == HttpStatusCode.TooManyRequests)
            {
                _session.MatchProblem = FileAnalysisMatchProblem.RateLimited;
                _session.MatchRetryAfter = matched.RetryAfter;
                _session.MatchStatus = FileAnalysisMatchStatus.Failed;
            }
            else if (!matched.IsSuccess && matched.Status == HttpStatusCode.ServiceUnavailable
                     && matched.Problem.IsFileAnalysisFeatureDisabled())
            {
                // Analysis was switched off while this review was open (R2-F N2): the disabled state, as today.
                _session.SetPhase(FileAnalysisPhase.Disabled);
                return;
            }
            else if (matched.IsSuccess)
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
    // None/Llm suggestions while preserving any row the reviewer already curated. For an attached review this
    // is "Match names", which states what it sends first and only then calls match.
    private Task ReMatchAsync() => RunMatchAsync(preserveManual: true);

    /// <summary>
    /// "Start new analysis" after a <c>409 file_analysis_destination_changed</c> (issue #434): the open
    /// analysis was consented under different provider settings, so the way on is a fresh consent. The
    /// disclosure is re-fetched, the affirmation resets, and focus lands on the consent step.
    /// </summary>
    private Task StartNewAnalysisAsync() => ReturnToConsentAsync();

    /// <summary>
    /// Try again, Review and analyze, Re-analyze and Analyze again all go back through consent (issue #445 §3,
    /// S9): the box unticked, the disclosure re-read, and the second-transfer alert when the latest job may
    /// already have sent the document — a new job, never a silent re-send.
    /// </summary>
    private async Task ReturnToConsentAsync()
    {
        if (_session.Job is { } latest && FileAnalysisJobCopy.MayHaveBeenSent(latest.DocumentSent))
            _priorSent = latest.DocumentSent;
        else if (_terminal is { ShowSent: true } && FileAnalysisJobCopy.MayHaveBeenSent(_terminalSent))
            _priorSent = _terminalSent;

        StopPolling();
        Disclosures.Invalidate();
        _disclosure = await Disclosures.GetAsync();
        _consentChecked = false;
        _analyzeRateLimited = false;
        _analyzeRetryAfter = null;
        _session.MatchProblem = FileAnalysisMatchProblem.None;
        _session.MatchRetryAfter = null;
        _session.MatchStatus = FileAnalysisMatchStatus.NotRun;
        _session.Rows.Clear();
        _session.Job = null;
        _jobId = Guid.Empty;
        _jobStatus = null;
        _attached = false;
        _attachNote = false;
        _terminal = null;
        _tooLarge = false;
        _session.SetPhase(FileAnalysisPhase.Consent);
        StateHasChanged();
    }

    private Task ReanalyzeAsync() => ReturnToConsentAsync();

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
            _attached = true;
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

    private Task AnalyzeAgain() => ReturnToConsentAsync();

    // ── Import ───────────────────────────────────────────────────────────────
    private async Task ImportAsync()
    {
        if (_isImporting || _session.SelectedCount == 0)
            return;

        _isImporting = true;
        try
        {
            // Let every staged inline create land first. A merchant or a category created in the
            // review is linked to its row against a TEMPORARY id, and BuildImportRequest parses those
            // ids straight into the request — so importing while a POST is still in flight would post
            // an id no server row backs. Awaiting resolves each staged create one way or the other:
            // a success has rewritten the row to the real id, a failure has rolled the row back.
            await WhenCreatesSettledAsync();

            var imported = await FileAnalysis.ImportAsync(_jobId, _session.BuildImportRequest());
            if (!imported.IsSuccess)
            {
                // Client-owned copy: the dialog renders no server text (issue #445 §5).
                Snackbar.Add(imported.Status == HttpStatusCode.ServiceUnavailable
                    ? "Import failed: analysis isn’t available on this server right now."
                    : "Import failed. Try again.", Severity.Error);
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
        catch
        {
            Snackbar.Add("Import failed. Try again.", Severity.Error);
        }
        finally
        {
            _isImporting = false;
        }
    }

    // Closing never cancels the job (issue #445 §3): it stops this dialog's poller, and the host refreshes its map.
    private Task CloseAsync()
    {
        StopPolling();
        _submitCts?.Cancel();
        return OpenChanged.InvokeAsync(false);
    }

    public void Dispose()
    {
        StopPolling();
        _submitCts?.Cancel();
        _submitCts?.Dispose();
    }

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
    /// <summary>
    /// Every staged create started in this review, so <see cref="ImportAsync"/> can wait for them.
    /// </summary>
    /// <remarks>
    /// The grid raises its create callbacks fire-and-forget — it has to, because the picker hands the
    /// option back synchronously — so the task is captured here instead. Each entry already handles
    /// its own failure, so awaiting the set never throws.
    /// </remarks>
    private readonly List<Task> _pendingCreates = [];

    private Task WhenCreatesSettledAsync() => Task.WhenAll(_pendingCreates.ToArray());

    private Task CreateContactAsync(FileAnalysisPendingContact pending)
    {
        var task = RunCreateContactAsync(pending);
        _pendingCreates.Add(task);
        return task;
    }

    private async Task RunCreateContactAsync(FileAnalysisPendingContact pending)
    {
        var (tempId, name, type) = pending;
        try
        {
            // The type is the one the reviewer picked on the create row — never guessed after the fact.
            var result = await Contacts.CreateAsync(ContactQuickCreate.BuildPayload(name, type));
            if (!result.IsSuccess)
            {
                Snackbar.Add($"Couldn’t create “{name}”.", Severity.Error);
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
        catch
        {
            Snackbar.Add($"Couldn’t create “{name}”.", Severity.Error);
            _session.RollbackCreatedContact(tempId);
        }

        StateHasChanged();
    }

    // ── Inline category-tag create ────────────────────────────────────────────
    // The grid already staged the tag optimistically against a temp id; this is the round trip that
    // reconciles it with the real one or rolls the whole thing back.
    private Task CreateTagAsync(FileAnalysisPendingTag pending)
    {
        var task = RunCreateTagAsync(pending);
        _pendingCreates.Add(task);
        return task;
    }

    private async Task RunCreateTagAsync(FileAnalysisPendingTag pending)
    {
        var (tempId, name) = pending;
        try
        {
            var result = await TransactionTags.CreateAsync(new TagWrite(name, null, false, Icon: null, Color: null));
            if (result.IsSuccess && result.CreatedId is { } id)
            {
                // The session-wide tag cache is now stale — the next picker must re-fetch.
                ReferenceData.InvalidateTransactionTags();
                _session.ReconcileCreatedTag(tempId, id, name);
            }
            else
            {
                // The create row is withheld when a listed tag already carries the name, so a conflict
                // here means an ARCHIVED tag holds it — which this cannot select or un-archive.
                Snackbar.Add(result.Status == System.Net.HttpStatusCode.Conflict
                    ? $"Couldn’t create “{name}”: an archived tag already uses that name."
                    : $"Couldn’t create “{name}”.", Severity.Error);
                _session.RollbackCreatedTag(tempId);
            }
        }
        catch
        {
            Snackbar.Add($"Couldn’t create “{name}”.", Severity.Error);
            _session.RollbackCreatedTag(tempId);
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
