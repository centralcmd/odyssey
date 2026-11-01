using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The rendered Content Security Policy on the running client container (issue #370 AC 9). This is the
/// browser tier because <c>E2E_BASE_URL</c> is the client origin; it needs no browser, only the headers.
/// The service worker is asserted beside the shell because a <c>location</c> block declaring its own
/// <c>add_header</c> would silently drop the CSP from just that path.
/// </summary>
[Collection(StackCollection.Name)]
public sealed class MapCspHeaderTests(StackFixture fixture)
{
    private const string DefaultTileOrigin = "https://tiles.openfreemap.org";

    [SkippableTheory]
    [InlineData("/")]
    [InlineData("/service-worker.js")]
    public async Task The_csp_admits_the_tile_origin_and_only_a_self_worker(string path)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        using var client = new HttpClient();
        using var response = await client.GetAsync(fixture.BaseUrl + path);
        response.EnsureSuccessStatusCode();

        // Aspire serves the client from Kestrel, without nginx and so without its CSP: a different deployment
        // shape, not a broken one, so it is identified POSITIVELY by Kestrel's Server header. Anything else
        // (nginx directly, or Caddy in front of it) must carry the CSP — a location block declaring its own
        // add_header drops every inherited header, and that is the regression this test exists to fail on.
        var server = string.Join(' ', response.Headers.Server.Select(s => s.ToString()));
        Skip.If(
            server.Contains("Kestrel", StringComparison.OrdinalIgnoreCase),
            $"{fixture.BaseUrl} is served by '{server}', not the client container's nginx (an Aspire stack?); the CSP is a Compose concern.");

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values), $"No CSP on {path}.");
        var policy = Assert.Single(values);
        Assert.DoesNotContain("${", policy, StringComparison.Ordinal);
        var directives = Directives(policy);

        // The stack under test runs the shipped default unless its operator changed MAP_ALLOWED_ORIGINS.
        var expected = Environment.GetEnvironmentVariable("MAP_ALLOWED_ORIGINS") ?? DefaultTileOrigin;
        foreach (var origin in expected.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.Contains(origin, directives["connect-src"]);
            Assert.Contains(origin, directives["img-src"]);
        }

        Assert.Equal(["'self'"], directives["worker-src"]);
    }

    private static Dictionary<string, string[]> Directives(string policy) =>
        policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(t => t[0], t => t[1..], StringComparer.Ordinal);
}
