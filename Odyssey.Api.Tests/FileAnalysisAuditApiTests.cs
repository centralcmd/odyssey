using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AccountType = Odyssey.Context.AccountType;
using AccountFileType = Odyssey.Context.AccountFileType;
using AnalyzerProvider = Odyssey.Context.AnalyzerProvider;
using JobStatus = Odyssey.Context.FileAnalysisJobStatus;

namespace Odyssey.Api.Tests;

/// <summary>
/// Contract coverage for the admin-only external-AI analysis audit trail
/// (<c>GET /api/file-analysis/audit</c>): it is gated by the dedicated <c>file-analysis.audit</c>
/// claim (Admin-only, intentionally not <c>users.read</c>), stays readable even when the analysis
/// feature is disabled (the audit record outlives the toggle), and the controller enriches each
/// row's initiating user (name/email) from the identity store.
/// </summary>
public class FileAnalysisAuditApiTests
{
    [Fact]
    public async Task GetAuditLog_WithoutAuditClaim_ReturnsForbidden()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileAnalysisRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/file-analysis/audit");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAuditLog_WithUsersReadButNotAuditClaim_ReturnsForbidden()
    {
        // The audit boundary is deliberately separate from users.read — holding users.read is not enough.
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/file-analysis/audit");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAuditLog_WithAuditClaim_ReturnsEmptyListWhenNoTransfers()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileAnalysisAudit]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/file-analysis/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = (await response.Content.ReadFromJsonAsync<PagedResult<FileAnalysisAuditEntry>>())?.Items;
        Assert.NotNull(entries);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetAuditLog_EnrichesInitiatingUserFromIdentityStore()
    {
        // The real auditor is an Admin, who holds both file-analysis.audit and users.read; the name is
        // resolved via the shared resolver (#316) and the dedicated Email column is preserved unchanged.
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileAnalysisAudit, PermissionClaims.UsersRead]);
        using var client = factory.CreateClient();

        const string userId = "audit-initiator-id";
        await SeedUserAsync(factory, userId, "mara@odyssey.app");
        await SeedTransferAsync(factory, userId);

        var response = await client.GetAsync("/api/file-analysis/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = (await response.Content.ReadFromJsonAsync<PagedResult<FileAnalysisAuditEntry>>())?.Items;
        var entry = Assert.Single(entries!);
        Assert.Equal(userId, entry.RequestedByUserId);
        Assert.Equal("mara@odyssey.app", entry.User?.Name);
        Assert.Equal("mara@odyssey.app", entry.User?.Email);
        Assert.Equal("statement.pdf", entry.File?.Name);
        Assert.Equal("Everyday Checking", entry.Account?.Name);
        Assert.True(entry.ConsentRecorded);
    }

    [Fact]
    public async Task GetAuditLog_UnknownInitiator_NameIsUnknownUser_EmailNull()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileAnalysisAudit, PermissionClaims.UsersRead]);
        using var client = factory.CreateClient();

        // A transfer whose initiating user no longer exists in the identity store: the resolver returns
        // "Unknown user" for the name (never a raw GUID — #316 §9), and the Email column is null.
        await SeedTransferAsync(factory, "deleted-user-id");

        var response = await client.GetAsync("/api/file-analysis/audit");

        var entries = (await response.Content.ReadFromJsonAsync<PagedResult<FileAnalysisAuditEntry>>())?.Items;
        var entry = Assert.Single(entries!);
        Assert.Equal("Unknown user", entry.User?.Name);
        Assert.Null(entry.User?.Email);
    }

    private static async Task SeedUserAsync(OdysseyApiFactory factory, string id, string email)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await userManager.CreateAsync(
            new ApplicationUser { Id = id, UserName = email, Email = email, EmailConfirmed = true },
            "Password123!Safe");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    // Seeds the full account → file → analysis-job chain a completed transfer needs.
    private static async Task SeedTransferAsync(OdysseyApiFactory factory, string requestedByUserId)
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
            AccountNumber = "••4471",
        };
        db.Accounts.Add(account);

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        db.FileBlob.Add(blob);
        var file = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = requestedByUserId,
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 318_000,
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
            AttachedByUserId = requestedByUserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = AccountFileType.Statement,
        };
        db.AccountFiles.Add(accountFile);
        await db.SaveChangesAsync();

        db.FileAnalysisJobs.Add(new FileAnalysisJob
        {
            AccountFileId = accountFile.Id,
            RequestedByUserId = requestedByUserId,
            Status = JobStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow.AddSeconds(5),
            AnalyzerProvider = AnalyzerProvider.Claude,
            AnalyzerModel = "claude-opus-4-7",
            ConsentRecorded = true,
            ConsentText = "I consent.",
            ConsentMethod = "Per-document checkbox",
            LawfulBasis = "Consent · GDPR Art. 6(1)(a)",
        });
        await db.SaveChangesAsync();
    }
}
