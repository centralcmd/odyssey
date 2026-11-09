using Odyssey.Core;
using Odyssey.Dtos;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ContextJobStatus = Odyssey.Context.FileAnalysisJobStatus;
using ContextReviewStatus = Odyssey.Context.CandidateTransactionReviewStatus;
using ContextAnalyzerProvider = Odyssey.Context.AnalyzerProvider;
using ContextMatchStatus = Odyssey.Context.FileAnalysisMatchStatus;
using ContextMatchMethod = Odyssey.Context.MatchMethod;
using DtoJobStatus = Odyssey.Dtos.Finance.FileAnalysisJobStatus;
using DtoReviewStatus = Odyssey.Dtos.Finance.CandidateTransactionReviewStatus;
using DtoMatchStatus = Odyssey.Dtos.Finance.FileAnalysisMatchStatus;
using DtoMatchMethod = Odyssey.Dtos.Finance.MatchMethod;
using Context = Odyssey.Context;

namespace Odyssey.Core.Finance;

public class FileAnalysisService
{
    private readonly OdysseyContext context;
    private readonly IFileAnalysisProvider provider;
    private readonly IContactLookup contactLookup;
    private readonly FileAnalysisOptions options;
    private readonly IFileAnalysisSettingsLookup settingsLookup;
    private readonly ILogger<FileAnalysisService> logger;
    private readonly TimeProvider timeProvider;
    private readonly FileAnalysisJobQueue queue;
    private readonly IFileAnalysisPromptTemplate promptTemplate;

