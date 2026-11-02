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
        files.AddRange(Directory.EnumerateFiles(Path.Combine(ClientSource.Root, "Pages", "Journal"), "LocationMap.*"));
        return files;
    }

    [Fact]
    public void The_scan_set_is_the_module_and_the_component()
    {
        var names = ScanSet().Where(File.Exists).Select(Path.GetFileName).ToList();

        Assert.Contains("location-map.js", names);
        Assert.Contains("LocationMap.razor", names);
        Assert.Contains("LocationMap.razor.cs", names);
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

    // ── The self-hosted basemap's archive wiring (issue #384 §3.3, AC 8) ─────────────────────────────

    [Fact]
    public void The_basemap_archive_is_registered_verbatim_under_its_one_url()
    {
        var module = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "js", "location-map.js"));

        // Pre-registered under exactly this URL: an unregistered pmtiles:// URL makes the library build a
        // default source of its own, silently. 'same-origin' is inside FetchSource's declared type and sends
        // the session cookie to the deployment only, for nginx's auth_request.
        Assert.Contains("const protocol = new pmtiles.Protocol();", module, StringComparison.Ordinal);
        Assert.Contains(
            "protocol.add(new pmtiles.PMTiles(new pmtiles.FetchSource('/basemap/basemap.pmtiles', new Headers(), 'same-origin')));",
            module, StringComparison.Ordinal);
        Assert.Contains("lib.addProtocol('pmtiles', protocol.tile);", module, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(module, @"new\s+pmtiles\.FetchSource\("));
        Assert.Single(Regex.Matches(module, @"new\s+pmtiles\.PMTiles\("));
    }

    [Fact]
    public void Only_the_map_module_references_pmtiles()
    {
        var root = ClientSource.Root;
        var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal)
                || f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains(Path.Combine("wwwroot", "lib"), StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "location-map.js" && Path.GetFileName(f) != "service-worker.published.js")
            .Where(f => File.ReadAllText(f).Contains("pmtiles", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0, "pmtiles referenced outside location-map.js: " + string.Join(", ", offenders));
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
