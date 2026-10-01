using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Xunit;
using Context = Odyssey.Context;
using static Odyssey.Core.Tests.ClientDateHandlingTests;

namespace Odyssey.Core.Tests;

/// <summary>
/// The service-side twin of the DTO's <c>[EnumDataType]</c> on <c>DaysOfWeek</c> (issue #243): a caller
/// that bypasses model validation must still be refused rather than reach the weekly generator.
/// </summary>
public class RecurrencePatternServiceValidationTests
{
    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    public async Task Create_DaysOfWeekWithUndefinedBit_IsRefused(int rawDays)
    {
        await using var context = TestContextFactory.Create();
        var calendar = new Context.Calendar { Name = "Personal" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        var service = new RecurrencePatternService(context, new StubJournalLimits(), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Undefined day",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = (DaysOfWeekFlags)rawDays,
            OccurrenceCount = 3,
        }, "user-id"));

        Assert.Empty(context.RecurrencePatterns);
        Assert.Empty(context.CalendarEvents);
    }

    [Fact]
    public async Task Update_DaysOfWeekWithUndefinedBit_IsRefused_AndLeavesTheSeriesAsItWas()
    {
        await using var context = TestContextFactory.Create();
        var calendar = new Context.Calendar { Name = "Personal" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        var service = new RecurrencePatternService(context, new StubJournalLimits(), new FixedTimeProvider(Now));
        var request = new NewRecurrencePattern
        {
            CalendarId = calendar.CalendarId,
            Title = "Weekly",
            StartDateTime = new DateTime(2030, 1, 7, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2030, 1, 7, 9, 30, 0, DateTimeKind.Utc),
            Frequency = RecurrenceFrequency.Weekly,
            DaysOfWeek = DaysOfWeekFlags.Monday,
            OccurrenceCount = 3,
        };
        var created = await service.Create(request, "user-id");

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Update(created.RecurrencePatternId, request with { DaysOfWeek = (DaysOfWeekFlags)128 }, "user-id"));

        Assert.Equal(Context.DaysOfWeekFlags.Monday, context.RecurrencePatterns.Single().DaysOfWeek);
        Assert.Equal(3, context.CalendarEvents.Count());
    }

    [Theory]
    [InlineData(DaysOfWeekFlags.Monday, true)]
    [InlineData(DaysOfWeekFlagsExtensions.AllDays, true)]
    [InlineData((DaysOfWeekFlags)128, false)]
    [InlineData((DaysOfWeekFlags)129, false)]
    [InlineData((DaysOfWeekFlags)(-1), false)]
    public void HasOnlyDefinedDays_MatchesTheDefinedMask(DaysOfWeekFlags flags, bool expected) =>
        Assert.Equal(expected, flags.HasOnlyDefinedDays());
}
