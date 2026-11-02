using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The rendered Content Security Policy (issue #370 AC 8, 10). No nginx runs here, so the test renders the
/// template with the image's semantics itself — only <c>${MAP_ALLOWED_ORIGINS}</c> is replaced, because
/// the Dockerfile's <c>NGINX_ENVSUBST_FILTER</c> admits that one variable — and asserts on the OUTPUT.
/// </summary>
public class MapCspTemplateTests
{
    internal const string Placeholder = "${MAP_ALLOWED_ORIGINS}";

    internal static readonly string TemplatePath =
        Path.Combine(ClientSource.Root, "nginx", "templates", "default.conf.template");

    private static readonly Lazy<string> Template = new(() => File.ReadAllText(TemplatePath));

    // The policy as shipped before issue #370, minus the three directives the map touches. Any change to
    // these is a CSP change in its own right and should be made here deliberately.
    private static readonly Dictionary<string, string> Unchanged = new(StringComparer.Ordinal)
    {
        ["default-src"] = "'self'",
        ["script-src"] = "'self' 'wasm-unsafe-eval' 'unsafe-inline'",
        ["style-src"] = "'self' 'unsafe-inline'",
        ["font-src"] = "'self' data:",
        ["object-src"] = "'none'",
        ["base-uri"] = "'self'",
        ["form-action"] = "'self'",
        ["frame-ancestors"] = "'none'",
    };

    internal static string Render(string origins) => Template.Value.Replace(Placeholder, origins, StringComparison.Ordinal);

    [Fact]
    public void The_placeholder_appears_only_in_the_csp_header()
    {
        var uncommented = HashComments(Template.Value);
        var csp = CspLine(Template.Value);

        Assert.Equal(2, Regex.Matches(csp, Regex.Escape(Placeholder)).Count);
        Assert.Equal(2, Regex.Matches(uncommented, Regex.Escape(Placeholder)).Count);
    }

    [Fact]
    public void The_configured_origins_reach_connect_src_and_img_src_and_nothing_else_moves()
    {
        var directives = Directives(Render("https://tiles.example https://glyphs.example"));

        Assert.Equal(["'self'", "https://tiles.example", "https://glyphs.example"], directives["connect-src"]);
        Assert.Equal(["'self'", "data:", "blob:", "https://tiles.example", "https://glyphs.example"], directives["img-src"]);
        Assert.Equal(["'self'"], directives["worker-src"]);
        foreach (var (name, value) in Unchanged)
        {
            Assert.Equal(value.Split(' '), directives[name]);
        }

        Assert.Equal(
            Unchanged.Keys.Concat(["img-src", "connect-src", "worker-src"]).Order(StringComparer.Ordinal),
            directives.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_empty_value_admits_only_self()
    {
        var directives = Directives(Render(string.Empty));

        Assert.Equal(["'self'"], directives["connect-src"]);
        Assert.Equal(["'self'", "data:", "blob:"], directives["img-src"]);
        Assert.Equal(["'self'"], directives["worker-src"]);
    }

    [Fact]
    public void Every_nginx_variable_survives_the_render_byte_for_byte()
    {
        var rendered = Render("https://tiles.example");

        Assert.Equal(Template.Value, rendered.Replace("https://tiles.example", Placeholder, StringComparison.Ordinal));
        var variables = Regex.Matches(HashComments(rendered), @"\$[A-Za-z_]+").Select(m => m.Value).ToHashSet();
        Assert.Superset(new HashSet<string> { "$host", "$scheme", "$forwarded_proto", "$http_x_forwarded_proto", "$uri" }, variables);
    }

    [Fact]
    public void No_location_block_declares_an_add_header_so_every_location_inherits_the_csp()
    {
        var text = Regex.Replace(Render("https://tiles.example"), @"(?m)#.*$", string.Empty);

        var locations = Regex.Matches(text, @"location[^{]*\{(?<body>[^{}]*)\}");
        Assert.True(locations.Count >= 3, "Expected the /api/, /api/api/locations and / location blocks.");
        foreach (Match location in locations)
        {
            Assert.DoesNotMatch(@"(?m)^\s*add_header\b", location.Groups["body"].Value);
        }

        Assert.Single(Regex.Matches(text, @"(?m)^\s*add_header\s+Content-Security-Policy\b"));
    }

    // nginx and YAML comments run from '#' to the end of the line.
    private static string HashComments(string text) => Regex.Replace(text, @"(?m)#.*$", string.Empty);

    private static string CspLine(string config) =>
        Regex.Match(config, @"(?m)^\s*add_header\s+Content-Security-Policy\s+""(?<policy>[^""]*)""").Groups["policy"].Value;

    private static Dictionary<string, string[]> Directives(string config)
    {
        var policy = CspLine(config);
        Assert.False(string.IsNullOrEmpty(policy), "No Content-Security-Policy header in the rendered config.");

        return policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(tokens => tokens[0], tokens => tokens[1..], StringComparer.Ordinal);
    }
}
