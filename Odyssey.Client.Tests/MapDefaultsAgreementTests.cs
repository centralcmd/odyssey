using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// One shipped map default, restated in six files (issue #370 AC 12): the API's <c>appsettings.json</c>, the
/// client Dockerfile, <c>docker-compose.yml</c> and the three <c>.env*.example</c> files. The CSP and the
/// API's origin check only agree while these do, so a provider change that misses one fails here rather
/// than as a blank map in production.
/// </summary>
public class MapDefaultsAgreementTests
{
    private const string Origin = "https://tiles.openfreemap.org";

    private static readonly string[] EnvExamples = [".env.example", ".env.localhost.example", ".env.prod.example"];

    [Fact]
    public void Every_file_carries_the_same_default_origin()
    {
        var found = new List<(string File, string Value)>
        {
            ("Odyssey.Api/appsettings.json", AppSettings().GetProperty("AllowedOrigins").GetString()!),
            ("Odyssey.Client/Dockerfile", Single("Odyssey.Client/Dockerfile", @"(?m)^ENV MAP_ALLOWED_ORIGINS=(?<v>.*)$")),
        };
        found.AddRange(All("docker-compose.yml", @"\$\{MAP_ALLOWED_ORIGINS-(?<v>[^}]*)\}").Select(v => ("docker-compose.yml", v)));
        found.AddRange(EnvExamples.Select(file => (file, Single(file, @"(?m)^#MAP_ALLOWED_ORIGINS=(?<v>.*)$"))));

        Assert.Equal(7, found.Count); // compose names it twice: the api and the client.
        var disagreeing = found.Where(f => f.Value != Origin).Select(f => $"{f.File}: '{f.Value}'").ToList();
        Assert.True(disagreeing.Count == 0, $"Default map origin differs from {Origin}:\n" + string.Join('\n', disagreeing));
    }

    [Theory]
    [InlineData("StyleUrlLight", "MAP_STYLE_URL_LIGHT")]
    [InlineData("StyleUrlDark", "MAP_STYLE_URL_DARK")]
    [InlineData("Attribution", "MAP_ATTRIBUTION")]
    [InlineData("AttributionUrl", "MAP_ATTRIBUTION_URL")]
    public void Compose_and_the_env_examples_restate_the_api_default(string key, string variable)
    {
        var expected = AppSettings().GetProperty(key).GetString()!;

        Assert.Equal(expected, Single("docker-compose.yml", $@"\$\{{{variable}-(?<v>[^}}]*)\}}"));
        foreach (var file in EnvExamples)
        {
            Assert.Equal(expected, Single(file, $@"(?m)^#{variable}=(?<v>.*)$"));
        }
    }

    [Fact]
    public void Compose_uses_the_single_dash_form_so_an_explicit_empty_value_survives()
    {
        var compose = Read("docker-compose.yml");

        Assert.DoesNotMatch(@"\$\{MAP_[A-Z_]+:-", compose);
        Assert.Equal(6, Regex.Matches(compose, @"\$\{MAP_[A-Z_]+-").Count);
    }

    [Fact]
    public void The_production_overlay_sets_no_map_variable_and_mounts_the_render_target()
    {
        var prod = HashComments(Read("docker-compose.prod.yml"));

        Assert.DoesNotContain("MAP_", prod, StringComparison.Ordinal);
        Assert.DoesNotContain("Map__", prod, StringComparison.Ordinal);
        Assert.Contains("- /etc/nginx/conf.d:uid=101,gid=101", prod, StringComparison.Ordinal);
    }

    // nginx and YAML comments run from '#' to the end of the line.
    private static string HashComments(string text) => Regex.Replace(text, @"(?m)#.*$", string.Empty);

    private static JsonElement AppSettings()
    {
        using var document = JsonDocument.Parse(Read("Odyssey.Api/appsettings.json"));
        return document.RootElement.GetProperty("Map").Clone();
    }

    private static string Read(string relativePath) => File.ReadAllText(ClientSource.Sibling(relativePath));

    private static string Single(string file, string pattern)
    {
        var values = All(file, pattern);
        Assert.True(values.Count == 1, $"{file}: expected one match for {pattern}, found {values.Count}.");
        return values[0];
    }

    private static List<string> All(string file, string pattern) =>
        Regex.Matches(Read(file), pattern).Select(m => m.Groups["v"].Value.TrimEnd('\r')).ToList();
}
