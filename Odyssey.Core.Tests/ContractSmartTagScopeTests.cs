using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Core.Tests;

/// <summary>
/// <see cref="SmartTagScopeRules"/> (issue #308 §8): normalisation of a client value, the copy from
/// the term, the bounds and the order rule. Values are produced the way the API binder produces them —
/// by <see cref="JsonSerializer"/> — so an offset arrives as a <see cref="DateTimeKind.Local"/> value
/// in the host's zone, exactly as it does over HTTP.
/// </summary>
[Collection(LocalTimeZoneCollection.Name)]
public class SmartTagScopeRulesTests
{
    private static DateTime Bind(string wire) => JsonSerializer.Deserialize<DateTime>($"\"{wire}\"");

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("2027-06-30", 2027, 6, 30)]
    [InlineData("2027-06-30T00:00:00", 2027, 6, 30)]
    [InlineData("2027-06-30T15:45:00Z", 2027, 6, 30)]
    [InlineData("2027-06-30T00:30:00+02:00", 2027, 6, 29)]
    public void NormalizeDay_TakesTheUtcCalendarDay(string wire, int year, int month, int day)
    {
        var normalized = SmartTagScopeRules.NormalizeDay(Bind(wire));

        Assert.Equal(Day(year, month, day), normalized);
        Assert.Equal(DateTimeKind.Utc, normalized!.Value.Kind);
    }

    [SkippableTheory]
    [InlineData("Pacific/Honolulu", -10)]
    [InlineData("Pacific/Kiritimati", 14)]
    public void NormalizeDay_DoesNotDependOnTheHostTimeZone(string zoneId, int offsetHours)
    {
        using var _ = LocalTimeZoneScope.Use(zoneId, TimeSpan.FromHours(offsetHours));

        Assert.Equal(Day(2027, 6, 30), SmartTagScopeRules.NormalizeDay(Bind("2027-06-30")));
        Assert.Equal(Day(2027, 6, 30), SmartTagScopeRules.NormalizeDay(Bind("2027-06-30T15:45:00Z")));
        Assert.Equal(Day(2027, 6, 29), SmartTagScopeRules.NormalizeDay(Bind("2027-06-30T00:30:00+02:00")));
    }

    [Fact]
    public void NormalizeDay_KeepsNullOpen() => Assert.Null(SmartTagScopeRules.NormalizeDay(null));

    [Fact]
    public void FromTerm_TakesTheStoredCalendarDays_WithoutConversion()
    {
        var (start, end) = SmartTagScopeRules.FromTerm(
            new DateTime(2021, 3, 1, 9, 30, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 31, 23, 0, 0, DateTimeKind.Local),
            null);

        Assert.Equal(Day(2021, 3, 1), start);
        Assert.Equal(Day(2026, 8, 31), end);
        Assert.Equal(DateTimeKind.Utc, start!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, end!.Value.Kind);
    }

    [Fact]
    public void FromTerm_OneOff_IsOpenOnBothSides_EvenWithStaleTermDates()
    {
        Assert.Equal((null, null), SmartTagScopeRules.FromTerm(Day(2021, 3, 1), Day(2022, 3, 1), Day(2021, 6, 1)));
    }

    [Fact]
    public void FromTerm_InvertedLegacyTerm_KeepsTheStartAndOpensTheEnd()
    {
        Assert.Equal((Day(2022, 3, 1), (DateTime?)null),
            SmartTagScopeRules.FromTerm(Day(2022, 3, 1), Day(2021, 3, 1), null));
    }

    [Fact]
    public void FromTerm_SameDayWithStartTimeAfterEndTime_IsAOneDayScope()
    {
        var day = Day(2025, 6, 15);

        Assert.Equal((day, day), SmartTagScopeRules.FromTerm(day.AddHours(10), day.AddHours(9), null));
    }

    [Fact]
    public void Bounds_AreInclusive()
    {
        Assert.False(SmartTagScopeRules.IsOutOfRange(SmartTagScopeRules.MinDate));
        Assert.False(SmartTagScopeRules.IsOutOfRange(SmartTagScopeRules.MaxDate));
        Assert.True(SmartTagScopeRules.IsOutOfRange(SmartTagScopeRules.MinDate.AddDays(-1)));
        Assert.False(SmartTagScopeRules.IsOutOfRange(null));
        Assert.Equal(TaxSettlementRange.MinDate.Date, SmartTagScopeRules.MinDate.Date);
    }

    [Fact]
    public void Order_SameDayIsValid_EarlierEndIsInverted()
    {
        Assert.False(SmartTagScopeRules.IsInverted(Day(2027, 6, 30), Day(2027, 6, 30)));
        Assert.True(SmartTagScopeRules.IsInverted(Day(2027, 7, 1), Day(2027, 6, 30)));
        Assert.False(SmartTagScopeRules.IsInverted(null, Day(2027, 6, 30)));
    }
}