    public FileAnalysisService(
        OdysseyContext context,
        IFileAnalysisProvider provider,
        IContactLookup contactLookup,
        IOptions<FileAnalysisOptions> options,
        IFileAnalysisSettingsLookup settingsLookup,
        ILogger<FileAnalysisService> logger,
        FileAnalysisJobQueue queue,
        IFileAnalysisPromptTemplate promptTemplate,
        TimeProvider? timeProvider = null)
    {
        this.queue = queue;
        this.promptTemplate = promptTemplate;
        this.context = context;
        this.provider = provider;
        this.contactLookup = contactLookup;
        this.options = options.Value;
        this.settingsLookup = settingsLookup;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates, records the consent and ENQUEUES an extraction (issue #444): the job is saved
    /// <c>Queued</c> with every consent stamp, and nothing is sent here. <c>Odyssey.Worker</c> claims it,
    /// re-checks the consent from an uncached settings read and only then transfers the document.
    /// </summary>
    public async Task<AnalyzeFileResponse> AnalyzeAsync(
        Guid accountId,
        Guid fileId,
        string requestedByUserId,
        AnalyzeFileRequest? consent = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(cancellationToken);

        // The metadata only: the bytes are the worker's to read, so the request stays fast whatever the size.
        var accountFile = await context.AccountFiles
            .Include(af => af.FileMetadata)
            .FirstOrDefaultAsync(
                af => af.AccountId == accountId && af.FileMetadataId == fileId,
                cancellationToken)
            ?? throw new DomainNotFoundException($"File {fileId} is not attached to account {accountId}.");

        if (accountFile.FileType != Context.AccountFileType.Statement)
            throw new DomainValidationException("Only files of type Statement can be analyzed.");

        var fileMetadata = accountFile.FileMetadata
            ?? throw new InvalidOperationException("File metadata could not be loaded.");

        // The analysis ceiling (issue #444 v4): the upload cap cannot bound the worker's memory, this does.
        if (fileMetadata.SizeBytes > FileAnalysisJobTiming.MaxAnalysisFileBytes)
            throw new FileAnalysisTooLargeException(FileAnalysisJobTiming.MaxAnalysisFileBytes);

        // Consent gate — analysis sends the complete document to a third-party AI processor, so a
        // per-document consent is a hard precondition (GDPR Art. 6(1)(a)), not a UI affordance. The
        // affirmed text is captured verbatim on the job for the audit log.
        if (consent?.ConsentAcknowledged != true)
            throw new DomainValidationException("Consent is required before sending this document to the external AI provider.");

        // One snapshot, read once: every stamp below describes one coherent moment.
        var settings = await settingsLookup.GetAsync(cancellationToken);

        // Issue #433: a failed settings read knows nothing, including the guidance flags the disclosure
        // version covers, so consent cannot be verified against it. 503, never a misleading 409.
        if (settings.ReadFailed)
            throw new FileAnalysisUnavailableException();

        // Refuse rather than substitute when the model or the destination is unusable — see
        // RequireTarget. Evaluated BEFORE the disclosure-version check, so a misconfigured instance
        // answers 503 rather than leaking disclosure state through a 409.
        var target = RequireTarget(settings);

        // The consent the user affirmed is bound to the disclosure they were shown (issue #439 §5.3c).
        // A mismatch (or a missing echo) is a 409 here, before any job row exists.
        if (!FileAnalysisDisclosureVersion.Matches(settings, consent.DisclosureVersion))
            throw new FileAnalysisDisclosureChangedException();

        // A stale open job must not wedge the file when no worker is running (issue #444 §3 step 1), so this
        // file's expired rows are failed inline before the one-open-job check.
        await queue.SweepAsync(accountFileId: accountFile.Id, cancellationToken: cancellationToken);

        // One open job per file. Check-then-insert, so two clients posting within one round-trip can both
        // pass it — accepted (product decision): each is separately consented and recorded, and the rate
        // limiter bounds it. The client closes the double-click case by refusing a second click.
        var open = await context.FileAnalysisJobs.AsNoTracking()
            .Where(j => j.AccountFileId == accountFile.Id
                && (j.Status == ContextJobStatus.Queued || j.Status == ContextJobStatus.Running))
            .OrderByDescending(j => j.QueuedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (open is { } openId)
            throw new FileAnalysisInProgressException(openId);

        var job = new FileAnalysisJob
        {
            AccountFileId = accountFile.Id,
            // Queued (issue #444): the worker claims it, re-checks this consent from an uncached read and
            // only then sends. StartedAt is set when the transfer starts, never here.
            Status = ContextJobStatus.Queued,
            QueuedAt = timeProvider.GetUtcNow().UtcDateTime,
            AnalyzerProvider = ContextAnalyzerProvider.Claude,
            // From the resolved target, not from options: the model is admin-editable now, so the value
            // stamped here has to be the value the request below is BUILT with, or the audit trail and
            // the transfer describe different runs.
            AnalyzerModel = Truncate(target.Model, 256),
            PromptVersion = promptTemplate.Version,
            // What the worker must hold to send (issue #444 §3 step 4): the template it would send, the
            // bytes the user consented to, and the destination's full origin (scheme, host and port).
            PromptTemplateHash = promptTemplate.Sha256,
            FileSha256 = Truncate(fileMetadata.Sha256Hash, 64),
            AnalyzerBaseUrlOrigin = Truncate(OriginOf(target.BaseUrl), 256),
            RequestedByUserId = requestedByUserId,
            ConsentRecorded = true,
            ConsentMethod = Truncate(string.IsNullOrWhiteSpace(consent.ConsentMethod) ? "Per-document checkbox" : consent.ConsentMethod, 128),
            ConsentText = Truncate(consent.ConsentText, 1024),
            // Lawful basis is admin-editable (issue #421 Wave 1) and stamped verbatim, so the audit
            // row records the basis asserted at the time of the transfer rather than whatever it is
            // now. That is the same reasoning that keeps the affirmed consent sentence persisted.
            LawfulBasis = Truncate(settings.LawfulBasis, 128),
            AutoLinkThresholdInForce = settings.AutoLinkThreshold,
            // The model output cap this run was built with (issue #434 §8). Stamped for the same
            // reason as the threshold beside it: MaxTokens bounds the model's output, so it bounds
            // extraction COMPLETENESS — without the stamp, a truncated extraction from six months ago
            // is indistinguishable from a model failure once an administrator retunes.
            MaxTokensInForce = settings.MaxTokens,
            // Transfer provenance (issue #439 §6), all three from the same snapshot as the lawful basis
            // above. The host records where the document actually went — which, with redirects disabled
            // on the outbound client, cannot diverge from where it was sent. The processor survived
            // only incidentally before, inside the composed consent sentence; the region, the fact that
            // decides whether this was a third-country transfer under GDPR Art. 44-49, survived nowhere.
            AnalyzerBaseUrlHost = Truncate(HostOf(target.BaseUrl), 256),
            ProcessorInForce = Truncate(settings.Processor, 128),
            ProcessorRegionInForce = Truncate(settings.ProcessorRegion, 128),
            // Issue #433: what this consent covers, stamped from the same snapshot. Match reads the
            // scope and compares the match-relevant version, so a later match never sends more, or
            // to a different destination, than this consent named.
            MatchVocabularyScope = FileAnalysisDisclosureVersion.CurrentScope,
            DisclosureVersionInForce = FileAnalysisDisclosureVersion.Compute(settings),
            MatchDisclosureVersionInForce =
                FileAnalysisDisclosureVersion.ComputeForMatch(settings, FileAnalysisDisclosureVersion.CurrentScope),
            ExtractionPromptAdditionInForce = Truncate(settings.ExtractionPromptAddition, 4000),
        };

        context.FileAnalysisJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Analysis job {JobId} queued.", job.Id);
        return new AnalyzeFileResponse(job.Id);
    }

    /// <summary>
    /// Runs a just-queued job in THIS process — only for a host with no worker (the <c>Testing</c>
    /// environment, the <c>ExchangeRateSyncJob</c> run-now precedent). The caller decides that by environment
    /// name alone, never by a setting.
    /// </summary>
    public async Task RunInlineAsync(Guid jobId, FileAnalysisExtractionRunner runner, CancellationToken cancellationToken)
    {
        if (await queue.TryClaimAsync(jobId, cancellationToken))
        {
            await runner.RunAsync(jobId, cancellationToken);
        }
    }

    /// <summary>A mapped candidate row (§3 step 6), shared with the worker's runner.</summary>
    internal static FileAnalysisCandidateTransaction ToCandidate(Guid jobId, ExtractedTransaction t, string accountCurrency) => new()
    {
        AnalysisJobId = jobId,
        TransactionDate = t.TransactionDate,
        BookingDate = t.BookingDate,
        Description = Truncate(t.Description, 1024) ?? string.Empty,
        Merchant = Truncate(t.Merchant, 512),
        CategoryHint = Truncate(t.CategoryHint, 256),
        Amount = t.Amount,
        Currency = NormalizeCurrency(t.Currency, accountCurrency),
        ExternalId = Truncate(t.ExternalId, 256),
        ReferenceNumber = Truncate(t.ReferenceNumber, 256),
        LlmConfidence = t.LlmConfidence.HasValue
            ? Math.Clamp(t.LlmConfidence.Value, 0m, 1m)
            : null,
        LlmModel = Truncate(t.LlmModel, 256),
        LlmProviderResponseId = Truncate(t.LlmProviderResponseId, 256),
        LlmRawJson = t.LlmRawJson,
        ReviewStatus = ContextReviewStatus.Pending,
    };

    public async Task<ExistingFileAnalysisJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        // No enabled-gate since issue #444: the job is a historical record, like the audit log — and a client
        // polling a job the kill switch cancelled must be able to read that it was cancelled. The inline sweep
        // fails this one job if it went stale, so a missing worker never leaves a client polling for ever.
        await queue.SweepAsync(jobId: jobId, cancellationToken: cancellationToken);

        var job = await context.FileAnalysisJobs
            .Include(j => j.CandidateTransactions)
                .ThenInclude(c => c.MatchedTags)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job is null)
            return null;

        var contactNames = await LoadMatchedContactNamesAsync(job, cancellationToken);
        // Only used as the fallback for jobs predating AutoLinkThresholdInForce; a stamped job ignores it.
        var liveSettings = await settingsLookup.GetAsync(cancellationToken);
        return MapJob(job, contactNames, liveSettings.AutoLinkThreshold);
    }

    // Slim, batched id→name projection for the matched contacts: a single query over the distinct
    // ids (no N+1), names only — NOT a Mapster-mapped Contact DTO, which would deep-map notes/org
    // numbers and re-introduce a cross-claim leak through the file-analysis read path.
    private async Task<Dictionary<Guid, string>> LoadMatchedContactNamesAsync(
        FileAnalysisJob job, CancellationToken cancellationToken)
    {
        var ids = job.CandidateTransactions
            .Where(c => c.MatchedContactId.HasValue)
            .Select(c => c.MatchedContactId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        var refs = await contactLookup.ResolveRefsAsync(ids, cancellationToken);
        return refs.Values.ToDictionary(r => r.ContactId, r => r.Name);
    }

    /// <summary>
    /// Each file's latest analysis state for an account (issue #444 §5.3) — the one account-scoped read the
    /// Files surface drives its chips, its menu and the dialog's start mode from. See
    /// <see cref="ResumableAnalysisSummary"/> for the precedence; counts are SQL-projected, so no candidate
    /// free-text is materialised.
    /// </summary>
    public async Task<IReadOnlyList<ResumableAnalysisSummary>> GetResumableJobsAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        // No enabled-gate since issue #444: these are reads of existing jobs, and View progress / View result
        // / Resume review must keep working while analysis is switched off.

        // 404 only when the account itself doesn't exist (an empty list otherwise).
        var accountExists = await context.Accounts
            .AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            throw new DomainNotFoundException($"Account {accountId} not found.");

        // One round-trip: every job for the account, projected to its file id, timestamps and SQL-computed
        // counts (COUNT + conditional COUNT) — never Include-then-count, which would pull candidate free-text.
        var jobs = await context.FileAnalysisJobs
            .AsNoTracking()
            .Where(j => j.AccountFile!.AccountId == accountId)
            .Select(j => new
            {
                FileId = j.AccountFile!.FileMetadataId,
                JobId = j.Id,
                j.Status,
                j.StartedAt,
                j.QueuedAt,
                j.TransferStartedAt,
                j.CompletedAt,
                j.FailureCode,
                CandidateCount = j.CandidateTransactions.Count,
                PendingCount = j.CandidateTransactions.Count(c => c.ReviewStatus == ContextReviewStatus.Pending),
            })
            .ToListAsync(cancellationToken);

        // Newest first by request time (QueuedAt, else the legacy StartedAt), tie-broken by id: deterministic.
        static DateTime At(DateTime? queued, DateTime? started) => queued ?? started ?? DateTime.MinValue;

        var summaries = new List<ResumableAnalysisSummary>();
        foreach (var file in jobs.GroupBy(x => x.FileId))
        {
            var ordered = file.OrderByDescending(x => At(x.QueuedAt, x.StartedAt)).ThenByDescending(x => x.JobId).ToList();
            var open = ordered.FirstOrDefault(x => x.Status is ContextJobStatus.Queued or ContextJobStatus.Running);
            var resumable = ordered.FirstOrDefault(x => x.Status == ContextJobStatus.Completed && x.PendingCount > 0);
            var newest = ordered[0];
            var newestFailed = newest.Status is ContextJobStatus.Failed or ContextJobStatus.Cancelled ? newest : null;

            var primary = open ?? resumable ?? newestFailed;
            if (primary is null)
            {
                continue; // never-analysed (no rows) or fully reviewed: uniformly absent
            }

            var latestFailure = newestFailed is not null && newestFailed.JobId != primary.JobId
                ? new LatestAnalysisFailure(
                    AnalysisJobId: newestFailed.JobId,
                    Status: MapJobStatus(newestFailed.Status),
                    FailureCode: newestFailed.FailureCode,
                    DocumentSent: FileAnalysisTerminal.DocumentSent(newestFailed.Status, newestFailed.QueuedAt, newestFailed.TransferStartedAt),
                    At: newestFailed.CompletedAt ?? At(newestFailed.QueuedAt, newestFailed.StartedAt))
                : null;
            var completed = primary.Status == ContextJobStatus.Completed;

            summaries.Add(new ResumableAnalysisSummary(
                FileId: file.Key,
                AnalysisJobId: primary.JobId,
                Status: MapJobStatus(primary.Status),
                StartedAt: primary.QueuedAt is null ? primary.StartedAt : primary.TransferStartedAt,
                CandidateCount: completed ? primary.CandidateCount : 0,
                PendingCount: completed ? primary.PendingCount : 0,
                DocumentSent: FileAnalysisTerminal.DocumentSent(primary.Status, primary.QueuedAt, primary.TransferStartedAt),
                FailureCode: primary.FailureCode,
                LatestFailure: latestFailure));
        }

        return summaries;
    }

    /// <summary>
    /// The external-AI transfer audit trail — every statement sent for analysis, newest first.
    /// User display name/email is not resolved here (the Finance domain has no identity context);
    /// callers enrich <see cref="FileAnalysisAuditEntry.User"/> from <see cref="FileAnalysisAuditEntry.RequestedByUserId"/>.
    /// </summary>
    public async Task<IReadOnlyList<FileAnalysisAuditEntry>> GetAuditLogAsync(CancellationToken cancellationToken = default)
    {
        // No enabled-gate: the audit trail is a historical accountability record that must stay
        // readable even after the analysis feature is turned off (records persist; the toggle only
        // controls whether new transfers can be initiated).

        var jobs = await context.FileAnalysisJobs
            .AsNoTracking()
            .Include(j => j.AccountFile)
                .ThenInclude(af => af!.FileMetadata)
            .Include(j => j.AccountFile)
                .ThenInclude(af => af!.Account)
            .Include(j => j.CandidateTransactions)
            // COALESCE(QueuedAt, StartedAt): a queued or never-started job sorts by its request time.
            .OrderByDescending(j => j.QueuedAt ?? j.StartedAt ?? DateTime.MinValue)
            .ToListAsync(cancellationToken);

        return jobs.Select(MapAuditEntry).ToList();
    }

    public async Task<ImportResponse> ImportCandidatesAsync(
        Guid jobId,
        ImportRequest request,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(cancellationToken);

        var job = await context.FileAnalysisJobs
            .Include(j => j.AccountFile)
                .ThenInclude(af => af!.Account)
            .Include(j => j.CandidateTransactions)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new DomainNotFoundException($"Analysis job {jobId} not found.");

        if (job.Status != ContextJobStatus.Completed)
            throw new DomainValidationException($"Job {jobId} is not in Completed state (current: {job.Status}).");

        var account = job.AccountFile?.Account
            ?? throw new InvalidOperationException("Account could not be resolved from the analysis job.");

        // The same rule TransactionService applies (issue #237). Refused for the whole batch rather than
        // per candidate: it is a property of the job's account, so every row would fail identically.
        TransactionWriteRules.EnsureAccountIsOpen(account);

        var candidateMap = job.CandidateTransactions.ToDictionary(c => c.Id);

        var imported = 0;
        var failures = new List<ImportFailure>();

        // Loaded once for the batch rather than probed per candidate; the table is small reference data.
        var activeCurrencies = (await context.Currencies
                .AsNoTracking()
                .Where(c => c.Archived == null)
                .Select(c => c.CurrencyCode)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // One lookup for every contact the batch names; an unknown id would otherwise reach the FK at
        // SaveChangesAsync and fail the whole batch as an unmapped 500.
        var requestedContactIds = request.Candidates
            .Where(r => r.ContactId is not null)
            .Select(r => r.ContactId!.Value)
            .Distinct()
            .ToList();
        var contactRefs = requestedContactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(requestedContactIds, cancellationToken);

        foreach (var req in request.Candidates)
        {
            if (!candidateMap.TryGetValue(req.CandidateId, out var candidate))
            {
                failures.Add(new ImportFailure(req.CandidateId, "Candidate not found in this job."));
                continue;
            }

            // A candidate becomes a ledger row once. Without this a double submit or a retry of the same
            // request (or the same id twice in one body) imported it again. The status is also the
            // entity's concurrency token, so two requests racing past this check cannot both commit.
            if (candidate.ReviewStatus != ContextReviewStatus.Pending)
            {
                failures.Add(new ImportFailure(
                    req.CandidateId, $"Candidate has already been reviewed ({candidate.ReviewStatus})."));
                continue;
            }

            // Apply optional overrides
            var txDate = req.TransactionDate ?? candidate.TransactionDate;
            var description = req.Description ?? candidate.Description;
            var amount = req.Amount ?? candidate.Amount;
            var currency = req.Currency ?? candidate.Currency;

            // Validate required fields
            if (string.IsNullOrWhiteSpace(description))
            {
                failures.Add(new ImportFailure(req.CandidateId, "Description is required."));
                continue;
            }

            var normalizedCurrency = NormalizeCurrency(currency, account.CurrencyCode);
            if (!CurrencyValidationService.IsIsoFormat(normalizedCurrency))
            {
                failures.Add(new ImportFailure(req.CandidateId, $"Invalid currency code '{currency}'."));
                continue;
            }

            // Transactions.CurrencyCode is a foreign key to Currencies (issue #241): an extracted code
            // the store does not hold would otherwise fail the whole batch's save, not just this row.
            // Active-only, the same rule EnsureSupportedAndActive applies on every other write path.
            if (!activeCurrencies.Contains(normalizedCurrency))
            {
                failures.Add(new ImportFailure(req.CandidateId, $"Currency '{normalizedCurrency}' is not supported."));
                continue;
            }

            try
            {
                TransactionWriteRules.EnsureCurrencyMatchesAccount(account, normalizedCurrency);
                TransactionWriteRules.EnsureContactIsValid(contactRefs, req.ContactId);
                var tags = await TransactionWriteRules.ResolveTagsAsync(context, req.TransactionTagIds, cancellationToken);

                var transaction = new Transaction
                {
                    AccountId = account.AccountId,
                    Description = Truncate(description, 256) ?? string.Empty,
                    Amount = amount,
                    TimeStamp = DateTimeNormalization.NormalizeToUtc(txDate),
                    CurrencyCode = normalizedCurrency,
                    // Optional review overrides: contact, tags, and reference (external id).
                    ContactId = req.ContactId,
                    TransactionTags = tags,
                    ExternalId = Truncate(string.IsNullOrWhiteSpace(req.ExternalId) ? candidate.ExternalId : req.ExternalId, 64),
                    InternalId = Truncate(candidate.InternalId, 64),
                    Status = TransactionStatus.New,
                    StatusChangedAt = timeProvider.GetUtcNow().UtcDateTime,
                };

                context.Transactions.Add(transaction);

                candidate.ReviewStatus = ContextReviewStatus.Accepted;
                candidate.ReviewedAt = timeProvider.GetUtcNow().UtcDateTime;
                candidate.ReviewedByUserId = userId;

                imported++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to import candidate {CandidateId}.", req.CandidateId);
                // A domain rule's message is written for the caller; anything else is internal detail.
                failures.Add(new ImportFailure(
                    req.CandidateId, ex is DomainException ? ex.Message : "The candidate could not be imported."));
            }
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request reviewed one of these candidates between our read and this save. The
            // save is one statement batch in one transaction, so nothing of this request committed.
            throw new DomainConflictException(
                "One or more candidates were reviewed by another request. Reload the analysis and try again.");
        }

        return new ImportResponse(imported, failures.Count, failures);
    }

    /// <summary>
    /// The text of the match prompt a names-only run sends, unchanged from before issue #433; stamped as
    /// <see cref="FileAnalysisJob.MatchPromptVersion"/> on such a run.
    /// </summary>
    private const string LegacyMatchPromptVersion = "1.0";

    /// <summary>
    /// The default payload budget, in characters of serialised vocabulary JSON (issue #433 §10): about
    /// 50 000 tokens for Latin text (an estimate, not measured). Over it, aliases are dropped for the run,
    /// then descriptions; names are always sent. Not a count cap and never a reason to skip matching.
    /// </summary>
    public const int DefaultMatchVocabularyBudget = 200_000;

    /// <summary>The payload budget in force. Settable so a test can reach the reduction stages cheaply.</summary>
    public int MatchVocabularyBudget { get; init; } = DefaultMatchVocabularyBudget;

    private static readonly System.Text.Json.JsonSerializerOptions BudgetJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The AI <em>match</em> step (issue #266): resolve each extracted candidate's free-text
    /// merchant/category to existing contacts/tags via a second LLM call, then persist the resolved
    /// ids/confidences. Since issue #433 the vocabulary carries contact aliases and tag descriptions
    /// when the job's consent covers them, every active contact and tag is sent (no count cap), and the
    /// administrator's match guidance is added. Runs only on an extraction-completed job; a provider
    /// failure is recorded on <see cref="FileAnalysisJob.MatchStatus"/> and never blocks importing.
    /// </summary>
    public async Task<ExistingFileAnalysisJob> MatchAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(cancellationToken);

        // One snapshot for the whole match run, used for the destination check AND for everything sent,
        // so the check and the transfer cannot describe different moments (issue #433 §3 step 2).
        var matchSettings = await settingsLookup.GetAsync(cancellationToken);

        // A failed read cannot verify the job's consent: refuse (503) rather than guess.
        if (matchSettings.ReadFailed)
            throw new FileAnalysisUnavailableException();

        // Same refusal as the analyze path: an unusable model or destination stops the run rather than
        // quietly falling back to the shipped defaults. The match call is a transfer too.
        var matchTarget = RequireTarget(matchSettings);

        var job = await BeginMatchAsync(jobId, matchSettings, matchTarget, cancellationToken);

        var providerCalled = false;
        try
        {
            job.AutoLinkThresholdInForce = matchSettings.AutoLinkThreshold;
            job.MaxTokensInForce = matchSettings.MaxTokens;
            job.MatchTimeoutSecondsInForce = matchSettings.MatchTimeoutSeconds;

            var scope = job.MatchVocabularyScope ?? FileAnalysisMatchVocabularyScope.NamesOnly;
            var extended = scope == FileAnalysisMatchVocabularyScope.NamesAliasesDescriptions;

            var vocabulary = await BuildVocabularyAsync(extended, cancellationToken);

            // Stamps BEFORE the provider call, in a second save after the claim (issue #433 §3 step 6):
            // a failed or timed-out match still records what was sent.
            job.VocabularyCount = vocabulary.TotalCount;
            job.ContactCount = vocabulary.Contacts.Count;
            job.TagCount = vocabulary.Tags.Count;
            job.AliasCount = vocabulary.Contacts.Sum(c => c.Aliases?.Count ?? 0);
            job.TagDescriptionCount = vocabulary.Tags.Count(t => t.Description is not null);
            job.VocabularyReduction = vocabulary.Reduction;
            job.MatchPromptAdditionInForce = extended ? Truncate(matchSettings.MatchPromptAddition, 4000) : null;
            job.MatchPromptVersion = extended ? Truncate(promptTemplate.Version, 32) : LegacyMatchPromptVersion;
            await context.SaveChangesAsync(cancellationToken);

            var candidates = job.CandidateTransactions.ToList();
            var inputs = candidates
                .Select((c, index) => new MatchCandidateInput(index, c.Merchant, c.CategoryHint))
                .ToList();

            var prompt = extended
                ? new MatchPromptOptions(true, matchSettings.MatchPromptAddition)
                : MatchPromptOptions.Legacy;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(matchSettings.MatchTimeoutSeconds));

            providerCalled = true;
            var results = await provider.MatchTransactionsAsync(
                inputs, vocabulary.Contacts, vocabulary.Tags, matchTarget, matchSettings.MaxTokens, prompt,
                timeoutCts.Token);

            ApplyMatches(candidates, results, vocabulary, matchSettings.AutoLinkThreshold);

            job.MatchStatus = ContextMatchStatus.Completed;
            job.MatchGeneration++;
            await SaveMatchesAsync(cancellationToken);

            logger.LogInformation("Match for job {JobId} completed over {Vocab} names.", jobId, job.VocabularyCount);
        }
        catch (Exception ex)
        {
            // Every failure after the claim ends the run as Failed — including the caller disconnecting,
            // which used to escape this handler and leave the job Running for good (issue #433). Saved
            // with CancellationToken.None for exactly that case. The reason is CURATED, never the raw
            // provider body: it is surfaced in the review dialog.
            var callerCancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            logger.LogError(ex, "Match for job {JobId} failed.", jobId);

            job.MatchStatus = ContextMatchStatus.Failed;
            job.MatchGeneration++;
            job.MatchFailureMessage = !providerCalled
                ? "Matching could not start."
                : ex is FileAnalysisCredentialException
                    ? "The matching provider credential is missing or unreadable on this server."
                    : "The matching provider returned an error.";
            await SaveFailedMatchAsync(job);

            if (callerCancelled)
                throw;
        }

        return await RequireJobAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Records a failed match even when the request was cancelled. Pending candidate changes from a
    /// half-applied run are discarded first, so only the job's status is written.
    /// </summary>
    private async Task SaveFailedMatchAsync(FileAnalysisJob job)
    {
        foreach (var entry in context.ChangeTracker.Entries()
                     .Where(e => e.Entity is not FileAnalysisJob && e.State != EntityState.Unchanged)
                     .ToList())
        {
            entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;
        }

        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException saveEx)
        {
            // Nothing more can be done for this run; the original failure is what the caller sees.
            logger.LogError(saveEx, "Recording the failed match for job {JobId} failed.", job.Id);
        }
    }

    /// <summary>
    /// Loads the job, checks that the destination and disclosure still match its consent, and claims it
    /// for matching. The precondition failures here are HTTP errors (<c>409</c>), and nothing has been
    /// sent when one is raised; a provider failure later is not — that sets <c>MatchStatus</c> and still
    /// returns the job so Review opens with the raw candidates.
    ///
    /// <para>
    /// The claim is the first write, and it is guarded twice: the running check, and the
    /// <see cref="FileAnalysisJob.MatchGeneration"/> concurrency token. The check alone is
    /// check-then-save, so two simultaneous requests could both pass it; the token makes the second
    /// save fail, and that failure is the same <c>409</c> "already running" (issue #433 §3 step 6).
    /// </para>
    /// </summary>
    private async Task<FileAnalysisJob> BeginMatchAsync(
        Guid jobId, FileAnalysisSettings settings, FileAnalysisTarget target, CancellationToken cancellationToken)
    {
        var job = await context.FileAnalysisJobs
            .Include(j => j.CandidateTransactions)
                .ThenInclude(c => c.MatchedTags)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new DomainNotFoundException($"Analysis job {jobId} not found.");

        if (job.Status != ContextJobStatus.Completed)
            throw new DomainConflictException($"Job {jobId} extraction is not Completed (current: {job.Status}).");
        if (job.MatchStatus == ContextMatchStatus.Running)
            throw new DomainConflictException($"A match for job {jobId} is already running.");

        EnsureDestinationUnchanged(job, settings, target);

        job.MatchStatus = ContextMatchStatus.Running;
        job.MatchFailureMessage = null;
        job.MatchGeneration++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainConflictException($"A match for job {jobId} is already running.");
        }

        return job;
    }

