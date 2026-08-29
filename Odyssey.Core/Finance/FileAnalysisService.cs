using Odyssey.Core;
using Odyssey.Dtos;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
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

    public FileAnalysisService(
        OdysseyContext context,
        IFileAnalysisProvider provider,
        IContactLookup contactLookup,
        IOptions<FileAnalysisOptions> options,
        IFileAnalysisSettingsLookup settingsLookup,
        ILogger<FileAnalysisService> logger,
        TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.provider = provider;
        this.contactLookup = contactLookup;
        this.options = options.Value;
        this.settingsLookup = settingsLookup;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AnalyzeFileResponse> AnalyzeAsync(
        Guid accountId,
        Guid fileId,
        string requestedByUserId,
        AnalyzeFileRequest? consent = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(cancellationToken);

        // Find the AccountFile joining this account to this file
        var accountFile = await context.AccountFiles
            .Include(af => af.FileMetadata)
                .ThenInclude(fm => fm!.FileBlob)
            .Include(af => af.Account)
            .FirstOrDefaultAsync(
                af => af.AccountId == accountId && af.FileMetadataId == fileId,
                cancellationToken)
            ?? throw new DomainNotFoundException($"File {fileId} is not attached to account {accountId}.");

        if (accountFile.FileType != Context.AccountFileType.Statement)
            throw new DomainValidationException("Only files of type Statement can be analyzed.");

        // Consent gate — analysis sends the complete document to a third-party AI processor, so a
        // per-document consent is a hard precondition (GDPR Art. 6(1)(a)), not a UI affordance. The
        // check lives here, right before the transfer, so no caller (UI, Swagger, script) can ship a
        // statement without it. The affirmed text is captured verbatim on the job for the audit log.
        if (consent?.ConsentAcknowledged != true)
            throw new DomainValidationException("Consent is required before sending this document to the external AI provider.");

        var fileMetadata = accountFile.FileMetadata
            ?? throw new InvalidOperationException("File metadata could not be loaded.");
        var fileBlob = fileMetadata.FileBlob
            ?? throw new InvalidOperationException("File content could not be loaded.");

        var promptTemplate = await LoadPromptTemplateAsync(cancellationToken);

        // One snapshot per analysis, read once: the values must not shift between the job's stamped
        // lawful basis, the future-date filter and the applied threshold within a single run. Since
        // issue #439 the model, the destination, the processor and the region all come from this same
        // snapshot, which is what lets the four job stamps below describe one coherent moment.
        var settings = await settingsLookup.GetAsync(cancellationToken);

        // Refuse rather than substitute when the model or the destination is unusable — see
        // RequireTarget. Evaluated BEFORE the disclosure-version check, so a misconfigured instance
        // answers 503 rather than leaking disclosure state through a 409.
        var target = RequireTarget(settings);

        // The consent the user affirmed is bound to the disclosure they were shown (issue #439 §5.3c).
        // Recomputed from THIS snapshot — the same one the transfer uses — so the comparison cannot be
        // defeated by the values shifting between the check and the send. A mismatch (or a missing
        // echo) is a 409 here, before any job row exists and before any provider request is made.
        if (!FileAnalysisDisclosureVersion.Matches(settings, consent.DisclosureVersion))
            throw new FileAnalysisDisclosureChangedException();

        var job = new FileAnalysisJob
        {
            AccountFileId = accountFile.Id,
            Status = ContextJobStatus.Running,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
            AnalyzerProvider = ContextAnalyzerProvider.Claude,
            // From the resolved target, not from options: the model is admin-editable now, so the value
            // stamped here has to be the value the request below is BUILT with, or the audit trail and
            // the transfer describe different runs.
            AnalyzerModel = Truncate(target.Model, 256),
            PromptVersion = options.PromptVersion,
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
        };

        context.FileAnalysisJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            var accountCurrency = accountFile.Account?.CurrencyCode ?? "USD";

            var extracted = await provider.ExtractTransactionsAsync(
                fileBlob.Content,
                fileMetadata.ContentType,
                accountCurrency,
                promptTemplate,
                target,
                settings.MaxTokens,
                cancellationToken);

            var maxFutureDate = timeProvider.GetUtcNow().UtcDateTime.AddDays(settings.MaxFutureTransactionDays);

            var candidates = extracted
                .Where(t => t.TransactionDate <= maxFutureDate)
                .Select(t => new FileAnalysisCandidateTransaction
                {
                    AnalysisJobId = job.Id,
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
                })
                .ToList();

            context.FileAnalysisCandidateTransactions.AddRange(candidates);

            job.Status = ContextJobStatus.Completed;
            job.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            job.FileTypeDetected = fileMetadata.ContentType;

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Analysis job {JobId} completed with {Count} candidates.", job.Id, candidates.Count);
        }
        catch (Exception ex)
        {
            // The raw exception (incl. any upstream provider body) is logged for diagnostics, but the
            // persisted FailureMessage is surfaced in the admin audit log, so record a CURATED reason
            // rather than ex.Message — never leak the provider body or internal details (mirrors the
            // match path above).
            logger.LogError(ex, "Analysis job {JobId} failed.", job.Id);
            job.Status = ContextJobStatus.Failed;
            job.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            // Three curated outcomes, most specific first. The credential case is separated from the
            // generic provider error (issue #445 AC 3) because only one of them is the administrator's
            // to fix, and a job that failed because this deployment has no usable key must not read as
            // "the provider misbehaved". Still curated, never ex.Message: FailureMessage is surfaced in
            // the admin audit log.
            (job.FailureCode, job.FailureMessage) = ex switch
            {
                FileAnalysisCredentialException =>
                    ("provider_credential_error",
                        "The analysis provider credential is missing or unreadable on this server."),
                FileAnalysisProviderException =>
                    ("provider_error", "The analysis provider returned an error."),
                _ => ("internal_error", "An internal error occurred during analysis."),
            };
            await context.SaveChangesAsync(cancellationToken);
            throw;
        }

        return new AnalyzeFileResponse(job.Id);
    }

    public async Task<ExistingFileAnalysisJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(cancellationToken);

        var job = await context.FileAnalysisJobs
            .Include(j => j.CandidateTransactions)
                .ThenInclude(c => c.MatchedTags)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job is null)
            return null;

        var contactNames = await LoadMatchedContactNamesAsync(job, cancellationToken);
        // Only used as the fallback for jobs predating AutoLinkThresholdInForce; a stamped job ignores it.
        var liveSettings = await settingsLookup.GetAsync(cancellationToken);
        return MapJob(job, contactNames, liveSettings.MatchMaxVocabulary, liveSettings.AutoLinkThreshold);
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
    /// The latest <em>resumable</em> analysis job per file for an account — the single account-scoped
    /// read that lets the Files surface offer "Resume review" in one request (no N+1). A job is
    /// resumable when extraction <see cref="ContextJobStatus.Completed"/> and at least one candidate is
    /// still <see cref="ContextReviewStatus.Pending"/>; failed/running/all-reviewed jobs are excluded.
    /// When several qualify for a file, the latest by <c>StartedAt</c> (tie-broken by <c>Id</c>) wins.
    /// Files with no resumable job are uniformly absent — never an existence oracle. Counts are
    /// SQL-projected so no candidate free-text is materialised.
    /// </summary>
    public async Task<IReadOnlyList<ResumableAnalysisSummary>> GetResumableJobsAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        // 503 before any account/file lookup — uniform disabled behaviour, checked first.
        await EnsureEnabledAsync(cancellationToken);

        // 404 only when the account itself doesn't exist (an empty list otherwise).
        var accountExists = await context.Accounts
            .AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            throw new DomainNotFoundException($"Account {accountId} not found.");

        // One round-trip: project every Completed job for the account to its file id, started-at and
        // SQL-computed counts (COUNT + conditional COUNT) — never Include-then-count, which would pull
        // all candidate free-text into memory. The pending filter keeps only jobs with work left.
        var jobs = await context.FileAnalysisJobs
            .AsNoTracking()
            .Where(j => j.AccountFile!.AccountId == accountId
                && j.Status == ContextJobStatus.Completed)
            .Select(j => new
            {
                FileId = j.AccountFile!.FileMetadataId,
                JobId = j.Id,
                j.StartedAt,
                CandidateCount = j.CandidateTransactions.Count,
                PendingCount = j.CandidateTransactions.Count(c => c.ReviewStatus == ContextReviewStatus.Pending),
            })
            .Where(x => x.PendingCount > 0)
            .ToListAsync(cancellationToken);

        // Reduce to the latest resumable job per file — deterministic (StartedAt desc, then Id desc)
        // so repeated calls are stable.
        return jobs
            .GroupBy(x => x.FileId)
            .Select(g => g.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.JobId).First())
            .Select(x => new ResumableAnalysisSummary(
                FileId: x.FileId,
                AnalysisJobId: x.JobId,
                Status: DtoJobStatus.Completed,
                StartedAt: x.StartedAt,
                CandidateCount: x.CandidateCount,
                PendingCount: x.PendingCount))
            .ToList();
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
            .OrderByDescending(j => j.StartedAt ?? DateTime.MinValue)
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

        var candidateMap = job.CandidateTransactions.ToDictionary(c => c.Id);

        var imported = 0;
        var failures = new List<ImportFailure>();

        foreach (var req in request.Candidates)
        {
            if (!candidateMap.TryGetValue(req.CandidateId, out var candidate))
            {
                failures.Add(new ImportFailure(req.CandidateId, "Candidate not found in this job."));
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

            if (!CurrencyValidationService.IsIsoFormat(NormalizeCurrency(currency, account.CurrencyCode)))
            {
                failures.Add(new ImportFailure(req.CandidateId, $"Invalid currency code '{currency}'."));
                continue;
            }

            try
            {
                var tags = await ResolveImportTagsAsync(req.TransactionTagIds, cancellationToken);

                var transaction = new Transaction
                {
                    AccountId = account.AccountId,
                    Description = Truncate(description, 256) ?? string.Empty,
                    Amount = amount,
                    TimeStamp = txDate,
                    CurrencyCode = NormalizeCurrency(currency, account.CurrencyCode),
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
                failures.Add(new ImportFailure(req.CandidateId, ex.Message));
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        return new ImportResponse(imported, failures.Count, failures);
    }

    /// <summary>
    /// The AI <em>match</em> step (issue #266): resolve each extracted candidate's free-text
    /// merchant/category to existing contacts/tags via a second LLM call that is sent the user's
    /// contact + tag NAMES only (token-mapped), then persist the resolved ids/confidences. Runs
    /// only on an extraction-completed job; a provider failure/over-cap is recorded on
    /// <see cref="FileAnalysisJob.MatchStatus"/> and never blocks importing the candidates.
    /// </summary>
    public async Task<ExistingFileAnalysisJob> MatchAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(cancellationToken);

        // One snapshot for the whole match run, and it is what gets stamped on the job — so the
        // threshold the matches were applied under is the threshold the read DTO later reports.
        var matchSettings = await settingsLookup.GetAsync(cancellationToken);

        // Same refusal as the analyze path: an unusable model or destination stops the run rather than
        // quietly falling back to the shipped defaults. The match call sends contact and tag NAMES to
        // the provider, so it is a transfer too.
        var matchTarget = RequireTarget(matchSettings);

        var job = await BeginMatchAsync(jobId, cancellationToken);
        job.AutoLinkThresholdInForce = matchSettings.AutoLinkThreshold;
        job.MaxTokensInForce = matchSettings.MaxTokens;
        job.MatchTimeoutSecondsInForce = matchSettings.MatchTimeoutSeconds;

        var vocabulary = await BuildVocabularyAsync(cancellationToken);
        job.VocabularyCount = vocabulary.TotalCount;

        // Over the per-list cap ⇒ skip the LLM (manual fallback) rather than truncate-and-leak a subset.
        var cap = matchSettings.MatchMaxVocabulary;
        if (vocabulary.LargestListCount > cap)
        {
            job.MatchStatus = ContextMatchStatus.Skipped;
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Match for job {JobId} skipped — vocabulary over cap ({Count} > {Cap}).",
                jobId, vocabulary.LargestListCount, cap);
            return await RequireJobAsync(jobId, cancellationToken);
        }

        var candidates = job.CandidateTransactions.ToList();
        var inputs = candidates
            .Select((c, index) => new MatchCandidateInput(index, c.Merchant, c.CategoryHint))
            .ToList();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(matchSettings.MatchTimeoutSeconds));

            var results = await provider.MatchTransactionsAsync(
                inputs, vocabulary.Contacts, vocabulary.Tags, matchTarget, matchSettings.MaxTokens,
                timeoutCts.Token);

            ApplyMatches(candidates, results, vocabulary, matchSettings.AutoLinkThreshold);

            job.MatchStatus = ContextMatchStatus.Completed;
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Match for job {JobId} completed over {Vocab} names.", jobId, job.VocabularyCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Provider error, timeout, or malformed output — record a CURATED reason (never the raw
            // provider body) and leave the extracted candidates intact and importable. The credential
            // case is separated for the same reason the analysis path separates it (issue #445): only
            // one of the two is the administrator's to fix, and there is no MatchFailureCode column, so
            // the message is the only place the distinction can live.
            logger.LogError(ex, "Match for job {JobId} failed.", jobId);
            job.MatchStatus = ContextMatchStatus.Failed;
            job.MatchFailureMessage = ex is FileAnalysisCredentialException
                ? "The matching provider credential is missing or unreadable on this server."
                : "The matching provider returned an error.";
            await context.SaveChangesAsync(cancellationToken);
        }

        return await RequireJobAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Loads the job and claims it for matching. The precondition failures here are HTTP errors (409);
    /// a provider failure later is not — that sets <c>MatchStatus</c> and still returns the job so Review
    /// opens with the raw candidates. The running guard is persisted before the long provider call so an
    /// overlapping POST hits the 409.
    /// </summary>
    private async Task<FileAnalysisJob> BeginMatchAsync(Guid jobId, CancellationToken cancellationToken)
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

        job.MatchStatus = ContextMatchStatus.Running;
        job.MatchFailureMessage = null;
        await context.SaveChangesAsync(cancellationToken);

        return job;
    }

    /// <summary>
    /// What the provider is allowed to see, and how to read its answer back. Names are token-mapped both
    /// ways: the entries carry opaque refs plus names, and the maps turn a returned ref back into an id —
    /// which is also the set-membership check that drops any ref the model invented.
    /// </summary>
    private sealed record MatchVocabulary(
        IReadOnlyList<VocabularyEntry> Contacts,
        IReadOnlyDictionary<string, Guid> ContactIdByRef,
        IReadOnlyList<VocabularyEntry> Tags,
        IReadOnlyDictionary<string, Guid> TagIdByRef)
    {
        public int TotalCount => Contacts.Count + Tags.Count;

        public int LargestListCount => Math.Max(Contacts.Count, Tags.Count);
    }

    /// <summary>
    /// Builds the vocabulary: non-archived contact + tag NAMES only (no ids/notes/org numbers), each
    /// assigned an opaque short ref token.
    /// <para>
    /// Contact moved from OdysseyContext to OdysseyContext (issue #325 follow-up): the all-contacts
    /// vocabulary is fetched through the lookup (ContactRef.Name already encodes the display-name
    /// resolution — DisplayName else Person "First Last" / Org LegalName).
    /// </para>
    /// </summary>
    private async Task<MatchVocabulary> BuildVocabularyAsync(CancellationToken cancellationToken)
    {
        var contacts = await contactLookup.ListActiveContactRefsAsync(cancellationToken);
        var tags = await context.TransactionTags
            .Where(t => t.Archived == null)
            .Select(t => new { t.TransactionTagId, t.Name })
            .ToListAsync(cancellationToken);

        var contactEntries = new List<VocabularyEntry>(contacts.Count);
        var contactIdByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < contacts.Count; i++)
        {
            var token = $"c{i}";
            contactEntries.Add(new VocabularyEntry(token, contacts[i].Name));
            contactIdByRef[token] = contacts[i].ContactId;
        }

        var tagEntries = new List<VocabularyEntry>(tags.Count);
        var tagIdByRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < tags.Count; i++)
        {
            var token = $"t{i}";
            tagEntries.Add(new VocabularyEntry(token, tags[i].Name));
            tagIdByRef[token] = tags[i].TransactionTagId;
        }

        return new MatchVocabulary(contactEntries, contactIdByRef, tagEntries, tagIdByRef);
    }

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
    private static string? HostOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;

    // Resolve the optional tag overrides for an imported transaction, validating that each requested
    // tag exists and is not archived. Throws so the per-candidate catch records it as a failure.
    private async Task<List<TransactionTag>> ResolveImportTagsAsync(IEnumerable<Guid>? tagIds, CancellationToken cancellationToken)
    {
        var distinctIds = tagIds?.Distinct().ToList() ?? [];
        if (distinctIds.Count == 0)
        {
            return [];
        }

        var tags = await context.TransactionTags
            .Where(tag => distinctIds.Contains(tag.TransactionTagId) && tag.Archived == null)
            .ToListAsync(cancellationToken);

        var missing = distinctIds.Except(tags.Select(tag => tag.TransactionTagId)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainValidationException(
                $"Transaction tag ID(s) {string.Join(", ", missing)} are invalid or archived.");
        }

        return tags;
    }

    private async Task<string> LoadPromptTemplateAsync(CancellationToken cancellationToken = default)
    {
        var path = options.PromptTemplatePath;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);

        if (!File.Exists(path))
            throw new InvalidOperationException($"Prompt template not found at '{path}'.");

        return await File.ReadAllTextAsync(path);
    }

    private static string NormalizeCurrency(string? currency, string fallback)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return fallback.ToUpperInvariant();
        return currency.Trim().ToUpperInvariant();
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length > maxLength ? value[..maxLength] : value;

    private static ExistingFileAnalysisJob MapJob(
        FileAnalysisJob job, IReadOnlyDictionary<Guid, string> contactNames, int matchMaxVocabulary,
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
        MaxVocabulary: matchMaxVocabulary
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
            At: job.StartedAt,
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
            ProcessorRegionInForce: job.ProcessorRegionInForce);
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
