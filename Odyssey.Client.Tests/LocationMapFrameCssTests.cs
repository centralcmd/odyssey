using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The map frame's shape (design system · LocationMap → Frame): full width at 4:3, capped at 70vh, never below
/// 256 px. The E2E tier checks the laid-out box but skips without a running stack; this pins the declarations
/// on every build.
/// </summary>
public class LocationMapFrameCssTests
{
    private static readonly Lazy<string> Frame = new(() =>
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsLocationMap.razor.css"));
        var block = Regex.Match(css, @"(?m)^\.loc-map-frame\s*\{(?<body>[^}]*)\}");
        Assert.True(block.Success, "OdsLocationMap.razor.css has no .loc-map-frame rule.");
        return block.Groups["body"].Value;
    });

    [Theory]
    [InlineData("width", "100%")]
    [InlineData("aspect-ratio", "4 / 3")]
    [InlineData("max-height", "70vh")]
    [InlineData("min-height", "calc(var(--space-16) * 4)")]
    public void The_frame_declares_its_shape(string property, string value)
    {
        var declared = Regex.Match(Frame.Value, $@"(?m)^\s*{Regex.Escape(property)}\s*:\s*(?<v>[^;]+);");

        Assert.True(declared.Success, $".loc-map-frame declares no {property}.");
        Assert.Equal(value, declared.Groups["v"].Value.Trim());
    }

    [Fact]
    public void The_floor_resolves_to_256_px()
    {
        var app = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "app.css"));
        var space16 = Regex.Match(app, @"--space-16:\s*(?<px>\d+)px;");

        Assert.True(space16.Success, "app.css declares no --space-16 in px.");
        Assert.Equal(256, int.Parse(space16.Groups["px"].Value, System.Globalization.CultureInfo.InvariantCulture) * 4);
    }
}