    /// <summary>
    /// Refuses a match whose destination or match disclosure differs from the one the job was consented
    /// under (issue #433 §3 step 2). Legacy stamps that are <c>NULL</c> skip their part of the check,
    /// which is the pre-feature behaviour. The extraction-guidance flag is not part of the match version:
    /// extraction already ran, so toggling that guidance must not refuse open analyses.
    /// </summary>
    private static void EnsureDestinationUnchanged(
        FileAnalysisJob job, FileAnalysisSettings settings, FileAnalysisTarget target)
    {
        var changed =
            (job.AnalyzerBaseUrlHost is { } host
                && !string.Equals(host, Truncate(HostOf(target.BaseUrl), 256), StringComparison.OrdinalIgnoreCase))
            || (job.ProcessorInForce is { } processor
                && !string.Equals(processor, Truncate(settings.Processor, 128), StringComparison.Ordinal))
            || (job.ProcessorRegionInForce is { } region
                && !string.Equals(region, Truncate(settings.ProcessorRegion, 128), StringComparison.Ordinal))
            || (job.MatchDisclosureVersionInForce is { } version
                && !string.Equals(
                    version,
                    FileAnalysisDisclosureVersion.ComputeForMatch(
                        settings, job.MatchVocabularyScope ?? FileAnalysisMatchVocabularyScope.NamesOnly),
                    StringComparison.Ordinal));

        if (changed)
            throw new FileAnalysisDestinationChangedException();
    }

