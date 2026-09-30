using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using FinanceDtos = Odyssey.Dtos.Finance;
using Odyssey.Core.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

public class TaxStatementServiceTests
{
    private static readonly DateTime YearStart = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime YearEnd = new(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private static NewTaxStatement NewStatement(string currency = "USD") => new()
    {
        Name = "2024 assessment",
        FiscalYear = 2024,
        StartDate = YearStart,
        EndDate = YearEnd,
        BaseCurrencyCode = currency,
    };

    [Fact]
    public async Task CreateAndGet_RoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var created = await service.Create(NewStatement("USD"));
        var fetched = await service.Get(created.TaxStatementId);

        Assert.NotNull(fetched);
        Assert.Equal("2024 assessment", fetched!.Name);
        Assert.Equal(2024, fetched.FiscalYear);
        Assert.Equal(TaxStatementStatus.New, fetched.Status);
        Assert.Null(fetched.Archived);
    }

    [Fact]
    public async Task Create_EndBeforeStart_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var request = NewStatement("USD");
        request.EndDate = request.StartDate.AddDays(-1);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));
    }

    [Fact]
    public async Task Create_NegativeAssessedTax_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var request = NewStatement("USD");
        request.AssessedTax = -1m;

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));
    }

    [Fact]
    public async Task Create_UnknownCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(NewStatement("ZZZ")));
    }

    [Fact]
    public async Task Report_SumsTaxAndIncomeTags_ExcludesOffCurrency()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var taxTag = SeedTag(context, "Tax");
        var incomeTag = SeedTag(context, "Salary");

        // In-period base-currency: counts. Off-currency: excluded.
        SeedTransaction(context, account, taxTag, 1000m, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, taxTag, 500m, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), "EUR");
        SeedTransaction(context, account, incomeTag, 8000m, new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        // Out of period: ignored.
        SeedTransaction(context, account, taxTag, 999m, new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        await context.SaveChangesAsync();

        var created = await service.Create(NewStatement("USD"));
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            TaxTagIds = [taxTag.TransactionTagId],
            IncomeTagIds = [incomeTag.TransactionTagId],
        });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.NotNull(report);
        Assert.Equal(1000m, report!.Derived.PaidTax);
        Assert.Equal(8000m, report.Derived.ActualIncome);
        Assert.Equal(1, report.ExcludedTransactionCount);
        Assert.Equal(1, report.ExcludedCurrencies["EUR"]);
    }

    [Fact]
    public async Task Report_Reconciliation_ComputesVariances()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var taxTag = SeedTag(context, "Advance tax");
        var incomeTag = SeedTag(context, "Wages");
        SeedTransaction(context, account, taxTag, 209000m, new DateTime(2024, 11, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, incomeTag, 842000m, new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        await context.SaveChangesAsync();

        var request = NewStatement();
        request.AssessedTax = 210000m;
        request.DeclaredTotalIncome = 850000m;
        request.SettlementAmount = 1000m;
        var created = await service.Create(request);
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            TaxTagIds = [taxTag.TransactionTagId],
            IncomeTagIds = [incomeTag.TransactionTagId],
        });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.Equal(1000m, report!.Reconciliation.OutstandingTax);       // 210000 - 209000
        Assert.Equal(8000m, report.Reconciliation.IncomeVariance);        // 850000 - 842000
        Assert.Equal(0m, report.Reconciliation.SettlementVariance);       // 1000 - 1000
    }

    [Fact]
    public async Task Report_NullOperands_YieldNullVariances()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var created = await service.Create(NewStatement());
        var report = await service.GetReport(created.TaxStatementId);

        Assert.Null(report!.Reconciliation.OutstandingTax);       // AssessedTax absent
        Assert.Null(report.Reconciliation.IncomeVariance);        // DeclaredTotalIncome absent
        Assert.Null(report.Reconciliation.SettlementVariance);    // SettlementAmount absent
    }

    [Fact]
    public async Task Report_CrossYearSettlement_ExcludedFromPaidTaxWhenUntagged()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var taxTag = SeedTag(context, "Advance tax");
        // Advance tax within the income year.
        SeedTransaction(context, account, taxTag, 209000m, new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        // Settlement paid in 2025 carries NO tax-payment tag — must not count.
        SeedTransaction(context, account, null, 1000m, new DateTime(2025, 10, 15, 0, 0, 0, DateTimeKind.Utc), "USD");
        await context.SaveChangesAsync();

        var request = NewStatement();
        request.AssessedTax = 210000m;
        request.SettlementAmount = 1000m;
        var created = await service.Create(request);
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            TaxTagIds = [taxTag.TransactionTagId],
        });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.Equal(209000m, report!.Derived.PaidTax);           // settlement excluded
        Assert.Equal(1000m, report.Reconciliation.OutstandingTax); // surfaces the balance
        Assert.Equal(0m, report.Reconciliation.SettlementVariance);
    }

    [Fact]
    public async Task Report_DerivedNetWorth_FromBaseCurrencyAccountsByType()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var asset = SeedAccount(context, ContextAccountType.SavingsAccount, "USD");
        var liability = SeedAccount(context, ContextAccountType.Mortgage, "USD");
        var offCurrency = SeedAccount(context, ContextAccountType.SavingsAccount, "EUR");
        SeedTransaction(context, asset, null, 2485000m, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, liability, null, -900000m, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, offCurrency, null, 50000m, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), "EUR");
        await context.SaveChangesAsync();

        var request = NewStatement("USD");
        request.DeclaredNetWorth = 1600000m;
        var created = await service.Create(request);

        var report = await service.GetReport(created.TaxStatementId);

        Assert.True(report!.Derived.Available);
        Assert.Equal(2485000m, report.Derived.TotalAssets);
        Assert.Equal(900000m, report.Derived.TotalLiabilities);
        Assert.Equal(1585000m, report.Derived.NetWorth);
        Assert.Equal(15000m, report.Reconciliation.NetWorthVariance); // 1600000 - 1585000
    }

    [Fact]
    public async Task UpdateStatus_StampsStatusAndComment()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var created = await service.Create(NewStatement("USD"));
        var before = created.StatusChangedAt;

        var updated = await service.UpdateStatus(created.TaxStatementId, new UpdateTaxStatementStatus
        {
            Status = TaxStatementStatus.Flagged,
            StatusComment = "Mismatch in assets",
        });

        Assert.Equal(TaxStatementStatus.Flagged, updated!.Status);
        Assert.Equal("Mismatch in assets", updated.StatusComment);
        Assert.True(updated.StatusChangedAt >= before);
    }

    [Fact]
    public async Task UpdateTags_UnknownTag_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var created = await service.Create(NewStatement("USD"));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
            {
                TaxTagIds = [Guid.NewGuid()],
            }));
    }

    [Fact]
    public async Task Delete_ArchivesAndHidesFromList()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var created = await service.Create(NewStatement("USD"));
        var deleted = await service.Delete(created.TaxStatementId);

        Assert.True(deleted);
        Assert.Empty((await service.ListAsync(new TaxStatementsQueryParams())).Items);
    }

    [Fact]
    public async Task AttachFile_PersistsFileType()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var created = await service.Create(NewStatement("USD"));

        var result = await service.AttachFile(
            created.TaxStatementId, Guid.NewGuid(), "user-1", FinanceDtos.TaxStatementFileType.TaxAssessment);

        Assert.Equal(Context.TaxStatementFileType.TaxAssessment, result.FileType);
    }

    [Fact]
    public async Task AttachFile_DefaultsToOtherFileType()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var created = await service.Create(NewStatement("USD"));

        var result = await service.AttachFile(created.TaxStatementId, Guid.NewGuid(), "user-1");

        Assert.Equal(Context.TaxStatementFileType.Other, result.FileType);
    }

    // ── Settlement role + range ──────────────────────────────────────────────

    [Fact]
    public async Task SettlementRange_DefaultsToPeriodPlusOneYear()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var created = await service.Create(NewStatement());

        Assert.False(created.SettlementRangeCustom);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), created.SettlementStartDate);
        Assert.Equal(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), created.SettlementEndDate);
        Assert.Equal(DateTimeKind.Utc, created.SettlementStartDate.Kind);
    }

    [Fact]
    public async Task SettlementRange_Default_FollowsAPeriodChange()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var created = await service.Create(NewStatement());

        var updated = await service.Update(created.TaxStatementId, new UpdateTaxStatement
        {
            Name = created.Name,
            FiscalYear = 2023,
            StartDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2023, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
        });

        Assert.False(updated!.SettlementRangeCustom);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), updated.SettlementStartDate);
        Assert.Equal(new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc), updated.SettlementEndDate);
    }

    [Fact]
    public void SettlementRange_Default_ClampsLeapDay()
    {
        var (start, _) = TaxSettlementRange.Default(new DateTime(2024, 2, 29), new DateTime(2024, 12, 31));

        Assert.Equal(new DateTime(2025, 2, 28), start);
    }

    [Fact]
    public async Task SettlementRange_Custom_IsStoredAndReported()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var request = NewStatement();
        request.SettlementStartDate = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        request.SettlementEndDate = new DateTime(2025, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        var created = await service.Create(request);

        Assert.True(created.SettlementRangeCustom);
        Assert.Equal(request.SettlementStartDate, created.SettlementStartDate);
        Assert.Equal(request.SettlementEndDate, created.SettlementEndDate);
    }

    [Fact]
    public async Task SettlementRange_OnlyOneEnd_Rejected()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var request = NewStatement();
        request.SettlementStartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));
    }

    [Fact]
    public async Task SettlementRange_EndBeforeStart_Rejected()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var request = NewStatement();
        request.SettlementStartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        request.SettlementEndDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));
    }

    [Fact]
    public async Task UpdateTags_SettlementRole_RoundTrips()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var tag = SeedTag(context, "Tax refund");
        await context.SaveChangesAsync();
        var created = await service.Create(NewStatement());

        var updated = await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            SettlementTagIds = [tag.TransactionTagId],
        });

        Assert.Equal([tag.TransactionTagId], updated!.SettlementTagIds);
        Assert.Empty(updated.TaxTagIds);
    }

    [Fact]
    public async Task UpdateTags_TagInBothTaxPaymentAndSettlement_Rejected()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var tag = SeedTag(context, "Tax");
        await context.SaveChangesAsync();
        var created = await service.Create(NewStatement());

        var ex = await Assert.ThrowsAsync<DomainUnprocessableException>(() => service.UpdateTags(created.TaxStatementId,
            new UpdateTaxStatementTags { TaxTagIds = [tag.TransactionTagId], SettlementTagIds = [tag.TransactionTagId] }));

        Assert.True(ex.Errors!.ContainsKey(nameof(UpdateTaxStatementTags.SettlementTagIds)));
        Assert.Empty((await service.Get(created.TaxStatementId))!.TaxTagIds);
    }

    [Fact]
    public async Task Report_SettlementPaid_SumsSettlementTagsWithinTheSettlementRange()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var taxTag = SeedTag(context, "Advance tax");
        var settlementTag = SeedTag(context, "Tax settlement");
        var refundTag = SeedTag(context, "Tax refund");
        SeedTransaction(context, account, taxTag, 209000m, new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        // In the default settlement range (2025): counts, the refund with its sign.
        SeedTransaction(context, account, settlementTag, 1500m, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, refundTag, -500m, new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        // Inside the income year, not the settlement range: ignored.
        SeedTransaction(context, account, settlementTag, 777m, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        // Off-currency: excluded and tallied.
        SeedTransaction(context, account, settlementTag, 50m, new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc), "EUR");
        await context.SaveChangesAsync();

        var request = NewStatement();
        request.AssessedTax = 210000m;
        request.SettlementAmount = 1000m;
        var created = await service.Create(request);
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            TaxTagIds = [taxTag.TransactionTagId],
            SettlementTagIds = [settlementTag.TransactionTagId, refundTag.TransactionTagId],
        });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.Equal(1000m, report!.Derived.SettlementPaid);                   // 1500 − 500
        Assert.Equal(209000m, report.Derived.PaidTax);
        Assert.Equal(0m, report.Reconciliation.SettlementRecordedVariance);    // 1000 − 1000
        Assert.Equal(0m, report.Reconciliation.AdvancePaidVariance);           // (210000 − 1000) − 209000
        Assert.Equal(1, report.ExcludedCurrencies["EUR"]);
    }

    [Fact]
    public async Task Report_SettlementPaid_UsesACustomRange()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var settlementTag = SeedTag(context, "Tax settlement");
        SeedTransaction(context, account, settlementTag, 300m, new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, settlementTag, 400m, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        await context.SaveChangesAsync();

        var request = NewStatement();
        request.SettlementStartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        request.SettlementEndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var created = await service.Create(request);
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            SettlementTagIds = [settlementTag.TransactionTagId],
        });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.Equal(400m, report!.Derived.SettlementPaid);
        Assert.Null(report.Reconciliation.SettlementRecordedVariance);        // SettlementAmount absent
        Assert.Null(report.Reconciliation.AdvancePaidVariance);
    }

    private static UpdateTaxStatement UpdateFrom(ExistingTaxStatement s) => new()
    {
        Name = s.Name,
        FiscalYear = s.FiscalYear,
        StartDate = s.StartDate,
        EndDate = s.EndDate,
        BaseCurrencyCode = s.BaseCurrencyCode,
    };

    [Fact]
    public async Task Update_SettlementRangeHalfSet_Rejected()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var created = await service.Create(NewStatement());
        var request = UpdateFrom(created);
        request.SettlementEndDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(created.TaxStatementId, request));

        Assert.True(ex.Errors!.ContainsKey(nameof(UpdateTaxStatement.SettlementStartDate)));
    }

    [Fact]
    public async Task Update_SettlementRangeInverted_Rejected()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var created = await service.Create(NewStatement());
        var request = UpdateFrom(created);
        request.SettlementStartDate = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        request.SettlementEndDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(created.TaxStatementId, request));

        Assert.True(ex.Errors!.ContainsKey(nameof(UpdateTaxStatement.SettlementEndDate)));
    }

    [Fact]
    public async Task Update_NullRange_RevertsACustomRangeToTheDefault()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var request = NewStatement();
        request.SettlementStartDate = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        request.SettlementEndDate = new DateTime(2025, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        var created = await service.Create(request);

        var updated = await service.Update(created.TaxStatementId, UpdateFrom(created));

        Assert.False(updated!.SettlementRangeCustom);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), updated.SettlementStartDate);
    }

    [Theory]
    [InlineData(9999)]
    [InlineData(1899)]
    public async Task Create_PeriodDateOutOfBounds_Rejected(int year)
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var request = NewStatement();
        request.StartDate = new DateTime(Math.Min(year, 2024), 1, 1, 0, 0, 0, DateTimeKind.Utc);
        request.EndDate = new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));
    }

    [Fact]
    public async Task Create_SettlementDateOutOfBounds_Rejected()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var request = NewStatement();
        request.SettlementStartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        request.SettlementEndDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));

        Assert.True(ex.Errors!.ContainsKey(nameof(NewTaxStatement.SettlementEndDate)));
    }

    [Fact]
    public async Task Get_LegacyRowInTheLastCalendarYear_StillProjects()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        // A row written before the date bound existed: the +1-year default must not throw on read.
        var id = Guid.NewGuid();
        context.TaxStatements.Add(new TaxStatement
        {
            TaxStatementId = id,
            Name = "Legacy",
            FiscalYear = 2024,
            StartDate = YearStart,
            EndDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var fetched = await service.Get(id);
        var report = await service.GetReport(id);

        Assert.Equal(new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc), fetched!.SettlementEndDate);
        Assert.NotNull(report);
    }

    [Fact]
    public void Overlap_ReturnsOnlyTagsInBothSets()
    {
        Assert.Equal(["b"], TaxSettlementRange.Overlap(["a", "b"], ["b", "c"]));
        Assert.Empty(TaxSettlementRange.Overlap(["a"], ["c"]));
    }

    [Fact]
    public async Task UpdateTags_NullSettlementTagIds_KeepsTheStoredSettlementTags()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var settlementTag = SeedTag(context, "Tax refund");
        var incomeTag = SeedTag(context, "Salary");
        await context.SaveChangesAsync();
        var created = await service.Create(NewStatement());
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags { SettlementTagIds = [settlementTag.TransactionTagId] });

        var updated = await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags { IncomeTagIds = [incomeTag.TransactionTagId] });

        Assert.Equal([settlementTag.TransactionTagId], updated!.SettlementTagIds);
        Assert.Equal([incomeTag.TransactionTagId], updated.IncomeTagIds);
    }

    [Fact]
    public async Task UpdateTags_EmptySettlementTagIds_ClearsThem()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var settlementTag = SeedTag(context, "Tax refund");
        await context.SaveChangesAsync();
        var created = await service.Create(NewStatement());
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags { SettlementTagIds = [settlementTag.TransactionTagId] });

        var updated = await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags { SettlementTagIds = [] });

        Assert.Empty(updated!.SettlementTagIds);
    }

    [Fact]
    public async Task UpdateTags_NullSettlementTagIds_StillGuardsTheOverlapAgainstStoredTags()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var tag = SeedTag(context, "Tax");
        await context.SaveChangesAsync();
        var created = await service.Create(NewStatement());
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags { SettlementTagIds = [tag.TransactionTagId] });

        await Assert.ThrowsAsync<DomainUnprocessableException>(() => service.UpdateTags(created.TaxStatementId,
            new UpdateTaxStatementTags { TaxTagIds = [tag.TransactionTagId] }));
    }

    [Fact]
    public async Task Report_SettlementWindow_IncludesTheWholeLastDay()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var tag = SeedTag(context, "Tax settlement");
        // Default window is 2025-01-01 → 2025-12-31, stored at midnight but read as whole days (issue #238).
        SeedTransaction(context, account, tag, 1m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, tag, 10m, new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, tag, 1000m, new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc), "USD");
        // Just outside on either side: excluded.
        SeedTransaction(context, account, tag, 100m, new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, tag, 10000m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        await context.SaveChangesAsync();
        var created = await service.Create(NewStatement());
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags { SettlementTagIds = [tag.TransactionTagId] });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.Equal(1011m, report!.Derived.SettlementPaid);
    }

    [Fact]
    public async Task Report_Period_IncludesTheWholeEndDay()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var taxTag = SeedTag(context, "Tax");
        var incomeTag = SeedTag(context, "Salary");
        // YearEnd is 2024-12-31 at midnight; a transaction later that day belongs to the period (issue #238).
        SeedTransaction(context, account, taxTag, 1m, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, taxTag, 10m, new DateTime(2024, 12, 31, 14, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, incomeTag, 500m, new DateTime(2024, 12, 31, 23, 59, 0, DateTimeKind.Utc), "USD");
        // The first instant of the next day, and the last one before the period: excluded.
        SeedTransaction(context, account, taxTag, 100m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, incomeTag, 1000m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), "USD");
        SeedTransaction(context, account, taxTag, 10000m, new DateTime(2023, 12, 31, 23, 59, 59, DateTimeKind.Utc), "USD");
        await context.SaveChangesAsync();
        var created = await service.Create(NewStatement());
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            TaxTagIds = [taxTag.TransactionTagId],
            IncomeTagIds = [incomeTag.TransactionTagId],
        });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.Equal(11m, report!.Derived.PaidTax);
        Assert.Equal(500m, report.Derived.ActualIncome);
    }

    [Fact]
    public async Task Create_EndBeforeStart_IsKeyedOnEndDate()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var request = NewStatement();
        request.EndDate = request.StartDate.AddDays(-1);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));

        Assert.True(ex.Errors!.ContainsKey(nameof(NewTaxStatement.EndDate)));
    }

    [Fact]
    public async Task Report_SettlementPaid_CountsATransactionWithTwoSettlementTagsOnce()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var settlement = SeedTag(context, "Tax settlement");
        var refund = SeedTag(context, "Tax refund");
        context.Transactions.Add(new Transaction
        {
            TransactionId = Guid.NewGuid(),
            Description = "Test",
            Amount = 700m,
            TimeStamp = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            AccountId = account.AccountId,
            TransactionTags = [settlement, refund],
            CurrencyCode = "USD",
        });
        await context.SaveChangesAsync();
        var created = await service.Create(NewStatement());
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            SettlementTagIds = [settlement.TransactionTagId, refund.TransactionTagId],
        });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.Equal(700m, report!.Derived.SettlementPaid);
    }

    [Fact]
    public async Task Report_CustomRangeOverlappingThePeriod_DoesNotCountAnAdvancePaymentTwice()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);
        var account = SeedAccount(context, ContextAccountType.CheckingAccount, "USD");
        var taxTag = SeedTag(context, "Advance tax");
        var settlementTag = SeedTag(context, "Tax settlement");
        context.Transactions.Add(new Transaction
        {
            TransactionId = Guid.NewGuid(),
            Description = "Test",
            Amount = 500m,
            TimeStamp = new DateTime(2024, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            AccountId = account.AccountId,
            TransactionTags = [taxTag, settlementTag],
            CurrencyCode = "USD",
        });
        await context.SaveChangesAsync();
        var request = NewStatement();
        request.SettlementStartDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        request.SettlementEndDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var created = await service.Create(request);
        await service.UpdateTags(created.TaxStatementId, new UpdateTaxStatementTags
        {
            TaxTagIds = [taxTag.TransactionTagId],
            SettlementTagIds = [settlementTag.TransactionTagId],
        });

        var report = await service.GetReport(created.TaxStatementId);

        Assert.Equal(500m, report!.Derived.PaidTax);
        Assert.Equal(0m, report.Derived.SettlementPaid);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private static Account SeedAccount(OdysseyContext context, ContextAccountType type, string currency)
    {
        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = $"{type}",
            Description = "Test",
            Opened = DateTime.UtcNow,
            AccountType = type,
            CurrencyCode = currency,
        };
        context.Accounts.Add(account);
        return account;
    }

    private static TransactionTag SeedTag(OdysseyContext context, string name)
    {
        var tag = new TransactionTag { TransactionTagId = Guid.NewGuid(), Name = name };
        context.TransactionTags.Add(tag);
        return tag;
    }

    private static void SeedTransaction(
        OdysseyContext context, Account account, TransactionTag? tag, decimal amount, DateTime timestamp, string currency)
    {
        context.Transactions.Add(new Transaction
        {
            TransactionId = Guid.NewGuid(),
            Description = "Test",
            Amount = amount,
            TimeStamp = timestamp,
            AccountId = account.AccountId,
            TransactionTags = tag is null ? new List<TransactionTag>() : new List<TransactionTag> { tag },
            CurrencyCode = currency,
        });
    }
}
