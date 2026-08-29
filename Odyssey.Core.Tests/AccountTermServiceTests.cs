using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using TermKind = Odyssey.Dtos.Finance.TermKind;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using BillingPeriod = Odyssey.Dtos.Finance.BillingPeriod;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class AccountTermServiceTests
{
    private static async Task<Guid> SeedAccountAsync(
        OdysseyContext context,
        DtoAccountType accountType = DtoAccountType.SavingsAccount,
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

    private static NewAccountTerm InterestRate(decimal value, DateTime effectiveFrom) => new()
    {
        TermKind = TermKind.InterestRate,
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = effectiveFrom,
    };

    [Fact]
    public async Task Create_InterestRateOnSavingsAccount_Persists()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, InterestRate(0.0325m, new DateTime(2026, 1, 1)));

        Assert.Equal(TermKind.InterestRate, created.TermKind);
        Assert.Equal(0.0325m, created.Value);
        Assert.Null(created.CurrencyCode);
        Assert.NotEqual(Guid.Empty, created.AccountTermId);

        var history = await service.GetHistory(accountId);
        Assert.NotNull(history);
        Assert.Single(history!);
    }

    [Fact]
    public async Task Create_AmountFee_DefaultsCurrencyToAccountCurrency()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount, "EUR");
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.ServiceFee,
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        Assert.Equal("EUR", created.CurrencyCode);
    }

    [Fact]
    public async Task Create_AmountFee_RejectsUnsupportedCurrency()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.ServiceFee,
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            CurrencyCode = "ZZZ",
            EffectiveFrom = new DateTime(2026, 1, 1),
        }));
    }

    [Theory]
    [InlineData(DtoAccountType.Cash)]
    [InlineData(DtoAccountType.Property)]
    [InlineData(DtoAccountType.Vehicle)]
    public async Task Create_InterestRateOnIneligibleAccount_Throws(DtoAccountType accountType)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, accountType);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, InterestRate(0.05m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task Create_InterestRateOnCheckingAccount_Persists()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, InterestRate(0.001m, new DateTime(2026, 1, 1)));

        Assert.Equal(TermKind.InterestRate, created.TermKind);
    }

    [Fact]
    public async Task Create_ExpectedReturn_AllowedOnInvestmentRejectedOnChecking()
    {
        await using var context = TestContextFactory.Create();
        var investmentId = await SeedAccountAsync(context, DtoAccountType.InvestmentAccount);
        var checkingId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        var created = await service.Create(investmentId, new NewAccountTerm
        {
            TermKind = TermKind.ExpectedReturn,
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.07m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });
        Assert.Equal(TermKind.ExpectedReturn, created.TermKind);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(checkingId, new NewAccountTerm
        {
            TermKind = TermKind.ExpectedReturn,
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.07m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        }));
    }

    [Fact]
    public async Task Create_PercentageOutOfRange_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, InterestRate(1.5m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task Create_NegativeAmount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.ServiceFee,
            ValueUnit = TermValueUnit.Amount,
            Value = -1m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        }));
    }

    [Fact]
    public async Task Create_NegativeInterestRate_Allowed()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, InterestRate(-0.005m, new DateTime(2026, 1, 1)));

        Assert.Equal(-0.005m, created.Value);
    }

    [Fact]
    public async Task Create_BillingPeriodOnInterestRate_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.InterestRate,
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.03m,
            BillingPeriod = BillingPeriod.Monthly,
            EffectiveFrom = new DateTime(2026, 1, 1),
        }));
    }

    [Theory]
    [InlineData(TermKind.InterestRate, DtoAccountType.SavingsAccount)]
    [InlineData(TermKind.ExpectedReturn, DtoAccountType.InvestmentAccount)]
    public async Task Create_RateKindWithAmountUnit_Throws(TermKind kind, DtoAccountType accountType)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, accountType);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, new NewAccountTerm
        {
            TermKind = kind,
            ValueUnit = TermValueUnit.Amount,
            Value = 100m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        }));
    }

    [Fact]
    public async Task Create_ServiceFeeWithBillingPeriod_RoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.ServiceFee,
            ValueUnit = TermValueUnit.Amount,
            Value = 2m,
            BillingPeriod = BillingPeriod.Daily,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        var history = await service.GetHistory(accountId);
        var term = Assert.Single(history!);
        Assert.Equal(BillingPeriod.Daily, term.BillingPeriod);
    }

    [Fact]
    public async Task Create_DuplicateKindAndEffectiveFrom_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.Create(accountId, InterestRate(0.03m, date));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(accountId, InterestRate(0.04m, date)));
    }

    [Fact]
    public async Task Create_OnMissingAccount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(
            () => service.Create(Guid.NewGuid(), InterestRate(0.03m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task GetCurrent_ReturnsLatestEntryPerKindOnOrBeforeAsOf()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, InterestRate(0.025m, new DateTime(2026, 3, 1)));
        await service.Create(accountId, InterestRate(0.02m, new DateTime(2026, 6, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 4, 1));
        var entry = Assert.Single(current!);
        Assert.Equal(0.025m, entry.Value);
        Assert.Equal(new DateTime(2026, 3, 1), entry.EffectiveFrom);

        // History still shows all three.
        var history = await service.GetHistory(accountId);
        Assert.Equal(3, history!.Count);
    }

    [Fact]
    public async Task GetCurrent_NewerEntrySupersedesPrevious()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, InterestRate(0.02m, new DateTime(2026, 6, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 12, 1));
        var entry = Assert.Single(current!);
        Assert.Equal(0.02m, entry.Value);
    }

    [Fact]
    public async Task GetCurrent_OnePerKind()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.ServiceFee,
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        var current = await service.GetCurrent(accountId);
        Assert.Equal(2, current!.Count);
        Assert.Contains(current, t => t.TermKind == TermKind.InterestRate);
        Assert.Contains(current, t => t.TermKind == TermKind.ServiceFee);
    }

    [Fact]
    public async Task GetHistory_FiltersByKind()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.ServiceFee,
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        var rates = await service.GetHistory(accountId, TermKind.InterestRate);
        var entry = Assert.Single(rates!);
        Assert.Equal(TermKind.InterestRate, entry.TermKind);
    }

    [Fact]
    public async Task GetHistory_OnMissingAccount_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountTermService(context);

        Assert.Null(await service.GetHistory(Guid.NewGuid()));
        Assert.Null(await service.GetCurrent(Guid.NewGuid()));
    }

    [Fact]
    public async Task Update_TermNotOnAccount_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        var updated = await service.Update(accountId, Guid.NewGuid(), InterestRate(0.01m, new DateTime(2026, 1, 1)));
        Assert.False(updated);
    }

    [Fact]
    public async Task Update_ChangesValue()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        var updated = await service.Update(accountId, created.AccountTermId, InterestRate(0.04m, new DateTime(2026, 1, 1)));

        Assert.True(updated);
        var history = await service.GetHistory(accountId);
        Assert.Equal(0.04m, Assert.Single(history!).Value);
    }

    [Fact]
    public async Task Delete_TermNotOnAccount_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        Assert.False(await service.Delete(accountId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_RemovesTerm()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        Assert.True(await service.Delete(accountId, created.AccountTermId));
        Assert.Empty((await service.GetHistory(accountId))!);
    }

    [Fact]
    public async Task DeletingAccount_CascadesToTerms()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));

        var account = await context.Accounts
            .Include(a => a.AccountTerms)
            .FirstAsync(a => a.AccountId == accountId);
        context.Accounts.Remove(account);
        await context.SaveChangesAsync();

        Assert.Empty(await context.AccountTerms.ToListAsync());
    }
}
