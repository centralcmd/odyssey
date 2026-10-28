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
/// The Admin side of weather (issue #348 §5 endpoints 6–10). Only an Admin can send a location's
/// coordinates to MET Norway: turning weather on snapshots the site, and a later move of the location
/// changes nothing until an Admin applies it.
/// </summary>
[ApiController]
[Route("api/weather/locations")]
public sealed class WeatherLocationsController(WeatherLocationService service) : ControllerBase
{
    [HttpGet(Name = "GetWeatherLocations")]
    [Authorize(Policy = PermissionClaims.WeatherLocationsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingWeatherLocation>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Every location with weather settings, paged, sorted by name.")]
    public async Task<IActionResult> Get([FromQuery] WeatherLocationsQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{locationId:guid}", Name = "GetWeatherLocation")]
    [Authorize(Policy = PermissionClaims.WeatherLocationsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingWeatherLocation))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One location's weather settings.")]
    public async Task<IActionResult> Get([FromRoute] Guid locationId, CancellationToken cancellationToken = default)
    {
        var row = await service.GetAsync(locationId, cancellationToken);
        return row is null ? this.NotFoundProblem($"No weather settings for location ID {locationId}.") : Ok(row);
    }

    [HttpPut("{locationId:guid}", Name = "PutWeatherLocation")]
    [Authorize(Policy = PermissionClaims.WeatherLocationsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingWeatherLocation))]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingWeatherLocation))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Turn weather on or off for a location, or change its settings.")]
    public async Task<IActionResult> Put(
        [FromRoute] Guid locationId, [FromBody] UpsertWeatherLocation request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var result = await service.UpsertAsync(locationId, request, userId, cancellationToken);
        if (result is null)
        {
            return this.NotFoundProblem($"Location ID {locationId} not found.");
        }

        return result.Created
            ? CreatedAtRoute("GetWeatherLocation", new { locationId }, result.Row)
            : Ok(result.Row);
    }

    [HttpPost("{locationId:guid}/apply-location", Name = "ApplyWeatherLocation")]
    [Authorize(Policy = PermissionClaims.WeatherLocationsUpdate)]
    [EnableRateLimiting(WeatherRateLimiting.RefreshPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingWeatherLocation))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Use the location's current position and time zone for weather from now on.")]
    public async Task<IActionResult> Apply(
        [FromRoute] Guid locationId, [FromBody] ApplyWeatherLocation request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var row = await service.ApplyAsync(locationId, request, userId, cancellationToken);
        return row is null ? this.NotFoundProblem($"No weather settings for location ID {locationId}.") : Ok(row);
    }

    [HttpPost("{locationId:guid}/refresh", Name = "RefreshWeatherLocation")]
    [Authorize(Policy = PermissionClaims.WeatherLocationsUpdate)]
    [EnableRateLimiting(WeatherRateLimiting.RefreshPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WeatherRefreshResult))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Fetch the forecast now, still honouring MET's expiry and any pause.")]
    public async Task<IActionResult> Refresh([FromRoute] Guid locationId, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var result = await service.RefreshAsync(locationId, userId, cancellationToken);
        return result is null ? this.NotFoundProblem($"Weather is not on for location ID {locationId}.") : Ok(result);
    }

    [HttpDelete("{locationId:guid}", Name = "DeleteWeatherLocation")]
    [Authorize(Policy = PermissionClaims.WeatherHistoryDelete)]
    [EnableRateLimiting(WeatherRateLimiting.RefreshPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a location's stored weather once its historical data is off. With the forecast still on, only the measured data goes and the forecast setup stays; with both off, every weather record goes. The location itself is untouched.")]
    public async Task<IActionResult> Delete([FromRoute] Guid locationId, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.PurgeAsync(locationId, userId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"No weather settings for location ID {locationId}.");
    }
}
