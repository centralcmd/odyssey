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

    private static NewAccountTerm Fee(string? label, decimal value, DateTime effectiveFrom) => new()
    {
        TermKind = TermKind.Fee,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
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
            TermKind = TermKind.Fee,
            Label = "Account fee",
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
            TermKind = TermKind.Fee,
            Label = "Account fee",
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
            TermKind = TermKind.Fee,
            Label = "Account fee",
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
    public async Task Create_FeeWithBillingPeriod_RoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.Fee,
            Label = "Account fee",
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
    public async Task Create_DuplicateSeriesAndEffectiveFrom_Throws()
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
    public async Task GetCurrent_ReturnsLatestEntryPerSeriesOnOrBeforeAsOf()
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
    public async Task GetCurrent_OnePerSeries_AcrossKinds()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, new NewAccountTerm
        {
            TermKind = TermKind.Fee,
            Label = "Account fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        var current = await service.GetCurrent(accountId);
        Assert.Equal(2, current!.Count);
        Assert.Contains(current, t => t.TermKind == TermKind.InterestRate);
        Assert.Contains(current, t => t.TermKind == TermKind.Fee);
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
            TermKind = TermKind.Fee,
            Label = "Account fee",
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

    // ── Series labels ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_TwoLabelledFeesOnOneDate_BothAreInForce()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.Create(accountId, Fee("ATM · abroad", 25m, date));
        await service.Create(accountId, Fee("ATM · domestic", 5m, date));

        var current = await service.GetCurrent(accountId, date);
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "ATM · abroad", "ATM · domestic" }, current.Select(t => t.Label));
    }

    [Fact]
    public async Task GetCurrent_SupersessionHappensWithinOneLabelOnly()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        await service.Create(accountId, Fee("ATM · abroad", 25m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, Fee("ATM · domestic", 5m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, Fee("ATM · abroad", 30m, new DateTime(2026, 6, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 12, 1));
        Assert.Equal(2, current!.Count);
        Assert.Equal(30m, current.Single(t => t.Label == "ATM · abroad").Value);
        Assert.Equal(5m, current.Single(t => t.Label == "ATM · domestic").Value);
    }

    [Fact]
    public async Task GetCurrent_UnlabelledRateAndLabelledFeeAreSeparateSeries()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        await service.Create(accountId, InterestRate(0.22m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, Fee("Annual card fee", 95m, new DateTime(2026, 1, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 2, 1));
        Assert.Equal(2, current!.Count);
        Assert.Null(current.Single(t => t.TermKind == TermKind.InterestRate).Label);
        Assert.Equal("Annual card fee", current.Single(t => t.TermKind == TermKind.Fee).Label);
    }

    [Theory]
    [InlineData("atm · abroad")]
    [InlineData("  ATM   ·   abroad  ")]
    [InlineData("AtM · AbRoAd")]
    public async Task Create_LabelDifferingOnlyByCaseOrSpacing_Conflicts(string collidingLabel)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.Create(accountId, Fee("ATM · abroad", 25m, date));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(accountId, Fee(collidingLabel, 30m, date)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_FeeWithoutLabel_Throws(string? label)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Fee(label, 25m, new DateTime(2026, 1, 1))));
    }

    // There is no fee kind for which a label is optional, so the rule holds on every account type —
    // including the ones whose only eligible kind is Fee.
    [Theory]
    [InlineData(DtoAccountType.Cash)]
    [InlineData(DtoAccountType.Property)]
    [InlineData(DtoAccountType.Vehicle)]
    [InlineData(DtoAccountType.CheckingAccount)]
    [InlineData(DtoAccountType.InvestmentAccount)]
    public async Task Create_FeeWithoutLabel_ThrowsOnEveryAccountType(DtoAccountType accountType)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, accountType);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Fee(null, 25m, new DateTime(2026, 1, 1))));
    }

    [Theory]
    [InlineData(TermKind.InterestRate, DtoAccountType.SavingsAccount)]
    [InlineData(TermKind.ExpectedReturn, DtoAccountType.InvestmentAccount)]
    public async Task Create_RateKindWithLabel_Throws(TermKind kind, DtoAccountType accountType)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, accountType);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, new NewAccountTerm
        {
            TermKind = kind,
            Label = "Headline",
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.03m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        }));
    }

    [Fact]
    public async Task Create_OverlongLabel_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Fee(new string('x', TermLabel.MaxLength + 1), 5m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task Create_NormalizesLabelAndRoundTripsThroughHistory()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, Fee("  ATM   Abroad  ", 25m, new DateTime(2026, 1, 1)));

        Assert.Equal("ATM Abroad", created.Label);
        var history = await service.GetHistory(accountId);
        Assert.Equal("ATM Abroad", Assert.Single(history!).Label);
    }

    [Fact]
    public async Task Create_DerivesLabelKeyFromLabelAlone()
    {
        // Mass assignment: LabelKey is on no request DTO, so the only thing that can set it is the
        // service's own derivation from Label.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, Fee("  ATM   Abroad  ", 25m, new DateTime(2026, 1, 1)));

        var stored = await context.AccountTerms.AsNoTracking()
            .SingleAsync(t => t.AccountTermId == created.AccountTermId);
        Assert.Equal("ATM Abroad", stored.Label);
        Assert.Equal("atm abroad", stored.LabelKey);
    }

    [Fact]
    public async Task Update_ChangingOnlyTheLabel_MovesTheTermToItsOwnSeries()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        await service.Create(accountId, Fee("ATM", 25m, new DateTime(2026, 1, 1)));
        var second = await service.Create(accountId, Fee("ATM", 30m, new DateTime(2026, 6, 1)));

        // One series, so the later entry supersedes: one current entry.
        Assert.Single((await service.GetCurrent(accountId, new DateTime(2026, 12, 1)))!);

        Assert.True(await service.Update(accountId, second.AccountTermId, Fee("ATM · abroad", 30m, new DateTime(2026, 6, 1))));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 12, 1));
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "ATM", "ATM · abroad" }, current.Select(t => t.Label));
    }

    [Fact]
    public async Task GetCurrent_UnlabelledRateResolvesExactlyAsBefore()
    {
        // A term written before labels existed carries a null label, which IS the unnamed series.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new AccountTermService(context);

        var account = await context.Accounts.FirstAsync(a => a.AccountId == accountId);
        context.AccountTerms.AddRange(
            new AccountTerm
            {
                AccountId = account.AccountId,
                TermKind = Odyssey.Context.TermKind.InterestRate,
                ValueUnit = Odyssey.Context.TermValueUnit.Percentage,
                Value = 0.03m,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new AccountTerm
            {
                AccountId = account.AccountId,
                TermKind = Odyssey.Context.TermKind.InterestRate,
                ValueUnit = Odyssey.Context.TermValueUnit.Percentage,
                Value = 0.02m,
                EffectiveFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        await context.SaveChangesAsync();

        var current = await service.GetCurrent(accountId, new DateTime(2026, 12, 1));
        var entry = Assert.Single(current!);
        Assert.Equal(0.02m, entry.Value);
        Assert.Null(entry.Label);
    }
}
