using Odyssey.Core;
using Odyssey.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextJobStatus = Odyssey.Context.FileAnalysisJobStatus;
using ContextReviewStatus = Odyssey.Context.CandidateTransactionReviewStatus;
using ContextMatchStatus = Odyssey.Context.FileAnalysisMatchStatus;
using ContextMatchMethod = Odyssey.Context.MatchMethod;
using DtoJobStatus = Odyssey.Dtos.Finance.FileAnalysisJobStatus;
using DtoMatchStatus = Odyssey.Dtos.Finance.FileAnalysisMatchStatus;
using DtoMatchMethod = Odyssey.Dtos.Finance.MatchMethod;
using Odyssey.Core.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

public class FileAnalysisServiceTests
{
    // A fake provider whose behaviour (return list, or throw) is supplied per test.
    private sealed class FakeProvider(
        Func<byte[], string, string, string, CancellationToken, Task<List<ExtractedTransaction>>> behaviour,
        Func<IReadOnlyList<MatchCandidateInput>, IReadOnlyList<VocabularyEntry>, IReadOnlyList<VocabularyEntry>, CancellationToken, Task<List<MatchedCandidate>>>? matchBehaviour = null)
        : IFileAnalysisProvider
    {
        // Captures the last vocabulary the match step was handed, so tests can assert names-only.
        public IReadOnlyList<VocabularyEntry>? LastContactVocabulary { get; private set; }
        public IReadOnlyList<VocabularyEntry>? LastTagVocabulary { get; private set; }

        /// <summary>The model output cap the last call was handed (issue #434 key 1).</summary>
        public int? LastMaxTokens { get; private set; }

        /// <summary>The destination and model the last call was built with (issue #439).</summary>
        public FileAnalysisTarget? LastTarget { get; private set; }

        /// <summary>How many provider calls were made at all — pins "no request was made" assertions.</summary>
        public int CallCount { get; private set; }

        public Task<List<ExtractedTransaction>> ExtractTransactionsAsync(
            byte[] fileContent, string contentType, string accountCurrencyCode,
            string promptTemplate, FileAnalysisTarget target, int maxTokens,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTarget = target;
            LastMaxTokens = maxTokens;
            return behaviour(fileContent, contentType, accountCurrencyCode, promptTemplate, cancellationToken);
        }

        public Task<List<MatchedCandidate>> MatchTransactionsAsync(
            IReadOnlyList<MatchCandidateInput> candidates,
            IReadOnlyList<VocabularyEntry> contactVocabulary,
            IReadOnlyList<VocabularyEntry> tagVocabulary,
            FileAnalysisTarget target,
            int maxTokens,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTarget = target;
            LastMaxTokens = maxTokens;
            LastContactVocabulary = contactVocabulary;
            LastTagVocabulary = tagVocabulary;
            return (matchBehaviour ?? ((_, _, _, _) => Task.FromResult(new List<MatchedCandidate>())))
                (candidates, contactVocabulary, tagVocabulary, cancellationToken);
        }

        public static FakeProvider Returning(params ExtractedTransaction[] transactions) =>
            new((_, _, _, _, _) => Task.FromResult(transactions.ToList()));

        public static FakeProvider Throwing(Exception ex) =>
            new((_, _, _, _, _) => Task.FromException<List<ExtractedTransaction>>(ex));

        // A provider whose extraction returns the given transactions and whose match step runs `match`.
        public static FakeProvider Matching(
            Func<IReadOnlyList<MatchCandidateInput>, IReadOnlyList<VocabularyEntry>, IReadOnlyList<VocabularyEntry>, List<MatchedCandidate>> match,
            params ExtractedTransaction[] transactions) =>
            new((_, _, _, _, _) => Task.FromResult(transactions.ToList()),
                (c, cp, t, _) => Task.FromResult(match(c, cp, t)));

        public static FakeProvider MatchThrowing(Exception ex, params ExtractedTransaction[] transactions) =>
            new((_, _, _, _, _) => Task.FromResult(transactions.ToList()),
                (_, _, _, _) => Task.FromException<List<MatchedCandidate>>(ex));
    }

    // Contact moved to OdysseyContext; one journal per test backs both the seeded contacts and the
    // IContactLookup the service resolves through (xUnit creates a fresh test-class instance per test).
    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private FileAnalysisService CreateService(
        OdysseyContext context, IFileAnalysisProvider provider, FileAnalysisOptions options) =>
        new(context, provider, TestContextFactory.ContactLookup(journal), Options.Create(options),
            settingsLookup, NullLogger<FileAnalysisService>.Instance);

    // The six file-analysis settings now come from the store, not IOptions (issue #421 Wave 1).
    // Mutate settingsLookup.Settings in a test that needs a non-default value.
    private readonly FakeFileAnalysisSettingsLookup settingsLookup = new();

    // Writes the prompt template the service loads from disk to a temp file and points the options at it.
    private static FileAnalysisOptions EnabledOptions()
    {
        var promptPath = Path.Combine(Path.GetTempPath(), $"odyssey-prompt-{Guid.NewGuid():N}.txt");
        File.WriteAllText(promptPath, "Extract transactions.");
        return new FileAnalysisOptions
        {
            Enabled = true,
            PromptTemplatePath = promptPath,
            MaxFutureTransactionDays = 30,
        };
    }

