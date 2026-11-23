using Odyssey.Api.ApiDocs;
using Scalar.AspNetCore;
using Xunit;

namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// Every Scalar feature that contacts a third party is off, in code, in one place that no configuration can reach
/// (issue #569 §7.2). The option values are asserted here; <see cref="ScalarBundleIntegrityTests"/> is what catches a
/// NEW outbound feature arriving with a version bump.
/// </summary>
public class ScalarLockdownTests
{
    [Fact]
    public void TheLockdown_TurnsOffEveryOutboundFeature()
    {
        var options = new ScalarOptions();

        ScalarLockdown.Apply(options);

        Assert.False(options.DefaultFonts);
        Assert.True(options.Agent?.Disabled);
        Assert.True(options.Mcp?.Disabled);
        Assert.False(options.Telemetry);
        Assert.True(options.HideClientButton);
        Assert.Equal(DeveloperToolsVisibility.Never, options.ShowDeveloperTools);
        Assert.False(options.PersistentAuthentication);
        Assert.Null(options.ProxyUrl);
#pragma warning disable CS0618 // CdnUrl is obsolete in favour of BundleUrl, but still honoured: it must stay unset too.
        Assert.Null(options.CdnUrl);
#pragma warning restore CS0618
        Assert.Null(options.BundleUrl);
        Assert.Null(options.JavaScriptConfiguration);
        Assert.Null(options.HeadContent);
        Assert.Null(options.HeaderContent);
        Assert.True(options.PluginUrls is null || !options.PluginUrls.Any());
        Assert.True(options.Servers is null || !options.Servers.Any());
        Assert.True(options.DynamicNonce);
        Assert.True(options.DynamicBaseServerUrl);
        Assert.Null(options.Nonce);
        Assert.Equal(ScalarLockdown.PoweredByLinkCss, options.CustomCss);
    }

    [Fact]
    public void TheDocumentRoute_IsRelative_SoItResolvesUnderAProxyPrefix()
    {
        var options = new ScalarOptions();

        ScalarLockdown.Apply(options);

        Assert.Equal("openapi/{documentName}.json", options.OpenApiRoutePattern);
    }

    [Fact]
    public void ScalarIsConfigured_InExactlyOnePlace_FromNoConfigurationInput()
    {
        var sources = SourceTree.Files("Odyssey.Api", "*.cs")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(SourceTree.Root, path), File.ReadAllText);

        // One mapping, and it is handed the lockdown.
        var mappings = sources.Where(source => source.Value.Contains("MapScalarApiReference(", StringComparison.Ordinal)).ToList();
        var mapping = Assert.Single(mappings);
        Assert.Contains("MapScalarApiReference(ApiDocsHeaders.ScalarPathPrefix, ScalarLockdown.Apply)", mapping.Value, StringComparison.Ordinal);

        // Only the lockdown touches ScalarOptions members (the headers read one public constant).
        var configurers = sources
            .Where(source => source.Value.Contains("ScalarOptions options", StringComparison.Ordinal)
                || source.Value.Contains("options.Disable", StringComparison.Ordinal)
                || source.Value.Contains(".WithNonce(", StringComparison.Ordinal)
                || source.Value.Contains(".WithProxy", StringComparison.Ordinal)
                || source.Value.Contains(".WithCdnUrl(", StringComparison.Ordinal)
                || source.Value.Contains(".WithBundleUrl(", StringComparison.Ordinal))
            .Select(source => source.Key)
            .ToList();
        var lockdown = Assert.Single(configurers);
        Assert.EndsWith("ScalarLockdown.cs", lockdown, StringComparison.Ordinal);

        foreach (var forbidden in (string[])["IConfiguration", "IOptions", "IHostEnvironment", "Environment"])
        {
            Assert.DoesNotContain(forbidden, sources[lockdown], StringComparison.Ordinal);
        }
    }
}
