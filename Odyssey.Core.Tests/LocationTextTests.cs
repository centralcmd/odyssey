using Odyssey.Core.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// <see cref="LocationText"/> — the one normalization applied to location input and to provider text
/// (issue #350 §7.9, S7).
/// </summary>
public class LocationTextTests
{
    [Fact]
    public void Decomposed_text_is_stored_composed()
    {
        var decomposed = "Ål"; // A + combining ring above, then l

        Assert.Equal("Ål", LocationText.Clean(decomposed));
    }

    [Theory]
    [InlineData("Cab\u0085in")] // NEL, a C1 control
    [InlineData("Cab\u009Bin")] // CSI, a C1 control
    [InlineData("Cab\u0000in")]
    [InlineData("Cab‮in")] // right-to-left override
    [InlineData("Cab⁧in")] // right-to-left isolate
    public void Control_and_bidi_characters_are_removed(string input) =>
        Assert.Equal("Cabin", LocationText.Clean(input));

    [Fact]
    public void A_note_keeps_its_line_breaks_folded_to_newline()
    {
        Assert.Equal("one\ntwo\nthree", LocationText.Clean("one\r\ntwo\nthree", allowLineBreaks: true));
        Assert.Equal("onetwo", LocationText.Clean("one\r\ntwo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("‮\u0007")]
    public void Nothing_left_is_null(string? input) => Assert.Null(LocationText.Clean(input));

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var text = "abcd\U0001F3D4ef"; // the mountain is a surrogate pair at index 4–5

        var cut = LocationText.CleanAndTruncate(text, 5);

        Assert.Equal("abcd", cut);
        Assert.False(char.IsHighSurrogate(cut![^1]));
    }

    [Fact]
    public void Truncation_keeps_text_already_within_the_limit() =>
        Assert.Equal("Oslo", LocationText.CleanAndTruncate("  Oslo ", 10));
}
