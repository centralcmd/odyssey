using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The location map's markup-free contract (issue #371 AC 5, 24; S1, S2). A location's name and a remote
/// style's attribution are untrusted text, and the map surface is where they would become markup. Every
/// node is built with createElement + textContent, and MapLibre's attribution control stays off, so the
/// style's own HTML attribution never reaches the DOM.
/// </summary>
public class LocationMapSourceLintTests
{
    private static readonly string[] Forbidden =
        ["setHTML", "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "MarkupString"];

    private static IReadOnlyList<string> ScanSet()
    {
        var files = new List<string> { Path.Combine(ClientSource.Root, "wwwroot", "js", "location-map.js") };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(ClientSource.Root, "Components"), "OdsLocationMap.*"));
        return files;
    }

    [Fact]
    public void The_scan_set_is_the_module_and_the_component()
    {
        var names = ScanSet().Where(File.Exists).Select(Path.GetFileName).ToList();

        Assert.Contains("location-map.js", names);
        Assert.Contains("OdsLocationMap.razor", names);
        Assert.Contains("OdsLocationMap.razor.cs", names);
    }

    [Fact]
    public void No_file_in_the_scan_set_builds_markup_from_a_string()
    {
        var hits = ScanSet()
            .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, i)))
            .Where(x => !IsComment(x.line))
            .SelectMany(x => Forbidden.Where(f => x.line.Contains(f, StringComparison.Ordinal))
                .Select(f => $"{Path.GetFileName(x.file)}:{x.i + 1} uses {f}"))
            .ToList();

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void The_attribution_control_is_only_ever_off()
    {
        var module = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "js", "location-map.js"));

        var settings = Regex.Matches(module, @"attributionControl\s*:\s*(?<value>[^,\s}]+)");
        Assert.NotEmpty(settings);
        Assert.All(settings, m => Assert.Equal("false", m.Groups["value"].Value));
        Assert.DoesNotContain("AttributionControl", module, StringComparison.Ordinal);
        Assert.DoesNotContain("customAttribution", module, StringComparison.Ordinal);
    }

    [Fact]
    public void Tile_requests_never_carry_credentials()
    {
        var module = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "js", "location-map.js"));

        Assert.Matches(@"transformRequest:\s*url\s*=>\s*\(\{\s*url,\s*credentials:\s*'omit'\s*\}\)", module);
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("@*", StringComparison.Ordinal);
    }
}
