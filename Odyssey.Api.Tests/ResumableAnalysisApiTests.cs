using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AccountType = Odyssey.Context.AccountType;
using AccountFileType = Odyssey.Context.AccountFileType;
using AnalyzerProvider = Odyssey.Context.AnalyzerProvider;
using JobStatus = Odyssey.Context.FileAnalysisJobStatus;
using ReviewStatus = Odyssey.Context.CandidateTransactionReviewStatus;

namespace Odyssey.Api.Tests;

/// <summary>
/// Contract coverage for the account-scoped resumable-reviews endpoint
/// (<c>GET /api/accounts/{accountId}/files/analysis/resumable</c>): the <c>file-analysis.read</c>
/// claim gate (403 without it), the enabled happy path (200 + the latest resumable summary per file),
/// the account-not-found path (404), and the data-minimisation guarantee at the wire (the response
/// carries counts only — never candidate free-text). The 503-when-disabled short-circuit is proven in
/// <see cref="FileAnalysisFeatureFlagTests"/>.
/// </summary>
public class ResumableAnalysisApiTests
{
    // The kill switch is a SETTINGS ROW since issue #439, not configuration — so enabling it means
    // writing that row rather than passing a config key. It ships false, hence the explicit flip.
    private static async Task<OdysseyApiFactory> EnabledFactoryAsync(params string[] permissions)
    {
        var factory = new OdysseyApiFactory(permissions);
        await factory.EnableFileAnalysisAsync();
        return factory;
    }

    [Fact]
    public async Task GetResumable_WithoutFileAnalysisReadClaim_ReturnsForbidden()
    {
        // Authorization runs before the feature-flag check, so the claim gate holds regardless of the
        // flag — a holder of accounts.read but not file-analysis.read is stopped at 403.
        await using var factory = await EnabledFactoryAsync(PermissionClaims.AccountsRead);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/accounts/{Guid.NewGuid()}/files/analysis/resumable");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetResumable_EnabledUnknownAccount_ReturnsNotFound()
    {
        await using var factory = await EnabledFactoryAsync(PermissionClaims.FileAnalysisRead);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/accounts/{Guid.NewGuid()}/files/analysis/resumable");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetResumable_EnabledWithResumableJob_Returns200WithSummary()
    {
        await using var factory = await EnabledFactoryAsync(PermissionClaims.FileAnalysisRead);
        using var client = factory.CreateClient();

        var (accountId, fileId, jobId) = await SeedResumableJobAsync(factory, pending: 4, reviewed: 2);

        var response = await client.GetAsync($"/api/accounts/{accountId}/files/analysis/resumable");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summaries = await response.Content.ReadFromJsonAsync<List<ResumableAnalysisSummary>>();
        var summary = Assert.Single(summaries!);
        Assert.Equal(fileId, summary.FileId);
        Assert.Equal(jobId, summary.AnalysisJobId);
        Assert.Equal(Odyssey.Dtos.Finance.FileAnalysisJobStatus.Completed, summary.Status);
        Assert.Equal(6, summary.CandidateCount);
        Assert.Equal(4, summary.PendingCount);
    }

    [Fact]
    public async Task GetResumable_ResponseBody_CarriesNoCandidateFreeText()
    {
        await using var factory = await EnabledFactoryAsync(PermissionClaims.FileAnalysisRead);
        using var client = factory.CreateClient();

        // A candidate whose description/merchant are distinctive sentinels — the data-minimisation
        // guarantee is that none of them ever reach the summary response.
        var (accountId, _, _) = await SeedResumableJobAsync(
            factory, pending: 1, reviewed: 0,
            description: "SENTINEL_DESC_ZZZ", merchant: "SENTINEL_MERCHANT_ZZZ");

        var response = await client.GetAsync($"/api/accounts/{accountId}/files/analysis/resumable");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("SENTINEL_DESC_ZZZ", body);
        Assert.DoesNotContain("SENTINEL_MERCHANT_ZZZ", body);
    }

    // Seeds account → statement file → completed analysis job → candidates (pending + reviewed),
    // returning the account, file and job ids.
    private static async Task<(Guid accountId, Guid fileId, Guid jobId)> SeedResumableJobAsync(
        OdysseyApiFactory factory, int pending, int reviewed,
        string description = "Candidate", string? merchant = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var account = new Account
        {
            Name = "Everyday Checking",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        db.Accounts.Add(account);

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        db.FileBlob.Add(blob);
        var file = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = "seed-user",
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = "hash",
            FileBlobId = blob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        };
        db.FileMetadata.Add(file);
        await db.SaveChangesAsync();

        var accountFile = new AccountFile
        {
            AccountId = account.AccountId,
            FileMetadataId = file.Id,
            AttachedByUserId = "seed-user",
            AttachedAtUtc = DateTime.UtcNow,
            FileType = AccountFileType.Statement,
        };
        db.AccountFiles.Add(accountFile);
        await db.SaveChangesAsync();

        var job = new FileAnalysisJob
        {
            AccountFileId = accountFile.Id,
            RequestedByUserId = "seed-user",
            Status = JobStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow.AddSeconds(5),
            AnalyzerProvider = AnalyzerProvider.Claude,
            ConsentRecorded = true,
        };
        db.FileAnalysisJobs.Add(job);
        await db.SaveChangesAsync();

        void AddCandidate(ReviewStatus status) => db.FileAnalysisCandidateTransactions.Add(
            new FileAnalysisCandidateTransaction
            {
                AnalysisJobId = job.Id,
                TransactionDate = DateTime.UtcNow,
                Description = description,
                Merchant = merchant,
                Amount = -1m,
                Currency = "USD",
                ReviewStatus = status,
            });
        for (var i = 0; i < pending; i++) AddCandidate(ReviewStatus.Pending);
        for (var i = 0; i < reviewed; i++) AddCandidate(ReviewStatus.Accepted);
        await db.SaveChangesAsync();

        return (account.AccountId, file.Id, job.Id);
    }
}