    /// <summary>
    /// What the provider is allowed to see, and how to read its answer back. Names are token-mapped both
    /// ways: the entries carry opaque refs plus names, and the maps turn a returned ref back into an id —
    /// which is also the set-membership check that drops any ref the model invented. An alias is never a
    /// ref: a match on one is answered with its contact's ref.
    /// </summary>
    private sealed record MatchVocabulary(
        IReadOnlyList<VocabularyEntry> Contacts,
        IReadOnlyDictionary<string, Guid> ContactIdByRef,
        IReadOnlyList<VocabularyEntry> Tags,
        IReadOnlyDictionary<string, Guid> TagIdByRef,
        FileAnalysisVocabularyReduction Reduction)
    {
        public int TotalCount => Contacts.Count + Tags.Count;
    }

    /// <summary>
    /// Builds the vocabulary: every non-archived contact and tag, each assigned an opaque short ref
    /// token. With <paramref name="extended"/> (the job's consent covers it, issue #433) a contact carries
    /// its alias values and a tag its description; never alias labels, notes or organisation numbers.
    ///
    /// <para>
    /// There is no count cap. The only bound is the payload budget, applied in stages: over it, aliases
    /// are dropped; still over, descriptions are dropped. Names are always sent, and matching always runs.
    /// </para>
    /// </summary>
    private async Task<MatchVocabulary> BuildVocabularyAsync(bool extended, CancellationToken cancellationToken)
    {
        IReadOnlyList<ContactMatchRef> contacts = extended
            ? await contactLookup.ListActiveContactMatchRefsAsync(cancellationToken)
            : (await contactLookup.ListActiveContactRefsAsync(cancellationToken))
                .Select(c => new ContactMatchRef(c.ContactId, c.Name, []))
                .ToList();

        var tags = await context.TransactionTags
            .Where(t => t.Archived == null)
            .Select(t => new { t.TransactionTagId, t.Name, t.Description })
            .ToListAsync(cancellationToken);

        var contactIdByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var fullContacts = new List<VocabularyEntry>(contacts.Count);
        for (var i = 0; i < contacts.Count; i++)
        {
            var token = $"c{i}";
            fullContacts.Add(new VocabularyEntry(
                token, contacts[i].Name, extended && contacts[i].Aliases.Count > 0 ? contacts[i].Aliases : null));
            contactIdByRef[token] = contacts[i].ContactId;
        }

        var tagIdByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var fullTags = new List<VocabularyEntry>(tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            var token = $"t{i}";
            fullTags.Add(new VocabularyEntry(
                token, tags[i].Name,
                Description: extended && !string.IsNullOrWhiteSpace(tags[i].Description) ? tags[i].Description : null));
            tagIdByRef[token] = tags[i].TransactionTagId;
        }

        var reduction = FileAnalysisVocabularyReduction.None;
        IReadOnlyList<VocabularyEntry> sentContacts = fullContacts;
        IReadOnlyList<VocabularyEntry> sentTags = fullTags;

        if (extended && Measure(sentContacts, sentTags) > MatchVocabularyBudget)
        {
            sentContacts = fullContacts.Select(c => c with { Aliases = null }).ToList();
            reduction = FileAnalysisVocabularyReduction.AliasesOmitted;

            if (Measure(sentContacts, sentTags) > MatchVocabularyBudget)
            {
                sentTags = fullTags.Select(t => t with { Description = null }).ToList();
                reduction = FileAnalysisVocabularyReduction.AliasesAndDescriptionsOmitted;
            }
        }

        return new MatchVocabulary(sentContacts, contactIdByRef, sentTags, tagIdByRef, reduction);
    }

