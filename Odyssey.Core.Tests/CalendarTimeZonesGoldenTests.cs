using Odyssey.Core.Journal;
using Odyssey.TestShared;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The server's half of the calendar converter parity (PR #528 architect review): <see cref="CalendarTimeZones"/> against
/// the shared golden table the client's <c>CalendarZoneClock</c> is tested against too.
/// </summary>
public class CalendarTimeZonesGoldenTests
{
    public static TheoryData<CalendarZoneGoldenCases.Case> Cases() => [.. CalendarZoneGoldenCases.All];

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_server_converts_every_golden_case(CalendarZoneGoldenCases.Case c)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(c.Zone);

        var utc = CalendarTimeZones.ToUtc(c.Wall, zone, out var adjusted);

        Assert.Equal(c.Utc, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(c.Kind == CalendarZoneGoldenCases.Kind.Gap, adjusted);
        Assert.Equal(c.Saved, CalendarTimeZones.Normalize(c.Wall, zone));
    }
}
