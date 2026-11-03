using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Odyssey.Core.Weather;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The Admin side of weather points (issue #387 §5 endpoints 1–8), replacing <c>/api/weather/locations</c>. Only
/// an Admin can send a position to MET Norway: creating or moving a point is what freezes a site. The picker
/// (<c>GET options</c>, endpoint 9) is every role's and lives on <see cref="WeatherController"/>.
/// </summary>
[ApiController]
[Route("api/weather/points")]
public sealed class WeatherPointsController(WeatherPointService service) : ControllerBase
{
    [HttpGet(Name = "GetWeatherPoints")]
    [Authorize(Policy = PermissionClaims.WeatherPointsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingWeatherPoint>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [SwaggerOperation(Summary = "Every weather point, paged; sortBy=LinkedLocationCount needs locations.read.")]
    public async Task<IActionResult> Get([FromQuery] WeatherPointsQueryParams query, CancellationToken cancellationToken = default)
    {
        // The order alone counts places, so it is refused rather than served unsorted (AC 19).
        if (query.SortBy == WeatherPointSortBy.LinkedLocationCount && !CanReadLocations())
        {
            return Forbid();
        }

        return Ok(await service.ListAsync(query, CanReadLocations(), cancellationToken));
    }

    [HttpGet("{weatherPointId:guid}", Name = "GetWeatherPoint")]
    [Authorize(Policy = PermissionClaims.WeatherPointsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingWeatherPoint))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One weather point.")]
    public async Task<IActionResult> Get([FromRoute] Guid weatherPointId, CancellationToken cancellationToken = default)
    {
        var point = await service.GetAsync(weatherPointId, CanReadLocations(), cancellationToken);
        return point is null ? PointNotFound(weatherPointId) : Ok(point);
    }

    [HttpGet("{weatherPointId:guid}/linked-locations", Name = "GetWeatherPointLinkedLocations")]
    [Authorize(Policy = PermissionClaims.WeatherPointsRead)]
    [Authorize(Policy = PermissionClaims.LocationsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IReadOnlyList<WeatherPointLinkedLocation>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The places that use a weather point: id, name and whole-km distance only.")]
    public async Task<IActionResult> LinkedLocations([FromRoute] Guid weatherPointId, CancellationToken cancellationToken = default)
    {
        var linked = await service.LinkedLocationsAsync(weatherPointId, cancellationToken);
        return linked is null ? PointNotFound(weatherPointId) : Ok(linked);
    }

    [HttpPost(Name = "PostWeatherPoint")]
    [Authorize(Policy = PermissionClaims.WeatherPointsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingWeatherPoint))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a weather point from coordinates, or by copying a reviewed place's position. Links nothing.")]
    public async Task<IActionResult> Post([FromBody] NewWeatherPoint request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        // Copying a place's position reads that place (AC 4).
        if (request.FromLocationId is not null && !CanReadLocations())
        {
            return Forbid();
        }

        var created = await service.CreateAsync(request, userId, cancellationToken, CanReadLocations());
        return CreatedAtRoute("GetWeatherPoint", new { weatherPointId = created.WeatherPointId }, created);
    }

    [HttpPut("{weatherPointId:guid}", Name = "PutWeatherPoint")]
    [Authorize(Policy = PermissionClaims.WeatherPointsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WeatherPointUpdateResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Change a weather point's settings; a changed position, altitude or time zone starts a new site. Not an upsert.")]
    public async Task<IActionResult> Put(
        [FromRoute] Guid weatherPointId, [FromBody] UpdateWeatherPoint request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var result = await service.UpdateAsync(weatherPointId, request, userId, CanReadLocations(), cancellationToken);
        return result is null ? PointNotFound(weatherPointId) : Ok(result);
    }

    [HttpPost("{weatherPointId:guid}/refresh", Name = "RefreshWeatherPoint")]
    [Authorize(Policy = PermissionClaims.WeatherPointsUpdate)]
    [EnableRateLimiting(WeatherRateLimiting.RefreshPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WeatherRefreshResult))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Fetch the forecast now, still honouring MET's expiry and any pause.")]
    public async Task<IActionResult> Refresh([FromRoute] Guid weatherPointId, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var result = await service.RefreshAsync(weatherPointId, userId, cancellationToken);
        return result is null ? this.NotFoundProblem($"No weather point ID {weatherPointId} with its forecast on.") : Ok(result);
    }

    [HttpDelete("{weatherPointId:guid}/history", Name = "DeleteWeatherPointHistory")]
    [Authorize(Policy = PermissionClaims.WeatherPointsDelete)]
    [EnableRateLimiting(WeatherRateLimiting.RefreshPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a weather point's measured data once its history is off. The point and its forecast stay.")]
    public async Task<IActionResult> DeleteHistory([FromRoute] Guid weatherPointId, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.PurgeHistoryAsync(weatherPointId, userId, cancellationToken) ? NoContent() : PointNotFound(weatherPointId);
    }

    [HttpDelete("{weatherPointId:guid}", Name = "DeleteWeatherPoint")]
    [Authorize(Policy = PermissionClaims.WeatherPointsDelete)]
    [EnableRateLimiting(WeatherRateLimiting.RefreshPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a weather point and all its data once both halves are off. Its places become unlinked.")]
    public async Task<IActionResult> Delete([FromRoute] Guid weatherPointId, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.DeleteAsync(weatherPointId, userId, cancellationToken) ? NoContent() : PointNotFound(weatherPointId);
    }

    private bool CanReadLocations() => User.HasPermission(PermissionClaims.LocationsRead);

    private ObjectResult PointNotFound(Guid weatherPointId) => this.NotFoundProblem($"Weather point ID {weatherPointId} not found.");
}
