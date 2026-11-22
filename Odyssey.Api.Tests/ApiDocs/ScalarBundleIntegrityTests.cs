using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Scalar.AspNetCore;
using Xunit;

namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// The CI guard for the promise that nothing leaves the deployment through the docs page (issue #569 §7.2). Any
/// <c>Scalar.AspNetCore</c> bump fails here until someone re-inspects the new version and updates the pins, the same
/// way <c>MapLibreBundleIntegrityTests</c> guards the vendored map library.
/// </summary>
/// <remarks>
/// Updating a pin is the re-inspection, not a formality: extract the new bundle, review every new origin and every
/// new assembly reference, disable or CSP-block anything outbound, and record the result in the pull request.
/// </remarks>
public partial class ScalarBundleIntegrityTests
{
    private const string BundleResource = "ScalarStaticAssets.scalar.js";

    /// <summary>SHA-256 of the decompressed browser bundle in Scalar.AspNetCore 3.1.1.</summary>
    private const string BundleSha256 = "68fb59b2835e3e9aaec45e7dc863ddb55ae78656365486eb96d73b98096ed7d8";

    /// <summary>
    /// Every host the 3.1.1 bundle names, and why naming it is harmless. "CSP" means the docs page's
    /// <c>connect-src</c>/<c>img-src 'self'</c> refuses any request to it and <c>Referrer-Policy: no-referrer</c>
    /// strips a navigation; "inert" means documentation text, a link in Scalar's own help, or example data.
    /// </summary>
    private static readonly Dictionary<string, string> KnownHosts = new(StringComparer.Ordinal)
    {
        ["fonts.scalar.com"] = "default fonts: off (DisableDefaultFonts)",
        ["api.scalar.com"] = "Agent Scalar chat and share upload: agent off (DisableAgent); upload CSP",
        ["proxy.scalar.com"] = "request proxy: no ProxyUrl set; CSP",
        ["client.scalar.com"] = "\"open in API client\": HideClientButton",
        ["registry.scalar.com"] = "registry/share links: CSP",
        ["dashboard.scalar.com"] = "hosted dashboard links: CSP",
        ["editor.scalar.com"] = "hosted editor link: CSP",
        ["galaxy.scalar.com"] = "OAuth example placeholders: inert; CSP",
        ["docs.scalar.com"] = "Scalar help link: inert",
        ["scalar.com"] = "Scalar marketing/help links: inert",
        ["www.scalar.com"] = "Scalar marketing link: inert",
        ["scalar.cal.com"] = "Scalar sales booking link: inert",
        ["app.cal.com"] = "Scalar sales booking link: inert",
        ["app.loops.so"] = "Scalar newsletter form: CSP",
        ["discord.gg"] = "Scalar community link: inert",
        ["x.com"] = "Scalar social link: inert",
        ["github.com"] = "Scalar repository links: inert",
        ["avatars.githubusercontent.com"] = "example avatar images: CSP (img-src 'self')",
        ["picsum.photos"] = "example images: CSP (img-src 'self')",
        ["loremflickr.com"] = "example images: CSP (img-src 'self')",
        ["apidocumentation.com"] = "Scalar product link: inert",
        ["example.com"] = "example data: inert",
        ["api.example.com"] = "example data: inert",
        ["api-hub.com"] = "example data: inert",
        ["byte-craft.io"] = "example data: inert",
        ["cloud-nest.org"] = "example data: inert",
        ["code-base.dev"] = "example data: inert",
        ["data-flow.co"] = "example data: inert",
        ["dev-portal.net"] = "example data: inert",
        ["my-app.io"] = "example data: inert",
        ["open-source.dev"] = "example data: inert",
        ["tech-wave.net"] = "example data: inert",
        ["test-site.org"] = "example data: inert",
        ["web-pulse.io"] = "example data: inert",
        ["www.w3.org"] = "XML/SVG namespace URIs: inert",
        ["json-schema.org"] = "JSON Schema dialect URIs: inert",
        ["developer.mozilla.org"] = "HTTP reference links: inert",
        ["html.spec.whatwg.org"] = "spec reference link: inert",
        ["tailwindcss.com"] = "license header: inert",
        ["vuejs.org"] = "error-reference link: inert",
        ["unhead.unjs.io"] = "error-reference link: inert",
        ["cloud.google.com"] = "OAuth documentation link: inert",
        ["developers.cloudflare.com"] = "documentation link: inert",
        ["www.ibm.com"] = "documentation link: inert",
    };

    /// <summary>The assemblies Scalar.AspNetCore 3.1.1 references: no networking, no process, no reflection emit.</summary>
    private static readonly string[] KnownAssemblyReferences =
    [
        "Microsoft.AspNetCore.Authorization.Policy",
        "Microsoft.AspNetCore.Http.Abstractions",
        "Microsoft.AspNetCore.Http.Extensions",
        "Microsoft.AspNetCore.Http.Features",
        "Microsoft.AspNetCore.Http.Results",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Features",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Primitives",
        "Microsoft.Net.Http.Headers",
        "System.Collections",
        "System.Collections.Concurrent",
        "System.ComponentModel",
        "System.ComponentModel.Primitives",
        "System.IO.Compression",
        "System.Linq",
        "System.Memory",
        "System.Runtime",
        "System.Runtime.InteropServices",
        "System.Security.Cryptography",
        "System.Text.Encodings.Web",
        "System.Text.Json",
    ];

    [Fact]
    public void TheBundle_IsThePinnedOne()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ReadBundle())));

        Assert.True(hash == BundleSha256,
            $"Scalar's bundle changed (sha256 {hash}). Re-inspect it per issue #569 §7.2 before updating the pin.");
    }

    [Fact]
    public void EveryHostTheBundleNames_IsReviewed()
    {
        var bundle = ReadBundle();
        var hosts = AbsoluteUrlHost().Matches(bundle).Select(match => match.Groups["host"].Value)
            .Concat(ProtocolRelativeHost().Matches(bundle).Select(match => match.Groups["host"].Value))
            .Concat(BareScalarHost().Matches(bundle).Select(match => match.Value))
            .Select(host => host.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var unreviewed = hosts.Where(host => !KnownHosts.ContainsKey(host)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unreviewed.Count == 0, "Unreviewed host(s) in Scalar's bundle: " + string.Join(", ", unreviewed));

        // A stale entry would make the list look more reviewed than the bundle is.
        var stale = KnownHosts.Keys.Where(host => !hosts.Contains(host)).ToList();
        Assert.True(stale.Count == 0, "Reviewed host(s) no longer in the bundle: " + string.Join(", ", stale));
    }

    [Fact]
    public void TheManagedCode_ReferencesOnlyTheReviewedAssemblies()
    {
        using var stream = File.OpenRead(typeof(ScalarOptions).Assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(KnownAssemblyReferences.Order(StringComparer.Ordinal), references);
    }

    private static string ReadBundle()
    {
        using var resource = typeof(ScalarOptions).Assembly.GetManifestResourceStream(BundleResource)
            ?? throw new InvalidOperationException($"{BundleResource} is not embedded in Scalar.AspNetCore.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"https?://(?<host>[A-Za-z0-9.-]+\.[A-Za-z]{2,})")]
    private static partial Regex AbsoluteUrlHost();

    [GeneratedRegex(@"(?<![:A-Za-z0-9])//(?<host>[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})(?=[/:""'`])")]
    private static partial Regex ProtocolRelativeHost();

    [GeneratedRegex(@"\b(?:[a-z0-9-]+\.)*scalar\.com\b", RegexOptions.IgnoreCase)]
    private static partial Regex BareScalarHost();
}