/// <summary>
/// The contract service's half of issue #308: the create copy, independence from later term edits,
/// the order rule, the change log line, and the stamps round-trip — plus the motivating case end to
/// end through the match.
/// </summary>
public class ContractSmartTagScopeServiceTests
{
    private const string UserId = "scope-user";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext context = TestContextFactory.Create();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class RecordingLogger : ILogger<ContractService>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    private ContractService Service(ILogger<ContractService>? logger = null) =>
        new(context, TestContextFactory.ContactLookup(context), new FixedTimeProvider(FixedToday),
            logger ?? NullLogger<ContractService>.Instance);

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private Task<ExistingContract> Create(DateTime? start, DateTime? end, DateTime? completion = null) =>
        Service().Create(new NewContract
        {
            Name = "Employment", Type = DtoContractType.Employment,
            StartDate = start, EndDate = end, CompletionDate = completion,
        }, UserId);

    /// <summary>The carry-forward every client builder performs, with optional overrides.</summary>
    private static UpdateContract CarryForward(ExistingContract c) => new()
    {
        Name = c.Name,
        Type = c.Type,
        Description = c.Description,
        ReferenceNumber = c.ReferenceNumber,
        StartDate = c.StartDate,
        EndDate = c.EndDate,
        CompletionDate = c.CompletionDate,
        SmartTagScopeStart = c.SmartTagScopeStart,
        SmartTagScopeEnd = c.SmartTagScopeEnd,
        IsArchived = c.Archived is not null,
        IsPaused = c.Paused is not null,
        Ready = c.Ready,
        Signed = c.Signed,
    };

    [Fact]
    public async Task Create_CopiesTheTerm_AndTheStoredTermKeepsItsTimeOfDay()
    {
        var created = await Create(new DateTime(2021, 3, 1, 9, 30, 0), Day(2026, 8, 31));

        Assert.Equal(Day(2021, 3, 1), created.SmartTagScopeStart);
        Assert.Equal(Day(2026, 8, 31), created.SmartTagScopeEnd);
        Assert.Equal(new DateTime(2021, 3, 1, 9, 30, 0), context.Contracts.Single().StartDate);
    }

    [Fact]
    public async Task Create_OneOff_HasAnOpenScope()
    {
        var created = await Create(null, null, completion: Day(2026, 3, 1));

        Assert.Null(created.SmartTagScopeStart);
        Assert.Null(created.SmartTagScopeEnd);
    }

