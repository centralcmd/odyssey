using System.Text.Json;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The two self-hosted basemap styles and the static assets they depend on (issue #384 §3.4, §7.11, AC 9–12).
/// With either style selected nothing may leave the deployment, so every resource URL is same-origin; and the
/// operator supplies only the PMTiles file, so every glyph range, the sprite and the licences ship here.
/// </summary>
public class SelfHostedBasemapStyleTests
{
    private const string Archive = "pmtiles:///basemap/basemap.pmtiles";
    private const string Glyphs = "/maps/self-hosted/fonts/{fontstack}/{range}.pbf";
    private const string Sprite = "/maps/self-hosted/sprites/ofm";

    // Latin, Latin Extended-A/B, IPA, Greek, Cyrillic; Latin Extended Additional, Greek Extended, General
    // Punctuation, currency and letterlike symbols. Measured at 2.7 MB for three stacks (§3.4).
    private static readonly string[] ShippedRanges =
        ["0-255", "256-511", "512-767", "768-1023", "1024-1279", "1280-1535", "7680-7935", "7936-8191", "8192-8447", "8448-8703"];

    private static readonly string Maps = Path.Combine(ClientSource.Root, "wwwroot", "maps");

    public static TheoryData<string> Styles => new() { "self-hosted-light.json", "self-hosted-dark.json" };

    private static JsonElement Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Maps, file))).RootElement.Clone();

    [Theory]
    [MemberData(nameof(Styles))]
    public void Every_resource_is_served_by_the_deployment(string file)
    {
        var style = Load(file);

        var source = Assert.Single(style.GetProperty("sources").EnumerateObject());
        Assert.Equal("vector", source.Value.GetProperty("type").GetString());
        Assert.Equal(Archive, source.Value.GetProperty("url").GetString());
        Assert.False(source.Value.TryGetProperty("tiles", out _));
        Assert.Equal(Glyphs, style.GetProperty("glyphs").GetString());
        Assert.Equal(Sprite, style.GetProperty("sprite").GetString());

        // Provenance links in metadata are allowed (they are never fetched); nothing else may be absolute.
        var withoutMetadata = JsonSerializer.Serialize(
            style.EnumerateObject().Where(p => p.Name != "metadata").ToDictionary(p => p.Name, p => p.Value));
        Assert.DoesNotContain("http://", withoutMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", withoutMetadata, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void Every_layer_draws_from_the_archive(string file)
    {
        var sources = Load(file).GetProperty("layers").EnumerateArray()
            .Where(l => l.TryGetProperty("source", out _))
            .Select(l => l.GetProperty("source").GetString())
            .Distinct()
            .ToList();

        Assert.Equal(["openmaptiles"], sources);
    }

    [Fact]
    public void The_dark_style_keeps_odyssey_darks_layers_exactly()
    {
        // So OdysseyDarkBasemapTests' building and label-contrast guarantees hold for it too (AC 10).
        var shipped = Load("odyssey-dark.json").GetProperty("layers").GetRawText();
        var selfHosted = Load("self-hosted-dark.json").GetProperty("layers").GetRawText();

        Assert.Equal(Normalize(shipped), Normalize(selfHosted));
    }

    [Fact]
    public void The_light_style_has_no_raster_layer()
    {
        var layers = Load("self-hosted-light.json").GetProperty("layers").EnumerateArray().ToList();

        Assert.DoesNotContain(layers, l => l.GetProperty("type").GetString() == "raster");
        Assert.DoesNotContain(layers, l => l.GetProperty("id").GetString() == "natural_earth");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void Every_font_stack_ships_the_measured_ranges(string file)
    {
        var stacks = Load(file).GetProperty("layers").EnumerateArray()
            .Where(l => l.TryGetProperty("layout", out var layout) && layout.TryGetProperty("text-font", out _))
            .Select(l => l.GetProperty("layout").GetProperty("text-font"))
            .Select(f => string.Join(",", f.EnumerateArray().Select(e => e.GetString())))
            .Distinct()
            .ToList();
        Assert.NotEmpty(stacks);

        foreach (var stack in stacks)
        {
            var folder = Path.Combine(Maps, "self-hosted", "fonts", stack);
            Assert.True(Directory.Exists(folder), $"No glyphs for font stack '{stack}'.");
            Assert.Equal(
                ShippedRanges.Select(r => r + ".pbf").Order(StringComparer.Ordinal),
                Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void The_glyph_fallback_and_licence_ship_and_the_set_stays_small()
    {
        var fonts = Path.Combine(Maps, "self-hosted", "fonts");

        Assert.Equal(0, new FileInfo(Path.Combine(fonts, "empty.pbf")).Length);
        Assert.Contains("SIL Open Font License", File.ReadAllText(Path.Combine(fonts, "OFL.txt")), StringComparison.Ordinal);
        var bytes = Directory.EnumerateFiles(fonts, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.True(bytes <= 4 * 1024 * 1024, $"The shipped glyphs are {bytes / 1024} KB; the budget is 4 MB (§3.4).");
    }

    [Fact]
    public void The_sprite_and_every_licence_ship()
    {
        var sprites = Path.Combine(Maps, "self-hosted", "sprites");
        foreach (var name in new[] { "ofm.json", "ofm.png", "ofm@2x.json", "ofm@2x.png" })
            Assert.True(new FileInfo(Path.Combine(sprites, name)).Length > 0, $"{name} is missing or empty.");

        Assert.Contains("CC0 1.0 Universal", File.ReadAllText(Path.Combine(sprites, "LICENSE.txt")), StringComparison.Ordinal);
        Assert.Contains("BSD 3-Clause", File.ReadAllText(Path.Combine(Maps, "self-hosted-dark.LICENSE.txt")), StringComparison.Ordinal);
        var light = File.ReadAllText(Path.Combine(Maps, "self-hosted-light.LICENSE.txt"));
        Assert.Contains("MIT", light, StringComparison.Ordinal);
        Assert.Contains("Creative Commons Attribution 3.0", light, StringComparison.Ordinal);
        Assert.Contains("CC-BY 4.0", light, StringComparison.Ordinal);
    }

    [Fact]
    public void No_published_asset_lives_under_basemap()
    {
        // /basemap/ is the volume's; a published file there would be shadowed by nginx's exact-path location.
        Assert.False(Directory.Exists(Path.Combine(ClientSource.Root, "wwwroot", "basemap")));
    }

    private static string Normalize(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
}
