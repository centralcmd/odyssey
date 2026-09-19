using Odyssey.Client.Pages.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="PartyTerm"/> is ONE formatter for both party surfaces — insurance policies and, since
/// issue #121, contracts. The rename from <c>InsurancePartyTerm</c> is the point: the two carry the
/// same <c>FromDate</c>/<c>ToDate</c> pair with the same semantics, and two copies of a format drift.
/// </summary>
public class PartyTermTests
{
    /// <summary>
    /// Both dates absent is the DEFAULT term — the policy's or contract's own extent — and renders no
    /// line at all. Absence is the healthy, common case here, not a missing value, so a caption saying
    /// so would be noise on every ordinary party.
    /// </summary>
    [Fact]
    public void The_default_term_formats_to_nothing()
    {
        Assert.Null(PartyTerm.Format(null, null));
    }

    [Fact]
    public void One_open_end_is_stated_as_such()
    {
        Assert.Equal("from Mar 1 2026", PartyTerm.Format(new DateTime(2026, 3, 1), null));
        Assert.Equal("to Mar 1 2026", PartyTerm.Format(null, new DateTime(2026, 3, 1)));
    }

    /// <summary>A closed range writes its shared year once, at the end — the compact form the
    /// fixed-height tile line fits.</summary>
    [Fact]
    public void A_closed_range_writes_a_shared_year_once()
    {
        Assert.Equal("Mar 1 – Dec 31 2026",
            PartyTerm.Format(new DateTime(2026, 3, 1), new DateTime(2026, 12, 31)));
        Assert.Equal("Mar 1 2025 – Dec 31 2026",
            PartyTerm.Format(new DateTime(2025, 3, 1), new DateTime(2026, 12, 31)));
    }
}
