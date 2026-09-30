using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using Context = Odyssey.Context;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #242: an omitted nullable date on update keeps the stored value rather than resetting to
/// now, and every client-supplied date is normalized to UTC before it is validated or persisted.
/// </summary>
public class ClientDateHandlingTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Opened2019 = new(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class StubJournalLimits : IJournalLimitsLookup
    {
        public Task<JournalLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalLimits(
                PhotoMaxLinksPerKind: 50,
                PhotoMaxAlbumMembers: 500,
                JournalEntryMaxLinksPerKind: 50,
                JournalTaskMaxLinksPerKind: 50,
                PhotoMetadataReadBytes: 1024 * 1024,
                PhotoMetadataExtractionTimeoutSeconds: 5,
                CalendarMaxWindowDays: 92,
                CalendarMaxEventDurationDays: 366,
                RecurrenceMaxGeneratedOccurrences: 500,
                IsDegraded: false));
    }

    private static AccountService NewAccountService(Context.OdysseyContext context) =>
        new(context, TestContextFactory.EmptyContactLookup(), new FixedTimeProvider(Now));

    private static NewAccount AccountRequest(DateTime? opened, DateTime? closed = null) => new()
    {
        Name = "Checking",
        Description = "Primary",
        AccountType = DtoAccountType.CheckingAccount,
        CurrencyCode = "USD",
        Archived = false,
        Opened = opened,
        Closed = closed,
    };

    [Fact]
    public async Task AccountCreate_WithoutOpened_DefaultsToNow()
    {
        await using var context = TestContextFactory.Create();
        var service = NewAccountService(context);

        var created = await service.Create(AccountRequest(opened: null));

        Assert.Equal(Now, created.Opened);
    }

    [Fact]
    public async Task AccountUpdate_WithoutOpened_KeepsStoredDate()
    {
        await using var context = TestContextFactory.Create();
        var service = NewAccountService(context);
        var created = await service.Create(AccountRequest(Opened2019));

        var updated = await service.Update(created.AccountId, AccountRequest(opened: null));

        Assert.NotNull(updated);
        Assert.Equal(Opened2019, updated!.Opened);
        Assert.Equal(Opened2019, (await context.Accounts.FindAsync(created.AccountId))!.Opened);
    }

    [Fact]
    public async Task AccountUpdate_WithOpened_AppliesIt()
    {
        await using var context = TestContextFactory.Create();
        var service = NewAccountService(context);
        var created = await service.Create(AccountRequest(Opened2019));
        var reopened = new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        var updated = await service.Update(created.AccountId, AccountRequest(reopened));

        Assert.Equal(reopened, updated!.Opened);
    }

    [Fact]
    public async Task AccountUpdate_NullClosed_StillClearsIt()
    {
        await using var context = TestContextFactory.Create();
        var service = NewAccountService(context);
        var created = await service.Create(AccountRequest(Opened2019, closed: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var updated = await service.Update(created.AccountId, AccountRequest(Opened2019, closed: null));

        Assert.Null(updated!.Closed);
    }

    [Fact]
    public async Task AccountCreateAndUpdate_NormalizeOpenedAndClosedToUtc()
    {
        await using var context = TestContextFactory.Create();
        var service = NewAccountService(context);
        var unspecified = new DateTime(2019, 3, 1, 8, 30, 0, DateTimeKind.Unspecified);
        var local = new DateTime(2024, 6, 15, 18, 45, 0, DateTimeKind.Local);

        var created = await service.Create(AccountRequest(unspecified, closed: local));

        AssertUtc(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), created.Opened);
        AssertUtc(local.ToUniversalTime(), created.Closed);

        var updated = await service.Update(created.AccountId, AccountRequest(local, closed: unspecified));

        AssertUtc(local.ToUniversalTime(), updated!.Opened);
        AssertUtc(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), updated.Closed);
    }

    [Fact]
    public async Task TransactionCreate_WithoutTimeStamp_DefaultsToNow()
    {
        await using var context = TestContextFactory.Create();
        var (service, accountId) = await NewTransactionServiceAsync(context);

        var created = await service.Create(new NewTransaction { Description = "Coffee", Amount = 3, AccountId = accountId });

        Assert.Equal(Now, created.TimeStamp);
    }

    [Fact]
    public async Task TransactionUpdate_WithoutTimeStamp_KeepsStoredDate()
    {
        await using var context = TestContextFactory.Create();
        var (service, accountId) = await NewTransactionServiceAsync(context);
        var original = new DateTime(2021, 7, 4, 9, 0, 0, DateTimeKind.Utc);
        var created = await service.Create(new NewTransaction
        {
            Description = "Coffee", Amount = 3, AccountId = accountId, TimeStamp = original,
        });

        var updated = await service.Update(created.TransactionId, new NewTransaction
        {
            Description = "Coffee (edited)", Amount = 4, AccountId = accountId,
        });

        Assert.NotNull(updated);
        Assert.Equal(original, updated!.TimeStamp);
    }

    [Fact]
    public async Task TaxStatementCreateAndUpdate_NormalizeEveryDateToUtc()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context, new FixedTimeProvider(Now));
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var end = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Local);
        var settled = new DateTime(2025, 8, 1, 10, 0, 0, DateTimeKind.Local);
        var settlementStart = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var settlementEnd = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);
        var filed = new DateTime(2025, 3, 15, 14, 0, 0, DateTimeKind.Local);
        var approved = new DateTime(2025, 6, 1, 9, 0, 0, DateTimeKind.Unspecified);

        var created = await service.Create(new NewTaxStatement
        {
            Name = "2024 assessment",
            FiscalYear = 2024,
            StartDate = start,
            EndDate = end,
            BaseCurrencyCode = "USD",
            SettledAtUtc = settled,
            SettlementStartDate = settlementStart,
            SettlementEndDate = settlementEnd,
            FiledAtUtc = filed,
            TaxOfficeApprovedAtUtc = approved,
        });

        AssertTaxStatementDates(created);

        var updated = await service.Update(created.TaxStatementId, new UpdateTaxStatement
        {
            Name = "2024 assessment",
            FiscalYear = 2024,
            StartDate = start,
            EndDate = end,
            BaseCurrencyCode = "USD",
            SettledAtUtc = settled,
            SettlementStartDate = settlementStart,
            SettlementEndDate = settlementEnd,
            FiledAtUtc = filed,
            TaxOfficeApprovedAtUtc = approved,
        });

        AssertTaxStatementDates(updated!);

        void AssertTaxStatementDates(ExistingTaxStatement s)
        {
            AssertUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), s.StartDate);
            AssertUtc(end.ToUniversalTime(), s.EndDate);
            AssertUtc(settled.ToUniversalTime(), s.SettledAtUtc);
            AssertUtc(DateTime.SpecifyKind(settlementStart, DateTimeKind.Utc), s.SettlementStartDate);
            AssertUtc(DateTime.SpecifyKind(settlementEnd, DateTimeKind.Utc), s.SettlementEndDate);
            AssertUtc(filed.ToUniversalTime(), s.FiledAtUtc);
            AssertUtc(DateTime.SpecifyKind(approved, DateTimeKind.Utc), s.TaxOfficeApprovedAtUtc);
        }
    }

    [Fact]
    public async Task CalendarEventCreateAndUpdate_NormalizeTimesToUtc()
    {
        await using var context = TestContextFactory.Create();
        var calendar = new Context.Calendar { Name = "Personal" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        var service = new CalendarEventService(context, new StubJournalLimits(), new FixedTimeProvider(Now));
        var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Unspecified);
        var end = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Unspecified);

        var request = new NewCalendarEvent
        {
            CalendarId = calendar.CalendarId, Title = "Dentist", StartDateTime = start, EndDateTime = end,
        };
        var created = await service.Create(request, "user-id");

        AssertUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), created.StartDateTime);
        AssertUtc(DateTime.SpecifyKind(end, DateTimeKind.Utc), created.EndDateTime);
        Assert.Equal(DateTimeKind.Unspecified, request.StartDateTime.Kind);

        var updated = await service.Update(created.CalendarEventId, request, "user-id");

        AssertUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), updated!.StartDateTime);
        AssertUtc(DateTime.SpecifyKind(end, DateTimeKind.Utc), updated.EndDateTime);
    }

    [Fact]
    public async Task RecurrencePatternCreate_NormalizesTimesToUtc()
    {
        await using var context = TestContextFactory.Create();
        var calendar = new Context.Calendar { Name = "Personal" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        var service = new RecurrencePatternService(context, new StubJournalLimits(), new FixedTimeProvider(Now));
        var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Unspecified);
        var until = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Unspecified);

        var created = await service.Create(new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Standup",
            StartDateTime = start,
            EndDateTime = start.AddMinutes(15),
            Frequency = RecurrenceFrequency.Daily,
            RecurrenceEndDate = until,
        }, "user-id");

        AssertUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), created.StartDateTime);
        AssertUtc(DateTime.SpecifyKind(start.AddMinutes(15), DateTimeKind.Utc), created.EndDateTime);
        AssertUtc(DateTime.SpecifyKind(until, DateTimeKind.Utc), created.RecurrenceEndDate);
    }

    private static async Task<(TransactionService Service, Guid AccountId)> NewTransactionServiceAsync(
        Context.OdysseyContext context)
    {
        var account = new Context.Account
        {
            Name = "Checking",
            Description = "Daily use",
            AccountType = Context.AccountType.CheckingAccount,
            Opened = Opened2019,
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return (new TransactionService(context, TestContextFactory.EmptyContactLookup(), new FixedTimeProvider(Now)),
            account.AccountId);
    }

    private static void AssertUtc(DateTime expected, DateTime? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(DateTimeKind.Utc, actual!.Value.Kind);
        Assert.Equal(expected, actual.Value);
    }
}