    /// <summary>The serialised size of a vocabulary, in characters, for the payload budget.</summary>
    private static int Measure(IReadOnlyList<VocabularyEntry> contacts, IReadOnlyList<VocabularyEntry> tags) =>
        System.Text.Json.JsonSerializer.Serialize(new { contacts, tags }, BudgetJsonOptions).Length;

    /// <summary>
    /// Writes the provider's answer onto the candidate rows. The server owns the auto-link policy: a field
    /// whose confidence reaches the threshold is auto-applied (MatchMethod = Llm); a sub-threshold match is
    /// still persisted (id + confidence) as a suggestion but leaves the row MatchMethod = None — so the
    /// stored data, not a client constant, distinguishes auto-linked from suggested for every consumer.
    /// </summary>
    private void ApplyMatches(
        List<FileAnalysisCandidateTransaction> candidates,
        IReadOnlyList<MatchedCandidate> results,
        MatchVocabulary vocabulary,
        decimal threshold)
    {
        // Last write per index wins, in case the model emits a duplicate index.
        var byIndex = results
            .GroupBy(r => r.Index)
            .ToDictionary(g => g.Key, g => g.Last());

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            // Idempotency: a re-run never clobbers a reviewer-curated row — only None/Llm rows are
            // refreshed with fresh suggestions.
            if (candidate.MatchMethod == ContextMatchMethod.Manual)
                continue;

            ClearPriorSuggestions(candidate);

            if (!byIndex.TryGetValue(i, out var match))
                continue;

            // Whether any field reached the auto-link threshold — drives the row's MatchMethod.
            var autoApplied = false;

            // Set-membership validation: a ref the model invents (not in the sent list) is dropped.
            if (match.ContactRef is { } contactRef && vocabulary.ContactIdByRef.TryGetValue(contactRef, out var contactId))
            {
                var confidence = ClampConfidence(match.ContactConfidence);
                candidate.MatchedContactId = contactId;
                candidate.MerchantMatchConfidence = confidence;
                if (confidence >= threshold)
                    autoApplied = true;
            }

            var tagIds = match.TagRefs
                .Select(r => vocabulary.TagIdByRef.TryGetValue(r, out var id) ? (Guid?)id : null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();
            if (tagIds.Count > 0)
            {
                foreach (var tagId in tagIds)
                {
                    candidate.MatchedTags.Add(new FileAnalysisCandidateTag
                    {
                        CandidateTransactionId = candidate.Id,
                        TransactionTagId = tagId,
                    });
                }

                var confidence = ClampConfidence(match.CategoryConfidence);
                candidate.CategoryMatchConfidence = confidence;
                if (confidence >= threshold)
                    autoApplied = true;
            }

            // Llm = at least one field was auto-applied (≥ threshold); a row carrying only
            // sub-threshold suggestions stays None (the ids are suggestions, not applied values).
            candidate.MatchMethod = autoApplied ? ContextMatchMethod.Llm : ContextMatchMethod.None;
        }
    }

