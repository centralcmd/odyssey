using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using TermKind = Odyssey.Dtos.Finance.TermKind;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using Interval = Odyssey.Dtos.Finance.Interval;

namespace Odyssey.Core.Tests;

/// <summary>
/// The CONTRACT half of <see cref="TermService"/> (issue #135): the contract-specific rules, and —
/// more importantly — the rules that must NOT differ from the account path, since both owners run one
/// validator. Anything asserted here about a shared rule is asserted because a per-owner regression in
/// it would be invisible on the account tests.
/// </summary>
public class ContractTermServiceTests
{
    private static ContractService Contracts(OdysseyContext context, ISystemSettingsLookup? settings = null) =>
        new(context,
            TestContextFactory.EmptyContactLookup(),
            TimeProvider.System,
            settings ?? new FakeSystemSettingsLookup(),
            NullLogger<ContractService>.Instance);

    private static TermService Terms(OdysseyContext context, ISystemSettingsLookup? settings = null) =>
        new(context, TimeProvider.System, settings ?? new FakeSystemSettingsLookup());

    private static async Task<Guid> SeedContractAsync(
        OdysseyContext context, DtoContractType type = DtoContractType.Rental, bool archived = false,
        ISystemSettingsLookup? settings = null)
    {
        var created = await Contracts(context, settings).Create(new NewContract
        {
            Name = "Maple St lease",
            Type = type,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }, userId: null);

        if (archived)
        {
            var contract = await context.Contracts.FirstAsync(c => c.ContractId == created.ContractId);
            contract.Archived = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            await context.SaveChangesAsync();
        }

        return created.ContractId;
    }

    private static async Task<Guid> SeedAccountAsync(
        OdysseyContext context, DtoAccountType accountType = DtoAccountType.SavingsAccount)
    {
        var account = await new AccountService(context, TestContextFactory.EmptyContactLookup())
            .Create(new NewAccount
            {
                Name = "Savings",
                Description = "Seeded for the cross-owner assertions.",
                AccountType = accountType,
                CurrencyCode = "USD",
                Archived = false,
            });
        return account.AccountId;
    }

    private static NewTerm Rent(decimal value, DateTime effectiveFrom, string? label = "Monthly rent", string? currency = "USD") => new()
    {
        TermKind = TermKind.Fee,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = currency,
        Interval = Interval.Monthly,
        EffectiveFrom = effectiveFrom,
    };

    private static NewTerm InterestRate(decimal value, DateTime effectiveFrom) => new()
    {
        TermKind = TermKind.InterestRate,
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = effectiveFrom,
    };

    // ── Ownership ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_OnContract_SetsContractOwnerAndLeavesAccountNull()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);

        var created = await Terms(context).CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.Equal(contractId, created.ContractId);
        Assert.Null(created.AccountId);

