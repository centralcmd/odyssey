using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using Context = Odyssey.Context;
using static Odyssey.Core.Tests.ClientDateHandlingTests;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #242, the converting half: <see cref="DateTimeKind.Local"/> inputs under a pinned UTC+9 zone,
/// with every expectation a literal UTC instant rather than a <c>ToUniversalTime()</c> call that
/// mirrors the implementation.
///
/// The "…OnlyAfterNormalization" cases are the ordering proof. Each input is valid only once
/// converted — raw, its ticks fail the service's check (end before start, or not on a UTC midnight)
/// — so moving the normalization after validation turns each of them into a rejection.
/// </summary>
[Collection(LocalTimeZoneCollection.Name)]
public class LocalTimeZoneDateHandlingTests
{
    private static DateTime Tokyo(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Local);

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [SkippableFact]
    public void NormalizeToUtc_Local_ConvertsByTheZoneOffset()
    {
        using var zone = LocalTimeZoneScope.UseTokyo();

        AssertUtc(Utc(2024, 12, 31, 20), DateTimeNormalization.NormalizeToUtc(Tokyo(2025, 1, 1, 5)));
        AssertUtc(Utc(2024, 12, 31, 20), DateTimeNormalization.NormalizeToUtc((DateTime?)Tokyo(2025, 1, 1, 5)));
    }

    [Fact]
    public void NormalizeToUtc_Nullable_NullStaysNull()
    {
        Assert.Null(DateTimeNormalization.NormalizeToUtc((DateTime?)null));
    }

    [Fact]
    public void NormalizeToUtc_Nullable_UnspecifiedKeepsTicksAndBecomesUtc()
    {
        var unspecified = new DateTime(2025, 1, 1, 5, 6, 7, DateTimeKind.Unspecified).AddTicks(89);

        var normalized = DateTimeNormalization.NormalizeToUtc((DateTime?)unspecified);

        Assert.NotNull(normalized);
        Assert.Equal(DateTimeKind.Utc, normalized!.Value.Kind);
        Assert.Equal(unspecified.Ticks, normalized.Value.Ticks);
    }

    [Fact]
    public void NormalizeToUtc_Nullable_UtcIsUnchanged()
    {
        var utc = Utc(2025, 1, 1, 5);

        AssertUtc(utc, DateTimeNormalization.NormalizeToUtc((DateTime?)utc));
    }

    [SkippableFact]
    public async Task Account_LocalOpenedAndClosed_AreConvertedOnCreateAndUpdate()
    {
        using var zone = LocalTimeZoneScope.UseTokyo();
        await using var context = TestContextFactory.Create();
        var service = NewAccountService(context);

        var created = await service.Create(AccountRequest(Tokyo(2019, 3, 1, 9), closed: Tokyo(2024, 6, 15, 8)));

        AssertUtc(Utc(2019, 3, 1, 0), created.Opened);
        AssertUtc(Utc(2024, 6, 14, 23), created.Closed);

        var updated = await service.Update(created.AccountId, AccountRequest(Tokyo(2018, 1, 1, 3), closed: Tokyo(2025, 2, 1, 12)));

        AssertUtc(Utc(2017, 12, 31, 18), updated!.Opened);
        AssertUtc(Utc(2025, 2, 1, 3), updated.Closed);
    }

    [SkippableFact]
    public async Task Transaction_LocalTimeStamp_IsConvertedOnUpdate()
    {
        using var zone = LocalTimeZoneScope.UseTokyo();
        await using var context = TestContextFactory.Create();
        var (service, accountId) = await NewTransactionServiceAsync(context);
        var created = await service.Create(new NewTransaction
        {
            Description = "Coffee", Amount = 3, AccountId = accountId, TimeStamp = Utc(2021, 7, 1, 0),
        });

        var updated = await service.Update(created.TransactionId, new NewTransaction
        {
            Description = "Coffee", Amount = 3, AccountId = accountId, TimeStamp = Tokyo(2021, 7, 4, 5),
        });

        AssertUtc(Utc(2021, 7, 3, 20), updated!.TimeStamp);
    }

    [SkippableFact]
    public async Task TaxStatement_EveryLocalDate_IsConvertedOnCreateAndUpdate()
    {
        using var zone = LocalTimeZoneScope.UseTokyo();
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context, new FixedTimeProvider(Now));

