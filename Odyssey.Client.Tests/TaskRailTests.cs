using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The task list card's deadline rail. The scale's edges are spelled out rather than computed — a bare
/// "0" reads as "no deadline" and a raw day count stops being parseable past 90 days — so the edges are
/// exactly what these pin. Every state also carries the same fact in words, because the visual tokens
/// are decorative and an uppercase "tmrw" is not something a screen reader should be handed.
/// </summary>
public class TaskRailTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static TaskRail Rail(int daysFromToday, JournalTaskStatus status = JournalTaskStatus.Backlog) =>
        TaskRail.For(status, Today.AddDays(daysFromToday));

    [Fact]
    public void No_deadline_is_a_dash_with_no_tone()
    {
        var rail = TaskRail.For(JournalTaskStatus.Backlog, null);

        Assert.True(rail.Dash);
        Assert.Null(rail.Tone);
        Assert.Null(rail.Count);
        Assert.Equal("No deadline", rail.AccessibleText);
    }

    [Fact]
    public void Today_and_tomorrow_are_words_not_a_zero_or_a_one()
    {
        Assert.Equal("today", Rail(0).Word);
        Assert.Null(Rail(0).Count);
        Assert.Equal("Due today", Rail(0).AccessibleText);

        Assert.Equal("tmrw", Rail(1).Word);
        Assert.Null(Rail(1).Count);
        Assert.Equal("Due tomorrow", Rail(1).AccessibleText);
    }

    [Theory]
    [InlineData(-1, "−", 1, "day", "1 day overdue")]
    [InlineData(-8, "−", 8, "days", "8 days overdue")]
    [InlineData(2, "+", 2, "days", "Due in 2 days")]
    [InlineData(90, "+", 90, "days", "Due in 90 days")]
    public void A_day_count_is_signed_and_its_unit_agrees(
        int days, string sign, int count, string unit, string accessible)
    {
        var rail = Rail(days);

        Assert.Equal(sign, rail.Sign);
        Assert.Equal(count, rail.Count);
        Assert.Equal(unit, rail.Unit);
        Assert.Equal(accessible, rail.AccessibleText);
        Assert.Null(rail.Word);
    }

    // The minus sign is U+2212, the same glyph negative money uses — never a hyphen.
    [Fact]
    public void Overdue_uses_the_minus_sign_not_a_hyphen()
    {
        Assert.Equal("−", Rail(-5).Sign);
        Assert.DoesNotContain("-", Rail(-5).Sign!, StringComparison.Ordinal);
    }

    // The rollover is on the ABSOLUTE distance, so it has to be pinned on the overdue side too — a
    // scale that rolled only forwards would print "−120 days" beside "+4 months".
    [Theory]
    [InlineData(90, "days")]
    [InlineData(91, "months")]
    [InlineData(-90, "days")]
    [InlineData(-91, "months")]
    public void The_rollover_boundary_is_ninety_days_in_both_directions(int days, string unit)
        => Assert.Equal(unit, Rail(days).Unit);

    // 135 days is exactly 4.5 months. Banker's rounding would give 4 there and 6 at 165 (5.5); away
    // from zero gives 5 and 6, which is what keeps the count rising as the deadline recedes.
    [Theory]
    [InlineData(135, 5)]
    [InlineData(165, 6)]
    [InlineData(-135, 5)]
    public void A_half_month_rounds_away_from_zero(int days, int months)
        => Assert.Equal(months, Rail(days).Count);

    [Theory]
    [InlineData(91, "+", 3, "Due in 3 months")]
    [InlineData(121, "+", 4, "Due in 4 months")]
    [InlineData(-366, "−", 12, "12 months overdue")]
    public void Past_ninety_days_the_count_rolls_to_months(int days, string sign, int count, string accessible)
    {
        var rail = Rail(days);

        Assert.Equal(sign, rail.Sign);
        Assert.Equal(count, rail.Count);
        Assert.Equal("months", rail.Unit);
        Assert.Equal(accessible, rail.AccessibleText);
    }

    // Overdue · soon (within three days) · resting. The tone drives colour only, which is why every
    // case above also states the deadline in words.
    [Theory]
    [InlineData(-1, "overdue")]
    [InlineData(0, "soon")]
    [InlineData(3, "soon")]
    [InlineData(4, null)]
    public void Tone_marks_overdue_and_the_three_day_window(int days, string? tone)
        => Assert.Equal(tone, Rail(days).Tone);

    // A finished task has no countdown left to run, whatever its deadline said.
    [Fact]
    public void Done_shows_a_tick_and_ignores_the_deadline()
    {
        var rail = Rail(-40, JournalTaskStatus.Done);

        Assert.True(rail.Tick);
        Assert.Equal("done", rail.Tone);
        Assert.Equal("done", rail.Unit);
        Assert.Null(rail.Count);
        Assert.Equal("Done", rail.AccessibleText);
    }

    [Fact]
    public void Archived_shows_a_muted_tick()
    {
        var rail = Rail(-40, JournalTaskStatus.Archived);

        Assert.True(rail.Tick);
        Assert.Equal("muted", rail.Tone);
        Assert.Equal("archived", rail.Unit);
        Assert.Equal("Archived", rail.AccessibleText);
    }
}
