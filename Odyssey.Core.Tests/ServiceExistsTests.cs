using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Tests;

/// <summary>
/// The presence checks the controllers use instead of building the full read model (issue #287 M9):
/// true for a row that exists, false for any other id.
/// </summary>
public class ServiceExistsTests
{
    [Fact]
    public async Task AccountExists_IsTrueOnlyForAStoredAccount()
    {
        await using var context = TestContextFactory.Create();
        var account = await SeedAccountAsync(context);
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());

        Assert.True(await service.Exists(account.AccountId));
        Assert.False(await service.Exists(Guid.NewGuid()));
    }

    [Fact]
    public async Task TransactionExists_IsTrueOnlyForAStoredTransaction()
    {
        await using var context = TestContextFactory.Create();
        var account = await SeedAccountAsync(context);
        var service = new TransactionService(context, TestContextFactory.EmptyContactLookup());
        var transaction = await service.Create(new NewTransaction
        {
            Description = "Groceries",
            Amount = 100,
            AccountId = account.AccountId,
        });

        Assert.True(await service.Exists(transaction.TransactionId));
        Assert.False(await service.Exists(account.AccountId));
        Assert.False(await service.Exists(Guid.NewGuid()));
    }

    private static async Task<Account> SeedAccountAsync(OdysseyContext context)
    {
        var account = new Account
        {
            Name = "Checking",
            Description = "For testing",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }
}