    // Seeds an account + a file blob + an AccountFile of the given type, returning the file id.
    private static async Task<(Guid accountId, Guid fileId)> SeedStatementAsync(
        OdysseyContext context, Context.AccountFileType fileType = Context.AccountFileType.Statement)
    {
        var account = new Account
        {
            Name = "Checking",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var fileId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            UploadedByUserId = "user-1",
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = "hash",
            FileBlobId = blob.Id,
            FileBlob = blob,
            UploadedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        context.AccountFiles.Add(new AccountFile
        {
            AccountId = account.AccountId,
            FileMetadataId = fileId,
            AttachedByUserId = "user-1",
            AttachedAtUtc = DateTime.UtcNow,
            FileType = fileType,
        });
        await context.SaveChangesAsync();

        return (account.AccountId, fileId);
    }

    private static ExtractedTransaction Extracted(DateTime date, string description = "Coffee", decimal amount = -4.50m) =>
        new(date, null, description, null, null, amount, "USD", null, null, null, null, null, null);

    // An acknowledged consent payload — a hard precondition now enforced before any transfer.
    /// <summary>
    /// A consent payload echoing the disclosure version currently in force (issue #439 §5.3c).
    ///
    /// <para>
    /// Instance rather than static, and computed from <see cref="settingsLookup"/> rather than
    /// hard-coded, because a test that mutates the disclosure values must get the matching version
    /// without restating the hash. A test that wants a <em>mismatch</em> passes its own string, and one
    /// that wants the omitted case passes null — both are a <c>409</c>.
    /// </para>
    /// </summary>
    private AnalyzeFileRequest Consent(string? disclosureVersion = null) => new()
    {
        ConsentAcknowledged = true,
        ConsentText = "I consent to sending the complete file.",
        ConsentMethod = "Per-document checkbox",
        DisclosureVersion = disclosureVersion ?? FileAnalysisDisclosureVersion.Compute(settingsLookup.Settings),
    };

    // A completed job row inserted directly, for audit-projection tests that need controlled timestamps.
    private static FileAnalysisJob NewJob(Guid accountFileId, string userId, DateTime startedAt) => new()
    {
        AccountFileId = accountFileId,
        RequestedByUserId = userId,
        Status = ContextJobStatus.Completed,
        StartedAt = startedAt,
        CompletedAt = startedAt.AddSeconds(5),
        AnalyzerProvider = Context.AnalyzerProvider.Claude,
        AnalyzerModel = "claude-opus-4-7",
        ConsentRecorded = true,
    };

    // ── Feature-flag gate ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_WhenDisabled_ThrowsDisabledException()
    {
        await using var context = TestContextFactory.Create();
        // The switch is a live SETTINGS read since issue #439, not an options flag — so disabling means
        // flipping the lookup, and FileAnalysisOptions.Enabled is now inert documentation of record.
        settingsLookup.Enabled = false;
        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        await Assert.ThrowsAsync<FileAnalysisDisabledException>(() =>
            service.AnalyzeAsync(Guid.NewGuid(), Guid.NewGuid(), "user-1"));
    }

    [Fact]
    public async Task GetJobAsync_WhenDisabled_ThrowsDisabledException()
    {
        await using var context = TestContextFactory.Create();
        settingsLookup.Enabled = false;
        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        await Assert.ThrowsAsync<FileAnalysisDisabledException>(() => service.GetJobAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ImportCandidatesAsync_WhenDisabled_ThrowsDisabledException()
    {
        await using var context = TestContextFactory.Create();
        settingsLookup.Enabled = false;
        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        await Assert.ThrowsAsync<FileAnalysisDisabledException>(() =>
            service.ImportCandidatesAsync(Guid.NewGuid(), new ImportRequest(new List<ImportCandidateRequest>()), "user-1"));
    }

    // ── Pre-analysis guards ─────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_FileNotAttachedToAccount_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            service.AnalyzeAsync(Guid.NewGuid(), Guid.NewGuid(), "user-1"));
    }

    [Fact]
    public async Task AnalyzeAsync_NonStatementFile_ThrowsValidation()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context, Context.AccountFileType.Contract);
        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1"));
    }

