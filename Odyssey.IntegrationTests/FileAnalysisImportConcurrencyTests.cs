using Microsoft.EntityFrameworkCore;
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

    private static ImportRequest Request(Guid candidateId) =>
        new([new ImportCandidateRequest(candidateId, null, null, null, null)]);

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
        context.FileAnalysisCandidateTransactions.Add(new FileAnalysisCandidateTransaction
        {
            Id = candidateId,
            AnalysisJobId = jobId,
            TransactionDate = DateTime.UtcNow.AddDays(-1),
            Description = "Groceries",
            Amount = -42.10m,
            Currency = "USD",
            ReviewStatus = ContextReviewStatus.Pending,
        });
        await context.SaveChangesAsync();
        return (jobId, candidateId);
    }

    private static FileAnalysisService NewService(OdysseyContext context) =>
        new(context, new NoopAnalysisProvider(), new ContactLookup(context),
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

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    // Import never calls the provider.
    private sealed class NoopAnalysisProvider : IFileAnalysisProvider
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
            Task.FromResult(new List<MatchedCandidate>());
    }

    // Import reads only the kill switch.
    private sealed class EnabledSettingsLookup : IFileAnalysisSettingsLookup
    {
        public Task<FileAnalysisSettings> GetAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Import reads no file-analysis setting.");

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
