using Odyssey.Core.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>Unit coverage for the shared snippet truncation used by both journal read projections.</summary>
public class JournalTextTests
{
    [Fact]
    public void Truncate_null_passes_through() => Assert.Null(JournalText.Truncate(null, 200));

    [Fact]
    public void Truncate_short_returns_whole() => Assert.Equal("hi", JournalText.Truncate("hi", 200));

    [Fact]
    public void Truncate_at_boundary_returns_whole() =>
        Assert.Equal(new string('x', 200), JournalText.Truncate(new string('x', 200), 200));

    [Fact]
    public void Truncate_long_ascii_cuts_at_max() =>
        Assert.Equal(new string('x', 200), JournalText.Truncate(new string('x', 250), 200));

    [Fact]
    public void Truncate_does_not_split_a_surrogate_pair_at_the_boundary()
    {
        // 199 ASCII + an emoji (a surrogate pair) so the 200-unit boundary falls between the pair's
        // two halves. The cut must step back to 199 rather than emit a lone high surrogate.
        var value = new string('a', 199) + "\U0001F600" + "tail";

        var result = JournalText.Truncate(value, 200);

        Assert.Equal(199, result!.Length);
        Assert.Equal(new string('a', 199), result);
        Assert.DoesNotContain('\uD83D', result); // no lone high surrogate left behind
    }
}
