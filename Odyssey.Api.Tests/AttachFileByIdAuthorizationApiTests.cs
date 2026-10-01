using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #233. Attaching an existing file by id makes that file's bytes downloadable through the
/// record's own read claim, so the record's <c>*.update</c> claim alone must not be enough — otherwise
/// a caller without <c>files.read</c> could name any file id and read it back through the record
/// (a confused deputy). The contract and property surfaces already stacked <c>files.read</c>; these
/// are the three that did not.
/// </summary>
public sealed class AttachFileByIdAuthorizationApiTests
{
    public enum Surface
    {
        Account,
        Transaction,
        TaxStatement,
    }

    public static TheoryData<Surface, string[], HttpStatusCode> Cases() => new()
    {
        { Surface.Account, [PermissionClaims.AccountsUpdate], HttpStatusCode.Forbidden },
        { Surface.Account, [PermissionClaims.FilesRead], HttpStatusCode.Forbidden },
        { Surface.Account, [PermissionClaims.AccountsUpdate, PermissionClaims.FilesRead], HttpStatusCode.Created },

        { Surface.Transaction, [PermissionClaims.TransactionsUpdate], HttpStatusCode.Forbidden },
        { Surface.Transaction, [PermissionClaims.FilesRead], HttpStatusCode.Forbidden },
        { Surface.Transaction, [PermissionClaims.TransactionsUpdate, PermissionClaims.FilesRead], HttpStatusCode.Created },

        { Surface.TaxStatement, [PermissionClaims.TaxesUpdate], HttpStatusCode.Forbidden },
        { Surface.TaxStatement, [PermissionClaims.FilesRead], HttpStatusCode.Forbidden },
        { Surface.TaxStatement, [PermissionClaims.TaxesUpdate, PermissionClaims.FilesRead], HttpStatusCode.Created },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Attach_RequiresRecordUpdateAndFilesRead(Surface surface, string[] claims, HttpStatusCode expected)
    {
        await using var factory = new OdysseyApiFactory(claims);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = surface switch
        {
            Surface.Account => await client.PostAsJsonAsync(
                $"/api/accounts/{seeded.AccountId}/files", new AttachAccountFileRequest(seeded.FileId)),
            Surface.Transaction => await client.PostAsJsonAsync(
                $"/api/transactions/{seeded.TransactionId}/files", new AttachTransactionFileRequest(seeded.FileId)),
            Surface.TaxStatement => await client.PostAsJsonAsync(
                $"/api/tax-statements/{seeded.TaxStatementId}/files", new AttachTaxStatementFileRequest(seeded.FileId)),
            _ => throw new ArgumentOutOfRangeException(nameof(surface)),
        };

        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<(Guid AccountId, Guid TransactionId, Guid TaxStatementId, Guid FileId)> SeedAsync(
        OdysseyApiFactory factory)
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

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        db.FileBlob.Add(blob);
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            FileName = "document.pdf",
            ContentType = "application/pdf",
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

        return (account.AccountId, transaction.TransactionId, taxStatement.TaxStatementId, metadata.Id);
    }
}
