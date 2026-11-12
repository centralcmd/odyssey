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

    /// <summary>
    /// The access check nginx's <c>auth_request</c> makes before serving the self-hosted basemap file (issue
    /// #384 §5.1). Reads nothing and writes nothing: the authorization policy is the whole answer. It exists
    /// whichever basemap is configured, so the route cannot tell configurations apart.
    /// </summary>
    /// <remarks>
    /// <c>no-store</c> keeps every HTTP cache away from it; nginx's own cache holds the 204 for 60 s and is
    /// told to ignore this header. Not on the identity rate limiter: every call comes from the client
    /// container's address, so a per-address limiter would throttle all users together.
    /// </remarks>
    [HttpGet("basemap-access", Name = "GetBasemapAccess")]
    [Authorize(Policy = PermissionClaims.LocationsRead)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [SwaggerOperation(Summary = "204 when the caller may read the self-hosted basemap file; nginx's access check.")]
    public IActionResult GetBasemapAccess()
    {
        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }
}