        var row = await context.Terms.SingleAsync();
        Assert.Equal(contractId, row.ContractId);
        Assert.Null(row.AccountId);
    }

    [Fact]
    public async Task Create_OnAccount_StillSetsAccountOwnerAndLeavesContractNull()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);

        var created = await Terms(context).Create(accountId, new NewTerm
        {
            TermKind = TermKind.Fee,
            Label = "Monthly fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 4m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        Assert.Equal(accountId, created.AccountId);
        Assert.Null(created.ContractId);
    }

    [Fact]
    public async Task Create_OnMissingContract_Throws404()
    {
        await using var context = TestContextFactory.Create();

        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            Terms(context).CreateForContract(Guid.NewGuid(), Rent(1m, new DateTime(2026, 1, 1))));
    }

    /// <summary>
    /// The two owners' series never interact, however identical the kind, label and date. This is the
    /// property the whole owner-scoped duplicate guard exists to hold.
    /// </summary>
    [Fact]
    public async Task Create_SameKindLabelAndDateOnBothOwners_IsTwoSeriesNotAConflict()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var accountId = await SeedAccountAsync(context);
        var service = Terms(context);
        var date = new DateTime(2026, 10, 1);

        await service.CreateForContract(contractId, Rent(14500m, date));
        await service.Create(accountId, Rent(14500m, date));

        Assert.Equal(2, await context.Terms.CountAsync());
        Assert.Single(await service.GetContractHistory(contractId) ?? []);
        Assert.Single(await service.GetHistory(accountId) ?? []);
    }

    [Fact]
    public async Task GetHistory_OnAnAccount_NeverReturnsContractTerms()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var accountId = await SeedAccountAsync(context);
        var service = Terms(context);

        await service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.Empty(await service.GetHistory(accountId) ?? []);
        Assert.Empty(await service.GetCurrent(accountId) ?? []);
    }

    // ── Kind eligibility ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(DtoContractType.Employment)]
    [InlineData(DtoContractType.Service)]
    [InlineData(DtoContractType.Rental)]
    [InlineData(DtoContractType.Other)]
    public async Task Create_FeeAndInterestRate_AreAcceptedOnEveryContractType(DtoContractType type)
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context, type);
        var service = Terms(context);

        await service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));
        await service.CreateForContract(contractId, InterestRate(0.0325m, new DateTime(2026, 10, 1)));

        Assert.Equal(2, (await service.GetContractHistory(contractId))!.Count);
    }

    [Fact]
    public async Task Create_ExpectedReturnOnAContract_IsRefused()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Terms(context).CreateForContract(contractId, new NewTerm
            {
                TermKind = TermKind.ExpectedReturn,
                ValueUnit = TermValueUnit.Percentage,
                Value = 0.07m,
                EffectiveFrom = new DateTime(2026, 1, 1),
            }));

        Assert.Contains("contracts", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_UnknownKind_IsRefused()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Terms(context).CreateForContract(contractId, new NewTerm
            {
                TermKind = TermKind.Unknown,
                ValueUnit = TermValueUnit.Amount,
                Value = 1m,
                CurrencyCode = "USD",
                EffectiveFrom = new DateTime(2026, 1, 1),
            }));
    }

    // ── Currency ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_AmountWithoutCurrencyOnAContract_IsRefusedNamingTheField()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Terms(context).CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1), currency: null)));

        Assert.NotNull(error.Errors);
        Assert.True(error.Errors!.ContainsKey(nameof(NewTerm.CurrencyCode)));
    }

    [Fact]
    public async Task Create_AmountWithoutCurrencyOnAnAccount_StillDefaultsToTheAccountCurrency()
    {
        await using var context = TestContextFactory.Create();
        var accountId = await SeedAccountAsync(context);

        var created = await Terms(context).Create(accountId, new NewTerm
        {
            TermKind = TermKind.Fee,
            Label = "Monthly fee",
            ValueUnit = TermValueUnit.Amount,
            Value = 4m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        Assert.Equal("USD", created.CurrencyCode);
    }

    [Fact]
    public async Task Create_PercentageOnAContract_NeedsNoCurrencyAndStoresNone()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);

        var created = await Terms(context).CreateForContract(contractId, InterestRate(0.0325m, new DateTime(2026, 1, 1)));

        Assert.Null(created.CurrencyCode);
    }

    // ── The archive state refuses nothing ────────────────────────────────────

    /// <summary>
    /// <b>Archiving never blocks a term write.</b> A lease that has ended still gains its final rent
    /// entry, and a closing fee is recorded after the agreement is filed away — which is precisely
    /// when an archived contract is the one being written to. The per-contract cap is the only thing
    /// that refuses a term write.
    /// </summary>
    [Fact]
    public async Task Create_OnAnArchivedContract_IsAllowed()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context, archived: true);

        var created = await Terms(context).CreateForContract(
            contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.Equal(14500m, created.Value);
        Assert.NotNull((await context.Contracts.FirstAsync(c => c.ContractId == contractId)).Archived);
    }

    [Fact]
    public async Task UpdateAndDelete_OnAnArchivedContract_AreAllowedToo()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);
        var term = await service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        var contract = await context.Contracts.FirstAsync(c => c.ContractId == contractId);
        contract.Archived = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();

        Assert.True(await service.UpdateForContract(
            contractId, term.TermId, Rent(15000m, new DateTime(2026, 11, 1))));
        Assert.Equal(15000m, (await context.Terms.SingleAsync()).Value);

        Assert.True(await service.DeleteForContract(contractId, term.TermId));
        Assert.Empty(await context.Terms.ToListAsync());
    }

    [Fact]
    public async Task GetHistory_OnAnArchivedContract_StillReads()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);
        await service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        var contract = await context.Contracts.FirstAsync(c => c.ContractId == contractId);
        contract.Archived = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();

        Assert.Single((await service.GetContractHistory(contractId))!);
    }

    // ── The cap ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_BeyondTheCap_IsUnprocessableWhileUpdateStaysAllowed()
    {
        await using var context = TestContextFactory.Create();
        var settings = new FakeSystemSettingsLookup
        {
            Caps = new FinanceRequestCaps(
                MaxPartiesPerContract: 25, MaxFilesPerContract: 50, MaxTermsPerContract: 2,
                MaxSummaryContracts: 1000, MaxRenewalsPerPolicy: 100, MaxFilesPerParent: 50,
                MaxLinksPerPolicy: 50),
        };
        var contractId = await SeedContractAsync(context, settings: null);
        var service = Terms(context, settings);

        var first = await service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 1, 1)));
        await service.CreateForContract(contractId, Rent(14800m, new DateTime(2026, 7, 1)));

        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(() =>
            service.CreateForContract(contractId, Rent(15000m, new DateTime(2027, 1, 1))));
        Assert.Contains("2", error.Message);

        // An update replaces a row rather than adding one, so it is row-count-neutral and exempt.
        Assert.True(await service.UpdateForContract(contractId, first.TermId, Rent(14600m, new DateTime(2026, 1, 1))));
    }

    [Fact]
    public async Task Create_OnAnAccount_IsNeverCapped()
    {
        await using var context = TestContextFactory.Create();
        var settings = new FakeSystemSettingsLookup
        {
            Caps = new FinanceRequestCaps(25, 50, 1, 1000, 100, 50, 50),
        };
        var accountId = await SeedAccountAsync(context);
        var service = Terms(context, settings);

        await service.Create(accountId, new NewTerm
        {
            TermKind = TermKind.Fee, Label = "A", ValueUnit = TermValueUnit.Amount, Value = 1m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });
        await service.Create(accountId, new NewTerm
        {
            TermKind = TermKind.Fee, Label = "B", ValueUnit = TermValueUnit.Amount, Value = 2m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        Assert.Equal(2, (await service.GetHistory(accountId))!.Count);
    }

    // ── Cross-owner addressing ───────────────────────────────────────────────

    [Fact]
    public async Task UpdateAndDelete_WithAnAccountsTermId_AreNotFoundAndLeaveItUntouched()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var accountId = await SeedAccountAsync(context);
        var service = Terms(context);

        var accountTerm = await service.Create(accountId, new NewTerm
        {
            TermKind = TermKind.Fee, Label = "Monthly fee", ValueUnit = TermValueUnit.Amount, Value = 4m,
            EffectiveFrom = new DateTime(2026, 1, 1),
        });

        Assert.False(await service.UpdateForContract(contractId, accountTerm.TermId, Rent(99m, new DateTime(2026, 1, 1))));
        Assert.False(await service.DeleteForContract(contractId, accountTerm.TermId));

        var row = await context.Terms.SingleAsync(t => t.TermId == accountTerm.TermId);
        Assert.Equal(4m, row.Value);
        Assert.Equal(accountId, row.AccountId);
    }

    [Fact]
    public async Task UpdateAndDelete_WithAnotherContractsTermId_AreNotFound()
    {
        await using var context = TestContextFactory.Create();
        var first = await SeedContractAsync(context);
        var second = await SeedContractAsync(context);
        var service = Terms(context);

        var term = await service.CreateForContract(first, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.False(await service.UpdateForContract(second, term.TermId, Rent(99m, new DateTime(2026, 10, 1))));
        Assert.False(await service.DeleteForContract(second, term.TermId));
        Assert.Equal(14500m, (await context.Terms.SingleAsync()).Value);
    }

    [Fact]
    public async Task UpdateAndDelete_WithAnAccountId_NeverReachAContractTerm()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var accountId = await SeedAccountAsync(context);
        var service = Terms(context);

        var term = await service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.False(await service.Update(accountId, term.TermId, Rent(99m, new DateTime(2026, 10, 1))));
        Assert.False(await service.Delete(accountId, term.TermId));
        Assert.Equal(14500m, (await context.Terms.SingleAsync()).Value);
    }

    // ── Supersession and the duplicate guard, within a contract ──────────────

    [Fact]
    public async Task GetCurrent_ReturnsTheLatestEntryPerSeriesOnOrBeforeToday()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);

        await service.CreateForContract(contractId, Rent(14000m, new DateTime(2024, 1, 1)));
        await service.CreateForContract(contractId, Rent(14500m, new DateTime(2025, 1, 1)));
        await service.CreateForContract(contractId, Rent(99999m, DateTime.UtcNow.Date.AddYears(5)));
        await service.CreateForContract(contractId, Rent(450m, new DateTime(2025, 1, 1), label: "Service charge"));

        var current = (await service.GetContractCurrent(contractId))!;

        Assert.Equal(2, current.Count);
        Assert.Equal(14500m, current.Single(t => t.Label == "Monthly rent").Value);
        Assert.Equal(450m, current.Single(t => t.Label == "Service charge").Value);
    }

    [Fact]
    public async Task Create_DuplicateSeriesEntryOnTheSameDate_IsAConflictAndFoldsTheLabel()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);
        var date = new DateTime(2026, 10, 1);

        await service.CreateForContract(contractId, Rent(14500m, date));

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.CreateForContract(contractId, Rent(15000m, date, label: "  monthly   RENT  ")));

        // A different label on the same date is a different series and is accepted.
        await service.CreateForContract(contractId, Rent(450m, date, label: "Service charge"));
        Assert.Equal(2, (await service.GetContractHistory(contractId))!.Count);
    }

    [Fact]
    public async Task GetHistory_IsNewestEffectiveFirstAndFiltersByKindAndAsOf()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);

        await service.CreateForContract(contractId, Rent(14000m, new DateTime(2024, 1, 1)));
        await service.CreateForContract(contractId, Rent(14500m, new DateTime(2025, 1, 1)));
        await service.CreateForContract(contractId, InterestRate(0.05m, new DateTime(2026, 1, 1)));

        var all = (await service.GetContractHistory(contractId))!;
        Assert.Equal([new DateTime(2026, 1, 1), new DateTime(2025, 1, 1), new DateTime(2024, 1, 1)],
            all.Select(t => t.EffectiveFrom));

        var fees = (await service.GetContractHistory(contractId, TermKind.Fee))!;
        Assert.Equal(2, fees.Count);

        var asOf = (await service.GetContractHistory(contractId, asOf: new DateTime(2024, 6, 1)))!;
        Assert.Single(asOf);
    }

    [Fact]
    public async Task Get_OnAMissingContract_IsNullSoTheControllerCan404()
    {
        await using var context = TestContextFactory.Create();

        Assert.Null(await Terms(context).GetContractHistory(Guid.NewGuid()));
        Assert.Null(await Terms(context).GetContractCurrent(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetCurrent_OnAContractWithNoTerms_IsAnEmptyListNotNull()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);

        Assert.Empty((await Terms(context).GetContractCurrent(contractId))!);
        Assert.Empty((await Terms(context).GetContractHistory(contractId))!);
    }

    // ── Shared rules, verified on the contract path ──────────────────────────
    // One validator serves both owners, so these are here to catch a regression that made a shared
    // rule owner-dependent — not to re-test the account path.

    [Fact]
    public async Task Create_LabelRules_AreUnchangedOnAContract()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);

        // A fee requires a label.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 1, 1), label: null)));

        // A rate refuses one.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateForContract(contractId, new NewTerm
            {
                TermKind = TermKind.InterestRate,
                Label = "Late payment",
                ValueUnit = TermValueUnit.Percentage,
                Value = 0.05m,
                EffectiveFrom = new DateTime(2026, 1, 1),
            }));
    }

    [Fact]
    public async Task Create_ValueCoherence_IsUnchangedOnAContract()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);

        // A percentage outside [-1, 1].
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateForContract(contractId, InterestRate(1.5m, new DateTime(2026, 1, 1))));

        // A negative amount.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateForContract(contractId, Rent(-1m, new DateTime(2026, 1, 1))));

        // An interval on a rate kind.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateForContract(contractId, new NewTerm
            {
                TermKind = TermKind.InterestRate,
                ValueUnit = TermValueUnit.Percentage,
                Value = 0.05m,
                Interval = Interval.Monthly,
                EffectiveFrom = new DateTime(2026, 1, 1),
            }));

        // An unsupported currency.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateForContract(contractId, Rent(1m, new DateTime(2026, 1, 1), currency: "XXX")));
    }

    [Fact]
    public async Task Create_PeriodicIntervalWithoutACount_StoresTheIdentityCadence()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);

        var created = await Terms(context).CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.Equal(Interval.Monthly, created.Interval);
        Assert.Equal(TermIntervalCount.Min, created.IntervalCount);
    }

    [Fact]
    public async Task Update_ReplacesTheRowInPlaceAndKeepsItsOwner()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);
        var term = await service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.True(await service.UpdateForContract(contractId, term.TermId, Rent(15000m, new DateTime(2026, 10, 1))));

        var row = await context.Terms.SingleAsync();
        Assert.Equal(term.TermId, row.TermId);
        Assert.Equal(15000m, row.Value);
        Assert.Equal(contractId, row.ContractId);
        Assert.Null(row.AccountId);
    }

    [Fact]
    public async Task Delete_RemovesTheTermAndLeavesTheContract()
    {
        await using var context = TestContextFactory.Create();
        var contractId = await SeedContractAsync(context);
        var service = Terms(context);
        var term = await service.CreateForContract(contractId, Rent(14500m, new DateTime(2026, 10, 1)));

        Assert.True(await service.DeleteForContract(contractId, term.TermId));

        Assert.Empty(await context.Terms.ToListAsync());
        Assert.NotNull(await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId));
    }
}
