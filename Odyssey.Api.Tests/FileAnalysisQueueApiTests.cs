using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using Context = Odyssey.Context;
using ContextJobStatus = Odyssey.Context.FileAnalysisJobStatus;

namespace Odyssey.Api.Tests;

/// <summary>
/// The queued analyze contract on the wire (issue #444 §6): <c>202</c> with a <c>Location</c> to the job, a
/// <c>409</c> naming the open job, the job read's polling headers, and the run log's refusal of a manual run.
/// </summary>
public class FileAnalysisQueueApiTests
{
    private static readonly string[] Claims = [PermissionClaims.FileAnalysisRead, PermissionClaims.FileAnalysisCreate];

    [Fact]
    public async Task Analyze_Is202_WithALocationToTheJob()
    {
        await using var factory = new OdysseyApiFactory(Claims);
        await factory.EnableFileAnalysisAsync();
        var (accountId, fileId, _) = await SeedStatementAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/accounts/{accountId}/files/{fileId}/analyze", await ConsentAsync(factory));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AnalyzeFileResponse>();
        Assert.NotNull(body);
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/file-analysis/{body.AnalysisJobId}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analyze_WhileAJobIsOpen_Is409_NamingTheOpenJob()
    {
        await using var factory = new OdysseyApiFactory(Claims);
        await factory.EnableFileAnalysisAsync();
        var (accountId, fileId, accountFileId) = await SeedStatementAsync(factory);
        var openId = await SeedJobAsync(factory, accountFileId, ContextJobStatus.Queued);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/accounts/{accountId}/files/{fileId}/analyze", await ConsentAsync(factory));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(FileAnalysisCodes.AnalysisInProgress, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(openId, problem.RootElement.GetProperty(FileAnalysisCodes.AnalysisJobIdExtension).GetGuid());
    }

    [Fact]
    public async Task Analyze_AboveTheCeiling_Is422_WithTheLimit()
    {
        await using var factory = new OdysseyApiFactory(Claims);
        await factory.EnableFileAnalysisAsync();
        var (accountId, fileId, _) = await SeedStatementAsync(factory, FileAnalysisJobTiming.MaxAnalysisFileBytes + 1);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/accounts/{accountId}/files/{fileId}/analyze", await ConsentAsync(factory));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(FileAnalysisCodes.FileTooLargeForAnalysis, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(FileAnalysisJobTiming.MaxAnalysisFileBytes, problem.RootElement.GetProperty(FileAnalysisCodes.LimitBytesExtension).GetInt64());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("file", out _));
    }

    [Fact]
    public async Task ReadingAnOpenJob_AsksTheClientToPoll_AndForbidsCaching()
    {
        // Analysis is never enabled here: the job read answers with the switch off (issue #444 §6).
        await using var factory = new OdysseyApiFactory(Claims);
        var (_, _, accountFileId) = await SeedStatementAsync(factory);
        var jobId = await SeedJobAsync(factory, accountFileId, ContextJobStatus.Queued);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/file-analysis/{jobId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(TimeSpan.FromSeconds(2), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task ReadingAFinishedJob_CarriesNoRetryAfter()
    {
        await using var factory = new OdysseyApiFactory(Claims);
        var (_, _, accountFileId) = await SeedStatementAsync(factory);
        var jobId = await SeedJobAsync(factory, accountFileId, ContextJobStatus.Completed);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/file-analysis/{jobId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task TheQueue_HasNoManualRun()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, .. Claims]);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/background-jobs/{JobType.FileAnalysis}/runs", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JobRunConflictCodes.ManualRunUnsupported, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<AnalyzeFileRequest> ConsentAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IFileAnalysisSettingsLookup>().GetUncachedAsync();
        return new AnalyzeFileRequest
        {
            ConsentAcknowledged = true,
            ConsentText = "I consent to sending the complete file.",
            ConsentMethod = "Per-document checkbox",
            DisclosureVersion = FileAnalysisDisclosureVersion.Compute(settings),
        };
    }

    private static async Task<Guid> SeedJobAsync(OdysseyApiFactory factory, Guid accountFileId, ContextJobStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var job = new FileAnalysisJob
        {
            Id = Guid.NewGuid(),
            AccountFileId = accountFileId,
            RequestedByUserId = TestAuthHandler.DefaultActorUserId,
            Status = status,
            QueuedAt = DateTime.UtcNow,
            CompletedAt = status == ContextJobStatus.Completed ? DateTime.UtcNow : null,
            AnalyzerProvider = Context.AnalyzerProvider.Claude,
            ConsentRecorded = true,
        };
        context.FileAnalysisJobs.Add(job);
        await context.SaveChangesAsync();
        return job.Id;
    }

    private static async Task<(Guid AccountId, Guid FileId, Guid AccountFileId)> SeedStatementAsync(
        OdysseyApiFactory factory, long sizeBytes = 3)
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
            SizeBytes = sizeBytes,
            Sha256Hash = Convert.ToHexStringLower(SHA256.HashData(blob.Content)),
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
        await context.SaveChangesAsync();
        return (account.AccountId, fileId, accountFile.Id);
    }
}
