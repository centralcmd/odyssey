using Odyssey.Client.Pages.Calendar;
using Odyssey.TestShared;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client's half of the calendar converter parity (PR #528 architect review): <see cref="CalendarZoneClock"/> against
/// the shared golden table the server's <c>CalendarTimeZones</c> is tested against too. The client's result chooses the
/// wall-clock a drag or resize sends, so a divergence would move an event, not just misword a hint.
/// </summary>
public class CalendarZoneClockGoldenTests
{
    public static TheoryData<CalendarZoneGoldenCases.Case> Cases() => [.. CalendarZoneGoldenCases.All];

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_client_converts_every_golden_case(CalendarZoneGoldenCases.Case c)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(c.Zone);

        var utc = CalendarZoneClock.ToUtc(c.Wall, zone, out var adjustment);

        Assert.Equal(c.Utc, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(c.Kind switch
        {
            CalendarZoneGoldenCases.Kind.Gap => ZoneAdjustment.Gap,
            CalendarZoneGoldenCases.Kind.Ambiguous => ZoneAdjustment.Ambiguous,
            _ => ZoneAdjustment.None,
        }, adjustment);
        Assert.Equal(c.Saved, CalendarZoneClock.Saved(c.Wall, zone));
        Assert.Equal(c.Saved, CalendarZoneClock.ToWall(c.Utc, zone));
    }

    [Fact]
    public void The_table_spans_both_hemispheres_gaps_overlaps_and_fractional_offsets()
    {
        var all = CalendarZoneGoldenCases.All;
        Assert.Contains(all, c => c.Kind == CalendarZoneGoldenCases.Kind.Gap && c.Zone.StartsWith("Australia/", StringComparison.Ordinal));
        Assert.Contains(all, c => c.Kind == CalendarZoneGoldenCases.Kind.Ambiguous && c.Zone.StartsWith("Australia/", StringComparison.Ordinal));
        Assert.Contains(all, c => c.Zone == "Asia/Kathmandu");
        Assert.Contains(all, c => c.Zone == "America/St_Johns" && c.Kind == CalendarZoneGoldenCases.Kind.Gap);
    }
}
