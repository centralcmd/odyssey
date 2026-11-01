namespace Odyssey.Dtos.Journal;

/// <summary>
/// The basemap the location map draws (issue #370), from the API's deploy-time <c>Map</c> section. Read-only
/// instance configuration, so it carries no validation attributes; the API validated every value at
/// start-up.
/// </summary>
/// <remarks>
/// <see cref="Attribution"/> <b>replaces</b> the style's own credits: the client turns MapLibre's attribution
/// control off and renders this as plain text, so no string a remote style or TileJSON serves ever reaches
/// the DOM. The deployment's allowed origins are deliberately absent — the client has no use for them.
/// </remarks>
public sealed record MapStyles
{
    /// <summary>An absolute https URL or a root-relative path.</summary>
    public string LightStyleUrl { get; set; } = string.Empty;

    /// <inheritdoc cref="LightStyleUrl" />
    public string DarkStyleUrl { get; set; } = string.Empty;

    /// <summary>Plain text, never markup.</summary>
    public string Attribution { get; set; } = string.Empty;

    /// <summary>An absolute https link for <see cref="Attribution"/>.</summary>
    public string AttributionUrl { get; set; } = string.Empty;
}
