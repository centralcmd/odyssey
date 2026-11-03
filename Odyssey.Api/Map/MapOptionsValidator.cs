using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Odyssey.Api.Map;

/// <summary>
/// The derived rules on <see cref="MapOptions"/> (issue #370 §8). A failure stops the API at start-up,
/// naming the key and the rule — never the value — so a misconfigured basemap is never a runtime
/// <c>500</c> nor a style the CSP silently blocks.
/// </summary>
public sealed class MapOptionsValidator : IValidateOptions<MapOptions>
{
    private readonly ILogger<MapOptionsValidator> logger;

    public MapOptionsValidator()
        : this(NullLogger<MapOptionsValidator>.Instance)
    {
    }

    public MapOptionsValidator(ILogger<MapOptionsValidator> logger)
    {
        this.logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, MapOptions options)
    {
        var failures = new List<string>();

        void Check(string property, string? value, Func<string, string?> rule)
        {
            // Null and empty are the data annotations' to report ([Required]), so they are not repeated here.
            if (string.IsNullOrEmpty(value))
                return;

            var failure = Generic(value) ?? rule(value);
            if (failure is not null)
                failures.Add($"{MapOptions.SectionName}:{property} {failure}.");
        }

        var origins = (IReadOnlyList<Uri>)[];
        var allowedOrigins = options.AllowedOrigins ?? string.Empty;
        var originFailure = Generic(allowedOrigins) ?? MapOriginRule.TryParseAllowedOrigins(allowedOrigins, out origins);
        if (originFailure is not null)
            failures.Add($"{MapOptions.SectionName}:{nameof(MapOptions.AllowedOrigins)} {originFailure}.");

        // A shipped same-origin style whose remote resources the CSP would not admit (the off-switch with
        // the shipped dark default left in place) fails here rather than as a silently blank map.
        void CheckShippedStyle(string property, string? value)
        {
            // An unparseable origin list is already reported; naming the shipped origin then could echo the
            // configured value back, which this validator never does.
            if (originFailure is not null || value is null
                || !MapOptions.ShippedStyleRemoteOrigins.TryGetValue(value, out var needed))
                return;

            var missing = needed.Where(n => !origins.Any(o => MapOriginRule.SameOrigin(o, n))).ToList();
            if (missing.Count > 0)
            {
                failures.Add($"{MapOptions.SectionName}:{property} names a style the client ships that loads its tiles, "
                    + $"glyphs and sprite from {string.Join(" ", missing.Select(m => m.GetLeftPart(UriPartial.Authority)))}; "
                    + $"list that origin in {MapOptions.SectionName}:{nameof(MapOptions.AllowedOrigins)} or point the style elsewhere.");
            }
        }

        Check(nameof(MapOptions.StyleUrlLight), options.StyleUrlLight, v => MapOriginRule.CheckStyleUrl(v, origins));
        Check(nameof(MapOptions.StyleUrlDark), options.StyleUrlDark, v => MapOriginRule.CheckStyleUrl(v, origins));
        CheckShippedStyle(nameof(MapOptions.StyleUrlLight), options.StyleUrlLight);
        CheckShippedStyle(nameof(MapOptions.StyleUrlDark), options.StyleUrlDark);
        CheckNotUnderBasemap(nameof(MapOptions.StyleUrlLight), options.StyleUrlLight);
        CheckNotUnderBasemap(nameof(MapOptions.StyleUrlDark), options.StyleUrlDark);
        Check(nameof(MapOptions.AttributionUrl), options.AttributionUrl, MapOriginRule.CheckAttributionUrl);

        // V10 (issue #384): /basemap/ serves the tile file and nothing else. nginx would 404 a style there
        // anyway; this turns a silently blank map into a start-up message.
        void CheckNotUnderBasemap(string property, string? value)
        {
            if (value is not null && (value == MapOptions.BasemapPathPrefix || value.StartsWith(MapOptions.BasemapPathPrefix + "/", StringComparison.Ordinal)))
                failures.Add($"{MapOptions.SectionName}:{property} must name a style shipped under /maps/; /basemap/ serves only the tile file.");
        }

        if (MapOptions.SelfHostedStyles.Contains(options.StyleUrlLight ?? string.Empty)
            || MapOptions.SelfHostedStyles.Contains(options.StyleUrlDark ?? string.Empty))
        {
            if (BasemapFileFailure(options.BasemapFilePath) is { } basemapFailure)
                failures.Add($"{MapOptions.SectionName}:{nameof(MapOptions.BasemapFilePath)} {basemapFailure}.");
            else if (failures.Count == 0)
                WarnOnAttribution(options.Attribution);
        }

        // Attribution is the one [Required] value whose blank form ("   ") the attribute lets through.
        if (options.Attribution is not null
            && (Generic(options.Attribution) ?? MapOriginRule.CheckAttribution(options.Attribution)) is { } attributionFailure)
        {
            failures.Add($"{MapOptions.SectionName}:{nameof(MapOptions.Attribution)} {attributionFailure}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// V1–V9 and V11 of issue #384: the file a self-hosted style draws from. Reads the 127-byte header and
    /// the file's length, never a tile. The rule is returned; the path never is.
    /// </summary>
    private string? BasemapFileFailure(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "must name the basemap file while Map:StyleUrlLight or Map:StyleUrlDark selects a self-hosted style";
        if (Generic(path) is { } generic)
            return generic;
        if (!Path.IsPathFullyQualified(path))
            return "must be an absolute path";

        var info = new FileInfo(path);
        if (info.LinkTarget is not null || Directory.Exists(path))
            return "must be a regular file, not a symlink or a directory";
        if (!info.Exists)
            return "names a file that does not exist; mount the basemap volume and copy the extract to it";

        var buffer = new byte[PmtilesHeader.Length];
        long fileLength;
        int read;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
            fileLength = stream.Length;
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return "cannot be read by the API; make it world-readable (0644)";
        }

        if (read < PmtilesHeader.Length)
            return "is truncated or declares sections beyond its end";
        if (!PmtilesHeader.HasMagic(buffer) || buffer[7] != 3)
            return "is not a PMTiles version 3 archive";

        var header = PmtilesHeader.Parse(buffer);
        if (!header.SectionsFit((ulong)fileLength))
            return "is truncated or declares sections beyond its end";
        if (header.TileType != PmtilesHeader.VectorTileType)
            return "must hold vector (MVT) tiles";
        if (!PmtilesHeader.IsDecodable(header.InternalCompression) || !PmtilesHeader.IsDecodable(header.TileCompression))
            return "uses a compression the browser library cannot decode; rebuild it with gzip";
        if (!BoundsAreValid(header))
            return "declares an invalid bounding box or zoom range";

        logger.LogInformation(
            "Self-hosted basemap validated: bounds {MinLongitude},{MinLatitude},{MaxLongitude},{MaxLatitude}, zoom {MinZoom}-{MaxZoom}.",
            header.MinLongitude, header.MinLatitude, header.MaxLongitude, header.MaxLatitude, header.MinZoom, header.MaxZoom);
        return null;
    }

    // Web Mercator's limit is 85.0511287798…; a whole-world archive stores it rounded up in E7
    // (85.0511288), so the bound sits just above that rather than at the truncated 85.0511.
    private const double MaxMercatorLatitude = 85.0512;

    private static bool BoundsAreValid(PmtilesHeader h) =>
        h.MinZoom <= h.MaxZoom && h.MaxZoom <= 24
        && h.MinLongitude is >= -180 and <= 180 && h.MaxLongitude is >= -180 and <= 180
        && h.MinLatitude is >= -MaxMercatorLatitude and <= MaxMercatorLatitude
        && h.MaxLatitude is >= -MaxMercatorLatitude and <= MaxMercatorLatitude
        && h.MinLongitude <= h.MaxLongitude && h.MinLatitude <= h.MaxLatitude;

    /// <summary>
    /// W1: the data is OpenStreetMap under ODbL and the schema OpenMapTiles under CC-BY, so the caption must
    /// credit both — and the shipped OpenFreeMap credit no longer applies. A warning, not a failure.
    /// </summary>
    private void WarnOnAttribution(string? attribution)
    {
        var text = attribution ?? string.Empty;
        if (!text.Contains("OpenStreetMap", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("OpenMapTiles", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OpenFreeMap", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "{Key} should credit OpenStreetMap and OpenMapTiles, and not OpenFreeMap, while a self-hosted basemap style is selected: the licences of the data and the schema require it.",
                $"{MapOptions.SectionName}:{nameof(MapOptions.Attribution)}");
        }
    }

    private static string? Generic(string value)
    {
        if (MapOriginRule.HasControlCharacter(value))
            return "must not contain control characters (tab, carriage return, newline included)";
        if (MapOriginRule.HasPlaceholder(value))
            return "must not contain an unrendered '{{' or '}}' placeholder";

        return null;
    }
}