        var created = await service.Create(new NewTaxStatement
        {
            Name = "2024 assessment",
            FiscalYear = 2024,
            BaseCurrencyCode = "USD",
            StartDate = Tokyo(2024, 1, 1, 9),
            EndDate = Tokyo(2024, 12, 31, 9),
            SettledAtUtc = Tokyo(2025, 8, 1, 10),
            SettlementStartDate = Tokyo(2025, 1, 1, 9),
            SettlementEndDate = Tokyo(2025, 12, 31, 9),
            FiledAtUtc = Tokyo(2025, 3, 15, 5),
            TaxOfficeApprovedAtUtc = Tokyo(2025, 6, 1, 9),
        });

        AssertUtc(Utc(2024, 1, 1, 0), created.StartDate);
        AssertUtc(Utc(2024, 12, 31, 0), created.EndDate);
        AssertUtc(Utc(2025, 8, 1, 1), created.SettledAtUtc);
        AssertUtc(Utc(2025, 1, 1, 0), created.SettlementStartDate);
        AssertUtc(Utc(2025, 12, 31, 0), created.SettlementEndDate);
        AssertUtc(Utc(2025, 3, 14, 20), created.FiledAtUtc);
        AssertUtc(Utc(2025, 6, 1, 0), created.TaxOfficeApprovedAtUtc);

        var updated = await service.Update(created.TaxStatementId, new UpdateTaxStatement
        {
            Name = "2024 assessment",
            FiscalYear = 2024,
            BaseCurrencyCode = "USD",
            StartDate = Tokyo(2024, 1, 2, 9),
            EndDate = Tokyo(2024, 12, 30, 9),
            SettledAtUtc = Tokyo(2025, 8, 2, 10),
            SettlementStartDate = Tokyo(2025, 1, 2, 9),
            SettlementEndDate = Tokyo(2025, 12, 30, 9),
            FiledAtUtc = Tokyo(2025, 3, 16, 5),
            TaxOfficeApprovedAtUtc = Tokyo(2025, 6, 2, 9),
        });

