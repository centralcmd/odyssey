using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextJobStatus = Odyssey.Context.FileAnalysisJobStatus;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

/// <summary>
/// The extraction queue (issue #444): enqueue without a transfer, the claim, every pre-send refusal (each with
/// zero provider calls and <c>DocumentSent = No</c>), the transfer built from the job's stamps, cancellation,
/// and the stale sweep. The relational races (two workers, a sweep winning mid-call on MariaDB) are re-run in
/// <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class FileAnalysisQueueTests
{
    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();
    private readonly FakeFileAnalysisSettingsLookup settingsLookup = new();
    private readonly FileAnalysisTestHarness.KeyStored secrets = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    // ── Enqueue ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_SavesAQueuedJobWithEveryStamp_AndSendsNothing()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var provider = new RecordingProvider();
        var service = CreateService(context, provider);

        var response = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        var job = await context.FileAnalysisJobs.AsNoTracking().SingleAsync();
        Assert.Equal(response.AnalysisJobId, job.Id);
        Assert.Equal(ContextJobStatus.Queued, job.Status);
        Assert.Equal(time.GetUtcNow().UtcDateTime, job.QueuedAt);
        Assert.Null(job.StartedAt);
        Assert.Null(job.TransferStartedAt);
        Assert.Equal(FileAnalysisTestHarness.Template.Sha256, job.PromptTemplateHash);
        Assert.Equal(FileAnalysisTestHarness.Template.Version, job.PromptVersion);
        Assert.Equal(Sha256Of([1, 2, 3]), job.FileSha256);
        Assert.Equal("https://api.anthropic.com:443", job.AnalyzerBaseUrlOrigin);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Enqueue_WhileAJobIsOpen_Refuses409_WithThatJobsId()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, new RecordingProvider());
        var first = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        var refused = await Assert.ThrowsAsync<FileAnalysisInProgressException>(() =>
            service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent()));

        Assert.Equal(first.AnalysisJobId, refused.AnalysisJobId);
        Assert.Equal(FileAnalysisCodes.AnalysisInProgress, refused.Code);
        Assert.Equal(first.AnalysisJobId, refused.Extensions![FileAnalysisCodes.AnalysisJobIdExtension]);
        Assert.Single(context.FileAnalysisJobs);
    }

    [Fact]
    public async Task Enqueue_AFileOverTheAnalysisCeiling_Refuses422_AndWritesNoJob()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context, sizeBytes: FileAnalysisJobTiming.MaxAnalysisFileBytes + 1);
        var service = CreateService(context, new RecordingProvider());

        var refused = await Assert.ThrowsAsync<FileAnalysisTooLargeException>(() =>
            service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent()));

        Assert.Equal(FileAnalysisCodes.FileTooLargeForAnalysis, refused.Code);
        Assert.Equal(422, refused.StatusCode);
        Assert.Contains("file", refused.Errors!.Keys);
        Assert.Equal(FileAnalysisJobTiming.MaxAnalysisFileBytes, refused.Extensions![FileAnalysisCodes.LimitBytesExtension]);
        Assert.Empty(context.FileAnalysisJobs);
    }

    [Fact]
    public async Task Enqueue_AStaleQueuedJob_IsFailedInline_SoANewAnalysisIsAccepted()
    {
        // AC 11: with no worker running, a Queued row past UnclaimedTimeout does not wedge the file.
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, new RecordingProvider());
        var stale = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        time.Advance(FileAnalysisJobTiming.UnclaimedTimeout + TimeSpan.FromMinutes(1));
        var fresh = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        var old = await context.FileAnalysisJobs.AsNoTracking().SingleAsync(j => j.Id == stale.AnalysisJobId);
        Assert.Equal(ContextJobStatus.Failed, old.Status);
        Assert.Equal(FileAnalysisCodes.NotStarted, old.FailureCode);
        Assert.NotEqual(stale.AnalysisJobId, fresh.AnalysisJobId);
    }

    [Fact]
    public async Task GetJob_AStaleQueuedJob_IsFailedInline_AndReadsNotStarted()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, new RecordingProvider());
        var queued = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        time.Advance(FileAnalysisJobTiming.UnclaimedTimeout + TimeSpan.FromMinutes(1));
        var job = await service.GetJobAsync(queued.AnalysisJobId);

        Assert.Equal(Odyssey.Dtos.Finance.FileAnalysisJobStatus.Failed, job!.Status);
        Assert.Equal(FileAnalysisCodes.NotStarted, job.FailureCode);
        Assert.Equal(FileAnalysisDocumentSent.No, job.DocumentSent);
    }

    // ── The run ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_Completes_WithTheTransferStampedBeforeTheCall_AndSentYes()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var provider = new RecordingProvider(Extracted("Groceries"));
        var service = CreateService(context, provider);

        var response = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());
        time.Advance(TimeSpan.FromSeconds(3));
        await RunAsync(context, provider, response.AnalysisJobId);

        var job = await context.FileAnalysisJobs.AsNoTracking().Include(j => j.CandidateTransactions).SingleAsync();
        Assert.Equal(ContextJobStatus.Completed, job.Status);
        Assert.Single(job.CandidateTransactions);
        Assert.NotNull(job.ClaimedAt);
        Assert.NotNull(job.TransferStartedAt);
        Assert.True(job.TransferStartedAt >= job.ClaimedAt && job.ClaimedAt >= job.QueuedAt);
        Assert.Equal(job.TransferStartedAt, job.StartedAt);
        Assert.Null(job.RunId);
        Assert.Null(job.LeaseUntilUtc);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(FileAnalysisDocumentSent.Yes, (await service.GetJobAsync(job.Id))!.DocumentSent);
    }

    [Fact]
    public async Task Run_BuildsTheRequestFromTheJobsStamps_NotFromLaterSettings()
    {
        // AC 7.
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        settingsLookup.Settings = settingsLookup.Settings with { MaxTokens = 4000, ExtractionPromptAddition = "Dates are DD.MM." };
        var provider = new RecordingProvider(Extracted("Rent"));
        var service = CreateService(context, provider);
        var response = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        // Neither value is part of the disclosure version, so changing them refuses nothing.
        settingsLookup.Settings = settingsLookup.Settings with { MaxTokens = 9000, ExtractionPromptAddition = "Something else." };
        await RunAsync(context, provider, response.AnalysisJobId);

        Assert.Equal(4000, provider.LastMaxTokens);
        Assert.Equal("Dates are DD.MM.", provider.LastAddition);
        Assert.Equal(FileAnalysisTestHarness.Template.Text, provider.LastPrompt);
    }

    public static TheoryData<string> Refusals =>
    [
        "requester-deleted", "requester-disabled", "requester-lost-claim", "file-retyped", "file-bytes-changed",
        "settings-read-failed", "model-unusable", "origin-port", "origin-scheme", "model", "processor", "region",
        "disclosure", "template", "key-not-set", "key-unreadable", "switched-off",
    ];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EveryPreSendCheck_RefusesWithItsCode_AndNothingIsSent(string change)
    {
        // AC 6.
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var provider = new RecordingProvider(Extracted("Coffee"));
        var service = CreateService(context, provider);
        var response = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());
        var template = FileAnalysisTestHarness.Template;

        var (expectedStatus, expectedCode) = await ApplyAsync(context, change, fileId);
        if (change == "template")
        {
            template = new FileAnalysisTestHarness.TestPromptTemplate("A different prompt.");
        }

        await RunAsync(context, provider, response.AnalysisJobId, template);

        var job = await context.FileAnalysisJobs.AsNoTracking().SingleAsync();
        Assert.Equal(expectedStatus, job.Status);
        Assert.Equal(expectedCode, job.FailureCode);
        Assert.Null(job.TransferStartedAt);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(FileAnalysisDocumentSent.No, FileAnalysisTerminal.DocumentSent(job));
    }

    [Fact]
    public async Task ADestinationChangeTheCacheHasNotSeen_IsStillRefused()
    {
        // AC 7 (security S2): the comparison reads the store, not the 30 s cached snapshot.
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var provider = new RecordingProvider(Extracted("Coffee"));
        var service = CreateService(context, provider);
        var response = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        settingsLookup.UncachedSettings = settingsLookup.Settings with { BaseUrl = "https://gateway.example.com" };
        await RunAsync(context, provider, response.AnalysisJobId);

        var job = await context.FileAnalysisJobs.AsNoTracking().SingleAsync();
        Assert.Equal(FileAnalysisCodes.DestinationChanged, job.FailureCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ShutdownDuringTheCall_LeavesTheJobFailedInterrupted_PossiblySent()
    {
        // AC 8: written with CancellationToken.None, so a worker shutting down never strands Running.
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        using var shutdown = new CancellationTokenSource();
        var provider = new RecordingProvider { OnCall = token => { shutdown.Cancel(); token.ThrowIfCancellationRequested(); } };
        var service = CreateService(context, provider);
        var response = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        var queue = FileAnalysisTestHarness.Queue(context, time: time);
        Assert.True(await queue.TryClaimAsync(response.AnalysisJobId, CancellationToken.None));
        var result = await Runner(context, provider).RunAsync(response.AnalysisJobId, shutdown.Token);

        await using var fresh = TestContextFactory.Reopen(context);
        var job = await fresh.FileAnalysisJobs.AsNoTracking().SingleAsync();
        Assert.Equal(FileAnalysisRunResult.Failed, result);
        Assert.Equal(ContextJobStatus.Failed, job.Status);
        Assert.Equal(FileAnalysisCodes.Interrupted, job.FailureCode);
        Assert.Equal(FileAnalysisDocumentSent.Possibly, FileAnalysisTerminal.DocumentSent(job));
    }

    [Fact]
    public async Task ASweepThatFailsTheRowMidCall_Wins_AndTheResultIsDiscarded()
    {
        // AC 10 (the job half; the candidate half is relational).
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var provider = new RecordingProvider(Extracted("Coffee"));
        var service = CreateService(context, provider);
        var response = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());
        provider.OnCall = _ =>
        {
            // Another replica's sweep, after the lease expired, from its own context.
            using var other = TestContextFactory.Reopen(context);
            time.Advance(TimeSpan.FromHours(1));
            FileAnalysisTestHarness.Queue(other, time: time).SweepAsync().GetAwaiter().GetResult();
        };

        var result = await RunAsync(context, provider, response.AnalysisJobId);

        // That no candidate survives the lost save is a property of the TRANSACTION, which EF InMemory does not
        // have (it applies a failed batch partially): Odyssey.IntegrationTests pins it on MariaDB.
        await using var fresh = TestContextFactory.Reopen(context);
        var job = await fresh.FileAnalysisJobs.AsNoTracking().SingleAsync();
        Assert.Equal(FileAnalysisRunResult.Lost, result);
        Assert.Equal(ContextJobStatus.Failed, job.Status);
        Assert.Equal(FileAnalysisCodes.Interrupted, job.FailureCode);
    }

    [Fact]
    public async Task OnlyOneOfTwoClaimsWins()
    {
        await using var context = TestContextFactory.Create();
        var (accountId, fileId) = await SeedStatementAsync(context);
        var service = CreateService(context, new RecordingProvider());
        var response = await service.AnalyzeAsync(accountId, fileId, FileAnalysisTestHarness.RequesterId, Consent());

        await using var other = TestContextFactory.Reopen(context);
        var first = await FileAnalysisTestHarness.Queue(context, time: time).ClaimNextAsync(CancellationToken.None);
        var second = await FileAnalysisTestHarness.Queue(other, time: time).ClaimNextAsync(CancellationToken.None);

        Assert.Equal(response.AnalysisJobId, first);
        Assert.Null(second);
    }

    // ── The sweep ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_FailsAnExpiredLease_ALegacyRunningRow_AndAnUnclaimedJob_AndResendsNothing()
    {
        // AC 9.
        await using var context = TestContextFactory.Create();
        var (_, fileId) = await SeedStatementAsync(context);
        var accountFileId = await context.AccountFiles.Where(af => af.FileMetadataId == fileId).Select(af => af.Id).SingleAsync();
        var now = time.GetUtcNow().UtcDateTime;
        var lease = FileAnalysisJobTiming.Lease(new FileAnalysisOptions().TimeoutSeconds);

        var expired = NewJob(accountFileId, ContextJobStatus.Running, queuedAt: now.AddMinutes(-20), leaseUntil: now.AddSeconds(-1));
        var legacy = NewJob(accountFileId, ContextJobStatus.Running, queuedAt: null, startedAt: now - lease - TimeSpan.FromMinutes(1));
        var unclaimed = NewJob(accountFileId, ContextJobStatus.Queued, queuedAt: now - FileAnalysisJobTiming.UnclaimedTimeout - TimeSpan.FromSeconds(1));
        var healthy = NewJob(accountFileId, ContextJobStatus.Running, queuedAt: now.AddMinutes(-1), leaseUntil: now.AddMinutes(5));
        context.FileAnalysisJobs.AddRange(expired, legacy, unclaimed, healthy);
        await context.SaveChangesAsync();

        var swept = await FileAnalysisTestHarness.Queue(context, time: time).SweepAsync();

        var rows = await context.FileAnalysisJobs.AsNoTracking().ToDictionaryAsync(j => j.Id);
        Assert.Equal(3, swept);
        Assert.Equal(FileAnalysisCodes.Interrupted, rows[expired.Id].FailureCode);
        Assert.Equal(FileAnalysisCodes.Interrupted, rows[legacy.Id].FailureCode);
        Assert.Equal(FileAnalysisCodes.NotStarted, rows[unclaimed.Id].FailureCode);
        Assert.Equal(ContextJobStatus.Running, rows[healthy.Id].Status);
        Assert.All(new[] { expired.Id, legacy.Id, unclaimed.Id }, id => Assert.Equal(ContextJobStatus.Failed, rows[id].Status));
    }

    [Fact]
    public void TheLease_CoversTheWholeProviderBudget()
    {
        foreach (var seconds in new[] { FileAnalysisJobTiming.MinTimeoutSeconds, 120, FileAnalysisJobTiming.MaxTimeoutSeconds })
        {
            Assert.True(FileAnalysisJobTiming.Lease(seconds) > FileAnalysisJobTiming.TotalRequestTimeout(seconds));
        }

        Assert.Equal(TimeSpan.FromMinutes(8), FileAnalysisJobTiming.Lease(120));
        // The worker runs one job per tick inside RunOnceAsync; its whole budget must stay under the catalogue's
        // 300 s not-ticking threshold (issue #444 v4 nit).
        Assert.True(FileAnalysisJobTiming.TotalRequestTimeout(FileAnalysisJobTiming.MaxTimeoutSeconds) < TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void TheEmbeddedTemplate_Loads_AndHashesStably()
    {
        var template = new EmbeddedFileAnalysisPromptTemplate();

        Assert.False(string.IsNullOrWhiteSpace(template.Text));
        Assert.Equal(EmbeddedFileAnalysisPromptTemplate.CurrentVersion, template.Version);
        Assert.Equal(64, template.Sha256.Length);
        Assert.Equal(EmbeddedFileAnalysisPromptTemplate.HashOf(template.Text), template.Sha256);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<(ContextJobStatus Status, string Code)> ApplyAsync(OdysseyContext context, string change, Guid fileId)
    {
        var s = settingsLookup.Settings;
        switch (change)
        {
            case "requester-deleted":
                context.Users.Remove(await context.Users.SingleAsync(u => u.Id == FileAnalysisTestHarness.RequesterId));
                break;
            case "requester-disabled":
                (await context.Users.SingleAsync(u => u.Id == FileAnalysisTestHarness.RequesterId)).LockoutEnd = AccountLockout.DisabledLockoutEnd;
                break;
            case "requester-lost-claim":
                context.RoleClaims.RemoveRange(context.RoleClaims.Where(c => c.ClaimValue == PermissionClaims.FileAnalysisCreate));
                break;
            case "file-retyped":
                (await context.AccountFiles.SingleAsync(af => af.FileMetadataId == fileId)).FileType = Context.AccountFileType.Other;
                break;
            case "file-bytes-changed":
                (await context.FileBlob.SingleAsync()).Content = [9, 9, 9];
                break;
            case "settings-read-failed":
                settingsLookup.Settings = s with { ReadFailed = true };
                return (ContextJobStatus.Failed, FileAnalysisCodes.ConfigurationUnavailable);
            case "model-unusable":
                settingsLookup.Settings = s with { Model = null };
                return (ContextJobStatus.Failed, FileAnalysisCodes.ConfigurationUnavailable);
            case "origin-port":
                settingsLookup.Settings = s with { BaseUrl = "https://api.anthropic.com:8443" };
                return (ContextJobStatus.Failed, FileAnalysisCodes.DestinationChanged);
            case "origin-scheme":
                settingsLookup.Settings = s with { BaseUrl = "http://api.anthropic.com" };
                return (ContextJobStatus.Failed, FileAnalysisCodes.DestinationChanged);
            case "model":
                settingsLookup.Settings = s with { Model = "claude-other" };
                return (ContextJobStatus.Failed, FileAnalysisCodes.DestinationChanged);
            case "processor":
                settingsLookup.Settings = s with { Processor = "Someone Else" };
                return (ContextJobStatus.Failed, FileAnalysisCodes.DestinationChanged);
            case "region":
                settingsLookup.Settings = s with { ProcessorRegion = "Elsewhere" };
                return (ContextJobStatus.Failed, FileAnalysisCodes.DestinationChanged);
            case "disclosure":
                settingsLookup.Settings = s with { LawfulBasis = "Contract · GDPR Art. 6(1)(b)" };
                return (ContextJobStatus.Failed, FileAnalysisCodes.DisclosureChanged);
            case "template":
                return (ContextJobStatus.Failed, FileAnalysisCodes.PromptVersionMismatch);
            case "key-not-set":
                secrets.Result = SecretResult.NotSet;
                return (ContextJobStatus.Failed, FileAnalysisCodes.ProviderCredentialError);
            case "key-unreadable":
                secrets.Result = SecretResult.Unreadable;
                return (ContextJobStatus.Failed, FileAnalysisCodes.ProviderCredentialError);
            case "switched-off":
                settingsLookup.Enabled = false;
                return (ContextJobStatus.Cancelled, FileAnalysisCodes.Disabled);
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return change.StartsWith("requester", StringComparison.Ordinal)
            ? (ContextJobStatus.Cancelled, FileAnalysisCodes.RequesterUnavailable)
            : (ContextJobStatus.Cancelled, FileAnalysisCodes.FileChanged);
    }

    private FileAnalysisService CreateService(OdysseyContext context, IFileAnalysisProvider provider) =>
        FileAnalysisTestHarness.Create(
            context, provider, TestContextFactory.ContactLookup(journal), new FileAnalysisOptions(), settingsLookup,
            secrets, time);

    private FileAnalysisExtractionRunner Runner(OdysseyContext context, IFileAnalysisProvider provider,
        IFileAnalysisPromptTemplate? template = null) =>
        FileAnalysisTestHarness.Runner(context, provider, settingsLookup, secrets, time, template);

    private async Task<FileAnalysisRunResult> RunAsync(
        OdysseyContext context, IFileAnalysisProvider provider, Guid jobId, IFileAnalysisPromptTemplate? template = null)
    {
        Assert.True(await FileAnalysisTestHarness.Queue(context, time: time).TryClaimAsync(jobId, CancellationToken.None));
        return await Runner(context, provider, template).RunAsync(jobId, CancellationToken.None);
    }

    private AnalyzeFileRequest Consent() => new()
    {
        ConsentAcknowledged = true,
        ConsentText = "I consent to sending the complete file.",
        ConsentMethod = "Per-document checkbox",
        DisclosureVersion = FileAnalysisDisclosureVersion.Compute(settingsLookup.Settings),
    };

    private static FileAnalysisJob NewJob(
        Guid accountFileId, ContextJobStatus status, DateTime? queuedAt, DateTime? startedAt = null, DateTime? leaseUntil = null) => new()
    {
        AccountFileId = accountFileId,
        Status = status,
        QueuedAt = queuedAt,
        StartedAt = startedAt,
        LeaseUntilUtc = leaseUntil,
        RunId = status == ContextJobStatus.Running ? Guid.NewGuid() : null,
        AnalyzerProvider = AnalyzerProvider.Claude,
        RequestedByUserId = FileAnalysisTestHarness.RequesterId,
        ConsentRecorded = true,
    };

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static async Task<(Guid AccountId, Guid FileId)> SeedStatementAsync(OdysseyContext context, long sizeBytes = 3)
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
            UploadedByUserId = FileAnalysisTestHarness.RequesterId,
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = sizeBytes,
            Sha256Hash = Sha256Of(blob.Content),
            FileBlobId = blob.Id,
            FileBlob = blob,
            UploadedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        context.AccountFiles.Add(new AccountFile
        {
            AccountId = account.AccountId,
            FileMetadataId = fileId,
            AttachedByUserId = FileAnalysisTestHarness.RequesterId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = Context.AccountFileType.Statement,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (account.AccountId, fileId);
    }

    private static ExtractedTransaction Extracted(string description) =>
        new(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), null, description, null, null, -4.50m, "USD",
            null, null, null, null, null, null);

    /// <summary>Records what the runner sent; <see cref="OnCall"/> runs inside the call (a race, a shutdown).</summary>
    private sealed class RecordingProvider(params ExtractedTransaction[] returns) : IFileAnalysisProvider
    {
        public int CallCount { get; private set; }

        public int? LastMaxTokens { get; private set; }

        public string? LastAddition { get; private set; }

        public string? LastPrompt { get; private set; }

        public Action<CancellationToken>? OnCall { get; set; }

        public Task<List<ExtractedTransaction>> ExtractTransactionsAsync(
            byte[] fileContent, string contentType, string accountCurrencyCode, string promptTemplate,
            FileAnalysisTarget target, int maxTokens, string? promptAddition, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMaxTokens = maxTokens;
            LastAddition = promptAddition;
            LastPrompt = promptTemplate;
            OnCall?.Invoke(cancellationToken);
            return Task.FromResult(returns.ToList());
        }

        public Task<List<MatchedCandidate>> MatchTransactionsAsync(
            IReadOnlyList<MatchCandidateInput> candidates, IReadOnlyList<VocabularyEntry> contacts,
            IReadOnlyList<VocabularyEntry> tags, FileAnalysisTarget target, int maxTokens, MatchPromptOptions prompt,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<MatchedCandidate>());
    }
}
