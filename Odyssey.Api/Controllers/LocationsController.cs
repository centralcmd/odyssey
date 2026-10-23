using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Geocoding;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The household's locations (issue #350). There is no <c>DELETE</c>: a location is archived through
/// <c>PUT</c> with <c>archived: true</c>, and a <c>DELETE</c> request is a <c>405</c>.
/// </summary>
[ApiController]
[Route("api/locations")]
public sealed class LocationsController : ControllerBase
{
    private readonly LocationService service;

    public LocationsController(LocationService service)
    {
        this.service = service;
    }

    [HttpGet(Name = "GetLocations")]
    [Authorize(Policy = PermissionClaims.LocationsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingLocation>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List locations with search, status filter, sort and paging.")]
    public async Task<IActionResult> Get(
        [FromQuery] LocationsQueryParams query, CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("{id:guid}", Name = "GetLocation")]
    [Authorize(Policy = PermissionClaims.LocationsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingLocation))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a location, archived or not.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var location = await service.Get(id, cancellationToken);
        return location is null ? this.NotFoundProblem($"Location ID {id} not found.") : Ok(location);
    }

    [HttpPost(Name = "PostLocation")]
    [Authorize(Policy = PermissionClaims.LocationsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingLocation))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a location.")]
    public async Task<IActionResult> Post(
        [FromBody] NewLocation request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await service.Create(request, userId, cancellationToken);
        return CreatedAtRoute("GetLocation", new { id = created.LocationId }, created);
    }

    [HttpPut("{id:guid}", Name = "PutLocation")]
    [Authorize(Policy = PermissionClaims.LocationsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingLocation))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace a location; archived: true archives it, false restores it. Not an upsert.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateLocation request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Location ID {id} not found.") : Ok(updated);
    }

    /// <summary>
    /// The geocoding proxy. A <c>POST</c> although it is read-only, so the search text travels in the body
    /// and never appears in a URL — not in the hosting log line, an access log, browser history or a
    /// trace attribute (B2, S1). Gated on <c>locations.create</c>: only a caller who can save a location
    /// has a reason to send text to a third party.
    /// </summary>
    [HttpPost("search", Name = "SearchLocations")]
    [Authorize(Policy = PermissionClaims.LocationsCreate)]
    [EnableRateLimiting(LocationSearchRateLimiting.PerUserPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(LocationSearchResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Search an address or place name: Kartverket for Norway, Open-Meteo for the rest of the world.")]
    public async Task<IActionResult> Search(
        [FromBody] LocationSearchRequest request,
        [FromServices] LocationSearchService search,
        [FromServices] LocationSearchGlobalLimiter globalLimiter,
        CancellationToken cancellationToken = default)
    {
        // The per-user policy has already admitted this request. A cached result reaches no provider, so it
        // spends nothing from the instance-wide budget, which is checked only on a miss — and after the
        // per-user check, never before (see LocationSearchRateLimiting).
        if (search.TryGetCached(request, out var cached))
        {
            return Ok(cached);
        }

        using var lease = globalLimiter.Acquire();
        if (!lease.IsAcquired)
        {
            if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(NumberFormatInfo.InvariantInfo);
            }

            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: ReasonPhrases.GetReasonPhrase(StatusCodes.Status429TooManyRequests),
                detail: "Location search is busy right now. Wait a moment, then try again.");
        }

        return Ok(await search.SearchAsync(request, cancellationToken));
    }
}