    // Replace prior suggestions transactionally (delete-then-insert in the caller's one SaveChanges).
    private void ClearPriorSuggestions(FileAnalysisCandidateTransaction candidate)
    {
        if (candidate.MatchedTags.Count > 0)
            context.FileAnalysisCandidateTags.RemoveRange(candidate.MatchedTags);
        candidate.MatchedTags.Clear();
        candidate.MatchedContactId = null;
        candidate.MerchantMatchConfidence = null;
        candidate.CategoryMatchConfidence = null;
        candidate.MatchMethod = ContextMatchMethod.None;
    }

    private async Task<ExistingFileAnalysisJob> RequireJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        await GetJobAsync(jobId, cancellationToken)
            ?? throw new DomainNotFoundException($"Analysis job {jobId} not found.");

    private static decimal? ClampConfidence(decimal? value) =>
        value.HasValue ? Math.Clamp(value.Value, 0m, 1m) : null;

    /// <summary>
    /// The kill switch, checked first on every file-analysis entry point (issue #439 §5.1).
    ///
    /// <para>
    /// A <strong>live, uncached</strong> read, not a member of the per-run snapshot: "I turned it off"
    /// has to mean the next request is refused, not that the next request within the snapshot's 30s TTL
    /// may still transfer a document to a third party — and that TTL's eviction is instance-local, so
    /// on a multi-instance deployment a cached read would not even bound the window to the TTL
    /// everywhere. The cost is one single-row primary-key read on paths that each already do at least
    /// one round trip and, on analyze, a multi-second provider call.
    /// </para>
    /// </summary>
    private async Task EnsureEnabledAsync(CancellationToken cancellationToken)
    {
        if (!await settingsLookup.IsEnabledAsync(cancellationToken))
            throw new FileAnalysisDisabledException();
    }

