using System.ComponentModel.DataAnnotations;

namespace Odyssey.Api.Map;

/// <summary>
/// The basemap the location map draws (issue #370), bound from the deploy-time <c>Map</c> section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deploy-time on purpose, not a <c>SystemSettings</c> row.</b> The browser can only fetch from a host
/// the client container's Content Security Policy allows, and nginx renders that CSP from
/// <c>MAP_ALLOWED_ORIGINS</c> when the container starts. An admin-editable style URL would either be
/// silently blocked by the CSP or force a CSP wide enough to defeat it.
/// </para>
/// <para>
/// The compile-time bounds are attributes here; the derived rules (URL shape, the origin cross-check) are
/// <see cref="MapOptionsValidator"/>. Every message names the key and the rule, never the value.
/// </para>
/// </remarks>
public sealed class MapOptions
{
    public const string SectionName = "Map";

    /// <summary>
    /// The same-origin styles the client ships that still load tiles, glyphs and a sprite from a remote
    /// origin, keyed by their root-relative URL. A root-relative style passes the origin cross-check on its
    /// own, so without this list the off-switch (an empty <see cref="AllowedOrigins"/>) would start with
    /// such a style selected and the CSP would silently blank the map. <c>MapStylesApiTests</c> reads every
    /// style under the client's <c>wwwroot/maps/</c> and fails if this list disagrees with its URLs.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<Uri>> ShippedStyleRemoteOrigins =
        new Dictionary<string, IReadOnlyList<Uri>>(StringComparer.Ordinal)
        {
            ["/maps/odyssey-dark.json"] = [new Uri("https://tiles.openfreemap.org")],
        };

    /// <summary>
    /// The shipped styles that draw from the self-hosted basemap file (issue #384) rather than from a tile
    /// host. Selecting either one turns on the <see cref="BasemapFilePath"/> checks in
    /// <see cref="MapOptionsValidator"/>, which is what lets the start-up check know every file a selected
    /// style depends on.
    /// </summary>
    public static readonly IReadOnlySet<string> SelfHostedStyles =
        new HashSet<string>(StringComparer.Ordinal) { "/maps/self-hosted-light.json", "/maps/self-hosted-dark.json" };

    /// <summary>
    /// The one path the client container serves the basemap file at. Styles may not live under it: it
    /// serves the tile file and nothing else.
    /// </summary>
    public const string BasemapPathPrefix = "/basemap";

    /// <summary>An absolute https URL whose origin is in <see cref="AllowedOrigins"/>, or a root-relative path.</summary>
    [Required]
    [StringLength(2048)]
    public string StyleUrlLight { get; set; } = string.Empty;

    /// <inheritdoc cref="StyleUrlLight" />
    [Required]
    [StringLength(2048)]
    public string StyleUrlDark { get; set; } = string.Empty;

    /// <summary>Rendered as plain text, and replaces the style's own credits rather than adding to them.</summary>
    [Required]
    [StringLength(300)]
    public string Attribution { get; set; } = string.Empty;

    /// <summary>A plain https link; navigated, never fetched, so it is not checked against the origins.</summary>
    [Required]
    [StringLength(2048)]
    public string AttributionUrl { get; set; } = string.Empty;

    /// <summary>
    /// The origins the style is served from, separated by single spaces. Must equal the client container's
    /// <c>MAP_ALLOWED_ORIGINS</c>. Empty is the off-switch, and is valid only while both style URLs are
    /// root-relative and neither is one of the <see cref="ShippedStyleRemoteOrigins"/>. Never returned to the client.
    /// </summary>
    [StringLength(2048)]
    public string AllowedOrigins { get; set; } = string.Empty;

    /// <summary>
    /// Where the API finds the self-hosted basemap file to validate it at start-up (issue #384). Compose sets
    /// it; the operator does not. Required only while a <see cref="SelfHostedStyles"/> entry is selected.
    /// Never returned to the client.
    /// </summary>
    [StringLength(1024)]
    public string BasemapFilePath { get; set; } = string.Empty;
}
