using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

public class PeriodBoundsTests
{
    [Fact]
    public void InclusiveStart_DropsTheTimeOfDay()
    {
        var start = PeriodBounds.InclusiveStart(new DateTime(2025, 6, 1, 18, 30, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(14, 0, 0)]
    [InlineData(23, 59, 59)]
    public void ExclusiveEnd_IsTheNextMidnightWhateverTheTimeOfDay(int hour, int minute, int second)
    {
        var end = PeriodBounds.ExclusiveEnd(new DateTime(2025, 12, 31, hour, minute, second, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), end);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Bounds_PreserveTheKind(DateTimeKind kind)
    {
        var date = new DateTime(2025, 3, 10, 9, 0, 0, kind);

        Assert.Equal(kind, PeriodBounds.InclusiveStart(date).Kind);
        Assert.Equal(kind, PeriodBounds.ExclusiveEnd(date).Kind);
        Assert.Equal(kind, PeriodBounds.InclusiveEndInstant(date).Kind);
        Assert.Equal(kind, PeriodBounds.ExclusiveEnd(DateTime.SpecifyKind(DateTime.MaxValue, kind)).Kind);
    }

    [Fact]
    public void ExclusiveEnd_OnTheLastRepresentableDay_SaturatesInsteadOfThrowing()
    {
        Assert.Equal(DateTime.MaxValue, PeriodBounds.ExclusiveEnd(DateTime.MaxValue));
        Assert.Equal(DateTime.MaxValue, PeriodBounds.ExclusiveEnd(DateTime.MaxValue.Date));
        Assert.Equal(DateTime.MaxValue.AddTicks(-TimeSpan.TicksPerMicrosecond),
            PeriodBounds.InclusiveEndInstant(DateTime.MaxValue.Date));
    }

    [Fact]
    public void InclusiveEndInstant_IsTheLastMicrosecondOfTheEndDay()
    {
        var instant = PeriodBounds.InclusiveEndInstant(new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2025, 6, 30, 23, 59, 59, 999, 999, DateTimeKind.Utc), instant);
    }

    [Fact]
    public void Of_ReturnsBothBounds()
    {
        var (start, endExclusive) = PeriodBounds.Of(
            new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc), endExclusive);
    }

    [Fact]
    public void IsInverted_ComparesCalendarDays()
    {
        var day = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(PeriodBounds.IsInverted(day, day.AddDays(-1)));
        Assert.True(PeriodBounds.IsInverted(day, day.AddTicks(-1)));
        Assert.False(PeriodBounds.IsInverted(day, day));
        Assert.False(PeriodBounds.IsInverted(day.AddHours(18), day.AddHours(9)));
        Assert.False(PeriodBounds.IsInverted(day, day.AddDays(1)));
    }
}
