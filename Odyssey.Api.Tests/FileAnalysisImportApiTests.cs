using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using Context = Odyssey.Context;
using ContextJobStatus = Odyssey.Context.FileAnalysisJobStatus;
using ContextReviewStatus = Odyssey.Context.CandidateTransactionReviewStatus;

namespace Odyssey.Api.Tests;

/// <summary>
/// The two whole-request refusals the import gained in issue #237, driven through the exception
/// handler: a closed or archived account is a <c>400</c>, and a candidate another request reviewed
/// between this one's read and its save is a <c>409</c>. The per-candidate outcomes are covered by
/// <c>FileAnalysisServiceTests</c>; the real-engine rollback by <c>FileAnalysisImportConcurrencyTests</c>.
/// </summary>
public class FileAnalysisImportApiTests
{
    private static readonly string[] Claims = [PermissionClaims.FileAnalysisRead, PermissionClaims.FileAnalysisImport];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Import_into_a_closed_or_archived_account_is_a_400(bool closed)
    {
        await using var factory = new OdysseyApiFactory(Claims);
        await factory.EnableFileAnalysisAsync();
        var (jobId, candidateId) = await SeedAsync(factory, account =>
        {
            if (closed)
                account.Closed = DateTime.UtcNow;
            else
                account.Archived = DateTime.UtcNow;
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/file-analysis/{jobId}/import", Request(candidateId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("closed or archived", problem!.Detail, StringComparison.Ordinal);
        await AssertNothingImportedAsync(factory, candidateId);
    }

    [Fact]
    public async Task Import_of_a_candidate_reviewed_by_a_concurrent_request_is_a_409()
    {
        var interceptor = new ReviewElsewhereBeforeSave();
        await using var factory = new OdysseyApiFactory(
            Claims, configureServices: services =>
                services.ConfigureDbContext<OdysseyContext>(options => options.AddInterceptors(interceptor)));
        await factory.EnableFileAnalysisAsync();
        var (jobId, candidateId) = await SeedAsync(factory);
        interceptor.Armed = true;
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/file-analysis/{jobId}/import", Request(candidateId));

        Assert.True(interceptor.Fired);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Import_succeeds_once_and_reports_the_retry_as_a_failure()
    {
        await using var factory = new OdysseyApiFactory(Claims);
        await factory.EnableFileAnalysisAsync();
        var (jobId, candidateId) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var first = await (await client.PostAsJsonAsync($"/api/file-analysis/{jobId}/import", Request(candidateId)))
            .Content.ReadFromJsonAsync<ImportResponse>();
        var retry = await client.PostAsJsonAsync($"/api/file-analysis/{jobId}/import", Request(candidateId));

        Assert.Equal(1, first!.Imported);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<ImportResponse>();
        Assert.Equal(0, body!.Imported);
        Assert.Equal(candidateId, Assert.Single(body.Failures).CandidateId);
    }

    private static ImportRequest Request(Guid candidateId) =>
        new ImportRequest { Candidates = [new ImportCandidateRequest { CandidateId = candidateId }] };

    private static async Task AssertNothingImportedAsync(OdysseyApiFactory factory, Guid candidateId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.Transactions.ToListAsync());
        Assert.Equal(ContextReviewStatus.Pending,
            (await context.FileAnalysisCandidateTransactions.SingleAsync(c => c.Id == candidateId)).ReviewStatus);
    }

    private static async Task<(Guid JobId, Guid CandidateId)> SeedAsync(
        OdysseyApiFactory factory, Action<Account>? configureAccount = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        if (!await context.Currencies.AnyAsync(c => c.CurrencyCode == "USD"))
            context.Currencies.Add(new Currency { CurrencyCode = "USD", Name = "US Dollar", MinorUnits = 2, Symbol = "$" });

        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = "Checking",
            Description = "Seed",
            Opened = DateTime.UtcNow,
            AccountType = Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        configureAccount?.Invoke(account);
        context.Accounts.Add(account);

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var fileId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = "hash",
            FileBlobId = blob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        });
        var accountFile = new AccountFile
        {
            Id = Guid.NewGuid(),
            AccountId = account.AccountId,
            FileMetadataId = fileId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = Context.AccountFileType.Statement,
        };
        context.AccountFiles.Add(accountFile);
        var job = new FileAnalysisJob
        {
            Id = Guid.NewGuid(),
            AccountFileId = accountFile.Id,
            RequestedByUserId = "user-1",
            Status = ContextJobStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            AnalyzerProvider = Context.AnalyzerProvider.Claude,
            ConsentRecorded = true,
        };
        context.FileAnalysisJobs.Add(job);
        var candidate = new FileAnalysisCandidateTransaction
        {
            AnalysisJobId = job.Id,
            TransactionDate = DateTime.UtcNow.AddDays(-1),
            Description = "Groceries",
            Amount = -42.10m,
            Currency = "USD",
        };
        context.FileAnalysisCandidateTransactions.Add(candidate);
        await context.SaveChangesAsync();
        return (job.Id, candidate.Id);
    }

    /// <summary>
    /// Makes the race deterministic: just before the import's save, a second context marks the same
    /// candidate Accepted in the store, which is what a concurrent request committing first looks like.
    /// The import's tracked copy still says Pending, so its <c>WHERE ReviewStatus = Pending</c> misses.
    /// </summary>
    private sealed class ReviewElsewhereBeforeSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            var accepting = context.ChangeTracker.Entries<FileAnalysisCandidateTransaction>()
                .Where(e => e.State == EntityState.Modified && e.Entity.ReviewStatus == ContextReviewStatus.Accepted)
                .Select(e => e.Entity.Id)
                .ToList();

            if (Armed && !Fired && accepting.Count > 0)
            {
                Fired = true;
                var options = (DbContextOptions<OdysseyContext>)context.GetService<IDbContextOptions>();
                await using var other = new OdysseyContext(options);
                foreach (var candidate in await other.FileAnalysisCandidateTransactions
                             .Where(c => accepting.Contains(c.Id)).ToListAsync(cancellationToken))
                {
                    candidate.ReviewStatus = ContextReviewStatus.Accepted;
                }

                await other.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }
}
