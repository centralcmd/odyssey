using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Api.Map;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The self-hosted basemap (issue #384 §8.3, AC 1–6a): the start-up checks on the PMTiles file a
/// self-hosted style draws from, and the access probe nginx's <c>auth_request</c> calls. Files are
/// synthesized headers in a temp directory; no tile is ever needed, because the API never reads one.
/// </summary>
public sealed class MapBasemapValidationTests : IDisposable
{
    private const string SelfHostedLight = "/maps/self-hosted-light.json";
    private const string SelfHostedDark = "/maps/self-hosted-dark.json";
    private const string GoodAttribution = "© OpenMapTiles © OpenStreetMap contributors";

    private readonly string directory = Directory.CreateTempSubdirectory("odyssey-basemap-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    // ── AC 1, 2, 4 ────────────────────────────────────────────────────────

    [Fact]
    public void No_self_hosted_style_means_no_file_check()
    {
        var options = Options(light: "https://tiles.openfreemap.org/styles/liberty", dark: "/maps/odyssey-dark.json", path: "");
        options.AllowedOrigins = "https://tiles.openfreemap.org";

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void A_valid_archive_passes_and_its_bounds_are_logged()
    {
        var logger = new CapturingLogger<MapOptionsValidator>();

        var result = new MapOptionsValidator(logger).Validate(Microsoft.Extensions.Options.Options.DefaultName, Options(path: Write(Archive())));

        Assert.True(result.Succeeded, result.FailureMessage);
        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("10.1,61.5,10.2,61.6", info.Message.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("zoom 0-14", info.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void The_committed_fixture_a_real_archive_passes()
    {
        var fixture = Path.Combine(RepositoryRoot.Path, "TestShared", "Basemap", "basemap.pmtiles");

        var result = Validate(Options(path: fixture));

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void One_self_hosted_theme_and_one_remote_theme_start()
    {
        var options = Options(light: "https://tiles.openfreemap.org/styles/liberty", path: Write(Archive()));
        options.AllowedOrigins = "https://tiles.openfreemap.org";
        Assert.True(Validate(options).Succeeded);

        // The remote theme still needs its origin listed.
        options.AllowedOrigins = string.Empty;
        var refused = Validate(options);
        Assert.False(refused.Succeeded);
        Assert.Contains("Map:StyleUrlLight", refused.FailureMessage, StringComparison.Ordinal);
    }

    // ── AC 3, 3a: every rule names the key and the rule, never the path ────────

    public static TheoryData<string, string> Failures => new()
    {
        { "empty", "must name the basemap file" },
        { "relative", "must be an absolute path" },
        { "missing", "does not exist" },
        { "directory", "must be a regular file" },
        { "symlink", "must be a regular file" },
        { "short", "is truncated" },
        { "magic", "is not a PMTiles version 3 archive" },
        { "version2", "is not a PMTiles version 3 archive" },
        { "cut-in-tile-data", "is truncated" },
        { "metadata-beyond-eof", "is truncated" },
        { "leaves-beyond-eof", "is truncated" },
        { "overflow", "is truncated" },
        { "raster", "must hold vector (MVT) tiles" },
        { "brotli-tiles", "cannot decode" },
        { "zstd-internal", "cannot decode" },
        { "bounds", "invalid bounding box or zoom range" },
        { "zoom", "invalid bounding box or zoom range" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_bad_basemap_file_fails_naming_the_key_and_the_rule(string shape, string rule)
    {
        var path = Shape(shape);

        var result = Validate(Options(path: path));

        Assert.False(result.Succeeded, shape);
        Assert.Contains("Map:BasemapFilePath", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(rule, result.FailureMessage, StringComparison.Ordinal);
        if (path.Length > 0)
            Assert.DoesNotContain(path, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_file_fails_as_unreadable()
    {
        // Self-skipping (this project references no skip package): root reads a 0000 file regardless.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        var path = Write(Archive());
        File.SetUnixFileMode(path, UnixFileMode.None);

        var result = Validate(Options(path: path));

        Assert.Contains("cannot be read by the API", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/basemap/style.json")]
    [InlineData("/basemap")]
    public void A_style_under_basemap_is_refused(string url)
    {
        var result = Validate(Options(light: url, path: Write(Archive())));

        Assert.False(result.Succeeded);
        Assert.Contains("Map:StyleUrlLight must name a style shipped under /maps/", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_stops_the_host()
    {
        using var factory = new OdysseyApiFactory([PermissionClaims.LocationsRead], configuration: Config(Shape("missing")));

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains("Map:BasemapFilePath", exception.ToString(), StringComparison.Ordinal);
    }

    // ── AC 5: W1 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("© OpenStreetMap contributors")]
    [InlineData("OpenFreeMap © OpenMapTiles Data from OpenStreetMap")]
    public void An_attribution_missing_a_required_credit_warns_once_and_passes(string attribution)
    {
        var logger = new CapturingLogger<MapOptionsValidator>();
        var options = Options(path: Write(Archive()));
        options.Attribution = attribution;

        var result = new MapOptionsValidator(logger).Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        Assert.True(result.Succeeded);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Map:Attribution", warning.Message, StringComparison.Ordinal);
    }

    // ── AC 6, 6a: the endpoints ───────────────────────────────────────────

    [Fact]
    public async Task Map_styles_returns_the_self_hosted_urls_with_no_new_field()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.LocationsRead], configuration: Config(Write(Archive())));
        using var client = factory.CreateClient();

        var styles = (await client.GetFromJsonAsync<MapStyles>("/api/map-styles"))!;

        Assert.Equal(SelfHostedLight, styles.LightStyleUrl);
        Assert.Equal(SelfHostedDark, styles.DarkStyleUrl);
    }

    [Fact]
    public async Task The_probe_answers_204_with_no_store_and_no_body()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.LocationsRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/map-styles/basemap-access");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_probe_answers_the_same_whichever_basemap_is_configured()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.LocationsRead], configuration: Config(Write(Archive())));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/map-styles/basemap-access")).StatusCode);
    }

    [Fact]
    public async Task The_probe_is_403_without_locations_read()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.ContactsRead]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/map-styles/basemap-access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/map-styles")).StatusCode);
    }

    [Fact]
    public async Task The_probe_is_401_unauthenticated()
    {
        await using var factory = new OdysseyApiFactory(permissions: null);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/map-styles/basemap-access")).StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ValidateOptionsResult Validate(MapOptions options) =>
        new MapOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

    private static MapOptions Options(string light = SelfHostedLight, string dark = SelfHostedDark, string path = "") => new()
    {
        StyleUrlLight = light,
        StyleUrlDark = dark,
        Attribution = GoodAttribution,
        AttributionUrl = "https://www.openstreetmap.org/copyright",
        AllowedOrigins = string.Empty,
        BasemapFilePath = path,
    };

    private static Dictionary<string, string?> Config(string path) => new()
    {
        ["Map:StyleUrlLight"] = SelfHostedLight,
        ["Map:StyleUrlDark"] = SelfHostedDark,
        ["Map:AllowedOrigins"] = "",
        ["Map:Attribution"] = GoodAttribution,
        ["Map:BasemapFilePath"] = path,
    };

    private string Write(byte[] bytes, string name = "basemap.pmtiles")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Shape(string shape)
    {
        switch (shape)
        {
            case "empty": return string.Empty;
            case "relative": return "basemap/basemap.pmtiles";
            case "missing": return Path.Combine(directory, "absent.pmtiles");
            case "directory": return Directory.CreateDirectory(Path.Combine(directory, "dir.pmtiles")).FullName;
            case "symlink":
                var target = Write(Archive(), "real.pmtiles");
                var link = Path.Combine(directory, "basemap.pmtiles");
                File.CreateSymbolicLink(link, target);
                return link;
            case "short": return Write(Archive()[..100]);
            case "magic": return Write(Archive(a => a[0] = (byte)'X'));
            case "version2": return Write(Archive(a => a[7] = 2));
            case "cut-in-tile-data": return Write(Archive()[..400]);
            case "metadata-beyond-eof": return Write(Archive(a => U64(a, 32, 10_000)));
            case "leaves-beyond-eof": return Write(Archive(a => { U64(a, 40, 300); U64(a, 48, 10_000); }));
            case "overflow": return Write(Archive(a => { U64(a, 56, ulong.MaxValue - 1); U64(a, 64, 10); }));
            case "raster": return Write(Archive(a => a[99] = 2));
            case "brotli-tiles": return Write(Archive(a => a[98] = 3));
            case "zstd-internal": return Write(Archive(a => a[97] = 4));
            case "bounds": return Write(Archive(a => BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(114), 890_000_000)));
            case "zoom": return Write(Archive(a => { a[100] = 15; a[101] = 14; }));
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    /// <summary>
    /// A 1 KB stand-in for a PMTiles v3 vector archive: a valid header whose four sections lie inside the
    /// file, tile data last (as Planetiler writes it), bounds around Hamar, zoom 0–14, gzip throughout.
    /// </summary>
    private static byte[] Archive(Action<byte[]>? mutate = null)
    {
        var a = new byte[1024];
        "PMTiles"u8.CopyTo(a);
        a[7] = 3;
        U64(a, 8, 127); U64(a, 16, 100);     // root directory
        U64(a, 24, 227); U64(a, 32, 50);     // metadata
        U64(a, 40, 277); U64(a, 48, 0);      // leaf directories
        U64(a, 56, 277); U64(a, 64, 747);    // tile data, to the end of the file
        a[97] = 2; a[98] = 2; a[99] = 1;     // gzip, gzip, MVT
        a[100] = 0; a[101] = 14;
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(102), 101_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(106), 615_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(110), 102_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(114), 616_000_000);
        mutate?.Invoke(a);
        return a;
    }

    private static void U64(byte[] a, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(offset), value);
}
