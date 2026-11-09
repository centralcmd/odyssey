using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>OdsAddRow</c> — the dashed, list-closing create affordance (Odyssey Design System · AddRow).
/// </summary>
public class OdsAddRowTests
{
    /// <summary>
    /// The glyph tile is a neutral well on the action hover token, matching the closed-list ghost
    /// affordance — not a primary-tinted tile, which would read as a second call to action.
    /// </summary>
    [Fact]
    public void The_icon_tile_sits_on_the_action_default_hover_well()
    {
        var css = ClientSource.WithoutComments(
            File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsAddRow.razor.css")));

        var rule = Regex.Match(css, @"(?:^|\})\s*\.odc-add-row-ic\s*\{(?<body>[^{}]*)\}");
        Assert.True(rule.Success, "OdsAddRow.razor.css has no .odc-add-row-ic rule.");

        var background = Regex.Match(rule.Groups["body"].Value, @"(?:^|;)\s*background\s*:\s*(?<value>[^;]+);");
        Assert.True(background.Success, ".odc-add-row-ic declares no background.");
        Assert.Equal("var(--mud-palette-action-default-hover)", background.Groups["value"].Value.Trim());
    }
}
