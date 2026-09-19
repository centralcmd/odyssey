using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client half of the cadence (issue #120): one helper turns an interval and its count into
/// copy, so a summary tile, a history row, the record card's tile foot and the New/Edit dialog can
/// never word the same term differently.
/// </summary>
public class TermCadenceTextTests
{
    [Theory]
    [InlineData(Interval.Monthly, 1, "monthly")]
    [InlineData(Interval.Monthly, null, "monthly")]
    [InlineData(Interval.Monthly, 3, "every 3 months")]
    [InlineData(Interval.Weekly, 2, "every 2 weeks")]
    [InlineData(Interval.Daily, 10, "every 10 days")]
    [InlineData(Interval.Annually, 1, "annually")]
    [InlineData(Interval.PerOccurrence, null, "per occurrence")]
    [InlineData(Interval.PerUnit, null, "per unit")]
    public void A_cadence_reads_as_its_adverb_or_as_every_n_units(Interval interval, int? count, string expected)
    {
        Assert.Equal(expected, TermKindVisuals.CadenceText(interval, count));
    }

    /// <summary>
    /// A one-time charge and an unset interval carry no cadence at all. "One-time" is the ABSENCE of
    /// a rhythm rather than one of them, so saying it beside a value would be noise — and returning
    /// null is what lets every caller render the result unconditionally.
    /// </summary>
    [Theory]
    [InlineData(Interval.OneTime)]
    [InlineData(null)]
    public void A_one_time_or_unset_interval_has_no_cadence(Interval? interval)
    {
        Assert.Null(TermKindVisuals.CadenceText(interval, null));
        Assert.Null(TermKindVisuals.CadenceText(interval, 3));
    }

    /// <summary>
    /// The one condition the whole cadence UI hangs off: the dialog offers a count only for a
    /// periodic unit, and writes null for every other. It must agree with the server's own set.
    /// </summary>
    [Theory]
    [InlineData(Interval.Daily, true)]
    [InlineData(Interval.Weekly, true)]
    [InlineData(Interval.Monthly, true)]
    [InlineData(Interval.Annually, true)]
    [InlineData(Interval.OneTime, false)]
    [InlineData(Interval.PerOccurrence, false)]
    [InlineData(Interval.PerUnit, false)]
    public void The_periodic_set_matches_the_servers(Interval interval, bool periodic)
    {
        Assert.Equal(periodic, TermKindVisuals.IsPeriodic(interval));
    }

    [Fact]
    public void An_unset_interval_is_not_periodic()
    {
        Assert.False(TermKindVisuals.IsPeriodic(null));
    }

    /// <summary>
    /// The picker lists the units in READING order — occasions first, then rhythms shortest to
    /// longest. Ordering by ordinal would put PerUnit (6) and Weekly (7) last, behind Annually (5),
    /// since the ordinals are deliberately out of sequence around the retired 4.
    /// </summary>
    [Fact]
    public void The_picker_lists_the_units_in_reading_order()
    {
        Assert.Equal(
            [
                Interval.OneTime, Interval.PerOccurrence, Interval.PerUnit,
                Interval.Daily, Interval.Weekly, Interval.Monthly, Interval.Annually,
            ],
            TermKindVisuals.AllIntervals);
    }

    /// <summary>Every defined unit has display context — a picker entry with no label, or a periodic
    /// unit with no plural noun, would render a blank option or "every 3 ".</summary>
    [Fact]
    public void Every_defined_interval_has_display_context()
    {
        foreach (var interval in Enum.GetValues<Interval>())
        {
            var info = TermKindVisuals.InfoFor(interval);

            Assert.NotNull(info);
            Assert.NotEmpty(info!.Label);
            Assert.NotEmpty(info.Adverb);

            if (info.Periodic)
            {
                Assert.NotEmpty(info.One);
                Assert.NotEmpty(info.Many);
            }
        }

        Assert.Equal(Enum.GetValues<Interval>().Length, TermKindVisuals.AllIntervals.Count);
    }

    /// <summary>The retired ordinal has no display context either — a stale row reaching the client
    /// renders no cadence rather than borrowing another unit's wording.</summary>
    [Fact]
    public void The_retired_quarterly_ordinal_has_no_display_context()
    {
        Assert.Null(TermKindVisuals.InfoFor((Interval)4));
        Assert.Null(TermKindVisuals.CadenceText((Interval)4, 1));
        Assert.False(TermKindVisuals.IsPeriodic((Interval)4));
    }

    [Fact]
    public void A_new_fee_opens_on_the_monthly_default()
    {
        Assert.Equal(Interval.Monthly, TermKindVisuals.DefaultFeeInterval);
        Assert.True(TermKindVisuals.IsPeriodic(TermKindVisuals.DefaultFeeInterval));
    }
}
