using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using Interval = Odyssey.Dtos.Finance.Interval;
using TermDirection = Odyssey.Dtos.Finance.TermDirection;
using Odyssey.Core.Finance;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Core.Tests;

public class TermServiceTests
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

    /// <summary>A percentage term named like the former rate kind — now an ordinary labelled series.</summary>
    private static NewTerm InterestRate(decimal value, DateTime effectiveFrom) => new()
    {
        Label = "Interest rate",
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = effectiveFrom,
    };

    private static NewTerm Fee(string? label, decimal value, DateTime effectiveFrom) => new()
    {
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
        var service = new TermService(context);

        var created = await service.Create(accountId, InterestRate(0.0325m, new DateTime(2026, 1, 1)));

        Assert.Equal("Interest rate", created.Label);
        Assert.Equal(0.0325m, created.Value);
        Assert.Null(created.CurrencyCode);
        Assert.NotEqual(Guid.Empty, created.TermId);

        var history = await service.GetHistory(accountId);
        Assert.NotNull(history);
        Assert.Single(history!);
    }

    [Fact]
    public async Task Create_AmountFee_DefaultsCurrencyToAccountCurrency()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount, "EUR");
        var service = new TermService(context);

        var created = await service.Create(accountId, new NewTerm
        {
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
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, new NewTerm
        {
            Label = "Account fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            CurrencyCode = "ZZZ",
            EffectiveFrom = new DateTime(2026, 1, 1),
        }));
    }

    [Fact]
    public async Task Create_PercentageOutOfRange_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, InterestRate(1.5m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task Create_NegativeAmount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, new NewTerm
        {
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
        var service = new TermService(context);

        var created = await service.Create(accountId, InterestRate(-0.005m, new DateTime(2026, 1, 1)));

        Assert.Equal(-0.005m, created.Value);
    }

    [Fact]
    public async Task Create_FeeWithInterval_RoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new TermService(context);

        await service.Create(accountId, new NewTerm
        {
            Label = "Account fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 2m,
            Interval = Interval.Daily,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        var history = await service.GetHistory(accountId);
        var term = Assert.Single(history!);
        Assert.Equal(Interval.Daily, term.Interval);
    }

    [Fact]
    public async Task Create_DuplicateSeriesAndEffectiveFrom_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        var date = new DateTime(2026, 1, 1);
        await service.Create(accountId, InterestRate(0.03m, date));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Create(accountId, InterestRate(0.04m, date)));
    }

    [Fact]
    public async Task Create_OnMissingAccount_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(
            () => service.Create(Guid.NewGuid(), InterestRate(0.03m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task GetCurrent_ReturnsLatestEntryPerSeriesOnOrBeforeAsOf()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

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
        var service = new TermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, InterestRate(0.02m, new DateTime(2026, 6, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 12, 1));
        var entry = Assert.Single(current!);
        Assert.Equal(0.02m, entry.Value);
    }

    [Fact]
    public async Task GetCurrent_OnePerSeries_AcrossLabels()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, new NewTerm
        {
            Label = "Account fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 5m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        var current = await service.GetCurrent(accountId);
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "Account fee", "Interest rate" }, current.Select(t => t.Label));
    }

    [Fact]
    public async Task GetHistory_OnMissingAccount_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = new TermService(context);

        Assert.Null(await service.GetHistory(Guid.NewGuid()));
        Assert.Null(await service.GetCurrent(Guid.NewGuid()));
    }

    [Fact]
    public async Task Update_TermNotOnAccount_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        var updated = await service.Update(accountId, Guid.NewGuid(), InterestRate(0.01m, new DateTime(2026, 1, 1)));
        Assert.False(updated);
    }

    [Fact]
    public async Task Update_ChangesValue()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        var created = await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        var updated = await service.Update(accountId, created.TermId, InterestRate(0.04m, new DateTime(2026, 1, 1)));

        Assert.True(updated);
        var history = await service.GetHistory(accountId);
        Assert.Equal(0.04m, Assert.Single(history!).Value);
    }

    [Fact]
    public async Task Delete_TermNotOnAccount_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        Assert.False(await service.Delete(accountId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_RemovesTerm()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        var created = await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));
        Assert.True(await service.Delete(accountId, created.TermId));
        Assert.Empty((await service.GetHistory(accountId))!);
    }

    [Fact]
    public async Task DeletingAccount_CascadesToTerms()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        await service.Create(accountId, InterestRate(0.03m, new DateTime(2026, 1, 1)));

        var account = await context.Accounts
            .Include(a => a.Terms)
            .FirstAsync(a => a.AccountId == accountId);
        context.Accounts.Remove(account);
        await context.SaveChangesAsync();

        Assert.Empty(await context.Terms.ToListAsync());
    }

    // ── Series labels ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_TwoLabelledFeesOnOneDate_BothAreInForce()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new TermService(context);

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
        var service = new TermService(context);

        await service.Create(accountId, Fee("ATM · abroad", 25m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, Fee("ATM · domestic", 5m, new DateTime(2026, 1, 1)));
        await service.Create(accountId, Fee("ATM · abroad", 30m, new DateTime(2026, 6, 1)));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 12, 1));
        Assert.Equal(2, current!.Count);
        Assert.Equal(30m, current.Single(t => t.Label == "ATM · abroad").Value);
        Assert.Equal(5m, current.Single(t => t.Label == "ATM · domestic").Value);
    }

    [Theory]
    [InlineData("atm · abroad")]
    [InlineData("  ATM   ·   abroad  ")]
    [InlineData("AtM · AbRoAd")]
    public async Task Create_LabelDifferingOnlyByCaseOrSpacing_Conflicts(string collidingLabel)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new TermService(context);

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
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Fee(label, 25m, new DateTime(2026, 1, 1))));
    }

    /// <summary>
    /// Criterion 5, stated over the WHOLE enum rather than a hand-picked sample: a label is required on
    /// every term, so there is no account type on which one is optional. Enumerating the
    /// enum is the point — a future account type is covered the day it is added, where a list of
    /// InlineData would silently leave it out.
    /// </summary>
    public static TheoryData<DtoAccountType> EveryAccountType()
    {
        var data = new TheoryData<DtoAccountType>();
        foreach (var type in Enum.GetValues<DtoAccountType>().Where(t => t != DtoAccountType.Unknown))
            data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryAccountType))]
    public async Task Create_FeeWithoutLabel_ThrowsOnEveryAccountType(DtoAccountType accountType)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, accountType);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Fee(null, 25m, new DateTime(2026, 1, 1))));
    }

    /// <summary>The other half of the same criterion: a NAMED term is accepted on every account type,
    /// so the rule above is refusing the missing label and not the account.</summary>
    [Theory]
    [MemberData(nameof(EveryAccountType))]
    public async Task Create_NamedFee_IsAcceptedOnEveryAccountType(DtoAccountType accountType)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, accountType);
        var service = new TermService(context);

        var created = await service.Create(accountId, Fee("Account fee", 25m, new DateTime(2026, 1, 1)));

        Assert.Equal("Account fee", created.Label);
    }

    [Fact]
    public async Task Create_OverlongLabel_Throws()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new TermService(context);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(accountId, Fee(new string('x', TermLabel.MaxLength + 1), 5m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task Create_NormalizesLabelAndRoundTripsThroughHistory()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new TermService(context);

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
        var service = new TermService(context);

        var created = await service.Create(accountId, Fee("  ATM   Abroad  ", 25m, new DateTime(2026, 1, 1)));

        var stored = await context.Terms.AsNoTracking()
            .SingleAsync(t => t.TermId == created.TermId);
        Assert.Equal("ATM Abroad", stored.Label);
        Assert.Equal("atm abroad", stored.LabelKey);
    }

    [Fact]
    public async Task Update_ChangingOnlyTheLabel_MovesTheTermToItsOwnSeries()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new TermService(context);

        await service.Create(accountId, Fee("ATM", 25m, new DateTime(2026, 1, 1)));
        var second = await service.Create(accountId, Fee("ATM", 30m, new DateTime(2026, 6, 1)));

        // One series, so the later entry supersedes: one current entry.
        Assert.Single((await service.GetCurrent(accountId, new DateTime(2026, 12, 1)))!);

        Assert.True(await service.Update(accountId, second.TermId, Fee("ATM · abroad", 30m, new DateTime(2026, 6, 1))));

        var current = await service.GetCurrent(accountId, new DateTime(2026, 12, 1));
        Assert.Equal(2, current!.Count);
        Assert.Equal(new[] { "ATM", "ATM · abroad" }, current.Select(t => t.Label));
    }

    [Fact]
    public async Task GetCurrent_AnUnlabelledLegacyRowResolvesAsItsOwnSeries()
    {
        // A row written before labels were required carries a null label, which IS the unnamed series.
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.SavingsAccount);
        var service = new TermService(context);

        var account = await context.Accounts.FirstAsync(a => a.AccountId == accountId);
        context.Terms.AddRange(
            new Term
            {
                AccountId = account.AccountId,
                ValueUnit = Odyssey.Context.TermValueUnit.Percentage,
                Value = 0.03m,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new Term
            {
                AccountId = account.AccountId,
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

    // ── Defense in depth: the bounds a DIRECT (non-HTTP) caller must still hit ──
    //
    // These call TermService itself, with no WebApplicationFactory and no model binding in the path.
    // An API-level test cannot reach them at all: [ApiController] model validation rejects the same
    // input first, so the only way to prove the service check exists is to bypass the pipeline
    // exactly as a background job, a seeder or a future internal caller would (issue #120, AC 41-43).

    [Theory]
    [InlineData(4)]   // the retired Quarterly ordinal — the value this rule exists for
    [InlineData(99)]
    public async Task Create_CalledDirectlyWithAnUndefinedInterval_ThrowsAndPersistsNothing(int ordinal)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new TermService(context);

        var term = Fee("ATM · abroad", 25m, new DateTime(2026, 1, 1));
        term.Interval = (Interval)ordinal;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, term));

        // Before this rule the Mapster converter's `_ => OneTime` fallthrough silently PERSISTED the
        // value as OneTime — a write-path fail-open, not a read-path degradation.
        Assert.Empty(await context.Terms.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(99)]
    public async Task Update_CalledDirectlyWithAnUndefinedInterval_ThrowsAndLeavesTheRowUntouched(int ordinal)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new TermService(context);

        var original = Fee("ATM · abroad", 25m, new DateTime(2026, 1, 1));
        original.Interval = Interval.PerOccurrence;
        var created = await service.Create(accountId, original);

        var edit = Fee("ATM · abroad", 30m, new DateTime(2026, 1, 1));
        edit.Interval = (Interval)ordinal;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(accountId, created.TermId, edit));

        var stored = await context.Terms.AsNoTracking().SingleAsync();
        Assert.Equal(Odyssey.Context.Interval.PerOccurrence, stored.Interval);
        Assert.Equal(25m, stored.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(TermIntervalCount.Max + 1)]
    public async Task Create_CalledDirectlyWithACountOutsideItsBound_ThrowsAndPersistsNothing(int count)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new TermService(context);

        var term = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        term.Interval = Interval.Monthly;
        term.IntervalCount = count;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, term));
        Assert.Empty(await context.Terms.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(TermIntervalCount.Max + 1)]
    public async Task Update_CalledDirectlyWithACountOutsideItsBound_ThrowsAndLeavesTheRowUntouched(int count)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CheckingAccount);
        var service = new TermService(context);

        var original = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        original.Interval = Interval.Monthly;
        original.IntervalCount = 3;
        var created = await service.Create(accountId, original);

        var edit = Fee("Account maintenance", 45m, new DateTime(2026, 1, 1));
        edit.Interval = Interval.Monthly;
        edit.IntervalCount = count;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(accountId, created.TermId, edit));

        var stored = await context.Terms.AsNoTracking().SingleAsync();
        Assert.Equal(3, stored.IntervalCount);
    }

    [Fact]
    public void TheIntervalCountBound_IsTheOnePairTheRangeAttributeNames()
    {
        // Asserted by reflecting the attribute rather than by inspection, so the [Range] on the DTO
        // and the service check in ApplyAndValidate cannot drift into two different bounds.
        var range = typeof(NewTerm)
            .GetProperty(nameof(NewTerm.IntervalCount))!
            .GetCustomAttributes(typeof(RangeAttribute), inherit: false)
            .Cast<RangeAttribute>()
            .Single();

        Assert.Equal(TermIntervalCount.Min, range.Minimum);
        Assert.Equal(TermIntervalCount.Max, range.Maximum);
        Assert.Equal(1, TermIntervalCount.Min);
        Assert.Equal(1000, TermIntervalCount.Max);
    }

    [Fact]
    public async Task Create_NonPeriodicInterval_StoresANullCountRatherThanAMeaningless1()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context, DtoAccountType.CreditCard);
        var service = new TermService(context);

        var term = Fee("Card replacement", 15m, new DateTime(2026, 1, 1));
        term.Interval = Interval.OneTime;
        await service.Create(accountId, term);

        var stored = await context.Terms.AsNoTracking().SingleAsync();
        Assert.Null(stored.IntervalCount);
    }

    // ── Direction (issue #159) ───────────────────────────────────────────────

    /// <summary>
    /// The direct (non-HTTP) caller never reaches <c>[EnumDataType]</c> model validation, so the
    /// service checks the ordinal itself. Without this, an undefined value would fall through the
    /// Mapster converter and be PERSISTED as whichever member it defaults to — a write-path fail-open
    /// rather than a read-path degradation. Same reasoning as the <c>Interval</c> check beside it.
    /// </summary>
    [Fact]
    public async Task Create_AnUndefinedDirectionOrdinal_IsRefused_EvenOffTheHttpPath()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new TermService(context);

        var term = Fee("Card fee", 4m, new DateTime(2026, 1, 1));
        term.Direction = (TermDirection)99;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, term));
        Assert.Empty(context.Terms);
    }

    /// <summary>
    /// A contract's percentage term carries a direction like any other — an arrears rate charges, a
    /// deposit rate pays. Tested directly against the service, not only over HTTP: this file's convention is
    /// one unit test per eligibility rule, and the API tier proves the STATUS CODE rather than that
    /// the rule lives in the service every non-HTTP caller also goes through.
    /// </summary>
    [Fact]
    public async Task Create_IncomingOnAContractPercentageTerm_IsStored()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var term = InterestRate(0.0325m, new DateTime(2026, 1, 1));
        term.Direction = TermDirection.Incoming;

        await service.CreateForContract(contractId, term, userId: null);

        Assert.Equal(Odyssey.Context.TermDirection.Incoming,
            (await context.Terms.AsNoTracking().SingleAsync()).Direction);
    }

    /// <summary>
    /// V4 — an account-owned term may not carry a non-default direction, whatever its unit. No
    /// account surface reads a direction, so accepting one would let a user record a fact the product
    /// then contradicts.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_IncomingOnAnAccountTerm_Throws(bool percentage)
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new TermService(context);

        var term = percentage
            ? InterestRate(0.0325m, new DateTime(2026, 1, 1))
            : Fee("Interest received", 12m, new DateTime(2026, 1, 1));
        term.Direction = TermDirection.Incoming;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(accountId, term));
        Assert.Empty(context.Terms);
    }

    /// <summary>
    /// The mirror of V4: omitting the direction on an account term is the ordinary path, and what it
    /// stores is the default — so "not offered" and "refused" never become "silently rejected".
    /// </summary>
    [Fact]
    public async Task Create_AnAccountTerm_StoresTheDefaultDirection()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);
        var service = new TermService(context);

        await service.Create(accountId, Fee("Card fee", 4m, new DateTime(2026, 1, 1)));

        Assert.Equal(Odyssey.Context.TermDirection.Outgoing,
            (await context.Terms.AsNoTracking().SingleAsync()).Direction);
    }

    /// <summary>
    /// V2 — direction is accepted on any FEE, including the ones the roll-up ignores. A one-off signing
    /// bonus is legitimate incoming record-keeping: the roll-up's exclusions are about having no rate
    /// to PROJECT, not about direction.
    /// </summary>
    [Theory]
    [InlineData(Interval.OneTime)]
    [InlineData(Interval.PerOccurrence)]
    [InlineData(Interval.PerUnit)]
    [InlineData(null)]
    public async Task Create_ACadencelessFee_StillCarriesItsDirection(Interval? interval)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var term = Fee("Signing bonus", 5000m, new DateTime(2026, 1, 1));
        term.CurrencyCode = "USD";
        term.Interval = interval;
        term.Direction = TermDirection.Incoming;

        await service.CreateForContract(contractId, term, userId: null);

        Assert.Equal(Odyssey.Context.TermDirection.Incoming,
            (await context.Terms.AsNoTracking().SingleAsync()).Direction);
    }

    /// <summary>
    /// V3 — direction joins neither the series key nor the duplicate guard. Two entries sharing a
    /// <c>(kind, label)</c> and an effective date still collide however they are directed; if direction
    /// forked the series, correcting a mis-directed term would grow a second concurrent history
    /// instead of superseding the first.
    /// </summary>
    [Fact]
    public async Task Create_TheDuplicateGuard_IsBlindToDirection()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = new TermService(context);

        var outgoing = Fee("Base salary", 4000m, new DateTime(2026, 1, 1));
        outgoing.CurrencyCode = "USD";
        await service.CreateForContract(contractId, outgoing, userId: null);

        var incoming = Fee("Base salary", 4000m, new DateTime(2026, 1, 1));
        incoming.CurrencyCode = "USD";
        incoming.Direction = TermDirection.Incoming;

        await Assert.ThrowsAsync<DomainConflictException>(
            () => service.CreateForContract(contractId, incoming, userId: null));
    }

    private static async Task<Guid> SeedContractAsync(OdysseyContext context)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = "Employment Agreement",
            Type = Odyssey.Context.ContractType.Employment,
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }
}
