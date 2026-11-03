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
/// The household's places (issue #350) — since issue #380 the one store for every place and address in the
/// app. A place is archived through <c>PUT</c> with <c>archived: true</c>; two duplicates are merged with
/// <c>POST {id}/merge</c>; and an unused place is deleted outright with <c>DELETE</c>, under
/// <c>locations.delete</c> — refused with a <c>409</c> while anything uses it.
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
        return Ok(await service.ListAsync(query, cancellationToken, User.LocationClaims()));
    }

    [HttpGet("{id:guid}", Name = "GetLocation")]
    [Authorize(Policy = PermissionClaims.LocationsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingLocation))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a location, archived or not.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var location = await service.Get(id, cancellationToken, User.LocationClaims());
        return location is null ? this.NotFoundProblem($"Location ID {id} not found.") : Ok(location);
    }

    [HttpPost(Name = "PostLocation")]
    [Authorize(Policy = PermissionClaims.LocationsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingLocation))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
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
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace a location; archived: true archives it, false restores it. Not an upsert.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateLocation request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var updated = await service.Update(id, request, userId, cancellationToken, User.LocationClaims());
        return updated is null ? this.NotFoundProblem($"Location ID {id} not found.") : Ok(updated);
    }

    /// <summary>
    /// Merges a duplicate into another place (issue #380 §5.6): every reference moves to the target, and the
    /// source is archived as a forwarder. Needs <c>locations.update</c> plus the update claim of every kind of
    /// record that actually references the source, decided inside the merge's own transaction; a missing one
    /// is a <c>403</c> that does not say which.
    /// </summary>
    [HttpPost("{sourceId:guid}/merge", Name = "MergeLocation")]
    [Authorize(Policy = PermissionClaims.LocationsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingLocation))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Merge a location into another; returns the surviving target.")]
    public async Task<IActionResult> Merge(
        [FromRoute(Name = "sourceId")] Guid sourceId,
        [FromBody] MergeLocationRequest request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var target = await service.MergeAsync(sourceId, request.TargetLocationId!.Value, User.LocationClaims(), userId, cancellationToken);
        return target is null ? this.NotFoundProblem("Location not found.") : Ok(target);
    }

    /// <summary>
    /// Deletes an unused place outright — the erasure path (issue #380 §5.7). Anything that uses it, a merge
    /// forwarder included, is a <c>409</c> whose body does not say what.
    /// </summary>
    [HttpDelete("{id:guid}", Name = "DeleteLocation")]
    [Authorize(Policy = PermissionClaims.LocationsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a location nothing uses.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.DeleteAsync(id, userId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Location ID {id} not found.");
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
