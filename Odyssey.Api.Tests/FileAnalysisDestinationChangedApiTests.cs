using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.ApiClient;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Authorization;
using Xunit;
using Context = Odyssey.Context;
using ContextJobStatus = Odyssey.Context.FileAnalysisJobStatus;

namespace Odyssey.Api.Tests;

/// <summary>
/// The match refusal for an analysis whose provider settings changed since analyze (issue #433), on
/// the wire: a <c>409</c> whose <c>code</c> is the literal the client keys on (issue #434), and
/// nothing reaches the provider. Pins that the server's constant and the client's are one string.
/// </summary>
public class FileAnalysisDestinationChangedApiTests
{
    private static readonly string[] Claims = [PermissionClaims.FileAnalysisRead, PermissionClaims.FileAnalysisCreate];

    [Fact]
    public void TheServerAndClientCodes_AreTheSameLiteral()
    {
        Assert.Equal(FileAnalysisDestinationChangedException.DestinationChangedCode,
            ApiProblemExtensions.FileAnalysisDestinationChangedCode);
    }

    [Fact]
    public async Task AMatchUnderAChangedProcessor_Is409_WithTheDestinationChangedCode()
    {
        await using var factory = new OdysseyApiFactory(Claims);
        await factory.EnableFileAnalysisAsync();
        var (jobId, _) = await SeedAsync(factory, job => job.ProcessorInForce = "A processor that is no longer configured");
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/file-analysis/{jobId}/match", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.True(problem.IsFileAnalysisDestinationChanged());
        Assert.DoesNotContain("no longer configured", problem.Detail ?? string.Empty, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var stored = await context.FileAnalysisJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(Context.FileAnalysisMatchStatus.NotRun, stored.MatchStatus);
    }

    private static async Task<(Guid JobId, Guid CandidateId)> SeedAsync(
        OdysseyApiFactory factory, Action<FileAnalysisJob> configureJob)
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
            RequestedByUserId = TestAuthHandler.DefaultActorUserId,
            Status = ContextJobStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            AnalyzerProvider = Context.AnalyzerProvider.Claude,
            ConsentRecorded = true,
        };
        configureJob(job);
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

}
