using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The vendored MapLibre GL JS (issue #371 AC 21, S3, R2-S1). Dependabot and
/// <c>dotnet list package --vulnerable</c> cannot see these files, so the bytes are pinned here: an
/// unreviewed change to any of them fails CI. A bump updates these hashes, the folder's README and its
/// re-check record in one change.
/// </summary>
public class MapLibreBundleIntegrityTests
{
    private const string Version = "6.14.0";

    private static readonly string Folder = Path.Combine(ClientSource.Root, "wwwroot", "lib", "maplibre-gl");

    public static TheoryData<string, string> PinnedFiles => new()
    {
        { "maplibre-gl.js", "244d36122c1398da3a8259817cc6d71e04321afe6404f32606b4b6c6cc4f26fb" },
        { "maplibre-gl-worker.js", "5f1e2f0e1624082b7e88a06e4c86085992b0f8c02929801e552f3f7a119856a9" },
        { "maplibre-gl.css", "bacba6a85a867fcf602f2ff88331b8acf7edf3fbd063a519888cfbff39970193" },
        { "LICENSE.txt", "ee5fc05a0677eaf69601d2c7db0d9ecd6cc27c3abc1d0733bc9ed34707cf8ef2" },
    };

    [Theory]
    [MemberData(nameof(PinnedFiles))]
    public void Each_vendored_file_matches_its_pinned_hash(string file, string sha256)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Folder, file));

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void The_folder_holds_only_the_pinned_files_and_the_readme()
    {
        var expected = PinnedFiles.Select(row => (string)row[0]).Append("README.md").Order(StringComparer.Ordinal);

        Assert.Equal(expected, Directory.EnumerateFiles(Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_readme_names_the_pinned_version_and_every_hash()
    {
        var readme = File.ReadAllText(Path.Combine(Folder, "README.md"));

        Assert.Contains($"maplibre-gl@{Version}", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Regex.Matches(readme, @"maplibre-gl@(?<v>\d+\.\d+\.\d+)").Select(m => m.Groups["v"].Value),
            v => v != Version);
        foreach (var row in PinnedFiles)
            Assert.Contains((string)row[1], readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bundle_header_names_the_pinned_version()
    {
        using var reader = new StreamReader(Path.Combine(Folder, "maplibre-gl.js"));
        var header = string.Concat(Enumerable.Range(0, 4).Select(_ => reader.ReadLine()));

        Assert.Contains($"/v{Version}/LICENSE.txt", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_loads_maplibre_from_a_cdn()
    {
        var module = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "js", "location-map.js"));
        var index = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html"));

        Assert.DoesNotMatch(@"https?://[^'""`\s]*(unpkg|jsdelivr|cdnjs|esm\.sh|skypack)", module);
        Assert.DoesNotContain("maplibre", index, StringComparison.OrdinalIgnoreCase);
    }
}