    /// <summary>
    /// Resolves the destination and model for one run, or refuses (issue #439 §11).
    ///
    /// <para>
    /// <strong>Structural, not conditional.</strong> This does not test <c>IsDegraded</c> — a single
    /// boolean cannot say which field degraded, so a rule phrased against it would either block all
    /// analysis on an unrelated bad row (a blank processor, say) or need an invented value-comparison
    /// heuristic that stops firing the moment an administrator deliberately sets a value back to its
    /// default. <c>Model</c> and <c>BaseUrl</c> are simply null when unusable, so
    /// <see cref="FileAnalysisTarget"/> cannot be constructed and the analysis cannot proceed.
    /// </para>
    ///
    /// <para>
    /// The scope of the refusal is exactly these two fields. A degradation in any of the other seven
    /// leaves analysis working, with its existing consequence unchanged: <c>IsDegraded</c> still makes
    /// the claim-free disclosure endpoint answer <c>503</c> rather than present a fallback as
    /// authoritative legal text.
    /// </para>
    /// </summary>
    private static FileAnalysisTarget RequireTarget(FileAnalysisSettings settings)
    {
        if (settings.Model is not { } model || settings.BaseUrl is not { } baseUrl)
            throw new FileAnalysisUnavailableException();

        return new FileAnalysisTarget(baseUrl, model);
    }

    /// <summary>
    /// The host of a resolved base URL, for <see cref="FileAnalysisJob.AnalyzerBaseUrlHost"/>. Host
    /// only — the stored value is already validated to carry no path, query or <c>userinfo</c>, and
    /// this keeps that true of the stamp even if that ever changes.
    /// </summary>
    internal static string? HostOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;

