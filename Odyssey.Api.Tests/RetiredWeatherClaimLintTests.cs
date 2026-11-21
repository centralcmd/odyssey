using Odyssey.Testing;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #387 AC 9: the three weather claim values the weather-points feature renamed (§7.2) are gone from the
/// client, the typed API client and the operator documentation. A survivor is either a gate that can never be
/// satisfied again or documentation sending an operator after a claim no role holds. The release notes and
/// <c>CHANGELOG.md</c> must name them, so they are exempt — they live outside the scanned folders.
/// </summary>
public class RetiredWeatherClaimLintTests
{
    private static readonly string[] ScannedFolders = ["Odyssey.Client", "Odyssey.ApiClient", "docs"];

    private static readonly string[] ScannedExtensions = [".cs", ".razor", ".js", ".json", ".md", ".html", ".css"];

    [Fact]
    public void No_retired_weather_claim_value_remains_in_the_client_the_api_client_or_the_docs()
    {
        var root = SourceTree.Root;
        var files = ScannedFolders
            .SelectMany(folder => SourceTree.Files(folder, "*"))
            .Where(path => ScannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(files);

        var hits = files
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return AuthorizationPolicyTests.RetiredWeatherClaimValues
                    .Where(value => text.Contains(value, StringComparison.Ordinal))
                    .Select(value => $"{Path.GetRelativePath(root, path)}: {value}");
            })
            .ToList();

        Assert.True(hits.Count == 0, "Retired weather claim values still present:\n" + string.Join("\n", hits));
    }
}
