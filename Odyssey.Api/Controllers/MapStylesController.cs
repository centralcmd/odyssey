using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Odyssey.Api.Map;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The location map's basemap configuration (issue #370): two style URLs and the attribution to show in
/// place of the style's own. Instance configuration, not per-user data; an in-memory options read.
/// </summary>
/// <remarks>
/// Gated on <c>locations.read</c> because the location map is its only consumer — a map elsewhere widens it
/// deliberately. <c>Map:AllowedOrigins</c> is never returned.
/// </remarks>
[ApiController]
[Route("api/map-styles")]
public sealed class MapStylesController : ControllerBase
{
    private readonly IOptions<MapOptions> options;

    public MapStylesController(IOptions<MapOptions> options)
    {
        this.options = options;
    }

    [HttpGet(Name = "GetMapStyles")]
    [Authorize(Policy = PermissionClaims.LocationsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(MapStyles))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [SwaggerOperation(Summary = "The basemap style URLs and attribution for the location map.")]
    public IActionResult Get()
    {
        var map = options.Value;

        // Set here, on the 200 path only: a 401/403 is produced by authorization before this runs, so it
        // never carries a cacheable header.
        Response.Headers.CacheControl = "private, max-age=300";

        return Ok(new MapStyles
        {
            LightStyleUrl = map.StyleUrlLight,
            DarkStyleUrl = map.StyleUrlDark,
            Attribution = map.Attribution,
            AttributionUrl = map.AttributionUrl,
        });
    }
}