    /// <summary>
    /// The destination's scheme, host and port (issue #444 §3 step 4.4) — what the worker compares before
    /// sending, so a port or scheme change is a refusal too. Never the path, query or <c>userinfo</c>.
    /// </summary>
    internal static string? OriginOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}"
            : null;

    /// <summary>
    /// Saves a match run, yielding to an import that reviewed some of its candidates while the provider
    /// call was in flight: their review wins, they are restored as stored, and the rest is saved.
    ///
    /// <para>
    /// Two layers, because <c>ReviewStatus</c> is a concurrency token (issue #237). The re-read just
    /// before saving catches the ordinary case — a provider call takes seconds — on every provider.
    /// The catch covers the window between that read and the save; it relies on the failed save having
    /// rolled back, which holds on MariaDB (<c>FileAnalysisImportConcurrencyTests</c>) but not on the
    /// InMemory provider, which has no transactions and applies a failed batch partially.
    /// </para>
    /// </summary>
    private async Task SaveMatchesAsync(CancellationToken cancellationToken)
    {
        var candidates = context.ChangeTracker.Entries<FileAnalysisCandidateTransaction>().ToList();
        var ids = candidates.Select(e => e.Entity.Id).ToList();
        var storedStatuses = await context.FileAnalysisCandidateTransactions
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.ReviewStatus })
            .ToDictionaryAsync(c => c.Id, c => c.ReviewStatus, cancellationToken);

        foreach (var entry in candidates.Where(e =>
                     storedStatuses.TryGetValue(e.Entity.Id, out var stored)
                     && stored != e.Property(c => c.ReviewStatus).OriginalValue))
        {
            await RestoreAsStoredAsync(entry, cancellationToken);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                if (entry.Entity is FileAnalysisCandidateTransaction candidate)
                    await RestoreAsStoredAsync(context.Entry(candidate), cancellationToken);
                else
                    await entry.ReloadAsync(cancellationToken);
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    // A reload restores the candidate's own columns only; its tag links are separate rows, so the
    // links this run added are dropped and the ones it removed are kept, leaving them as stored.
    private async Task RestoreAsStoredAsync(
        EntityEntry<FileAnalysisCandidateTransaction> entry,
        CancellationToken cancellationToken)
    {
        var candidate = entry.Entity;
        var links = context.ChangeTracker.Entries<FileAnalysisCandidateTag>()
            .Where(e => e.Entity.CandidateTransactionId == candidate.Id)
            .ToList();
        var stored = links.Where(e => e.State != EntityState.Added).Select(e => e.Entity).ToList();

        foreach (var link in links)
            link.State = link.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;

        candidate.MatchedTags.Clear();
        foreach (var tag in stored)
            candidate.MatchedTags.Add(tag);

        await entry.ReloadAsync(cancellationToken);
    }

    private static string NormalizeCurrency(string? currency, string fallback)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return fallback.ToUpperInvariant();
        return currency.Trim().ToUpperInvariant();
    }

    internal static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length > maxLength ? value[..maxLength] : value;

    private static ExistingFileAnalysisJob MapJob(
        FileAnalysisJob job, IReadOnlyDictionary<Guid, string> contactNames,
        decimal liveAutoLinkThreshold) => new(
        Id: job.Id,
        AccountFileId: job.AccountFileId,
        Status: MapJobStatus(job.Status),
        FileTypeDetected: job.FileTypeDetected,
        StartedAt: job.StartedAt,
        CompletedAt: job.CompletedAt,
        FailureCode: job.FailureCode,
        FailureMessage: job.FailureMessage,
        AnalyzerProvider: job.AnalyzerProvider.ToString(),
        AnalyzerModel: job.AnalyzerModel,
        PromptVersion: job.PromptVersion,
        Candidates: job.CandidateTransactions.Select(c => MapCandidate(c, contactNames)).ToList(),
        MatchStatus: MapMatchStatus(job.MatchStatus),
        MatchFailureMessage: job.MatchFailureMessage,
        // The job's own stamped threshold, so a later edit cannot re-interpret a completed job's
        // stored confidences. Null only for jobs predating the column — those fall back to live.
        AutoLinkThreshold: (double)(job.AutoLinkThresholdInForce ?? liveAutoLinkThreshold),
        // Issue #433. A job without a stamp predates the feature and is names-only for its whole life.
        MatchVocabularyScope: job.MatchVocabularyScope ?? FileAnalysisMatchVocabularyScope.NamesOnly,
        ContactCount: job.ContactCount,
        TagCount: job.TagCount,
        AliasCount: job.AliasCount,
        TagDescriptionCount: job.TagDescriptionCount,
        VocabularyReduction: job.VocabularyReduction,
        QueuedAt: job.QueuedAt,
        TransferStartedAt: job.TransferStartedAt,
        DocumentSent: FileAnalysisTerminal.DocumentSent(job),
        ProcessorInForce: job.ProcessorInForce
    );

    private static FileAnalysisAuditEntry MapAuditEntry(FileAnalysisJob job)
    {
        var file = job.AccountFile?.FileMetadata;
        var account = job.AccountFile?.Account;
        var duration = job.StartedAt is { } started && job.CompletedAt is { } completed
            ? (long?)Math.Max(0, (long)(completed - started).TotalMilliseconds)
            : null;

        return new FileAnalysisAuditEntry(
            Id: job.Id,
            // A job that never transferred is dated by its request (issue #444 §5.4).
            At: job.TransferStartedAt ?? job.QueuedAt ?? job.StartedAt,
            RequestedByUserId: job.RequestedByUserId,
            User: null, // enriched by the API layer from RequestedByUserId
            File: file is null ? null : new FileAnalysisAuditFile(file.Id, file.FileName, job.AccountFile!.FileType.ToString()),
            Account: account is null ? null : new FileAnalysisAuditAccount(account.Name, account.AccountNumber),
            Provider: job.AnalyzerProvider.ToString(),
            Model: job.AnalyzerModel,
            PromptVersion: job.PromptVersion,
            Pages: null, // page count is not captured at upload time
            SizeBytes: file?.SizeBytes,
            Status: MapJobStatus(job.Status),
            Candidates: job.CandidateTransactions.Count,
            Imported: job.CandidateTransactions.Count(c => c.ReviewStatus == ContextReviewStatus.Accepted),
            LawfulBasis: job.LawfulBasis,
            RequestId: job.Id.ToString(),
            DurationMs: duration,
            Failure: job.FailureMessage,
            ConsentRecorded: job.ConsentRecorded,
            ConsentMethod: job.ConsentMethod,
            ConsentText: job.ConsentText,
            MatchStatus: MapMatchStatus(job.MatchStatus),
            VocabularyCount: job.VocabularyCount,
            AnalyzerBaseUrlHost: job.AnalyzerBaseUrlHost,
            ProcessorInForce: job.ProcessorInForce,
            ProcessorRegionInForce: job.ProcessorRegionInForce,
            // Issue #433: digests only, never the text. The scope is NOT defaulted here: null means the job
            // predates the feature, which is how "none sent" is told apart from "not recorded".
            ExtractionPromptAdditionDigest: PromptAdditionDigest.Of(job.ExtractionPromptAdditionInForce),
            MatchPromptAdditionDigest: PromptAdditionDigest.Of(job.MatchPromptAdditionInForce),
            MatchVocabularyScope: job.MatchVocabularyScope,
            // Issue #434: what the latest match run sent, for the log's "Matched against" fact.
            ContactCount: job.ContactCount,
            AliasCount: job.AliasCount,
            TagCount: job.TagCount,
            TagDescriptionCount: job.TagDescriptionCount,
            VocabularyReduction: job.VocabularyReduction,
            QueuedAt: job.QueuedAt,
            TransferStartedAt: job.TransferStartedAt,
            DocumentSent: FileAnalysisTerminal.DocumentSent(job));
    }

    private static ExistingFileAnalysisCandidateTransaction MapCandidate(
        FileAnalysisCandidateTransaction c, IReadOnlyDictionary<Guid, string> contactNames) => new(
        Id: c.Id,
        TransactionDate: c.TransactionDate,
        BookingDate: c.BookingDate,
        Description: c.Description,
        Merchant: c.Merchant,
        CategoryHint: c.CategoryHint,
        Amount: c.Amount,
        Currency: c.Currency,
        ExternalId: c.ExternalId,
        ReferenceNumber: c.ReferenceNumber,
        LlmConfidence: c.LlmConfidence,
        LlmModel: c.LlmModel,
        ReviewStatus: MapReviewStatus(c.ReviewStatus),
        ReviewedAt: c.ReviewedAt,
        MatchedContactId: c.MatchedContactId,
        MatchedContactName: c.MatchedContactId is { } cpId && contactNames.TryGetValue(cpId, out var name) ? name : null,
        MatchedTagIds: c.MatchedTags.Select(t => t.TransactionTagId).ToList(),
        MerchantMatchConfidence: c.MerchantMatchConfidence,
        CategoryMatchConfidence: c.CategoryMatchConfidence,
        MatchMethod: MapMatchMethod(c.MatchMethod)
    );

    private static DtoMatchStatus MapMatchStatus(ContextMatchStatus s) => s switch
    {
        ContextMatchStatus.Running => DtoMatchStatus.Running,
        ContextMatchStatus.Completed => DtoMatchStatus.Completed,
        ContextMatchStatus.Skipped => DtoMatchStatus.Skipped,
        ContextMatchStatus.Failed => DtoMatchStatus.Failed,
        _ => DtoMatchStatus.NotRun,
    };

    private static DtoMatchMethod MapMatchMethod(ContextMatchMethod m) => m switch
    {
        ContextMatchMethod.Llm => DtoMatchMethod.Llm,
        ContextMatchMethod.Manual => DtoMatchMethod.Manual,
        _ => DtoMatchMethod.None,
    };

    private static DtoJobStatus MapJobStatus(ContextJobStatus s) => s switch
    {
        ContextJobStatus.New => DtoJobStatus.New,
        ContextJobStatus.Queued => DtoJobStatus.Queued,
        ContextJobStatus.Running => DtoJobStatus.Running,
        ContextJobStatus.Completed => DtoJobStatus.Completed,
        ContextJobStatus.Failed => DtoJobStatus.Failed,
        ContextJobStatus.Cancelled => DtoJobStatus.Cancelled,
        _ => DtoJobStatus.New,
    };

    private static DtoReviewStatus MapReviewStatus(ContextReviewStatus s) => s switch
    {
        ContextReviewStatus.Accepted => DtoReviewStatus.Accepted,
        ContextReviewStatus.Rejected => DtoReviewStatus.Rejected,
        _ => DtoReviewStatus.Pending,
    };
}
