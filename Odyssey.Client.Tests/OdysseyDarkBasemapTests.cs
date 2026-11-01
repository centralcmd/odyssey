using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The shipped dark basemap, <c>wwwroot/maps/odyssey-dark.json</c> (issues #370, #371): OpenFreeMap's
/// <c>fiord</c> with its buildings made visible and its labels made legible. Upstream draws buildings at about
/// 16% effective opacity (fill-opacity 0.25 on a colour with alpha 0.65), navy on navy, 1.13:1 against the
/// ground, and several labels at 1.8–2.8:1 — the defects this copy exists to fix, so a re-sync from upstream
/// that brings either back fails here. The compose and <c>.env</c> copies of the default are
/// pinned by <see cref="MapDefaultsAgreementTests"/>.
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

        Assert.True(opacity >= 0.8, $"Building fill-opacity is {opacity}; upstream fiord's effective ~0.16 (0.25 × alpha 0.65) is what made houses invisible.");
        var ratio = Ratio(Blend(fill, background, opacity), background);
        Assert.True(ratio >= 1.5, $"Buildings are {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1 against the ground; they must read as distinct shapes.");
    }

    [Fact]
    public void Every_label_reads_against_the_ground_and_the_water()
    {
        // Labels sit on land and water alike; the thin halo is not counted, so the text colour carries it alone.
        var grounds = new[]
        {
            Hex(Layer("background").GetProperty("paint").GetProperty("background-color").GetString()!),
            Hex(Layer("water").GetProperty("paint").GetProperty("fill-color").GetString()!),
        };
        var failures = new List<string>();

        foreach (var layer in Root.GetProperty("layers").EnumerateArray())
        {
            var id = layer.GetProperty("id").GetString()!;
            // The continent label fades out by zoom 3 and never appears at the zooms the map allows.
            if (layer.GetProperty("type").GetString() != "symbol" || id == "place_continent")
                continue;

            var paint = layer.GetProperty("paint");
            if (paint.TryGetProperty("text-opacity", out var opacity) && opacity.ValueKind == JsonValueKind.Number && opacity.GetDouble() < 1)
                failures.Add($"{id}: text-opacity {opacity.GetDouble()} blends the label into the ground");

            foreach (var color in Colors(paint.GetProperty("text-color")))
            {
                var worst = grounds.Min(g => Ratio(color, g));
                if (worst < 4.5)
                    failures.Add($"{id}: {worst.ToString("0.00", CultureInfo.InvariantCulture)}:1");
            }
        }

        Assert.True(failures.Count == 0, "Labels below 4.5:1:\n" + string.Join('\n', failures));
    }

    [Fact]
    public void The_full_licence_text_ships_beside_the_style()
    {
        var licence = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "maps", "odyssey-dark.LICENSE.txt"));

        Assert.Contains("BSD 3-Clause", licence, StringComparison.Ordinal);
        Assert.Contains("CC-BY 4.0", licence, StringComparison.Ordinal);
        Assert.Contains("Redistributions in binary form must reproduce the above copyright notice", licence, StringComparison.Ordinal);
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

    // A text colour is a plain string or a zoom interpolation whose stops are strings.
    private static IEnumerable<(double R, double G, double B)> Colors(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? [Parse(value.GetString()!)]
            : value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String && e.GetString() is not ("interpolate" or "zoom"))
                .Select(e => Parse(e.GetString()!));

    private static (double R, double G, double B) Parse(string color)
    {
        if (color.StartsWith('#'))
            return Hex(color);

        var hsl = Regex.Match(color, @"^hsl\((?<h>\d+),\s*(?<s>\d+)%,\s*(?<l>\d+)%\)$");
        if (hsl.Success)
            return Hsl(double.Parse(hsl.Groups["h"].Value, CultureInfo.InvariantCulture),
                double.Parse(hsl.Groups["s"].Value, CultureInfo.InvariantCulture) / 100,
                double.Parse(hsl.Groups["l"].Value, CultureInfo.InvariantCulture) / 100);

        var rgb = Regex.Match(color, @"^rgb\((?<r>\d+),\s*(?<g>\d+),\s*(?<b>\d+)\)$");
        Assert.True(rgb.Success, $"Unrecognised label colour '{color}'.");
        return (int.Parse(rgb.Groups["r"].Value, CultureInfo.InvariantCulture) / 255.0,
            int.Parse(rgb.Groups["g"].Value, CultureInfo.InvariantCulture) / 255.0,
            int.Parse(rgb.Groups["b"].Value, CultureInfo.InvariantCulture) / 255.0);
    }

    private static (double R, double G, double B) Hsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return (r + m, g + m, b + m);
    }

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
