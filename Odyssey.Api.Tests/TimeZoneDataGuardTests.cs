using Odyssey.Api;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The time-zone database startup guard (issue #350 §11). Like <see cref="GlobalizationGuardTests"/>, the
/// failure it catches — an alpine image without tzdata — cannot be reproduced in-process, so the
/// measurement is pinned in the healthy direction and the refusal is asserted through the injected check.
/// </summary>
public class TimeZoneDataGuardTests
{
    [Fact]
    public void The_guard_passes_where_the_time_zone_database_is_present() =>
        TimeZoneDataGuard.EnsureTimeZoneDataAvailable();

    [Fact]
    public void The_guard_refuses_to_start_naming_every_zone_that_did_not_resolve()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TimeZoneDataGuard.EnsureTimeZoneDataAvailable(id => id != "Europe/Oslo" && id != "Etc/UTC"));

        Assert.Contains("Europe/Oslo", ex.Message);
        Assert.Contains("Etc/UTC", ex.Message);
        Assert.DoesNotContain("America/New_York", ex.Message);
        Assert.Contains("tzdata", ex.Message);
    }

    [Fact]
    public void The_canaries_include_a_recently_renamed_zone() =>
        Assert.Contains("Europe/Kyiv", TimeZoneDataGuard.Canaries);
}
