using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Odyssey.Api.Map;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// <c>GET /api/map-styles</c> and the start-up validation of the <c>Map</c> section (issue #370 AC 1, 2,
/// 4–7). A failing configuration must stop the host naming the key and the rule — never the value, which
/// could be a mistakenly pasted keyed URL.
/// </summary>
public class MapStylesApiTests
{
    private const string Path = "/api/map-styles";

    private static readonly string[] Reader = [PermissionClaims.LocationsRead];

    // ── The endpoint (AC 1, 2, 4) ──────────────────────────────────────────

    [Fact]
    public async Task Reader_gets_the_shipped_defaults_with_a_private_five_minute_cache()
    {
        await using var factory = new OdysseyApiFactory(Reader);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromSeconds(300), response.Headers.CacheControl.MaxAge);

        var styles = (await response.Content.ReadFromJsonAsync<MapStyles>())!;
        Assert.Equal("https://tiles.openfreemap.org/styles/liberty", styles.LightStyleUrl);
        Assert.Equal("/maps/odyssey-dark.json", styles.DarkStyleUrl);
        Assert.Equal("OpenFreeMap © OpenMapTiles Data from OpenStreetMap", styles.Attribution);
        Assert.Equal("https://www.openstreetmap.org/copyright", styles.AttributionUrl);
    }

    [Fact]
    public async Task The_allowed_origins_are_never_returned()
    {
        await using var factory = new OdysseyApiFactory(Reader);
        using var client = factory.CreateClient();

        using var body = JsonDocument.Parse(await client.GetStringAsync(Path));

        Assert.Equal(
            ["attribution", "attributionUrl", "darkStyleUrl", "lightStyleUrl"],
            body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_override_changes_the_returned_url()
    {
        await using var factory = Configured(new()
        {
            ["Map:StyleUrlLight"] = "https://maps.example.org/styles/light.json",
            ["Map:StyleUrlDark"] = "/maps/blank-dark.json",
            ["Map:AllowedOrigins"] = "https://maps.example.org",
        }, Reader);
        using var client = factory.CreateClient();

        var styles = (await client.GetFromJsonAsync<MapStyles>(Path))!;

        Assert.Equal("https://maps.example.org/styles/light.json", styles.LightStyleUrl);
        Assert.Equal("/maps/blank-dark.json", styles.DarkStyleUrl);
    }

    [Fact]
    public async Task The_off_switch_starts_and_serves_the_blank_styles()
    {
        await using var factory = Configured(new()
        {
            ["Map:StyleUrlLight"] = "/maps/blank-light.json",
            ["Map:StyleUrlDark"] = "/maps/blank-dark.json",
            ["Map:AllowedOrigins"] = "",
        }, Reader);
        using var client = factory.CreateClient();

        var styles = (await client.GetFromJsonAsync<MapStyles>(Path))!;

        Assert.Equal("/maps/blank-light.json", styles.LightStyleUrl);
        Assert.Equal("/maps/blank-dark.json", styles.DarkStyleUrl);
    }

    [Fact]
    public async Task Unauthenticated_is_401_without_a_cache_header()
    {
        await using var factory = new OdysseyApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task Without_locations_read_is_403_without_a_cache_header()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.AccountsRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.CacheControl?.MaxAge);
    }

    // ── Start-up validation (AC 5, 6) ──────────────────────────────────────

    public static TheoryData<string, string> RejectedStyleUrls => new()
    {
        { "http", "http://tiles.openfreemap.org/styles/positron" },
        { "query", "https://tiles.openfreemap.org/styles/positron?key=SECRETKEY123" },
        { "bare query", "https://tiles.openfreemap.org/styles/positron?" },
        { "userinfo", "https://SECRETKEY123@tiles.openfreemap.org/styles/positron" },
        { "fragment", "https://tiles.openfreemap.org/styles/positron#SECRETKEY123" },
        { "protocol-relative", "//evil.example/s.json" },
        { "backslash", "/\\evil.example/s.json" },
        { "leading dot-dot", "/../s.json" },
        { "inner dot-dot", "/maps/../s.json" },
        { "unlisted origin", "https://evil.example/s.json" },
        { "unlisted port", "https://tiles.openfreemap.org:8443/styles/positron" },
        { "placeholder", "https://tiles.openfreemap.org/styles/{{ OPENFREEMAP_LIGHT_STYLE }}" },
        { "newline", "https://tiles.openfreemap.org/styles/positron\n" },
        { "tab", "https://tiles.openfreemap.org/styles/\tpositron" },
        { "relative without slash", "maps/blank-light.json" },
        { "too long", "https://tiles.openfreemap.org/" + new string('a', 2048) },
    };

    [Theory]
    [MemberData(nameof(RejectedStyleUrls))]
    public void A_bad_style_url_stops_the_host_naming_the_key_never_the_value(string because, string value)
    {
        var message = StartupFailure(new() { ["Map:StyleUrlLight"] = value });

        Assert.Contains("StyleUrlLight", message, StringComparison.Ordinal);
        AssertNoEcho(value, message, because);
    }

    public static TheoryData<string, string, string> RejectedOtherValues => new()
    {
        { "blank attribution", "Map:Attribution", "   " },
        { "markup attribution", "Map:Attribution", "<img src=x onerror=alert(1)>" },
        { "ampersand attribution", "Map:Attribution", "Tiles & data SECRETKEY123" },
        { "placeholder attribution", "Map:Attribution", "{{ ATTRIBUTION }}" },
        { "http attribution url", "Map:AttributionUrl", "http://www.openstreetmap.org/copyright" },
        { "userinfo attribution url", "Map:AttributionUrl", "https://SECRETKEY123@www.openstreetmap.org/" },
        { "fragment attribution url", "Map:AttributionUrl", "https://www.openstreetmap.org/copyright#SECRETKEY123" },
        { "uppercase origin", "Map:AllowedOrigins", "https://TILES.openfreemap.org" },
        { "newline origin", "Map:AllowedOrigins", "https://tiles.openfreemap.org\n" },
        { "missing attribution", "Map:Attribution", "" },
        { "too-long attribution", "Map:Attribution", new string('a', 301) },
        { "missing attribution url", "Map:AttributionUrl", "" },
        { "too-long attribution url", "Map:AttributionUrl", "https://www.openstreetmap.org/" + new string('a', 2048) },
        { "too-long origins", "Map:AllowedOrigins", "https://" + new string('a', 2045) },
    };

    [Theory]
    [MemberData(nameof(RejectedOtherValues))]
    public void A_bad_value_stops_the_host_naming_the_key_never_the_value(string because, string key, string value)
    {
        var message = StartupFailure(new() { [key] = value });

        Assert.Contains(key.Split(':')[1], message, StringComparison.Ordinal);
        AssertNoEcho(value, message, because);
    }

    [Fact]
    public void The_bounds_are_inclusive_at_their_maximum()
    {
        var options = Defaults();
        options.Attribution = new string('a', 300);
        options.StyleUrlLight = "https://tiles.openfreemap.org/" + new string('a', 2048 - "https://tiles.openfreemap.org/".Length);

        var context = new System.ComponentModel.DataAnnotations.ValidationContext(options);
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        Assert.True(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, context, errors, validateAllProperties: true),
            string.Join("; ", errors.Select(e => e.ErrorMessage)));
        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void An_absolute_style_needs_at_least_one_allowed_origin()
    {
        var message = StartupFailure(new() { ["Map:AllowedOrigins"] = "" });

        // The shipped light style is absolute; the shipped dark one is same-origin but loads its tiles from
        // OpenFreeMap, so emptying the origins without repointing it must fail too.
        Assert.Contains("StyleUrlLight", message, StringComparison.Ordinal);
        Assert.Contains("StyleUrlDark", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_dark_style_needs_its_tile_origin_listed()
    {
        var options = Defaults();
        options.StyleUrlLight = "/maps/blank-light.json";
        options.StyleUrlDark = "/maps/odyssey-dark.json";
        options.AllowedOrigins = "";

        var refused = Validate(options);

        Assert.False(refused.Succeeded);
        Assert.Contains("Map:StyleUrlDark", refused.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("https://tiles.openfreemap.org", refused.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("StyleUrlLight", refused.FailureMessage, StringComparison.Ordinal);

        options.AllowedOrigins = "https://tiles.openfreemap.org";
        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void Every_shipped_style_declares_the_remote_origins_it_loads_from()
    {
        var directory = System.IO.Path.Combine(RepositoryRoot.Path, "Odyssey.Client", "wwwroot", "maps");
        var files = Directory.GetFiles(directory, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var url = "/maps/" + System.IO.Path.GetFileName(file);
            var found = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"""(?<u>https?://[^""]+)""")
                .Select(m => new Uri(m.Groups["u"].Value).GetLeftPart(UriPartial.Authority))
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
            var declared = MapOptions.ShippedStyleRemoteOrigins.TryGetValue(url, out var origins)
                ? origins.Select(o => o.GetLeftPart(UriPartial.Authority)).Order(StringComparer.Ordinal).ToList()
                : [];

            Assert.True(found.SequenceEqual(declared),
                $"{url} loads from [{string.Join(", ", found)}] but MapOptions.ShippedStyleRemoteOrigins declares [{string.Join(", ", declared)}].");
        }
    }

    [Fact]
    public void A_missing_style_url_stops_the_host()
    {
        var message = StartupFailure(new() { ["Map:StyleUrlDark"] = "" });

        Assert.Contains("StyleUrlDark", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_effective_port_is_compared_not_the_string()
    {
        var result = Validate(new MapOptions
        {
            StyleUrlLight = "https://tiles.openfreemap.org:443/styles/positron",
            StyleUrlDark = "https://tiles.openfreemap.org/styles/dark",
            Attribution = "OpenFreeMap © OpenMapTiles Data from OpenStreetMap",
            AttributionUrl = "https://www.openstreetmap.org/copyright?lang=en",
            AllowedOrigins = "https://tiles.openfreemap.org",
        });

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void The_host_is_compared_case_insensitively_against_a_lowercase_origin()
    {
        var result = Validate(new MapOptions
        {
            StyleUrlLight = "https://TILES.openfreemap.org/styles/positron",
            StyleUrlDark = "/maps/blank-dark.json",
            Attribution = "OpenFreeMap",
            AttributionUrl = "https://www.openstreetmap.org/copyright",
            AllowedOrigins = "https://tiles.openfreemap.org:443",
        });

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void An_uppercase_allowed_origin_is_rejected()
    {
        var options = Defaults();
        options.AllowedOrigins = "https://TILES.openfreemap.org";

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("AllowedOrigins", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_appsettings_section_validates()
    {
        using var document = JsonDocument.Parse(RepositoryRoot.ReadAllText("Odyssey.Api/appsettings.json"));
        var map = document.RootElement.GetProperty("Map").Deserialize<MapOptions>()!;

        var result = Validate(map);

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    // ── The shared vector table (AC 7, .NET half) ──────────────────────────

    [Fact]
    public void Every_shared_vector_gets_its_verdict_from_the_dotnet_rule()
    {
        var mismatches = MapOriginVectors.Load()
            .Where(v => (MapOriginRule.TryParseAllowedOrigins(v.Value, out _) is null) != v.Valid)
            .Select(v => $"{v.Note}: expected {(v.Valid ? "accept" : "reject")}")
            .ToList();

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    private static OdysseyApiFactory Configured(Dictionary<string, string?> configuration, string[]? permissions = null) =>
        new(permissions ?? Reader, configuration: configuration);

    private static string StartupFailure(Dictionary<string, string?> configuration)
    {
        using var factory = Configured(configuration);

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        var validation = Flatten(exception).OfType<OptionsValidationException>().FirstOrDefault();
        Assert.True(validation is not null, $"Expected an OptionsValidationException, got: {exception}");
        Assert.Equal(typeof(MapOptions), validation.OptionsType);
        return validation.Message;
    }

    private static void AssertNoEcho(string value, string message, string because)
    {
        Assert.False(
            message.Contains("SECRETKEY123", StringComparison.Ordinal)
            || (value.Trim().Length >= 4 && message.Contains(value.Trim(), StringComparison.Ordinal)),
            $"{because}: the failure message echoes the configured value: {message}");
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                yield return inner;
        }
        else if (exception.InnerException is not null)
        {
            foreach (var inner in Flatten(exception.InnerException))
                yield return inner;
        }
    }

    private static ValidateOptionsResult Validate(MapOptions options) =>
        new MapOptionsValidator().Validate(Options.DefaultName, options);

    private static MapOptions Defaults() => new()
    {
        StyleUrlLight = "https://tiles.openfreemap.org/styles/positron",
        StyleUrlDark = "https://tiles.openfreemap.org/styles/dark",
        Attribution = "OpenFreeMap © OpenMapTiles Data from OpenStreetMap",
        AttributionUrl = "https://www.openstreetmap.org/copyright",
        AllowedOrigins = "https://tiles.openfreemap.org",
    };
}

/// <summary>The rows of <c>TestShared/MapOriginVectors.json</c> (issue #370 AC 7).</summary>
internal sealed record MapOriginVector(string Value, bool Valid, string Note)
{
    public override string ToString() => Note;
}

internal static class MapOriginVectors
{
    public static IReadOnlyList<MapOriginVector> Load()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "MapOriginVectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("vectors").EnumerateArray()
            .Select(v => new MapOriginVector(
                v.GetProperty("value").GetString()!,
                v.GetProperty("valid").GetBoolean(),
                v.GetProperty("note").GetString()!))
            .ToList();
    }
}
