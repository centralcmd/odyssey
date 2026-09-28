using System.Text.RegularExpressions;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The calendar palette (Odyssey Design System · ColorSwatchSelect <c>CALENDAR_SWATCHES</c>). A swatch's
/// <c>Hex</c> is the persisted <c>Calendar.Color</c> value, so it stays a literal; its <c>Token</c> names the
/// ramp stop it must equal. These pin both halves: the literal agrees with the token in <c>app.css</c>, and
/// the whole row agrees with the design system's registry.
/// </summary>
public sealed class OdsCalendarSwatchesTests
{
    [Fact]
    public void Every_swatch_hex_equals_the_ramp_token_it_names()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css"));

        foreach (var swatch in OdsCalendarSwatches.All)
        {
            var declared = Regex.Match(css, Regex.Escape(swatch.Token) + @"\s*:\s*(?<hex>#[0-9A-Fa-f]{6})\s*;");
            Assert.True(declared.Success, $"{swatch.Token} is not declared as a hex in app.css.");
            Assert.Equal(declared.Groups["hex"].Value, swatch.Hex, ignoreCase: true);
        }
    }

    [Fact]
    public void The_palette_agrees_with_the_design_system()
    {
        var path = ClientSource.Sibling(Path.Combine("Odyssey Design System", "components", "ColorSwatchSelect.jsx"));
        var declared = Regex.Matches(
                File.ReadAllText(path),
                @"\{\s*key:\s*'(?<key>\w+)',\s*name:\s*'(?<name>[^']*)',\s*hex:\s*'(?<hex>#[0-9A-Fa-f]{6})',"
                + @"\s*fg:\s*'(?<fg>#[0-9A-Fa-f]{6})',\s*token:\s*'(?<token>--[a-z0-9-]+)'")
            .Select(m => new OdsCalendarSwatch(
                m.Groups["key"].Value, m.Groups["name"].Value, m.Groups["hex"].Value,
                m.Groups["fg"].Value, m.Groups["token"].Value))
            .ToList();

        Assert.NotEmpty(declared);
        Assert.Equal(declared, OdsCalendarSwatches.All);
    }
}
