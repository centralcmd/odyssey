using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class AccountEstimateServiceTests
{
    private static async Task<Guid> SeedAccountAsync(
        OdysseyContext context,
        DtoAccountType accountType = DtoAccountType.Property,
        string currencyCode = "USD")
    {
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());
        var account = await service.Create(new NewAccount
        {
            Name = "Test",
            Description = "Test account",
            AccountType = accountType,
            CurrencyCode = currencyCode,
            Archived = false,
        });
        return account.AccountId;
    }

    private static NewAccountEstimate Estimate(decimal value, DateTime effectiveFrom, string? currencyCode = null) => new()
    {
        Value = value,
        EffectiveFrom = effectiveFrom,
        CurrencyCode = currencyCode,
    };

    [Fact]
    public async Task Create_OnProperty_DefaultsCurrencyToAccountCurrency_AndPersists()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.Property, "EUR");
        var service = new AccountEstimateService(context);

        var created = await service.Create(accountId, Estimate(350000m, new DateTime(2026, 1, 1)));

        Assert.Equal(350000m, created.Value);
        Assert.Equal("EUR", created.CurrencyCode);
        Assert.NotEqual(Guid.Empty, created.AccountEstimateId);

        var history = await service.GetHistory(accountId);
        Assert.NotNull(history);
        Assert.Single(history!);
    }

    [Theory]
    [InlineData(DtoAccountType.Property)]
    [InlineData(DtoAccountType.Vehicle)]
    [InlineData(DtoAccountType.Cash)]
    [InlineData(DtoAccountType.CheckingAccount)]
    [InlineData(DtoAccountType.Mortgage)]
    public async Task Create_AllowedOnEveryAccountType(DtoAccountType accountType)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, accountType);
        var service = new AccountEstimateService(context);

        var created = await service.Create(accountId, Estimate(1000m, new DateTime(2026, 1, 1)));

        Assert.Equal(1000m, created.Value);
    }

    [Fact]
    public async Task Create_NegativeValue_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Estimate(-1m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task Create_CurrencyDifferentFromAccount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.Property, "USD");
        var service = new AccountEstimateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Estimate(1000m, new DateTime(2026, 1, 1), currencyCode: "EUR")));
    }

    [Fact]
    public async Task Create_MatchingAccountCurrency_Succeeds()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.Property, "USD");
        var service = new AccountEstimateService(context);

        var created = await service.Create(accountId, Estimate(1000m, new DateTime(2026, 1, 1), currencyCode: "usd"));

        Assert.Equal("USD", created.CurrencyCode);
    }

    [Fact]
    public async Task Create_UnsupportedCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.Property, "USD");
        var service = new AccountEstimateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Estimate(1000m, new DateTime(2026, 1, 1), currencyCode: "ZZZ")));
    }

    [Fact]
    public async Task Create_DuplicateEffectiveFrom_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        var date = new DateTime(2026, 1, 1);
        await service.Create(accountId, Estimate(1000m, date));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(accountId, Estimate(2000m, date)));
    }

    [Fact]
    public async Task Create_OnMissingAccount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountEstimateService(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(
            () => service.Create(Guid.NewGuid(), Estimate(1000m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task GetCurrent_ReturnsLatestEntryOnOrBeforeAsOf()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        await service.Create(accountId, Estimate(300000m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, Estimate(320000m, new DateTime(2026, 3, 1)));
        await service.Create(accountId, Estimate(340000m, new DateTime(2026, 6, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 4, 1));
        Assert.NotNull(current);
        Assert.Equal(320000m, current!.Value);
        Assert.Equal(new DateTime(2026, 3, 1), current.EffectiveFrom);

        // History still shows all three, newest first.
        var history = await service.GetHistory(accountId);
        Assert.Equal(3, history!.Count);
        Assert.Equal(340000m, history[0].Value);
    }

    [Fact]
    public async Task GetCurrent_NewerEntrySupersedesPrevious()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        await service.Create(accountId, Estimate(300000m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, Estimate(280000m, new DateTime(2026, 6, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 12, 1));
        Assert.Equal(280000m, current!.Value);
    }

    [Fact]
    public async Task GetCurrent_NoEstimateBeforeCutoff_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        await service.Create(accountId, Estimate(300000m, new DateTime(2026, 6, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 1, 1));
        Assert.Null(current);
    }

    [Fact]
    public async Task GetHistory_OnMissingAccount_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountEstimateService(context);

        Assert.Null(await service.GetHistory(Guid.NewGuid()));
        Assert.Null(await service.GetCurrent(Guid.NewGuid()));
    }

    [Fact]
    public async Task Update_ChangesValue()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        var created = await service.Create(accountId, Estimate(300000m, new DateTime(2026, 1, 1)));
        var updated = await service.Update(accountId, created.AccountEstimateId, Estimate(310000m, new DateTime(2026, 1, 1)));

        Assert.True(updated);
        var history = await service.GetHistory(accountId);
        Assert.Equal(310000m, Assert.Single(history!).Value);
    }

    [Fact]
    public async Task Update_EstimateNotOnAccount_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        var updated = await service.Update(accountId, Guid.NewGuid(), Estimate(1m, new DateTime(2026, 1, 1)));
        Assert.False(updated);
    }

    [Fact]
    public async Task Delete_RemovesEstimate()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        var created = await service.Create(accountId, Estimate(300000m, new DateTime(2026, 1, 1)));
        Assert.True(await service.Delete(accountId, created.AccountEstimateId));
        Assert.Empty((await service.GetHistory(accountId))!);
    }

    [Fact]
    public async Task Delete_EstimateNotOnAccount_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        Assert.False(await service.Delete(accountId, Guid.NewGuid()));
    }

    [Fact]
    public async Task DeletingAccount_CascadesToEstimates()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new AccountEstimateService(context);

        await service.Create(accountId, Estimate(300000m, new DateTime(2026, 1, 1)));

        var account = await context.Accounts
            .Include(a => a.AccountEstimates)
            .FirstAsync(a => a.AccountId == accountId);
        context.Accounts.Remove(account);
        await context.SaveChangesAsync();

        Assert.Empty(await context.AccountEstimates.ToListAsync());
    }
}
