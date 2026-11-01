using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The published service worker precaches every manifest .js/.css at install (issue #371 §10.2, F3). The
/// map's files are excluded so the first visit downloads no MapLibre. These run the worker's own exclusion
/// patterns, extracted from the file, against the manifest URLs a publish produces.
/// </summary>
public class LocationMapServiceWorkerTests
{
    private static readonly Lazy<IReadOnlyList<Regex>> Exclusions = new(() =>
    {
        var worker = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "service-worker.published.js"));
        var block = Regex.Match(worker, @"const offlineAssetsExclude = \[(?<body>[^\]]*(?:\][^\]]*)*?)\];", RegexOptions.Singleline);
        Assert.True(block.Success, "service-worker.published.js has no offlineAssetsExclude array.");
        return Regex.Matches(block.Groups["body"].Value, @"/(?<pattern>\^[^\n]*?)/,?\s*$", RegexOptions.Multiline)
            .Select(m => new Regex(m.Groups["pattern"].Value))
            .ToList();
    });

    [Theory]
    [InlineData("lib/maplibre-gl/maplibre-gl.js")]
    [InlineData("lib/maplibre-gl/maplibre-gl-worker.js")]
    [InlineData("lib/maplibre-gl/maplibre-gl.css")]
    [InlineData("js/location-map.js")]
    [InlineData("js/location-map.abc123xyz.js")]
    [InlineData("css/location-map.css")]
    [InlineData("css/location-map.abc123xyz.css")]
    [InlineData("maps/odyssey-dark.json")]
    [InlineData("maps/blank-light.json")]
    [InlineData("service-worker.js")]
    public void The_map_files_are_kept_out_of_the_precache(string url)
    {
        Assert.Contains(Exclusions.Value, pattern => pattern.IsMatch(url));
    }

    [Theory]
    [InlineData("js/plot-width.js")]
    [InlineData("css/app.css")]
    [InlineData("js/location-mapper.js")]
    [InlineData("appsettings.json")]
    [InlineData("_framework/blazor.webassembly.js")]
    public void Other_assets_are_still_precached(string url)
    {
        Assert.DoesNotContain(Exclusions.Value, pattern => pattern.IsMatch(url));
    }
}