    // ── Provider failure path ───────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_ProviderThrows_MarksJobFailedWithProviderCode_AndPropagates()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(
            context, FakeProvider.Throwing(new FileAnalysisProviderException("upstream 500")), EnabledOptions());

        await Assert.ThrowsAsync<FileAnalysisProviderException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", Consent()));

        var job = await context.FileAnalysisJobs.SingleAsync();
        Assert.Equal(ContextJobStatus.Failed, job.Status);
        Assert.Equal("provider_error", job.FailureCode);
        Assert.NotNull(job.CompletedAt);
        Assert.Empty(context.FileAnalysisCandidateTransactions);
    }

    [Fact]
    public async Task AnalyzeAsync_UnexpectedProviderError_RecordsInternalErrorCode()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(
            context, FakeProvider.Throwing(new InvalidOperationException("boom")), EnabledOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", Consent()));

        var job = await context.FileAnalysisJobs.SingleAsync();
        Assert.Equal(ContextJobStatus.Failed, job.Status);
        Assert.Equal("internal_error", job.FailureCode);
    }

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_HappyPath_PersistsCompletedJobWithPendingCandidates()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, FakeProvider.Returning(
            Extracted(DateTime.UtcNow.AddDays(-2), "Groceries", -42.10m),
            Extracted(DateTime.UtcNow.AddDays(-1), "Salary", 2000m)), EnabledOptions());

        var response = await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());

        var job = await context.FileAnalysisJobs.Include(j => j.CandidateTransactions).SingleAsync();
        Assert.Equal(response.AnalysisJobId, job.Id);
        Assert.Equal(ContextJobStatus.Completed, job.Status);
        Assert.NotNull(job.CompletedAt);
        Assert.Equal(2, job.CandidateTransactions.Count);
        Assert.All(job.CandidateTransactions, c => Assert.Equal(ContextReviewStatus.Pending, c.ReviewStatus));
    }

    // ── Consent gate + audit trail ───────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_RecordsConsentVerbatimOnTheJob()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, FakeProvider.Returning(Extracted(DateTime.UtcNow.AddDays(-1), "Coffee")), EnabledOptions());

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent() with
        {
            ConsentText = "I consent to sending the complete file.",
            ConsentMethod = "Per-document checkbox",
        });

        var job = await context.FileAnalysisJobs.SingleAsync();
        Assert.True(job.ConsentRecorded);
        Assert.Equal("I consent to sending the complete file.", job.ConsentText);
        Assert.Equal("Per-document checkbox", job.ConsentMethod);
        Assert.Equal("Consent · GDPR Art. 6(1)(a)", job.LawfulBasis);
    }

    [Theory]
    [InlineData(null)]   // no body at all
    [InlineData(false)]  // body present but not acknowledged (even if text were set)
    public async Task AnalyzeAsync_WithoutAcknowledgedConsent_ThrowsValidation_AndSendsNothing(bool? acknowledged)
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var sent = false;
        var provider = new FakeProvider((_, _, _, _, _) => { sent = true; return Task.FromResult(new List<ExtractedTransaction>()); });
        var service = CreateService(context, provider, EnabledOptions());

        var consent = acknowledged is null
            ? null
            : new AnalyzeFileRequest { ConsentAcknowledged = acknowledged.Value, ConsentText = "text present" };

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", consent));

        Assert.False(sent); // the document never reached the provider
        Assert.Empty(context.FileAnalysisJobs); // and no job/consent row was written
    }

    [Fact]
    public async Task GetAuditLogAsync_ProjectsTransferFields()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, FakeProvider.Returning(
            Extracted(DateTime.UtcNow.AddDays(-2), "Groceries", -42.10m),
            Extracted(DateTime.UtcNow.AddDays(-1), "Salary", 2000m)), EnabledOptions());

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent() with { ConsentText = "I consent." });

        var log = await service.GetAuditLogAsync();

        var entry = Assert.Single(log);
        Assert.Equal("user-1", entry.RequestedByUserId);
        Assert.Null(entry.User); // enrichment is the API layer's job
        Assert.Equal("statement.pdf", entry.File?.Name);
        Assert.Equal("Statement", entry.File?.Kind);
        Assert.Equal("Checking", entry.Account?.Name);
        Assert.Equal("Claude", entry.Provider);
        Assert.Equal(DtoJobStatus.Completed, entry.Status);
        Assert.Equal(2, entry.Candidates);
        Assert.Equal(0, entry.Imported);
        Assert.True(entry.ConsentRecorded);
        Assert.Equal("I consent.", entry.ConsentText);
        Assert.Equal(3, entry.SizeBytes);
        Assert.NotNull(entry.DurationMs);
    }

    [Fact]
    public async Task GetAuditLogAsync_OrdersNewestFirst()
    {
        await using var context = TestContextFactory.Create();
        await SeedStatementAsync(context);
        var accountFileId = (await context.AccountFiles.SingleAsync()).Id;

        // Two transfers against the same file, with explicit start times so the ordering is deterministic.
        var older = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc);
        context.FileAnalysisJobs.AddRange(
            NewJob(accountFileId, "user-older", older),
            NewJob(accountFileId, "user-newer", newer));
        await context.SaveChangesAsync();

        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());
        var log = await service.GetAuditLogAsync();

        Assert.Equal(2, log.Count);
        Assert.Equal("user-newer", log[0].RequestedByUserId);
        Assert.Equal("user-older", log[1].RequestedByUserId);
    }

    [Fact]
    public async Task GetAuditLogAsync_ReflectsImportedCount_AfterImport()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, FakeProvider.Returning(
            Extracted(DateTime.UtcNow.AddDays(-2), "Groceries", -42.10m),
            Extracted(DateTime.UtcNow.AddDays(-1), "Salary", 2000m)), EnabledOptions());

        var response = await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());
        var firstCandidate = (await context.FileAnalysisCandidateTransactions.FirstAsync()).Id;
        await service.ImportCandidatesAsync(
            response.AnalysisJobId,
            new ImportRequest([new ImportCandidateRequest(firstCandidate, null, null, null, null)]),
            "user-1");

        var entry = Assert.Single(await service.GetAuditLogAsync());
        Assert.Equal(2, entry.Candidates);
        Assert.Equal(1, entry.Imported);
    }

    [Fact]
    public async Task GetAuditLogAsync_ProjectsFailedTransfer()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(
            context, FakeProvider.Throwing(new FileAnalysisProviderException("scanned image — no text layer")), EnabledOptions());

        await Assert.ThrowsAsync<FileAnalysisProviderException>(() =>
            service.AnalyzeAsync(accountId, fileId, "user-1", Consent()));

        var entry = Assert.Single(await service.GetAuditLogAsync());
        Assert.Equal(DtoJobStatus.Failed, entry.Status);
        // The audit log surfaces a curated reason, never the raw provider body.
        Assert.Equal("The analysis provider returned an error.", entry.Failure);
        Assert.DoesNotContain("scanned image", entry.Failure ?? string.Empty);
        Assert.Equal(0, entry.Candidates);
        Assert.Equal(0, entry.Imported);
    }

    [Fact]
    public async Task GetAuditLogAsync_WhenDisabled_StillReturnsHistoricalRecords()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var enabled = EnabledOptions();
        await CreateService(context, FakeProvider.Returning(Extracted(DateTime.UtcNow.AddDays(-1), "Coffee")), enabled)
            .AnalyzeAsync(accountId, fileId, "user-1", Consent());

        // The audit trail must survive the switch being turned off — GetAuditLogAsync deliberately does
        // not call EnsureEnabledAsync, since a disabled instance still has to answer "what did we send?".
        settingsLookup.Enabled = false;
        var disabled = CreateService(context, FakeProvider.Returning(), enabled);
        var log = await disabled.GetAuditLogAsync();

        Assert.Single(log);
    }

    [Fact]
    public async Task AnalyzeAsync_DropsTransactionsBeyondFutureWindow()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, FakeProvider.Returning(
            Extracted(DateTime.UtcNow.AddDays(-1), "Kept"),
            // 400 days out — beyond the 30-day MaxFutureTransactionDays window, must be dropped.
            Extracted(DateTime.UtcNow.AddDays(400), "Dropped")), EnabledOptions());

        await service.AnalyzeAsync(accountId, fileId, "user-1", Consent());

        var candidate = await context.FileAnalysisCandidateTransactions.SingleAsync();
        Assert.Equal("Kept", candidate.Description);
    }

    // ── Resumable reviews ───────────────────────────────────────────────────────

    private static async Task<Guid> AccountFileIdOf(OdysseyContext context, Guid fileId) =>
        (await context.AccountFiles.SingleAsync(af => af.FileMetadataId == fileId)).Id;

    // Adds a job (Completed by default) to an account file with the given pending/reviewed candidate counts.
    private static async Task<Guid> AddJobAsync(
        OdysseyContext context, Guid accountFileId, DateTime startedAt, int pending, int reviewed = 0,
        ContextJobStatus status = ContextJobStatus.Completed)
    {
        var job = new FileAnalysisJob
        {
            AccountFileId = accountFileId,
            RequestedByUserId = "user-1",
            Status = status,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(5),
            AnalyzerProvider = Context.AnalyzerProvider.Claude,
            ConsentRecorded = true,
        };
        context.FileAnalysisJobs.Add(job);
        await context.SaveChangesAsync();

        void AddCandidate(ContextReviewStatus rs) => context.FileAnalysisCandidateTransactions.Add(
            new FileAnalysisCandidateTransaction
            {
                AnalysisJobId = job.Id,
                TransactionDate = startedAt,
                Description = "Candidate",
                Amount = -1m,
                Currency = "USD",
                ReviewStatus = rs,
            });
        for (var i = 0; i < pending; i++) AddCandidate(ContextReviewStatus.Pending);
        for (var i = 0; i < reviewed; i++) AddCandidate(ContextReviewStatus.Accepted);
        await context.SaveChangesAsync();
        return job.Id;
    }

    // Attaches another Statement file to an existing account, returning its file id.
    private static async Task<Guid> AddStatementFileAsync(OdysseyContext context, Guid accountId, string name)
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1] };
        var fileId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            UploadedByUserId = "user-1",
            FileName = name,
            ContentType = "application/pdf",
            SizeBytes = 1,
            Sha256Hash = "hash",
            FileBlobId = blob.Id,
            FileBlob = blob,
            UploadedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        context.AccountFiles.Add(new AccountFile
        {
            AccountId = accountId,
            FileMetadataId = fileId,
            AttachedByUserId = "user-1",
            AttachedAtUtc = DateTime.UtcNow,
            FileType = Context.AccountFileType.Statement,
        });
        await context.SaveChangesAsync();
        return fileId;
    }

    [Fact]
    public async Task GetResumableJobsAsync_WhenDisabled_ThrowsDisabledException()
    {
        await using var context = TestContextFactory.Create();
        settingsLookup.Enabled = false;
        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        await Assert.ThrowsAsync<FileAnalysisDisabledException>(() => service.GetResumableJobsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetResumableJobsAsync_AccountNotFound_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        await Assert.ThrowsAsync<DomainNotFoundException>(() => service.GetResumableJobsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetResumableJobsAsync_ReturnsCompletedJobWithPendingCandidates_AndSqlProjectedCounts()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var accountFileId = await AccountFileIdOf(context, fileId);
        await AddJobAsync(context, accountFileId, new DateTime(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc), pending: 4, reviewed: 2);

        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());
        var summary = Assert.Single(await service.GetResumableJobsAsync(accountId));

        Assert.Equal(fileId, summary.FileId);
        Assert.Equal(DtoJobStatus.Completed, summary.Status);
        Assert.Equal(6, summary.CandidateCount);
        Assert.Equal(4, summary.PendingCount);
    }

    [Fact]
    public async Task GetResumableJobsAsync_ExcludesAllReviewedFailedRunningAndNeverAnalysed_Uniformly()
    {
        await using var context = TestContextFactory.Create();

        // File A — completed, but every candidate already reviewed → not resumable.
        var (accountId, fileA) = await SeedStatementAsync(context);
        await AddJobAsync(context, await AccountFileIdOf(context, fileA), DateTime.UtcNow, pending: 0, reviewed: 3);

        // File B — only a Failed job (even with would-be pending candidates) → not resumable.
        var fileB = await AddStatementFileAsync(context, accountId, "b.pdf");
        await AddJobAsync(context, await AccountFileIdOf(context, fileB), DateTime.UtcNow, pending: 2, status: ContextJobStatus.Running);

        // File C — only a Failed job → not resumable.
        var fileC = await AddStatementFileAsync(context, accountId, "c.pdf");
        await AddJobAsync(context, await AccountFileIdOf(context, fileC), DateTime.UtcNow, pending: 2, status: ContextJobStatus.Failed);

        // File D — never analysed (no job at all).
        await AddStatementFileAsync(context, accountId, "d.pdf");

        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        // All four non-resumable reasons are uniformly absent — no existence oracle.
        Assert.Empty(await service.GetResumableJobsAsync(accountId));
    }

    [Fact]
    public async Task GetResumableJobsAsync_ReturnsLatestResumableJobPerFile()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var accountFileId = await AccountFileIdOf(context, fileId);

        await AddJobAsync(context, accountFileId, new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), pending: 2);
        var newer = await AddJobAsync(context, accountFileId, new DateTime(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc), pending: 5);

        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());
        var summary = Assert.Single(await service.GetResumableJobsAsync(accountId));

        Assert.Equal(newer, summary.AnalysisJobId);
        Assert.Equal(5, summary.PendingCount);
    }

    [Fact]
    public async Task GetResumableJobsAsync_WhenStartedAtTies_BreaksByJobId()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var accountFileId = await AccountFileIdOf(context, fileId);

        // Two qualifying jobs sharing the exact same StartedAt — the deterministic tie-break is the
        // larger Id, so repeated calls are stable regardless of insertion/scan order.
        var sharedStart = new DateTime(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc);
        var jobA = await AddJobAsync(context, accountFileId, sharedStart, pending: 2);
        var jobB = await AddJobAsync(context, accountFileId, sharedStart, pending: 7);

        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());
        var summary = Assert.Single(await service.GetResumableJobsAsync(accountId));

        var expected = new[] { jobA, jobB }.Max();
        Assert.Equal(expected, summary.AnalysisJobId);
        Assert.Equal(expected == jobB ? 7 : 2, summary.PendingCount);
    }

    [Fact]
    public async Task GetResumableJobsAsync_ReturnsOneSummaryPerFile()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileA) = await SeedStatementAsync(context);
        await AddJobAsync(context, await AccountFileIdOf(context, fileA), DateTime.UtcNow, pending: 3);

        var fileB = await AddStatementFileAsync(context, accountId, "b.pdf");
        await AddJobAsync(context, await AccountFileIdOf(context, fileB), DateTime.UtcNow, pending: 1);

        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());
        var summaries = await service.GetResumableJobsAsync(accountId);

        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.FileId == fileA);
        Assert.Contains(summaries, s => s.FileId == fileB);
    }

    [Fact]
    public async Task GetResumableJobsAsync_ReducesToLatestPerFile_AcrossMultipleMultiJobFiles()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileA) = await SeedStatementAsync(context);
        var afA = await AccountFileIdOf(context, fileA);
        var fileB = await AddStatementFileAsync(context, accountId, "b.pdf");
        var afB = await AccountFileIdOf(context, fileB);

        // Each file has two qualifying jobs; the reduction must return the latest of each — proving the
        // per-file grouping isn't confused by other files' jobs.
        await AddJobAsync(context, afA, new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc), pending: 2);
        var latestA = await AddJobAsync(context, afA, new DateTime(2026, 6, 20, 9, 0, 0, DateTimeKind.Utc), pending: 3);
        await AddJobAsync(context, afB, new DateTime(2026, 5, 2, 9, 0, 0, DateTimeKind.Utc), pending: 9);
        var latestB = await AddJobAsync(context, afB, new DateTime(2026, 6, 25, 9, 0, 0, DateTimeKind.Utc), pending: 4);

        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());
        var summaries = await service.GetResumableJobsAsync(accountId);

        Assert.Equal(2, summaries.Count);
        var a = Assert.Single(summaries, s => s.FileId == fileA);
        var b = Assert.Single(summaries, s => s.FileId == fileB);
        Assert.Equal(latestA, a.AnalysisJobId);
        Assert.Equal(3, a.PendingCount);
        Assert.Equal(latestB, b.AnalysisJobId);
        Assert.Equal(4, b.PendingCount);
    }

    // ── AI matching (issue #266) ──────────────────────────────────────────────

    // Seeds a Completed job with the given candidates (merchant, category) plus named contacts
    // and tags, returning the job id. The candidate list is index-ordered to match the provider input.
    private async Task<Guid> SeedMatchJobAsync(
        OdysseyContext context,
        (string? Merchant, string? Category)[] candidates,
        string[] contactNames,
        string[] tagNames,
        ContextJobStatus status = ContextJobStatus.Completed,
        ContextMatchStatus matchStatus = ContextMatchStatus.NotRun,
        string[]? archivedContacts = null,
        string[]? archivedTags = null)
    {
        var (_, fileId) = await SeedStatementAsync(context);
        var accountFileId = await AccountFileIdOf(context, fileId);

        var job = new FileAnalysisJob
        {
            AccountFileId = accountFileId,
            RequestedByUserId = "user-1",
            Status = status,
            MatchStatus = matchStatus,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            AnalyzerProvider = Context.AnalyzerProvider.Claude,
            ConsentRecorded = true,
        };
        context.FileAnalysisJobs.Add(job);

        foreach (var (merchant, category) in candidates)
            context.FileAnalysisCandidateTransactions.Add(new FileAnalysisCandidateTransaction
            {
                AnalysisJobId = job.Id,
                TransactionDate = DateTime.UtcNow,
                Description = "Candidate",
                Merchant = merchant,
                CategoryHint = category,
                Amount = -10m,
                Currency = "USD",
                ReviewStatus = ContextReviewStatus.Pending,
            });

        foreach (var name in contactNames)
            journal.Contacts.Add(new Contact { ExternalUid = $"urn:uuid:{Guid.NewGuid()}", NormalizedName = name.ToUpperInvariant(), Type = ContactType.Organization, OrganizationDetails = new() { LegalName = name } });
        foreach (var name in archivedContacts ?? [])
            journal.Contacts.Add(new Contact { ExternalUid = $"urn:uuid:{Guid.NewGuid()}", NormalizedName = name.ToUpperInvariant(), Type = ContactType.Organization, Archived = DateTime.UtcNow, OrganizationDetails = new() { LegalName = name } });
        foreach (var name in tagNames)
            context.TransactionTags.Add(new TransactionTag { Name = name });
        foreach (var name in archivedTags ?? [])
            context.TransactionTags.Add(new TransactionTag { Name = name, Archived = DateTime.UtcNow });

        await journal.SaveChangesAsync();
        await context.SaveChangesAsync();
        return job.Id;
    }

    /// <summary>
    /// Enabled options for the match tests. <c>Match.MaxVocabulary</c> and <c>Match.TimeoutSeconds</c>
    /// are set here only to prove they are IGNORED: both moved into the system-settings store in issue
    /// #434 and are read through <see cref="settingsLookup"/> now, so a test that wants a non-default
    /// cap sets <see cref="MatchVocabularyCap"/> instead. Leaving these values deliberately WRONG is
    /// what makes a regression to the options class fail rather than pass quietly.
    /// </summary>
    private static FileAnalysisOptions MatchOptions(double threshold = 0.60)
    {
        var opts = EnabledOptions();
        opts.Match.AutoLinkThreshold = threshold;
        opts.Match.MaxVocabulary = -1;
        opts.Match.TimeoutSeconds = -1;
        return opts;
    }

    /// <summary>Points the fake lookup's vocabulary cap at <paramref name="maxVocab"/>.</summary>
    private void MatchVocabularyCap(int maxVocab) =>
        settingsLookup.Settings = settingsLookup.Settings with { MatchMaxVocabulary = maxVocab };

    [Fact]
    public async Task MatchAsync_WhenDisabled_ThrowsDisabledException()
    {
        await using var context = TestContextFactory.Create();
        settingsLookup.Enabled = false;
        var service = CreateService(context, FakeProvider.Returning(), EnabledOptions());

        await Assert.ThrowsAsync<FileAnalysisDisabledException>(() => service.MatchAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task MatchAsync_ExtractionNotCompleted_ThrowsConflict()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon"], ["Groceries"],
            status: ContextJobStatus.Running);
        var service = CreateService(context, FakeProvider.Returning(), MatchOptions());

        await Assert.ThrowsAsync<DomainConflictException>(() => service.MatchAsync(jobId));
    }

    [Fact]
    public async Task MatchAsync_AlreadyRunning_ThrowsConflict()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon"], ["Groceries"],
            matchStatus: ContextMatchStatus.Running);
        var service = CreateService(context, FakeProvider.Returning(), MatchOptions());

        await Assert.ThrowsAsync<DomainConflictException>(() => service.MatchAsync(jobId));
    }

    [Fact]
    public async Task MatchAsync_AboveThreshold_PersistsLlmMatch()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN Mktp", "Shopping")], ["Amazon"], ["Groceries"]);
        var provider = FakeProvider.Matching((cands, cps, tags) =>
        [
            new MatchedCandidate(0,
                cps.First(v => v.Name == "Amazon").Ref, 0.95m,
                [tags.First(v => v.Name == "Groceries").Ref], 0.88m)
        ]);
        var service = CreateService(context, provider, MatchOptions());

        var job = await service.MatchAsync(jobId);

        Assert.Equal(DtoMatchStatus.Completed, job.MatchStatus);
        var candidate = Assert.Single(job.Candidates);
        Assert.NotNull(candidate.MatchedContactId);
        Assert.Equal("Amazon", candidate.MatchedContactName);
        Assert.Equal(0.95m, candidate.MerchantMatchConfidence);
        Assert.Equal(DtoMatchMethod.Llm, candidate.MatchMethod);
        Assert.Single(candidate.MatchedTagIds);
        Assert.Equal(0.88m, candidate.CategoryMatchConfidence);
    }

    [Fact]
    public async Task MatchAsync_ClampsOutOfRangeConfidence()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon"], ["Groceries"]);
        // The model returns a nonsensical confidence above 1.0; the service must clamp to [0,1].
        var provider = FakeProvider.Matching((_, cps, _) =>
        [
            new MatchedCandidate(0, cps.First(v => v.Name == "Amazon").Ref, 1.5m, [], null)
        ]);
        var service = CreateService(context, provider, MatchOptions());

        var job = await service.MatchAsync(jobId);

        var candidate = Assert.Single(job.Candidates);
        Assert.Equal(1.0m, candidate.MerchantMatchConfidence);
    }

    [Fact]
    public async Task MatchAsync_ExcludesArchivedTagsFromVocabulary()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon"], ["Groceries"],
            archivedTags: ["OldCategory"]);
        var provider = FakeProvider.Matching((_, _, _) => []);
        var service = CreateService(context, provider, MatchOptions());

        await service.MatchAsync(jobId);

        Assert.NotNull(provider.LastTagVocabulary);
        var names = provider.LastTagVocabulary!.Select(v => v.Name).ToList();
        Assert.Contains("Groceries", names);
        Assert.DoesNotContain("OldCategory", names); // archived tag excluded
    }

    [Fact]
    public async Task MatchAsync_DuplicateIndex_LastWriteWins()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon", "Spotify"], ["Groceries"]);
        // The model emits two results for the same index; the last one must win.
        var provider = FakeProvider.Matching((_, cps, _) =>
        [
            new MatchedCandidate(0, cps.First(v => v.Name == "Amazon").Ref, 0.95m, [], null),
            new MatchedCandidate(0, cps.First(v => v.Name == "Spotify").Ref, 0.92m, [], null)
        ]);
        var service = CreateService(context, provider, MatchOptions());

        var job = await service.MatchAsync(jobId);

        var candidate = Assert.Single(job.Candidates);
        Assert.Equal("Spotify", candidate.MatchedContactName);
    }

    [Fact]
    public async Task MatchAsync_AppliesResultsToTheCorrectCandidateByIndex()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context,
            [("AMZN Mktp", "Shopping"), ("SPOTIFY P0F3", "Music")],
            ["Amazon", "Spotify"], ["Groceries"]);
        // Map each candidate to a DISTINCT contact keyed off the merchant the provider was sent,
        // so a misalignment in the service's index→candidate apply would swap the names and fail.
        var provider = FakeProvider.Matching((cands, cps, _) =>
            cands.Select(c => new MatchedCandidate(
                c.Index,
                cps.First(v => v.Name == (c.Merchant!.Contains("AMZN") ? "Amazon" : "Spotify")).Ref,
                0.95m, [], null)).ToList());
        var service = CreateService(context, provider, MatchOptions());

        var job = await service.MatchAsync(jobId);

        var amazonRow = job.Candidates.Single(c => c.Merchant!.Contains("AMZN"));
        var spotifyRow = job.Candidates.Single(c => c.Merchant!.Contains("SPOTIFY"));
        Assert.Equal("Amazon", amazonRow.MatchedContactName);
        Assert.Equal("Spotify", spotifyRow.MatchedContactName);
    }

    [Fact]
    public async Task MatchAsync_BelowThreshold_PersistsSuggestionButLeavesMatchMethodNone()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN Mktp", "Shopping")], ["Amazon"], ["Groceries"]);
        // Both confidences are below the 0.60 auto-link threshold ⇒ suggestion-not-linked.
        var provider = FakeProvider.Matching((_, cps, tags) =>
        [
            new MatchedCandidate(0,
                cps.First(v => v.Name == "Amazon").Ref, 0.42m,
                [tags.First(v => v.Name == "Groceries").Ref], 0.40m)
        ]);
        var service = CreateService(context, provider, MatchOptions(threshold: 0.60));

        var job = await service.MatchAsync(jobId);

        var candidate = Assert.Single(job.Candidates);
        // The suggestion is persisted (id + confidence + tag) so the UI can offer an Apply chip…
        Assert.NotNull(candidate.MatchedContactId);
        Assert.Equal(0.42m, candidate.MerchantMatchConfidence);
        Assert.Single(candidate.MatchedTagIds);
        // …but nothing was auto-applied, so the row's provenance stays None (not Llm).
        Assert.Equal(DtoMatchMethod.None, candidate.MatchMethod);
    }

    [Fact]
    public async Task MatchAsync_ThresholdAndCapEchoedOnJob()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon"], ["Groceries"]);
        // Both the threshold and the vocabulary cap come from the system-settings store now (issue #421
        // Wave 1 and issue #434 key 2). MatchOptions leaves the options-class values at -1, so if either
        // read regressed to IOptions this would report -1 rather than the value under test.
        settingsLookup.Settings = settingsLookup.Settings with { AutoLinkThreshold = 0.7m };
        MatchVocabularyCap(250);
        var service = CreateService(context, FakeProvider.Matching((_, _, _) => []), MatchOptions(threshold: 0.1));

        var job = await service.MatchAsync(jobId);

        Assert.Equal(0.7, job.AutoLinkThreshold);
        Assert.Equal(250, job.MaxVocabulary);
    }

    /// <summary>
    /// The threshold is stamped on the job at match time (issue #421 Wave 1), so a later edit cannot
    /// re-interpret a completed analysis. Without the stamp, the read DTO reported whatever the setting
    /// happened to be when it was READ, silently reclassifying stored confidences as auto-linked or not.
    /// </summary>
    [Fact]
    public async Task MatchAsync_StampsTheThresholdSoALaterChangeDoesNotRewriteHistory()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon"], ["Groceries"]);

        settingsLookup.Settings = settingsLookup.Settings with { AutoLinkThreshold = 0.9m };
        var service = CreateService(context, FakeProvider.Matching((_, _, _) => []), MatchOptions());
        var atMatchTime = await service.MatchAsync(jobId);
        Assert.Equal(0.9, atMatchTime.AutoLinkThreshold);

        // An admin lowers the threshold afterwards; the completed job must not move with it.
        settingsLookup.Settings = settingsLookup.Settings with { AutoLinkThreshold = 0.2m };
        var reread = await service.GetJobAsync(jobId);

        Assert.NotNull(reread);
        Assert.Equal(0.9, reread!.AutoLinkThreshold);
    }

    [Fact]
    public async Task MatchAsync_SendsNamesOnly_ExcludingArchived()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon", "Rema 1000"], ["Groceries"],
            archivedContacts: ["OldVendor"]);
        var provider = FakeProvider.Matching((_, _, _) => []);
        var service = CreateService(context, provider, MatchOptions());

        await service.MatchAsync(jobId);

        Assert.NotNull(provider.LastContactVocabulary);
        var names = provider.LastContactVocabulary!.Select(v => v.Name).ToList();
        Assert.Contains("Amazon", names);
        Assert.Contains("Rema 1000", names);
        Assert.DoesNotContain("OldVendor", names); // archived excluded
    }

    [Fact]
    public async Task MatchAsync_HallucinatedRef_IsDropped()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon"], ["Groceries"]);
        var provider = FakeProvider.Matching((_, _, _) =>
        [
            new MatchedCandidate(0, "c999", 0.99m, ["t999"], 0.99m) // refs not in the sent lists
        ]);
        var service = CreateService(context, provider, MatchOptions());

        var job = await service.MatchAsync(jobId);

        var candidate = Assert.Single(job.Candidates);
        Assert.Null(candidate.MatchedContactId);
        Assert.Empty(candidate.MatchedTagIds);
        Assert.Equal(DtoMatchMethod.None, candidate.MatchMethod);
    }

    [Fact]
    public async Task MatchAsync_OverVocabularyCap_SkipsAndKeepsCandidates()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon", "Rema 1000", "Spotify"], ["Groceries"]);
        var provider = FakeProvider.Matching((_, _, _) => throw new Exception("should not be called"));
        MatchVocabularyCap(2);
        var service = CreateService(context, provider, MatchOptions());

        var job = await service.MatchAsync(jobId);

        Assert.Equal(DtoMatchStatus.Skipped, job.MatchStatus);
        Assert.Single(job.Candidates); // extracted candidate intact and importable
    }

    [Fact]
    public async Task MatchAsync_ProviderThrows_RecordsCuratedFailure_KeepsCandidates()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context, [("AMZN", "Shopping")], ["Amazon"], ["Groceries"]);
        var provider = FakeProvider.MatchThrowing(new FileAnalysisProviderException("upstream 500: secret internals"));
        var service = CreateService(context, provider, MatchOptions());

        var job = await service.MatchAsync(jobId);

        Assert.Equal(DtoMatchStatus.Failed, job.MatchStatus);
        Assert.NotNull(job.MatchFailureMessage);
        Assert.DoesNotContain("secret internals", job.MatchFailureMessage!); // curated, not the raw body
        Assert.Equal(DtoJobStatus.Completed, job.Status); // extraction status untouched
        Assert.Single(job.Candidates);
    }

    [Fact]
    public async Task MatchAsync_ReRun_PreservesManualRows_RefreshesLlm()
    {
        await using var context = TestContextFactory.Create();
        var jobId = await SeedMatchJobAsync(context,
            [("AMZN", "Shopping"), ("SPOTIFY", "Music")],
            ["Amazon", "Spotify", "Netflix"], ["Groceries"]);

        // The reviewer curated candidate 0 to a manual contact (persisted MatchMethod = Manual).
        var netflixId = (await journal.Contacts.SingleAsync(c => c.OrganizationDetails!.LegalName == "Netflix")).ContactId;
        var candidates = await context.FileAnalysisCandidateTransactions
            .Where(c => c.AnalysisJobId == jobId).ToListAsync();
        var manualCandidate = candidates.Single(c => c.Merchant == "AMZN");
        manualCandidate.MatchedContactId = netflixId;
        manualCandidate.MatchMethod = ContextMatchMethod.Manual;
        await context.SaveChangesAsync();

        // A re-run that would map BOTH candidates to Amazon.
        var provider = FakeProvider.Matching((cands, cps, _) =>
            cands.Select(c => new MatchedCandidate(c.Index, cps.First(v => v.Name == "Amazon").Ref, 0.9m, [], null)).ToList());
        var service = CreateService(context, provider, MatchOptions());

        await service.MatchAsync(jobId);

        // Re-read from the store (same in-memory db) to confirm the persisted state.
        await context.Entry(manualCandidate).ReloadAsync();
        Assert.Equal(netflixId, manualCandidate.MatchedContactId); // human decision not clobbered
        Assert.Equal(ContextMatchMethod.Manual, manualCandidate.MatchMethod);

        // The None candidate WAS refreshed with the fresh suggestion.
        var refreshed = candidates.Single(c => c.Merchant == "SPOTIFY");
        await context.Entry(refreshed).ReloadAsync();
        var amazonId = (await journal.Contacts.SingleAsync(c => c.OrganizationDetails!.LegalName == "Amazon")).ContactId;
        Assert.Equal(amazonId, refreshed.MatchedContactId);
        Assert.Equal(ContextMatchMethod.Llm, refreshed.MatchMethod);
    }
}
