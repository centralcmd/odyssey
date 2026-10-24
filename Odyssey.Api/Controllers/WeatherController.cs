using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// Weather reads, the picker and the caller's own default (issue #348 §5 endpoints 1–5 and 11–13). The
/// event and entry reads sit here rather than on their own controllers so every weather read shares one
/// rate limit and one service, and they stack the owning module's claim on top of <c>weather.read</c>.
/// </summary>
[ApiController]
[Route("api/weather")]
public sealed class WeatherController(WeatherReadService service) : ControllerBase
{
    [HttpGet("current", Name = "GetWeatherCurrent")]
    [Authorize(Policy = PermissionClaims.WeatherRead)]
    [EnableRateLimiting(WeatherRateLimiting.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WeatherCurrentResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The current conditions and today's summary for a place.")]
    public async Task<IActionResult> Current([FromQuery] WeatherLocationQuery query, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return Ok(await service.CurrentAsync(query.LocationId, userId, cancellationToken));
    }

    [HttpGet("hourly", Name = "GetWeatherHourly")]
    [Authorize(Policy = PermissionClaims.WeatherRead)]
    [EnableRateLimiting(WeatherRateLimiting.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WeatherHourlyResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The effective forecast steps in a window of at most 10 days.")]
    public async Task<IActionResult> Hourly([FromQuery] WeatherHourlyQuery query, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return Ok(await service.HourlyAsync(query, userId, cancellationToken));
    }

    [HttpGet("daily", Name = "GetWeatherDaily")]
    [Authorize(Policy = PermissionClaims.WeatherRead)]
    [EnableRateLimiting(WeatherRateLimiting.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WeatherDailyResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Day summaries over a range of at most 62 days; days with no data are omitted.")]
    public async Task<IActionResult> Daily([FromQuery] WeatherDailyQuery query, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return Ok(await service.DailyAsync(query, userId, cancellationToken));
    }

    [HttpGet("~/api/calendar-events/{id:guid}/weather", Name = "GetCalendarEventWeather")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [Authorize(Policy = PermissionClaims.WeatherRead)]
    [EnableRateLimiting(WeatherRateLimiting.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CalendarEventWeatherResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The weather for a calendar event's time or dates.")]
    public async Task<IActionResult> CalendarEvent(
        [FromRoute(Name = "id")] Guid id, [FromQuery] WeatherLocationQuery query, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var weather = await service.CalendarEventAsync(id, query.LocationId, userId, cancellationToken);
        return weather is null ? this.NotFoundProblem($"Calendar event ID {id} not found.") : Ok(weather);
    }

    [HttpGet("~/api/journal-entries/{id:guid}/weather", Name = "GetJournalEntryWeather")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    [Authorize(Policy = PermissionClaims.WeatherRead)]
    [EnableRateLimiting(WeatherRateLimiting.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(JournalEntryWeatherResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The weather for the day of a journal entry.")]
    public async Task<IActionResult> JournalEntry(
        [FromRoute(Name = "id")] Guid id, [FromQuery] WeatherLocationQuery query, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var weather = await service.JournalEntryAsync(id, query.LocationId, userId, cancellationToken);
        return weather is null ? this.NotFoundProblem($"Journal entry ID {id} not found.") : Ok(weather);
    }

    [HttpGet("locations/options", Name = "GetWeatherLocationOptions")]
    [Authorize(Policy = PermissionClaims.WeatherRead)]
    [EnableRateLimiting(WeatherRateLimiting.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WeatherLocationOptions))]
    [SwaggerOperation(Summary = "The places a user can pick for weather.")]
    public async Task<IActionResult> Options(CancellationToken cancellationToken = default) =>
        Ok(await service.OptionsAsync(cancellationToken));

    [HttpGet("preferences", Name = "GetWeatherPreferences")]
    [Authorize(Policy = PermissionClaims.WeatherRead)]
    [EnableRateLimiting(WeatherRateLimiting.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WeatherPreferences))]
    [SwaggerOperation(Summary = "The caller's own weather place, and the household default.")]
    public async Task<IActionResult> Preferences(CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return Ok(await service.PreferencesAsync(userId, cancellationToken));
    }

    [HttpPut("preferences", Name = "PutWeatherPreferences")]
    [Authorize(Policy = PermissionClaims.WeatherPreferencesUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Set the caller's own weather place; null follows the household default.")]
    public async Task<IActionResult> PutPreferences(
        [FromBody] UpdateWeatherPreferences request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        await service.SetPreferenceAsync(userId, request, cancellationToken);
        return NoContent();
    }
}
