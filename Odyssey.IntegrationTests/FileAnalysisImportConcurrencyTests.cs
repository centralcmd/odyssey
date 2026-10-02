using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextJobStatus = Odyssey.Context.FileAnalysisJobStatus;
using ContextReviewStatus = Odyssey.Context.CandidateTransactionReviewStatus;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Two import requests racing on one candidate (issue #237). Both read it as Pending; the
/// <c>ReviewStatus</c> concurrency token makes the second <c>UPDATE</c> match no row, and because the
/// candidate update and the ledger insert share one <c>SaveChangesAsync</c> the loser's transaction
/// rolls back with it. The InMemory tier sees the conflict but not the rollback — it has no
/// transactions — so the "exactly one ledger row" half lives here.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class FileAnalysisImportConcurrencyTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_file_analysis_import";
    private const string UserId = "import-user";

    [SkippableFact]
    public async Task The_losing_import_is_a_conflict_and_writes_no_ledger_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid jobId, candidateId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, UserId);
            (jobId, candidateId) = await SeedJobAsync(context);
        }

        await using var loser = NewContext();
        // The losing request has read the candidate as Pending before the winner commits.
        Assert.Equal(ContextReviewStatus.Pending,
            (await loser.FileAnalysisCandidateTransactions.SingleAsync(c => c.Id == candidateId)).ReviewStatus);

        await using (var winner = NewContext())
        {
            var result = await NewService(winner).ImportCandidatesAsync(jobId, Request(candidateId), UserId);
            Assert.Equal(1, result.Imported);
        }

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            NewService(loser).ImportCandidatesAsync(jobId, Request(candidateId), UserId));

        await using var verify = NewContext();
        Assert.Equal(1, await verify.Transactions.CountAsync());
        Assert.Equal(ContextReviewStatus.Accepted,
            (await verify.FileAnalysisCandidateTransactions.SingleAsync(c => c.Id == candidateId)).ReviewStatus);
    }

    /// <summary>
    /// The match step's fallback: an import commits after the match run's pre-save re-read but before
    /// its save, so the save itself conflicts. MariaDB rolls the failed batch back, the reviewed
    /// candidate is restored as stored — tag links included — and the retry saves the rest.
    /// </summary>
    [SkippableFact]
    public async Task A_match_save_that_conflicts_with_an_import_keeps_the_review_and_saves_the_rest()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid jobId, importedId, otherId, tagId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, UserId);
            (jobId, importedId) = await SeedJobAsync(context);
            otherId = Guid.NewGuid();
            context.FileAnalysisCandidateTransactions.Add(NewCandidate(otherId, jobId));
            var tag = new TransactionTag { Name = "Groceries" };
            context.TransactionTags.Add(tag);
            await context.SaveChangesAsync();
            tagId = tag.TransactionTagId;
        }

        var interceptor = new ImportBeforeMatchSave(() => NewContext(), jobId, importedId);
        await using (var matching = NewContext(interceptor))
        {
            var provider = new NoopAnalysisProvider(tags =>
                [new MatchedCandidate(0, null, null, [tags[0].Ref], 0.9m), new MatchedCandidate(1, null, null, [tags[0].Ref], 0.9m)]);
            var job = await NewService(matching, provider).MatchAsync(jobId);

            Assert.True(interceptor.Fired);
            Assert.Equal(Odyssey.Dtos.Finance.FileAnalysisMatchStatus.Completed, job.MatchStatus);
        }

        await using var verify = NewContext();
        Assert.Equal(ContextReviewStatus.Accepted,
            (await verify.FileAnalysisCandidateTransactions.SingleAsync(c => c.Id == importedId)).ReviewStatus);
        var links = await verify.FileAnalysisCandidateTags.ToListAsync();
        var link = Assert.Single(links);
        Assert.Equal(otherId, link.CandidateTransactionId);
        Assert.Equal(tagId, link.TransactionTagId);
        Assert.Equal(1, await verify.Transactions.CountAsync());
    }

    private static ImportRequest Request(Guid candidateId) =>
        new ImportRequest { Candidates = [new ImportCandidateRequest { CandidateId = candidateId }] };

    private static async Task<(Guid JobId, Guid CandidateId)> SeedJobAsync(OdysseyContext context)
    {
        var accountId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var accountFileId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Checking",
            Description = "Seed",
            Opened = DateTime.UtcNow,
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        });
        context.FileBlob.Add(new FileBlob { Id = blobId, Content = [1, 2, 3] });
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            UploadedByUserId = UserId,
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blobId,
        });
        context.AccountFiles.Add(new AccountFile
        {
            Id = accountFileId,
            AccountId = accountId,
            FileMetadataId = fileId,
            AttachedByUserId = UserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = Odyssey.Context.AccountFileType.Statement,
        });
        context.FileAnalysisJobs.Add(new FileAnalysisJob
        {
            Id = jobId,
            AccountFileId = accountFileId,
            RequestedByUserId = UserId,
            Status = ContextJobStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            AnalyzerProvider = Odyssey.Context.AnalyzerProvider.Claude,
            ConsentRecorded = true,
        });
        context.FileAnalysisCandidateTransactions.Add(NewCandidate(candidateId, jobId));
        await context.SaveChangesAsync();
        return (jobId, candidateId);
    }

    private static FileAnalysisCandidateTransaction NewCandidate(Guid id, Guid jobId) => new()
    {
        Id = id,
        AnalysisJobId = jobId,
        TransactionDate = DateTime.UtcNow.AddDays(-1),
        Description = "Groceries",
        Merchant = "Store",
        CategoryHint = "Food",
        Amount = -42.10m,
        Currency = "USD",
        ReviewStatus = ContextReviewStatus.Pending,
    };

    private static FileAnalysisService NewService(OdysseyContext context, NoopAnalysisProvider? provider = null) =>
        new(context, provider ?? new NoopAnalysisProvider(), new ContactLookup(context),
            Options.Create(new FileAnalysisOptions { Enabled = true }),
            new EnabledSettingsLookup(), NullLogger<FileAnalysisService>.Instance);

    private async Task MigrateAsync()
    {
        await using (var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options))
        {
            await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
            await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString));
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new OdysseyContext(builder.Options);
    }

    /// <summary>
    /// Commits an import of one candidate from a separate context just before the match run's own
    /// save — after its pre-save re-read — so the save meets the concurrency token rather than the
    /// re-read catching it first.
    /// </summary>
    private sealed class ImportBeforeMatchSave(Func<OdysseyContext> newContext, Guid jobId, Guid candidateId)
        : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var touchesCandidates = eventData.Context!.ChangeTracker.Entries<FileAnalysisCandidateTag>()
                .Any(e => e.State == EntityState.Added);
            if (!Fired && touchesCandidates)
            {
                Fired = true;
                await using var other = newContext();
                await NewService(other).ImportCandidatesAsync(jobId, Request(candidateId), UserId, cancellationToken);
            }

            return result;
        }
    }

    // Import never calls the provider; the match test supplies its tag matches.
    private sealed class NoopAnalysisProvider(
        Func<IReadOnlyList<VocabularyEntry>, List<MatchedCandidate>>? match = null) : IFileAnalysisProvider
    {
        public Task<List<ExtractedTransaction>> ExtractTransactionsAsync(
            byte[] fileContent, string contentType, string accountCurrencyCode,
            string promptTemplate, FileAnalysisTarget target, int maxTokens,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<ExtractedTransaction>());

        public Task<List<MatchedCandidate>> MatchTransactionsAsync(
            IReadOnlyList<MatchCandidateInput> candidates,
            IReadOnlyList<VocabularyEntry> contactVocabulary,
            IReadOnlyList<VocabularyEntry> tagVocabulary,
            FileAnalysisTarget target,
            int maxTokens,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(match?.Invoke(tagVocabulary) ?? []);
    }

    private sealed class EnabledSettingsLookup : IFileAnalysisSettingsLookup
    {
        public Task<FileAnalysisSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileAnalysisSettings(
                "Anthropic", "United States", "Consent · GDPR Art. 6(1)(a)",
                "https://www.anthropic.com/legal/privacy", 90, 0.60m,
                MaxTokens: 8096, MatchMaxVocabulary: 500, MatchTimeoutSeconds: 60,
                Model: "claude-sonnet-5", BaseUrl: "https://api.anthropic.com", IsDegraded: false));

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
