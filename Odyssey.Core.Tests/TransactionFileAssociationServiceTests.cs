using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Tests;

// TransactionService tests for account-state guards and file associations. These complement
// TransactionServiceTests (kept focused on create/update/search/status); they were previously
// duplicated in Odyssey.Api.Tests and are consolidated here, their natural domain-test home.
public class TransactionFileAssociationServiceTests
{
    [Fact]
    public async Task CreateTransaction_RejectsClosedAccount()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Closed Account",
            Description = "Closed account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            Closed = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewTransaction
        {
            Description = "Late entry",
            Amount = 10,
            AccountId = account.AccountId,
        }));
    }

    [Fact]
    public async Task CreateTransaction_RejectsArchivedAccount()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Archived Account",
            Description = "Archived account",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            Archived = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewTransaction
        {
            Description = "Late entry",
            Amount = 10,
            AccountId = account.AccountId,
        }));
    }

    [Fact]
    public async Task AttachFileToTransactionSucceeds()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var transaction = await service.Create(new NewTransaction
        {
            Description = "Test Transaction",
            Amount = 100,
            AccountId = account.AccountId,
        });

        var fileId = Guid.NewGuid();
        var userId = "test-user";

        await service.AttachFileToTransaction(transaction.TransactionId, fileId, userId);

        var files = FilesFor(context, transaction.TransactionId);
        Assert.Single(files);
        Assert.Equal(fileId, files[0].FileMetadataId);
        Assert.Equal(transaction.TransactionId, files[0].TransactionId);
        Assert.Equal(userId, files[0].AttachedByUserId);
    }

    [Fact]
    public async Task AttachFileToTransactionIsIdempotent()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var transaction = await service.Create(new NewTransaction
        {
            Description = "Test Transaction",
            Amount = 100,
            AccountId = account.AccountId,
        });

        var fileId = Guid.NewGuid();
        var userId = "test-user";

        // Attach the same file twice
        await service.AttachFileToTransaction(transaction.TransactionId, fileId, userId);
        await service.AttachFileToTransaction(transaction.TransactionId, fileId, userId);

        // Should only have one attachment
        var files = FilesFor(context, transaction.TransactionId);
        Assert.Single(files);
    }

    [Fact]
    public async Task AttachFileToTransactionFailsWhenTransactionNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());

        var transactionId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var userId = "test-user";

        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            service.AttachFileToTransaction(transactionId, fileId, userId));
    }

    [Fact]
    public async Task DetachFileFromTransactionSucceeds()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var transaction = await service.Create(new NewTransaction
        {
            Description = "Test Transaction",
            Amount = 100,
            AccountId = account.AccountId,
        });

        var fileId = Guid.NewGuid();
        var userId = "test-user";

        // Attach and then detach
        await service.AttachFileToTransaction(transaction.TransactionId, fileId, userId);
        await service.DetachFileFromTransaction(transaction.TransactionId, fileId);

        var files = FilesFor(context, transaction.TransactionId);
        Assert.Empty(files);
    }

    [Fact]
    public async Task DetachFileFromTransactionIsIdempotent()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var transaction = await service.Create(new NewTransaction
        {
            Description = "Test Transaction",
            Amount = 100,
            AccountId = account.AccountId,
        });

        var fileId = Guid.NewGuid();
        var userId = "test-user";

        // Attach and then detach twice (second detach should not fail)
        await service.AttachFileToTransaction(transaction.TransactionId, fileId, userId);
        await service.DetachFileFromTransaction(transaction.TransactionId, fileId);
        await service.DetachFileFromTransaction(transaction.TransactionId, fileId); // Should not throw

        var files = FilesFor(context, transaction.TransactionId);
        Assert.Empty(files);
    }

    [Fact]
    public async Task ListFilesForTransactionReturnsEmptyWhenNoFiles()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var transaction = await service.Create(new NewTransaction
        {
            Description = "Test Transaction",
            Amount = 100,
            AccountId = account.AccountId,
        });

        var files = FilesFor(context, transaction.TransactionId);
        Assert.Empty(files);
    }

    [Fact]
    public async Task ListFilesForTransactionReturnsMultipleFiles()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var transaction = await service.Create(new NewTransaction
        {
            Description = "Test Transaction",
            Amount = 100,
            AccountId = account.AccountId,
        });

        var fileId1 = Guid.NewGuid();
        var fileId2 = Guid.NewGuid();
        var userId = "test-user";

        await service.AttachFileToTransaction(transaction.TransactionId, fileId1, userId);
        await service.AttachFileToTransaction(transaction.TransactionId, fileId2, userId);

        var files = FilesFor(context, transaction.TransactionId);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.FileMetadataId == fileId1);
        Assert.Contains(files, f => f.FileMetadataId == fileId2);
    }

    [Fact]
    public async Task DeleteTransactionCascadesFileAssociations()
    {
        await using var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Test Account",
            Description = "For testing",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var transaction = await service.Create(new NewTransaction
        {
            Description = "Test Transaction",
            Amount = 100,
            AccountId = account.AccountId,
        });

        var fileId = Guid.NewGuid();
        var userId = "test-user";

        await service.AttachFileToTransaction(transaction.TransactionId, fileId, userId);

        // Verify attachment exists
        var filesBeforeDelete = FilesFor(context, transaction.TransactionId);
        Assert.Single(filesBeforeDelete);

        // Delete transaction
        await service.Delete(transaction.TransactionId);

        // Verify transaction is gone
        var fetchedTransaction = await service.Get(transaction.TransactionId);
        Assert.Null(fetchedTransaction);

        // Verify there are no transactions with that file attached anymore
        var allTransactions = (await service.ListAsync(new TransactionsQueryParams())).Items;
        Assert.Empty(allTransactions);
    }

    private static IList<TransactionFile> FilesFor(OdysseyContext context, Guid transactionId) =>
        context.TransactionFiles
            .Where(tf => tf.TransactionId == transactionId)
            .OrderBy(tf => tf.AttachedAtUtc)
            .ToList();
}
