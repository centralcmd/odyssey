using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountFileType = Odyssey.Context.AccountFileType;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// Contract coverage for issue #106: the two finance file surfaces used to return a raw user id with
/// no name attached, which — since Guest holds <c>transactions.read</c>, <c>accounts.read</c> and
/// <c>files.read</c> — was a harvesting primitive for ids the application never names to that caller.
/// These tests pin that both surfaces now carry a label resolved by <see cref="IUserDisplayNameResolver"/>
/// under the CALLER's own claims, that the id is still there (round-tripping needs it), and that a
/// caller whose claims do not license the name gets the resolver's neutral label rather than an email.
/// </summary>
public sealed class FinanceFileAttributionApiTests
{
    private const string UploaderUserId = "uploader-user-id";
    private const string UploaderEmail = "uploader@example.com";

    [Fact]
    public async Task GetTransactionFiles_ResolvesAttacherAndUploaderNames()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory, displayName: "Ada L.");

        var files = await client.GetFromJsonAsync<List<ExistingTransactionFile>>(
            $"/api/transactions/{seeded.TransactionId}/files");

        var file = Assert.Single(files!);
        Assert.Equal("Ada L.", file.AttachedByName);
        Assert.Equal("Ada L.", file.FileMetadata.UploadedByName);

        // The label accompanies the id; it does not replace it (issue #106 Scope).
        Assert.Equal(UploaderUserId, file.AttachedByUserId);
        Assert.Equal(UploaderUserId, file.FileMetadata.UploadedByUserId);
    }

    [Fact]
    public async Task GetTransaction_ResolvesAttachedFileNames()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory, displayName: "Ada L.");

        var transaction = await client.GetFromJsonAsync<ExistingTransaction>(
            $"/api/transactions/{seeded.TransactionId}");

        var file = Assert.Single(transaction!.TransactionFiles);
        Assert.Equal("Ada L.", file.AttachedByName);
        Assert.Equal("Ada L.", file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task ListTransactions_ResolvesAttachedFileNames()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        using var client = factory.CreateClient();
        await SeedAsync(factory, displayName: "Ada L.");

        var transactions = await client.GetPagedItemsAsync<ExistingTransaction>("/api/transactions");

        var file = Assert.Single(Assert.Single(transactions).TransactionFiles);
        Assert.Equal("Ada L.", file.AttachedByName);
        Assert.Equal("Ada L.", file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task GetAccountFiles_ResolvesAttacherAndUploaderNames()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.AccountsRead]);
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory, displayName: "Ada L.");

        var files = await client.GetFromJsonAsync<List<ExistingAccountFile>>(
            $"/api/accounts/{seeded.AccountId}/files");

        var file = Assert.Single(files!);
        Assert.Equal("Ada L.", file.AttachedByName);
        Assert.Equal("Ada L.", file.FileMetadata.UploadedByName);
        Assert.Equal(UploaderUserId, file.AttachedByUserId);
    }

    [Fact]
    public async Task GetAccountTransactions_ResolvesAttachedFileNames()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.AccountsRead]);
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory, displayName: "Ada L.");

        var transactions = await client.GetFromJsonAsync<List<ExistingTransaction>>(
            $"/api/accounts/{seeded.AccountId}/transactions");

        var file = Assert.Single(Assert.Single(transactions!).TransactionFiles);
        Assert.Equal("Ada L.", file.AttachedByName);
        Assert.Equal("Ada L.", file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task GetTransactionFiles_WithoutUsersRead_ReturnsNeutralLabelNotEmail()
    {
        // No profile at all, so the resolver's only remaining candidate is the email — which it
        // withholds from a caller without users.read. This is the claim-conditional behaviour the fix
        // inherits; it must add no disclosure over the bare id it replaces.
        await using var factory = new OdysseyApiFactory([PermissionClaims.TransactionsRead]);
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory, displayName: null);

        var response = await client.GetAsync($"/api/transactions/{seeded.TransactionId}/files");
        var body = await response.Content.ReadAsStringAsync();
        var files = await response.Content.ReadFromJsonAsync<List<ExistingTransactionFile>>();

        var file = Assert.Single(files!);
        Assert.Equal(UserDisplayNameResolver.UnknownUser, file.AttachedByName);
        Assert.Equal(UserDisplayNameResolver.UnknownUser, file.FileMetadata.UploadedByName);
        Assert.DoesNotContain(UploaderEmail, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTransactionFiles_WithUsersRead_FallsBackToEmail()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.TransactionsRead, PermissionClaims.UsersRead]);
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory, displayName: null);

        var files = await client.GetFromJsonAsync<List<ExistingTransactionFile>>(
            $"/api/transactions/{seeded.TransactionId}/files");

        Assert.Equal(UploaderEmail, Assert.Single(files!).AttachedByName);
    }

    [Fact]
    public async Task GetAccountFiles_WithDeletedAttacher_ReturnsNeutralLabel()
    {
        // The attribution foreign keys null these columns out when the account is deleted, so a null id
        // means "the author is gone" rather than "there was no author" — hence NameForAuthor, not
        // NameForOptional.
        await using var factory = new OdysseyApiFactory([PermissionClaims.AccountsRead]);
        using var client = factory.CreateClient();
        var seeded = await SeedAsync(factory, displayName: "Ada L.", attributeToUser: false);

        var files = await client.GetFromJsonAsync<List<ExistingAccountFile>>(
            $"/api/accounts/{seeded.AccountId}/files");

        var file = Assert.Single(files!);
        Assert.Null(file.AttachedByUserId);
        Assert.Equal(UserDisplayNameResolver.UnknownUser, file.AttachedByName);
        Assert.Equal(UserDisplayNameResolver.UnknownUser, file.FileMetadata.UploadedByName);
    }

    private static async Task<(Guid AccountId, Guid TransactionId)> SeedAsync(
        OdysseyApiFactory factory,
        string? displayName,
        bool attributeToUser = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

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

        var account = new Account
        {
            Name = "Everyday Checking",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        db.Accounts.Add(account);

        var attributedTo = attributeToUser ? UploaderUserId : null;

        var accountBlob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        db.FileBlob.Add(accountBlob);
        var accountFileMetadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = attributedTo,
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = "hash-account",
            FileBlobId = accountBlob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        };
        db.FileMetadata.Add(accountFileMetadata);

        var transactionBlob = new FileBlob { Id = Guid.NewGuid(), Content = [4, 5, 6] };
        db.FileBlob.Add(transactionBlob);
        var transactionFileMetadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = attributedTo,
            FileName = "receipt.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = "hash-transaction",
            FileBlobId = transactionBlob.Id,
            UploadedAtUtc = DateTime.UtcNow,
        };
        db.FileMetadata.Add(transactionFileMetadata);
        await db.SaveChangesAsync();

        db.AccountFiles.Add(new AccountFile
        {
            AccountId = account.AccountId,
            FileMetadataId = accountFileMetadata.Id,
            AttachedByUserId = attributedTo,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = AccountFileType.Statement,
        });

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

        db.TransactionFiles.Add(new TransactionFile
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.TransactionId,
            FileMetadataId = transactionFileMetadata.Id,
            AttachedByUserId = attributedTo,
            AttachedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return (account.AccountId, transaction.TransactionId);
    }
}
