using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsGlyphInk"/>'s output (issue #164): the exact relative-colour wrap each cap writes, the
/// two lightness channels it names, and the shape check that keeps anything but a colour out of an
/// inline <c>style</c>.
/// </summary>
public sealed class OdsGlyphInkTests
{
    [Fact]
    public void The_lightness_channels_name_the_two_app_css_tokens_with_a_pass_through_fallback()
    {
        Assert.Equal("var(--glyph-l, l)", OdsGlyphInk.GlyphLightness);
        Assert.Equal("var(--glyph-text-l, l)", OdsGlyphInk.TextLightness);
    }

    [Theory]
    [InlineData("oklch(0.77 0.14 55)")]
    [InlineData("var(--acct-cash)")]
    [InlineData("var(--rose-500, #F2557A)")]
    [InlineData("var(--rec, var(--brand-text))")]
    [InlineData("#15803D")]
    [InlineData("#fff")]
    [InlineData("rgb(178, 59, 59)")]
    [InlineData("rgba(20, 26, 44, 0.5)")]
    [InlineData("hsl(210 40% 50%)")]
    public void A_colour_is_wrapped_in_the_glyph_and_text_caps(string colour)
    {
        Assert.Equal($"oklch(from {colour} var(--glyph-l, l) c h)", OdsGlyphInk.Glyph(colour));
        Assert.Equal($"oklch(from {colour} var(--glyph-text-l, l) c h)", OdsGlyphInk.Text(colour));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        Assert.Equal("oklch(from #fff var(--glyph-l, l) c h)", OdsGlyphInk.Glyph("  #fff "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#zzzzzz")]
    [InlineData("oklch(0.7 0.1 20); background:url(x)")]
    [InlineData("var(--x); position:fixed")]
    [InlineData("var(--x, red);display:none")]
    [InlineData("rgb(1,2,3)\" onmouseover=\"x")]
    [InlineData("oklch(0.7 0.1 20)</style>")]
    [InlineData("var(x)")]
    [InlineData("expression(alert(1))")]
    [InlineData("url(javascript:alert(1))")]
    [InlineData("rgb(1,2,3) }")]
    public void Anything_but_a_recognised_colour_paints_inherit(string? input)
    {
        Assert.False(OdsGlyphInk.IsColour(input));
        Assert.Equal(OdsGlyphInk.Fallback, OdsGlyphInk.Glyph(input));
        Assert.Equal(OdsGlyphInk.Fallback, OdsGlyphInk.Text(input));
        Assert.Equal("inherit", OdsGlyphInk.Fallback);
    }
}