    [Fact]
    public async Task Create_DoesNotLogAScopeChange()
    {
        var logger = new RecordingLogger();
        await Service(logger).Create(new NewContract { Name = "x", StartDate = Day(2021, 3, 1) }, UserId);

        Assert.DoesNotContain(logger.Lines, l => l.Message.Contains("smart-tag scope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_ChangingTheTerm_DoesNotMoveTheScope()
    {
        var created = await Create(Day(2021, 3, 1), null);

        var request = CarryForward(created);
        request.StartDate = Day(2020, 1, 1);
        request.EndDate = Day(2026, 8, 31);
        var updated = (await Service().Update(created.ContractId, request, UserId))!;

        Assert.Equal(Day(2021, 3, 1), updated.SmartTagScopeStart);
        Assert.Null(updated.SmartTagScopeEnd);
    }

    [Fact]
    public async Task Update_InvertedScope_Is422KeyedOnTheEnd_AndNothingIsWritten()
    {
        var created = await Create(Day(2021, 3, 1), Day(2026, 8, 31));

        var request = CarryForward(created);
        request.Name = "Renamed";
        request.SmartTagScopeStart = Day(2027, 7, 1);
        request.SmartTagScopeEnd = Day(2027, 6, 30);
        var refusal = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => Service().Update(created.ContractId, request, UserId));

        Assert.Equal(SmartTagScopeRules.InvertedMessage, refusal.Message);
        Assert.True(refusal.Errors!.ContainsKey("smartTagScopeEnd"));
        // Drop the tracked (mutated, unsaved) entity so the read comes from the store.
        context.ChangeTracker.Clear();
        var stored = context.Contracts.Single();
        Assert.Equal("Employment", stored.Name);
        Assert.Equal(Day(2026, 8, 31), stored.SmartTagScopeEnd);
    }

    [Fact]
    public async Task Update_LogsWhichBoundsChanged_WithoutTheDates()
    {
        var created = await Create(Day(2021, 3, 1), Day(2026, 8, 31));
        var logger = new RecordingLogger();

        var request = CarryForward(created);
        request.SmartTagScopeEnd = Day(2027, 6, 30);
        await Service(logger).Update(created.ContractId, request, UserId);

        var line = Assert.Single(logger.Lines, l => l.Message.Contains("smart-tag scope", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(created.ContractId.ToString(), line.Message, StringComparison.Ordinal);
        Assert.Contains(UserId, line.Message, StringComparison.Ordinal);
        Assert.Contains("bounds end", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2027", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_LeavingTheScopeUnchanged_LogsNothingAboutIt()
    {
        var created = await Create(Day(2021, 3, 1), Day(2026, 8, 31));
        var logger = new RecordingLogger();

        var request = CarryForward(created);
        request.Name = "Renamed";
        await Service(logger).Update(created.ContractId, request, UserId);

        Assert.DoesNotContain(logger.Lines, l => l.Message.Contains("smart-tag scope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArchiveUnarchivePause_RoundTrip_LeavesANarrowedScopeUnchanged()
    {
        // Ended and signed, so it can be archived; then restored and paused requires Active — so the
        // contract's end is moved into the future before the pause.
        var created = await Create(Day(2021, 3, 1), Day(2026, 1, 31));
        var service = Service();
        var narrowed = CarryForward(created);
        narrowed.SmartTagScopeStart = Day(2022, 1, 1);
        narrowed.SmartTagScopeEnd = Day(2025, 12, 31);
        narrowed.Ready = Day(2021, 2, 1);
        narrowed.Signed = Day(2021, 2, 2);
        var current = (await service.Update(created.ContractId, narrowed, UserId))!;

        var archive = CarryForward(current);
        archive.IsArchived = true;
        current = (await service.Update(created.ContractId, archive, UserId))!;
        var restore = CarryForward(current);
        restore.IsArchived = false;
        restore.EndDate = Day(2027, 12, 31);
        current = (await service.Update(created.ContractId, restore, UserId))!;
        var pause = CarryForward(current);
        pause.IsPaused = true;
        current = (await service.Update(created.ContractId, pause, UserId))!;

        Assert.NotNull(current.Paused);
        Assert.Equal(Day(2022, 1, 1), current.SmartTagScopeStart);
        Assert.Equal(Day(2025, 12, 31), current.SmartTagScopeEnd);
    }

    [Fact]
    public async Task MotivatingCase_AnEndDateAddedLater_StillMatchesAPaymentAfterIt()
    {
        var created = await Create(Day(2021, 3, 1), null);
        var request = CarryForward(created);
        request.EndDate = Day(2026, 8, 31);
        await Service().Update(created.ContractId, request, UserId);

        var account = new Account
        {
            Name = "Checking", Description = "Daily", AccountType = Odyssey.Context.AccountType.CheckingAccount,
            Opened = Day(2020, 1, 1),
        };
        var employer = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}", DisplayName = "Employer", NormalizedName = "employer",
            Type = Odyssey.Dtos.ContactType.Organization,
        };
        var salary = new TransactionTag { Name = "Salary" };
        context.AddRange(account, employer, salary);
        await context.SaveChangesAsync();
        context.ContractSmartTags.Add(new ContractSmartTag
        {
            ContractId = created.ContractId, TransactionTagId = salary.TransactionTagId, AddedAt = FixedToday,
        });
        context.ContractParties.Add(new ContractParty
        {
            ContractId = created.ContractId, ContactId = employer.ContactId,
            Role = Odyssey.Context.ContractPartyRole.Employer,
        });
        var holidayPay = new Transaction
        {
            Description = "Holiday pay", Amount = 5000m, TimeStamp = Day(2027, 6, 15), AccountId = account.AccountId,
            ContactId = employer.ContactId, CurrencyCode = "NOK", TransactionTags = [salary],
        };
        context.Transactions.Add(holidayPay);
        await context.SaveChangesAsync();

        var result = (await new ContractSmartTagTransactionService(
                context, new TransactionService(context, TestContextFactory.ContactLookup(context)))
            .ListAsync(created.ContractId, new ContractSmartTagTransactionsQueryParams()))!;

        Assert.Null(result.Scope.ToExclusive);
        Assert.Equal([holidayPay.TransactionId], result.Page.Items.Select(t => t.TransactionId));
    }
}
