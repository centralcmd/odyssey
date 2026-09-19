using Odyssey.Client.Pages.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="TileRemovalFocus"/>'s neighbour choice — the logic behind "focus lands on the
/// neighbouring tile's menu, or the section fallback when the removal empties the section"
/// (#122 AC 13, WCAG 2.4.3).
/// </summary>
/// <remarks>
/// <para>
/// The surrounding mechanism needs a rendered component and JS interop, but the CHOICE does not: it is
/// a pure function over the keys, and it is the half that decides where a keyboard user lands. Pinning
/// it here means the two tile surfaces that share the helper — insurance and contracts — both inherit
/// the guarantee rather than each re-proving it through the DOM.
/// </para>
/// <para>
/// The "removed tile was last" case is the one worth having: taking the next member unconditionally
/// returns null there, which drops focus onto the document — the exact defect the helper exists to
/// prevent, and one that looks fine in every test that removes from the middle.
/// </para>
/// </remarks>
public class TileRemovalFocusTests
{
    private static readonly string[] Three = ["a", "b", "c"];

    /// <summary>Removing from the middle or the front lands on the NEXT tile.</summary>
    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "c")]
    public void Focus_moves_to_the_next_tile(string removed, string expected) =>
        Assert.Equal(expected, TileRemovalFocus.NeighbourOf(removed, Three));

    /// <summary>Removing the LAST tile falls back to the previous one — there is no next.</summary>
    [Fact]
    public void Removing_the_last_tile_falls_back_to_the_previous_one() =>
        Assert.Equal("b", TileRemovalFocus.NeighbourOf("c", Three));

    /// <summary>
    /// Removing the ONLY tile has no neighbour at all. Null is the signal that empties the section, and
    /// is what makes the caller fall through to its own fallback selector rather than focusing nothing.
    /// </summary>
    [Fact]
    public void Removing_the_only_tile_has_no_neighbour() =>
        Assert.Null(TileRemovalFocus.NeighbourOf("a", ["a"]));

    /// <summary>
    /// A key that is not in the list yields no neighbour rather than throwing or picking an arbitrary
    /// tile. A removal raced by a re-fetch can land here, and guessing would move focus to a party the
    /// user never pointed at.
    /// </summary>
    [Fact]
    public void An_unknown_key_yields_no_neighbour()
    {
        Assert.Null(TileRemovalFocus.NeighbourOf("zzz", Three));
        Assert.Null(TileRemovalFocus.NeighbourOf("a", []));
    }

    /// <summary>
    /// Keys are matched ORDINALLY. They are GUID strings on both surfaces, so a culture-sensitive
    /// comparison would be a latent correctness bug rather than a style point.
    /// </summary>
    [Fact]
    public void Keys_are_matched_ordinally() =>
        Assert.Null(TileRemovalFocus.NeighbourOf("A", Three));
}
