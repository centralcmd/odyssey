using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The shipped dark basemap, <c>wwwroot/maps/odyssey-dark.json</c> (issues #370, #371): OpenFreeMap's
/// <c>fiord</c> with its buildings made visible. Upstream draws them at about 16% opacity, navy on navy,
/// (1.13:1 against the ground), which is the defect this copy exists to fix — so a re-sync from upstream that
/// brings that back fails here.
/// </summary>
public class OdysseyDarkBasemapTests
{
    private static readonly Lazy<JsonDocument> Style = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "maps", "odyssey-dark.json"))));

    private static JsonElement Root => Style.Value.RootElement;

    [Fact]
    public void Buildings_stand_out_from_the_ground()
    {
        var building = Layer("building");
        var paint = building.GetProperty("paint");
        var opacity = paint.GetProperty("fill-opacity").GetDouble();
        var background = Hex(Layer("background").GetProperty("paint").GetProperty("background-color").GetString()!);
        var fill = Hex(paint.GetProperty("fill-color").GetString()!);

        Assert.True(opacity >= 0.8, $"Building fill-opacity is {opacity}; upstream fiord's 0.25 is what made houses invisible.");
        var ratio = Ratio(Blend(fill, background, opacity), background);
        Assert.True(ratio >= 1.5, $"Buildings are {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1 against the ground; they must read as distinct shapes.");
    }

    [Fact]
    public void Every_remote_resource_is_on_the_default_tile_host()
    {
        // The CSP admits only MAP_ALLOWED_ORIGINS; a URL on any other host would load nothing.
        var hosts = Regex.Matches(Root.GetRawText(), @"""(?<url>https?://[^""]+)""")
            .Select(m => new Uri(m.Groups["url"].Value).GetLeftPart(UriPartial.Authority))
            .Distinct()
            .ToList();

        Assert.Equal(["https://tiles.openfreemap.org"], hosts);
    }

    [Fact]
    public void The_licence_and_provenance_travel_with_the_file()
    {
        var metadata = Root.GetProperty("metadata");

        Assert.Contains("BSD 3-Clause", metadata.GetProperty("odyssey:license").GetString());
        Assert.Contains("CC-BY 4.0", metadata.GetProperty("odyssey:license").GetString());
        Assert.Contains("styles/fiord", metadata.GetProperty("odyssey:source").GetString());
    }

    [Fact]
    public void It_is_the_shipped_dark_default()
    {
        var appsettings = File.ReadAllText(ClientSource.Sibling(Path.Combine("Odyssey.Api", "appsettings.json")));

        Assert.Contains("\"StyleUrlDark\": \"/maps/odyssey-dark.json\"", appsettings, StringComparison.Ordinal);
    }

    private static JsonElement Layer(string id) =>
        Root.GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("id").GetString() == id);

    private static (double R, double G, double B) Hex(string hex) => (
        Convert.ToByte(hex.Substring(1, 2), 16) / 255.0,
        Convert.ToByte(hex.Substring(3, 2), 16) / 255.0,
        Convert.ToByte(hex.Substring(5, 2), 16) / 255.0);

    private static (double R, double G, double B) Blend((double R, double G, double B) top, (double R, double G, double B) under, double alpha) =>
        (top.R * alpha + under.R * (1 - alpha), top.G * alpha + under.G * (1 - alpha), top.B * alpha + under.B * (1 - alpha));

    private static double Ratio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        static double Lin(double s) => s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        static double L((double R, double G, double B) c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        var (la, lb) = (L(a), L(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
