using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The self-hosted basemap's deployment surface (issue #384 AC 13, 14, 19b): the nginx locations every review
/// round tightened, the read-only volume both containers mount, and Caddy leaving the byte ranges alone.
/// <c>BasemapNginxTests</c> (<c>Odyssey.IntegrationTests</c>) exercises the same template in the real image;
/// these pin the lines so a change to one is a reviewed change to both.
/// </summary>
public class SelfHostedBasemapDeploymentTests
{
    private static readonly Lazy<string> Template = new(() => Regex.Replace(File.ReadAllText(MapCspTemplateTests.TemplatePath), @"(?m)#.*$", string.Empty));

    private static string Location(string header) => Regex.Replace(RawLocation(header), @"[ \t]+", " ");

    private static string RawLocation(string header)
    {
        var start = Template.Value.IndexOf(header + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No `{header}` block in the template.");
        var depth = 0;
        for (var i = Template.Value.IndexOf('{', start); i < Template.Value.Length; i++)
        {
            if (Template.Value[i] == '{') depth++;
            else if (Template.Value[i] == '}' && --depth == 0) return Template.Value[start..(i + 1)];
        }

        throw new InvalidOperationException($"Unterminated `{header}` block.");
    }

    [Theory]
    [InlineData("access_log off;")]
    [InlineData("disable_symlinks on;")]
    [InlineData("max_ranges 1;")]
    [InlineData("if ($request_method != GET) {")]
    [InlineData("return 405;")]
    [InlineData("if ($http_range !~ \"^bytes=[0-9]+-[0-9]+$\") {")]
    [InlineData("return 416;")]
    [InlineData("limit_req zone=basemap burst=120 nodelay;")]
    [InlineData("limit_conn basemap_conn 16;")]
    [InlineData("limit_rate_after 512k;")]
    [InlineData("limit_rate 1m;")]
    [InlineData("auth_request /_basemap_access;")]
    [InlineData("default_type application/octet-stream;")]
    [InlineData("expires epoch;")]
    public void The_file_location_carries_every_control(string line)
    {
        Assert.Contains(line, Location("location = /basemap/basemap.pmtiles"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_range_check_comes_before_the_access_check()
    {
        var block = Location("location = /basemap/basemap.pmtiles");

        Assert.True(block.IndexOf("return 416;", StringComparison.Ordinal) < block.IndexOf("auth_request", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("internal;")]
    [InlineData("access_log off;")]
    [InlineData("proxy_pass http://api:8080/api/map-styles/basemap-access;")]
    [InlineData("proxy_set_header Host $host;")]
    [InlineData("proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;")]
    [InlineData("proxy_set_header X-Forwarded-Proto $forwarded_proto;")]
    [InlineData("proxy_connect_timeout 5s;")]
    [InlineData("proxy_cache basemap_auth;")]
    [InlineData("proxy_cache_key $http_cookie;")]
    [InlineData("proxy_no_cache $basemap_no_session;")]
    [InlineData("proxy_cache_bypass $basemap_no_session;")]
    [InlineData("proxy_cache_valid 204 60s;")]
    [InlineData("proxy_cache_lock on;")]
    [InlineData("proxy_ignore_headers Cache-Control Expires Set-Cookie;")]
    [InlineData("proxy_hide_header Set-Cookie;")]
    public void The_access_check_location_carries_every_directive(string line)
    {
        Assert.Contains(line, Location("location = /_basemap_access"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_access_check_caches_only_204_and_keys_on_no_single_cookie()
    {
        var block = Location("location = /_basemap_access");

        Assert.Single(Regex.Matches(block, @"proxy_cache_valid"));
        Assert.Single(Regex.Matches(block, @"proxy_cache_key"));
        Assert.DoesNotMatch(@"proxy_cache_key\s+\$cookie_", block);
    }

    [Fact]
    public void The_zones_key_on_the_real_client_and_the_cache_lives_under_var_cache_nginx()
    {
        Assert.Matches(@"limit_req_zone\s+\$basemap_client\s+zone=basemap:10m\s+rate=30r/s;", Template.Value);
        Assert.Matches(@"limit_conn_zone\s+\$basemap_client\s+zone=basemap_conn:10m;", Template.Value);
        Assert.Matches(@"proxy_cache_path /var/cache/nginx/basemap_auth \S.*keys_zone=basemap_auth:1m", Template.Value);
        Assert.Matches(@"map \$http_x_forwarded_for \$basemap_client \{", Template.Value);
        Assert.Matches(@"map \$http_cookie \$basemap_no_session \{", Template.Value);
    }

    [Fact]
    public void Everything_else_under_basemap_is_a_404_and_the_assets_never_fall_back_to_the_spa()
    {
        Assert.Contains("return 404;", Location("location ^~ /basemap"), StringComparison.Ordinal);
        Assert.Contains("try_files $uri /maps/self-hosted/fonts/empty.pbf;", Location("location ^~ /maps/self-hosted/fonts/"), StringComparison.Ordinal);
        Assert.Contains("try_files $uri =404;", Location("location ^~ /maps/self-hosted/"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("location = /basemap/basemap.pmtiles")]
    [InlineData("location ^~ /basemap")]
    [InlineData("location = /_basemap_access")]
    [InlineData("location ^~ /maps/self-hosted/fonts/")]
    [InlineData("location ^~ /maps/self-hosted/")]
    public void No_basemap_location_declares_an_add_header(string header)
    {
        // One add_header would silently drop every server-level header, the CSP and nosniff included.
        Assert.DoesNotContain("add_header", Location(header), StringComparison.Ordinal);
    }

    [Fact]
    public void The_render_substitutes_nothing_new()
    {
        Assert.Equal(["MAP_ALLOWED_ORIGINS"], Regex.Matches(Template.Value, @"\$\{(?<v>[A-Z_]+)\}").Select(m => m.Groups["v"].Value).Distinct());
    }

    [Fact]
    public void The_start_check_ships_beside_the_origin_check()
    {
        var script = File.ReadAllText(Path.Combine(ClientSource.Root, "nginx", "docker-entrypoint.d", "16-check-basemap-readable.sh"));

        Assert.Contains("CACHE_PARENT=/var/cache/nginx", script, StringComparison.Ordinal);
        Assert.Contains("FILE=/usr/share/nginx/html/basemap/basemap.pmtiles", script, StringComparison.Ordinal);
        Assert.Contains("basemap-unreadable", script, StringComparison.Ordinal);
        Assert.Contains("basemap-dir-unsearchable", script, StringComparison.Ordinal);
    }

    // ── AC 14: one read-only volume, mounted by api and client and nothing else ─────────────────────────

    [Fact]
    public void Api_and_client_mount_the_same_volume_read_only_and_nothing_else_does()
    {
        var compose = Read("docker-compose.yml");

        Assert.Single(Regex.Matches(compose, @"(?m)^\s+- basemap:/var/odyssey/basemap:ro\s*$"));
        Assert.Single(Regex.Matches(compose, @"(?m)^\s+- basemap:/usr/share/nginx/html/basemap:ro\s*$"));
        Assert.Equal(2, Regex.Matches(compose, @"(?m)^\s+- basemap:").Count);
        Assert.Matches(@"(?m)^  basemap:\s*$", compose);
        Assert.Contains("Map__BasemapFilePath: /var/odyssey/basemap/basemap.pmtiles", compose, StringComparison.Ordinal);
        // The access-check cache holds complete cookie headers: in memory in the dev stack too.
        Assert.Contains("- /var/cache/nginx:uid=101,gid=101", Regex.Replace(compose, @"(?m)#.*$", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void The_production_overlay_adds_no_basemap_mount_and_keeps_the_cache_in_memory()
    {
        var prod = Regex.Replace(Read("docker-compose.prod.yml"), @"(?m)#.*$", string.Empty);

        Assert.DoesNotContain("basemap", prod, StringComparison.Ordinal);
        Assert.Contains("- /var/cache/nginx:uid=101,gid=101", prod, StringComparison.Ordinal);
    }

    // ── AC 19b ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Caddy_never_encodes_the_basemap()
    {
        var caddy = Regex.Replace(Read("Caddyfile"), @"(?m)#.*$", string.Empty);

        Assert.Contains("@compressible not path /basemap/*", caddy, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\s*encode @compressible zstd gzip\s*$", caddy);
        Assert.DoesNotMatch(@"(?m)^\s*encode (zstd|gzip)", caddy);
    }

    private static string Read(string file) => File.ReadAllText(ClientSource.Sibling(file));
}
