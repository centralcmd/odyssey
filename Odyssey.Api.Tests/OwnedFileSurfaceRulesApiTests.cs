using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #287 H3 — the five file-attachment surfaces answer one set of rules, asserted per surface so a
/// surface that drifts fails by name. Before the shared <c>OwnedFileLinks</c> they disagreed: a
/// duplicate attach was a silent no-op on accounts and tax statements and a re-type on transactions;
/// detaching an unattached file was a <c>204</c> on accounts and transactions; and attach answered
/// <c>204</c> on two surfaces and an empty <c>201</c> on three.
/// </summary>
public sealed class OwnedFileSurfaceRulesApiTests
{
    public enum Surface
    {
        Account,
        Transaction,
        TaxStatement,
        Contract,
        Property,
    }

    public static TheoryData<Surface> Surfaces() =>
        [Surface.Account, Surface.Transaction, Surface.TaxStatement, Surface.Contract, Surface.Property];

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Attach_Answers201WithTheCreatedLink(Surface surface)
    {
        await using var factory = new OdysseyApiFactory(RolePermissions.AllClaims);
        var seeded = await SeedAsync(factory, "application/pdf");
        using var client = factory.CreateClient();

        var response = await AttachAsync(client, surface, seeded);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            seeded.FileId,
            body.RootElement.GetProperty("fileMetadata").GetProperty("id").GetGuid());
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task AttachingTheSameFileTwice_IsAConflict(Surface surface)
    {
        await using var factory = new OdysseyApiFactory(RolePermissions.AllClaims);
        var seeded = await SeedAsync(factory, "application/pdf");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await AttachAsync(client, surface, seeded)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AttachAsync(client, surface, seeded)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task DetachingAnUnattachedFile_IsNotFound(Surface surface)
    {
        await using var factory = new OdysseyApiFactory(RolePermissions.AllClaims);
        var seeded = await SeedAsync(factory, "application/pdf");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await AttachAsync(client, surface, seeded)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(FileRoute(surface, seeded))).StatusCode);

        // The link is gone, so a second detach is a 404 — never a silent 204.
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(FileRoute(surface, seeded))).StatusCode);
    }

    /// <summary>
    /// Issue #287 H4. Tax statements now attach through the shared document check, so the allow-list is
    /// <c>DocumentContentTypes.Allowed</c> — the same set contracts and properties use.
    /// </summary>
    [Theory]
    [InlineData(Surface.TaxStatement)]
    [InlineData(Surface.Contract)]
    [InlineData(Surface.Property)]
    public async Task DocumentSurfaces_RefuseAContentTypeOffTheSharedAllowList(Surface surface)
    {
        await using var factory = new OdysseyApiFactory(RolePermissions.AllClaims);
        var seeded = await SeedAsync(factory, "text/plain");
        using var client = factory.CreateClient();

        Assert.False(DocumentContentTypes.Allowed.Contains("text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, (await AttachAsync(client, surface, seeded)).StatusCode);
    }

    /// <summary>
    /// Issue #287 H4: the tax-statement controller keeps no private copy of the allow-list or of the
    /// download handler — a copy is a security control that a change to the shared one silently skips.
    /// </summary>
    [Fact]
    public void TaxStatementController_UsesTheSharedDocumentHelpers()
    {
        var source = RepositoryRoot.ReadAllText(
            Path.Combine("Odyssey.Api", "Controllers", "TaxStatementController.cs"));

        Assert.Contains("ValidateAttachableDocumentAsync(", source, StringComparison.Ordinal);
        Assert.Contains("StreamDocumentAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"application/pdf\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("XContentTypeOptions", source, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> AttachAsync(HttpClient client, Surface surface, Seeded seeded) =>
        surface switch
        {
            Surface.Account => client.PostAsJsonAsync(
                $"/api/accounts/{seeded.AccountId}/files", new AttachAccountFileRequest(seeded.FileId)),
            Surface.Transaction => client.PostAsJsonAsync(
                $"/api/transactions/{seeded.TransactionId}/files", new AttachTransactionFileRequest(seeded.FileId)),
            Surface.TaxStatement => client.PostAsJsonAsync(
                $"/api/tax-statements/{seeded.TaxStatementId}/files", new AttachTaxStatementFileRequest(seeded.FileId)),
            Surface.Contract => client.PostAsJsonAsync(
                $"/api/contracts/{seeded.ContractId}/files", new AttachContractFileRequest { FileMetadataId = seeded.FileId }),
            Surface.Property => client.PostAsJsonAsync(
                $"/api/properties/{seeded.PropertyId}/files", new AttachPropertyFileRequest { FileMetadataId = seeded.FileId }),
            _ => throw new ArgumentOutOfRangeException(nameof(surface)),
        };

    private static string FileRoute(Surface surface, Seeded seeded) => surface switch
    {
        Surface.Account => $"/api/accounts/{seeded.AccountId}/files/{seeded.FileId}",
        Surface.Transaction => $"/api/transactions/{seeded.TransactionId}/files/{seeded.FileId}",
        Surface.TaxStatement => $"/api/tax-statements/{seeded.TaxStatementId}/files/{seeded.FileId}",
        Surface.Contract => $"/api/contracts/{seeded.ContractId}/files/{seeded.FileId}",
        Surface.Property => $"/api/properties/{seeded.PropertyId}/files/{seeded.FileId}",
        _ => throw new ArgumentOutOfRangeException(nameof(surface)),
    };

    private sealed record Seeded(
        Guid AccountId, Guid TransactionId, Guid TaxStatementId, Guid ContractId, Guid PropertyId, Guid FileId);

    private static async Task<Seeded> SeedAsync(OdysseyApiFactory factory, string contentType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        var account = new Account
        {
            Name = "Everyday Checking",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        db.Accounts.Add(account);

        var taxStatement = new TaxStatement
        {
            Name = "2024 assessment",
            FiscalYear = 2024,
            StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.TaxStatements.Add(taxStatement);

        var contract = new Contract
        {
            Name = "Maple St lease",
            Type = Odyssey.Context.ContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Contracts.Add(contract);

        var property = new Property
        {
            Name = "Cabin",
            Description = "Cabin",
            Type = PropertyType.RealEstate,
            CurrencyCode = "USD",
            RealEstateDetails = new RealEstateDetails { Kind = RealEstateKind.Cabin },
        };
        db.Properties.Add(property);

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        db.FileBlob.Add(blob);
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            FileName = "document.bin",
            ContentType = contentType,
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            FileBlobId = blob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        };
        db.FileMetadata.Add(metadata);
        await db.SaveChangesAsync();

        var transaction = new Transaction
        {
            AccountId = account.AccountId,
            Amount = 12.5m,
            CurrencyCode = "USD",
            Description = "Lunch",
            TimeStamp = DateTime.UtcNow,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        return new Seeded(
            account.AccountId, transaction.TransactionId, taxStatement.TaxStatementId,
            contract.ContractId, property.PropertyId, metadata.Id);
    }
}