        AssertUtc(Utc(2024, 1, 2, 0), updated!.StartDate);
        AssertUtc(Utc(2024, 12, 30, 0), updated.EndDate);
        AssertUtc(Utc(2025, 8, 2, 1), updated.SettledAtUtc);
        AssertUtc(Utc(2025, 1, 2, 0), updated.SettlementStartDate);
        AssertUtc(Utc(2025, 12, 30, 0), updated.SettlementEndDate);
        AssertUtc(Utc(2025, 3, 15, 20), updated.FiledAtUtc);
        AssertUtc(Utc(2025, 6, 2, 0), updated.TaxOfficeApprovedAtUtc);
    }

    // Raw, End (Dec 31 23:30) is before Start (Jan 1 08:00) and is rejected; converted, Start is
    // Dec 31 23:00Z and the period is valid. Same shape for the settlement range.
    [SkippableFact]
    public async Task TaxStatement_PeriodAndSettlementRange_AreValidOnlyAfterNormalization()
    {
        using var zone = LocalTimeZoneScope.UseTokyo();
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context, new FixedTimeProvider(Now));

        var created = await service.Create(new NewTaxStatement
        {
            Name = "2024 assessment",
            FiscalYear = 2024,
            BaseCurrencyCode = "USD",
            StartDate = Tokyo(2024, 1, 1, 8),
            EndDate = Utc(2023, 12, 31, 23, 30),
            SettlementStartDate = Tokyo(2025, 1, 1, 8),
            SettlementEndDate = Utc(2024, 12, 31, 23, 30),
        });

        AssertUtc(Utc(2023, 12, 31, 23), created.StartDate);
        AssertUtc(Utc(2024, 12, 31, 23), created.SettlementStartDate);

        var updated = await service.Update(created.TaxStatementId, new UpdateTaxStatement
        {
            Name = "2024 assessment",
            FiscalYear = 2024,
            BaseCurrencyCode = "USD",
            StartDate = Tokyo(2024, 1, 1, 8),
            EndDate = Utc(2023, 12, 31, 23, 30),
            SettlementStartDate = Tokyo(2025, 1, 1, 8),
            SettlementEndDate = Utc(2024, 12, 31, 23, 30),
        });

        AssertUtc(Utc(2023, 12, 31, 23), updated!.StartDate);
    }

    // The mirror image: raw ticks are ordered (08:00 after 00:00), converted they are not.
    [SkippableFact]
    public async Task TaxStatement_EndBeforeStartOnlyAfterNormalization_IsRejected()
    {
        using var zone = LocalTimeZoneScope.UseTokyo();
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewTaxStatement
        {
            Name = "2024 assessment",
            FiscalYear = 2024,
            BaseCurrencyCode = "USD",
            StartDate = Utc(2024, 1, 1, 0),
            EndDate = Tokyo(2024, 1, 1, 8),
        }));
    }

    // 09:00 in Tokyo is 00:00Z: a UTC-midnight all-day span only once converted. Validated raw, the
    // 09:00 time of day fails the all-day check.
    [SkippableFact]
    public async Task CalendarEvent_AllDayOnUtcMidnightOnlyAfterNormalization_IsAcceptedOnCreateAndUpdate()
    {
        using var zone = LocalTimeZoneScope.UseTokyo();
        await using var context = TestContextFactory.Create();
        var calendar = new Context.Calendar { Name = "Personal" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        var service = new CalendarEventService(context, new StubJournalLimits(), new FixedTimeProvider(Now));

        var created = await service.Create(new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Holiday",
            StartDateTime = Tokyo(2026, 10, 1, 9),
            EndDateTime = Tokyo(2026, 10, 2, 9),
            IsAllDay = true,
        }, "user-id");

        AssertUtc(Utc(2026, 10, 1, 0), created.StartDateTime);
        AssertUtc(Utc(2026, 10, 2, 0), created.EndDateTime);

        var updated = await service.Update(created.CalendarEventId, new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId,
            Title = "Holiday",
            StartDateTime = Tokyo(2026, 10, 5, 9),
            EndDateTime = Tokyo(2026, 10, 7, 9),
            IsAllDay = true,
        }, "user-id");

        AssertUtc(Utc(2026, 10, 5, 0), updated!.StartDateTime);
        AssertUtc(Utc(2026, 10, 7, 0), updated.EndDateTime);
    }

    // Update carries two ordering signals: the all-day UTC-midnight check, and RecurrenceEndDate
    // (00:00Z) being on-or-after StartDateTime (09:00 Tokyo = 00:00Z) — raw, it is nine hours before.
    [SkippableFact]
    public async Task RecurrencePattern_LocalTimes_AreConvertedBeforeValidationOnCreateAndUpdate()
    {
        using var zone = LocalTimeZoneScope.UseTokyo();
        await using var context = TestContextFactory.Create();
        var calendar = new Context.Calendar { Name = "Personal" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        var service = new RecurrencePatternService(context, new StubJournalLimits(), new FixedTimeProvider(Now));

        var created = await service.Create(new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Gym",
            StartDateTime = Tokyo(2026, 10, 1, 9),
            EndDateTime = Tokyo(2026, 10, 2, 9),
            IsAllDay = true,
            Frequency = RecurrenceFrequency.Daily,
            RecurrenceEndDate = Tokyo(2026, 10, 5, 9),
        }, "user-id");

        AssertUtc(Utc(2026, 10, 1, 0), created.StartDateTime);
        AssertUtc(Utc(2026, 10, 2, 0), created.EndDateTime);
        AssertUtc(Utc(2026, 10, 5, 0), created.RecurrenceEndDate);

        var updated = await service.Update(created.RecurrencePatternId, new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Gym",
            StartDateTime = Tokyo(2026, 10, 3, 9),
            EndDateTime = Tokyo(2026, 10, 4, 9),
            IsAllDay = true,
            Frequency = RecurrenceFrequency.Daily,
            RecurrenceEndDate = Utc(2026, 10, 3, 0),
        }, "user-id");

        Assert.NotNull(updated);
        AssertUtc(Utc(2026, 10, 3, 0), updated!.StartDateTime);
        AssertUtc(Utc(2026, 10, 4, 0), updated.EndDateTime);
        AssertUtc(Utc(2026, 10, 3, 0), updated.RecurrenceEndDate);
    }
}
