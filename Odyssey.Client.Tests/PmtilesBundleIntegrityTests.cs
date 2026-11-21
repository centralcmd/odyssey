using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The vendored pmtiles library (issue #384 §7.7, AC 7, 8): Dependabot cannot see these files, so the bytes are
/// pinned, and so are the absences that keep the CSP unchanged. A bump updates these hashes, the folder's README
/// and its re-check record in one change — the <see cref="MapLibreBundleIntegrityTests"/> precedent.
/// </summary>
public class PmtilesBundleIntegrityTests
{
    private const string Version = "4.5.0";

    private static readonly string Folder = Path.Combine(ClientSource.Root, "wwwroot", "lib", "pmtiles");

    public static TheoryData<string, string> PinnedFiles => new()
    {
        { "pmtiles.js", "caf981bc46f6327ee7e65d5dc964d89d38a69f60edca2bd4c5c890c21b554c6c" },
        { "LICENSE.txt", "0371c38f338835f7fc13ed71176f3d92144e22c8b736a31cced57adbbeb647b3" },
        { "fflate-LICENSE.txt", "805f6cb28bb8b6d3a0badd83c93bccd9671fa01a3b4b92b7042b0743325ac243" },
    };

    [Theory]
    [MemberData(nameof(PinnedFiles))]
    public void Each_vendored_file_matches_its_pinned_hash(string file, string sha256)
    {
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Folder, file)))));
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

        Assert.Contains($"pmtiles@{Version}", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(Regex.Matches(readme, @"pmtiles@(?<v>\d+\.\d+\.\d+)").Select(m => m.Groups["v"].Value), v => v != Version);
        foreach (var row in PinnedFiles)
            Assert.Contains((string)row[1], readme, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eval(")]
    [InlineData("new Function")]
    [InlineData("Worker(")]
    [InlineData("blob:")]
    public void The_bundle_needs_nothing_the_csp_withholds(string construct)
    {
        Assert.DoesNotContain(construct, File.ReadAllText(Path.Combine(Folder, "pmtiles.js")), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_loads_pmtiles_from_a_cdn()
    {
        var module = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "js", "location-map.js"));

        Assert.Contains("'../lib/pmtiles/pmtiles.js'", module, StringComparison.Ordinal);
        Assert.DoesNotContain("pmtiles", File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html")), StringComparison.OrdinalIgnoreCase);
    }
}
