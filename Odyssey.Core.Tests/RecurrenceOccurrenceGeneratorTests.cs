using Odyssey.Context;
using Odyssey.Core.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Direct date-rule coverage for <see cref="RecurrenceOccurrenceGenerator"/> (issue #243). The API tests
/// count rows; these pin which dates the rules produce, for both the clamping generator and the strict
/// RFC 5545 projection the ICS exporter compares it against.
/// </summary>
public class RecurrenceOccurrenceGeneratorTests
{
    private const int Cap = 730;

    [Fact]
    public void Daily_StepsByInterval_KeepingTimeOfDayAndDuration()
    {
        var pattern = Pattern(RecurrenceFrequency.Daily, new DateTime(2030, 1, 30, 9, 15, 0, DateTimeKind.Utc), count: 4);
        pattern.Interval = 2;

        var occurrences = RecurrenceOccurrenceGenerator.Generate(pattern, Cap);

        Assert.Equal(
            [Utc(2030, 1, 30, 9, 15), Utc(2030, 2, 1, 9, 15), Utc(2030, 2, 3, 9, 15), Utc(2030, 2, 5, 9, 15)],
            occurrences.Select(o => o.Start));
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.FromMinutes(45), o.End - o.Start));
    }

    [Fact]
    public void Monthly31st_RecoversTheDayAfterShortMonths()
    {
        var pattern = Pattern(RecurrenceFrequency.Monthly, Utc(2030, 1, 31, 9, 0), count: 5);
        pattern.DayOfMonth = 31;

        var starts = RecurrenceOccurrenceGenerator.Generate(pattern, Cap).Select(o => o.Start);

        // Anchor-relative: the 28 February clamp must not drift every later month to the 28th.
        Assert.Equal(
            [Utc(2030, 1, 31, 9, 0), Utc(2030, 2, 28, 9, 0), Utc(2030, 3, 31, 9, 0), Utc(2030, 4, 30, 9, 0), Utc(2030, 5, 31, 9, 0)],
            starts);
    }

    [Fact]
    public void Monthly31st_RfcLiteral_SkipsMonthsWithoutTheDay()
    {
        var pattern = Pattern(RecurrenceFrequency.Monthly, Utc(2030, 1, 31, 9, 0), count: 3);
        pattern.DayOfMonth = 31;

        var starts = RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, Cap)!.Select(o => o.Start);

        Assert.Equal([Utc(2030, 1, 31, 9, 0), Utc(2030, 3, 31, 9, 0), Utc(2030, 5, 31, 9, 0)], starts);
    }

    [Fact]
    public void Yearly29February_ClampsInCommonYearsAndRecoversInLeapYears()
    {
        var pattern = Pattern(RecurrenceFrequency.Yearly, Utc(2028, 2, 29, 8, 0), count: 5);
        pattern.DayOfMonth = 29;
        pattern.MonthOfYear = 2;

        var starts = RecurrenceOccurrenceGenerator.Generate(pattern, Cap).Select(o => o.Start);

        Assert.Equal(
            [Utc(2028, 2, 29, 8, 0), Utc(2029, 2, 28, 8, 0), Utc(2030, 2, 28, 8, 0), Utc(2031, 2, 28, 8, 0), Utc(2032, 2, 29, 8, 0)],
            starts);
    }

    [Fact]
    public void Yearly29February_RfcLiteral_OnlyLeapYears()
    {
        var pattern = Pattern(RecurrenceFrequency.Yearly, Utc(2028, 2, 29, 8, 0), count: 2);
        pattern.DayOfMonth = 29;
        pattern.MonthOfYear = 2;

        var starts = RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, Cap)!.Select(o => o.Start);

        Assert.Equal([Utc(2028, 2, 29, 8, 0), Utc(2032, 2, 29, 8, 0)], starts);
    }

    [Fact]
    public void Weekly_EmitsSelectedDaysOfEveryIntervalthWeek()
    {
        var pattern = Pattern(RecurrenceFrequency.Weekly, Utc(2030, 1, 9, 7, 0), count: 4); // a Wednesday
        pattern.Interval = 2;
        pattern.DaysOfWeek = DaysOfWeekFlags.Monday | DaysOfWeekFlags.Friday;

        var starts = RecurrenceOccurrenceGenerator.Generate(pattern, Cap).Select(o => o.Start);

        // The anchor's own Monday (7 Jan) precedes the anchor, so the first match is that week's Friday.
        Assert.Equal([Utc(2030, 1, 11, 7, 0), Utc(2030, 1, 21, 7, 0), Utc(2030, 1, 25, 7, 0), Utc(2030, 2, 4, 7, 0)], starts);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(0)]
    [InlineData(256 | 512)]
    public void Weekly_WithNoDefinedDayBit_TerminatesWithNoOccurrences(int rawDays)
    {
        // A stored row predating issue #243's validation: the loop used to step one day at a time until
        // DateTime overflowed. It must now end immediately, for both projections.
        var pattern = Pattern(RecurrenceFrequency.Weekly, Utc(2030, 1, 7, 9, 0), count: 3);
        pattern.DaysOfWeek = (DaysOfWeekFlags)rawDays;

        Assert.Empty(RecurrenceOccurrenceGenerator.Generate(pattern, Cap));
        Assert.Empty(RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, Cap)!);
    }

    [Fact]
    public void Weekly_UndefinedBitsBesideAValidDay_AreIgnored()
    {
        var pattern = Pattern(RecurrenceFrequency.Weekly, Utc(2030, 1, 7, 9, 0), count: 2);
        pattern.DaysOfWeek = (DaysOfWeekFlags)129; // Monday | 128

        var starts = RecurrenceOccurrenceGenerator.Generate(pattern, Cap).Select(o => o.Start);

        Assert.Equal([Utc(2030, 1, 7, 9, 0), Utc(2030, 1, 14, 9, 0)], starts);
    }

    [Fact]
    public void RecurrenceEndDate_IsInclusiveOfAnOccurrenceStartingExactlyThen()
    {
        var pattern = Pattern(RecurrenceFrequency.Daily, Utc(2030, 1, 1, 9, 0), count: null);
        pattern.RecurrenceEndDate = Utc(2030, 1, 3, 9, 0);

        var starts = RecurrenceOccurrenceGenerator.Generate(pattern, Cap).Select(o => o.Start).ToList();

        Assert.Equal([Utc(2030, 1, 1, 9, 0), Utc(2030, 1, 2, 9, 0), Utc(2030, 1, 3, 9, 0)], starts);
        Assert.Equal(starts, RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, Cap)!.Select(o => o.Start));
    }

    [Fact]
    public void RecurrenceEndDate_ExcludesAnOccurrenceStartingAfterIt()
    {
        var pattern = Pattern(RecurrenceFrequency.Daily, Utc(2030, 1, 1, 9, 0), count: null);
        pattern.RecurrenceEndDate = Utc(2030, 1, 3, 8, 59);

        var starts = RecurrenceOccurrenceGenerator.Generate(pattern, Cap).Select(o => o.Start);

        Assert.Equal([Utc(2030, 1, 1, 9, 0), Utc(2030, 1, 2, 9, 0)], starts);
    }

    [Fact]
    public void ExactlyAtCap_IsAccepted()
    {
        const int cap = 10;
        var pattern = Pattern(RecurrenceFrequency.Daily, Utc(2030, 1, 1, 9, 0), count: null);
        pattern.RecurrenceEndDate = Utc(2030, 1, 10, 9, 0); // 10 occurrences

        Assert.Equal(cap, RecurrenceOccurrenceGenerator.Generate(pattern, cap).Count);
        Assert.Equal(cap, RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, cap)!.Count);
    }

    [Fact]
    public void OneOverCap_IsRefused()
    {
        const int cap = 10;
        var pattern = Pattern(RecurrenceFrequency.Daily, Utc(2030, 1, 1, 9, 0), count: null);
        pattern.RecurrenceEndDate = Utc(2030, 1, 11, 9, 0); // 11 occurrences

        Assert.Throws<DomainValidationException>(() => RecurrenceOccurrenceGenerator.Generate(pattern, cap));
        Assert.Null(RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, cap));
    }

    [Fact]
    public void OccurrenceCountEqualToCap_IsAccepted()
    {
        const int cap = 10;
        var pattern = Pattern(RecurrenceFrequency.Daily, Utc(2030, 1, 1, 9, 0), count: cap);

        Assert.Equal(cap, RecurrenceOccurrenceGenerator.Generate(pattern, cap).Count);
    }

    private static RecurrencePattern Pattern(RecurrenceFrequency frequency, DateTime start, int? count) => new()
    {
        CalendarId = Guid.NewGuid(),
        Title = "Series",
        StartDateTime = start,
        EndDateTime = start.AddMinutes(45),
        Frequency = frequency,
        OccurrenceCount = count,
    };

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
