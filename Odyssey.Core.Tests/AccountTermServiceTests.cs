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
            TermKind = TermKind.Fee,
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            Label = "Account maintenance",
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
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            CurrencyCode = "ZZZ",
            Label = "Account maintenance",
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
            ValueUnit = TermValueUnit.Amount,
            Value = -1m,
            Label = "Account maintenance",
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
            ValueUnit = TermValueUnit.Amount,
            Value = 2m,
            BillingPeriod = BillingPeriod.Daily,
            Label = "Account maintenance",
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

    // ── Labels: a series is (kind, label), not kind alone ────────────────────────

    private static NewAccountTerm Fee(decimal value, DateTime effectiveFrom, string? label, TermKind kind = TermKind.Fee) => new()
    {
        TermKind = kind,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        Label = label,
        EffectiveFrom = effectiveFrom,
    };

    [Fact]
    public async Task GetCurrent_TwoLabelledFeesOfOneKind_BothStayInForce()
    {
        // The defect this whole feature exists for: a card charges separately for a domestic and a
        // foreign cash withdrawal, and keying supersession on the kind made the later one replace
        // the earlier — silently, since the history table still listed both.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        await service.Create(accountId, Fee(25m, new DateTime(2026, 1, 1), "ATM withdrawal · domestic"));
        await service.Create(accountId, Fee(60m, new DateTime(2026, 3, 1), "ATM withdrawal · abroad"));

        var current = await service.GetCurrent(accountId);

        Assert.Equal(2, current!.Count);
        Assert.Equal(25m, current.Single(t => t.Label == "ATM withdrawal · domestic").Value);
        Assert.Equal(60m, current.Single(t => t.Label == "ATM withdrawal · abroad").Value);
    }

    [Fact]
    public async Task GetCurrent_SameLabel_StillSupersedes()
    {
        // The other half: within one series, supersession must still work exactly as before.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        await service.Create(accountId, Fee(25m, new DateTime(2026, 1, 1), "ATM withdrawal"));
        await service.Create(accountId, Fee(30m, new DateTime(2026, 6, 1), "ATM withdrawal"));

        var current = await service.GetCurrent(accountId);

        var only = Assert.Single(current!);
        Assert.Equal(30m, only.Value);
        Assert.Equal("ATM withdrawal", only.Label);
    }

    [Fact]
    public async Task GetCurrent_TheUnnamedRateAndANamedFee_AreSeparateSeries()
    {
        // Null is its own series, not a wildcard that absorbs the named ones. A rate is refused a
        // label and a fee requires one, so this pairing is the only form that shape now takes.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, InterestRate(0.01m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, Fee(2m, new DateTime(2026, 2, 1), "Wire transfer"));

        var current = await service.GetCurrent(accountId);

        Assert.Equal(2, current!.Count);
        Assert.Contains(current, t => t.Label is null && t.TermKind == TermKind.InterestRate);
        Assert.Contains(current, t => t.Label == "Wire transfer" && t.Value == 2m);
    }

    [Fact]
    public async Task Create_DuplicateKindAndDateWithDifferentLabels_Allowed()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.Create(accountId, Fee(25m, date, "Domestic"));
        var second = await service.Create(accountId, Fee(60m, date, "Abroad"));

        Assert.Equal("Abroad", second.Label);
        Assert.Equal(2, (await service.GetHistory(accountId))!.Count);
    }

    [Fact]
    public async Task Create_DuplicateKindDateAndLabel_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.Create(accountId, Fee(25m, date, "Domestic"));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(accountId, Fee(30m, date, "Domestic")));
    }

    [Theory]
    [InlineData("domestic")]
    [InlineData("  Domestic  ")]
    [InlineData("Domestic")]
    public async Task Create_LabelDifferingOnlyByCaseOrSpacing_CollidesWithExisting(string variant)
    {
        // Case-folding is what stops a typo forking a second series that then supersedes nothing.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.Create(accountId, Fee(25m, date, "Domestic"));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(accountId, Fee(30m, date, variant)));
    }

    [Fact]
    public async Task Create_LabelIsNormalizedButKeepsAuthorCasing()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        var created = await service.Create(accountId, Fee(25m, new DateTime(2026, 1, 1), "  ATM   Abroad  "));

        Assert.Equal("ATM Abroad", created.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_FeeWithoutLabel_Throws(string? label)
    {
        // One fee kind, so an unnamed fee is precisely the entry that cannot be told apart from the
        // next unnamed one.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Fee(10m, new DateTime(2026, 1, 1), label)));
    }

    [Fact]
    public async Task Create_TwoLabelledFees_Coexist()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new AccountTermService(context);

        await service.Create(accountId, Fee(10m, new DateTime(2026, 1, 1), "Card replacement"));
        await service.Create(accountId, Fee(2m, new DateTime(2026, 2, 1), "Paper statement"));

        var current = await service.GetCurrent(accountId);
        Assert.Equal(2, current!.Count);
        Assert.All(current, t => Assert.Equal(TermKind.Fee, t.TermKind));
    }

    [Theory]
    [InlineData(TermKind.InterestRate, DtoAccountType.SavingsAccount)]
    [InlineData(TermKind.ExpectedReturn, DtoAccountType.InvestmentAccount)]
    public async Task Create_LabelOnRateKind_Throws(TermKind kind, DtoAccountType accountType)
    {
        // A rate stays single-series: two labelled interest rates would both be in force, and the
        // account header, record card and history chart each headline exactly one.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, accountType);
        var service = new AccountTermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, new NewAccountTerm
        {
            TermKind = kind,
            ValueUnit = TermValueUnit.Percentage,
            Value = 0.03m,
            Label = "Promotional",
            EffectiveFrom = new DateTime(2026, 1, 1),
        }));
    }

    [Fact]
    public async Task Update_ChangingLabel_MovesTermToItsOwnSeries()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new AccountTermService(context);

        await service.Create(accountId, Fee(25m, new DateTime(2026, 1, 1), "ATM"));
        var second = await service.Create(accountId, Fee(30m, new DateTime(2026, 6, 1), "ATM"));

        // Before: one series, the later entry superseding the earlier.
        Assert.Single((await service.GetCurrent(accountId))!);

        var ok = await service.Update(accountId, second.AccountTermId, Fee(30m, new DateTime(2026, 6, 1), "ATM abroad"));

        Assert.True(ok);
        var current = await service.GetCurrent(accountId);
        Assert.Equal(2, current!.Count);
        Assert.Contains(current, t => t.Label == "ATM" && t.Value == 25m);
        Assert.Contains(current, t => t.Label == "ATM abroad" && t.Value == 30m);
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
            TermKind = TermKind.Fee,
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            Label = "Account maintenance",
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
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            Label = "Account maintenance",
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
