using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContractFileType = Odyssey.Context.ContractFileType;
using TaxStatementFileType = Odyssey.Context.TaxStatementFileType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The other three file surfaces that carried the same bare ids as the transaction and account ones
/// (issue #106, widened on review): contracts, tax statements and policy renewals. Tax statements are
/// the Guest-reachable one — Guest holds <c>taxes.read</c> — so leaving that surface alone would have
/// kept the harvesting primitive open on the very claim the issue's premise rests on.
/// </summary>
public sealed class FinanceFileAttributionModuleApiTests
{
    private const string UploaderUserId = "module-uploader-id";
    private const string UploaderEmail = "module-uploader@example.com";
    private const string DisplayName = "Grace H.";

    [Fact]
    public async Task GetTaxStatement_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TaxesRead]);
        using var client = factory.CreateClient();
        var statementId = await SeedTaxStatementAsync(factory);

        var statement = await client.GetFromJsonAsync<ExistingTaxStatement>($"/api/tax-statements/{statementId}");

        var file = Assert.Single(statement!.Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
        Assert.Equal(UploaderUserId, file.AttachedByUserId);
    }

    [Fact]
    public async Task ListTaxStatements_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TaxesRead]);
        using var client = factory.CreateClient();
        await SeedTaxStatementAsync(factory);

        var page = await client.GetFromJsonAsync<PagedResult<ExistingTaxStatement>>("/api/tax-statements");

        var file = Assert.Single(Assert.Single(page!.Items).Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task GetTaxStatement_WithoutUsersRead_ReturnsNeutralLabelNotEmail()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TaxesRead]);
        using var client = factory.CreateClient();
        var statementId = await SeedTaxStatementAsync(factory, displayName: null);

        var response = await client.GetAsync($"/api/tax-statements/{statementId}");
        var body = await response.Content.ReadAsStringAsync();
        var statement = await response.Content.ReadFromJsonAsync<ExistingTaxStatement>();

        var file = Assert.Single(statement!.Files);
        Assert.Equal(UserDisplayNameResolver.UnknownUser, file.AttachedByName);
        Assert.Equal(UserDisplayNameResolver.UnknownUser, file.FileMetadata.UploadedByName);
        Assert.DoesNotContain(UploaderEmail, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetContract_ResolvesFileAttribution()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.ContractsRead]);
        using var client = factory.CreateClient();
        var contractId = await SeedContractAsync(factory);

        var contract = await client.GetFromJsonAsync<ExistingContract>($"/api/contracts/{contractId}");

        var file = Assert.Single(contract!.Files);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
        Assert.Equal(UploaderUserId, file.AttachedByUserId);
    }

    private static async Task<Guid> SeedTaxStatementAsync(OdysseyApiFactory factory, string? displayName = DisplayName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        SeedUploader(db, displayName);

        var statement = new TaxStatement
        {
            Name = "2025 return",
            FiscalYear = 2025,
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.TaxStatements.Add(statement);
        var metadata = AddFile(db, "return.pdf", "hash-tax");
        await db.SaveChangesAsync();

        db.TaxStatementFiles.Add(new TaxStatementFile
        {
            TaxStatementId = statement.TaxStatementId,
            FileMetadataId = metadata.Id,
            AttachedByUserId = UploaderUserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = TaxStatementFileType.TaxReturn,
        });
        await db.SaveChangesAsync();
        return statement.TaxStatementId;
    }

    private static async Task<Guid> SeedContractAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        SeedUploader(db, DisplayName);

        var contract = new Contract { Name = "Lease", CreatedAtUtc = DateTime.UtcNow };
        db.Contracts.Add(contract);
        var metadata = AddFile(db, "lease.pdf", "hash-contract");
        await db.SaveChangesAsync();

        db.ContractFiles.Add(new ContractFile
        {
            ContractId = contract.ContractId,
            FileMetadataId = metadata.Id,
            AttachedByUserId = UploaderUserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = ContractFileType.Signed,
        });
        await db.SaveChangesAsync();
        return contract.ContractId;
    }

    private static void SeedUploader(OdysseyContext db, string? displayName)
    {
        db.Users.Add(new ApplicationUser
        {
            Id = UploaderUserId,
            UserName = UploaderEmail,
            Email = UploaderEmail,
        });

        if (displayName is not null)
        {
            db.UserProfiles.Add(new UserProfile { UserId = UploaderUserId, DisplayName = displayName });
        }
    }

    private static FileMetadata AddFile(OdysseyContext db, string fileName, string hash)
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        db.FileBlob.Add(blob);
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = UploaderUserId,
            FileName = fileName,
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = hash,
            FileBlobId = blob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        };
        db.FileMetadata.Add(metadata);
        return metadata;
    }
}
