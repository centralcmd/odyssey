using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/calendars")]
public class CalendarsController : ControllerBase
{
    private readonly ILogger<CalendarsController> logger;
    private readonly CalendarService service;

    public CalendarsController(
        ILogger<CalendarsController> logger,
        CalendarService service)
    {
        this.logger = logger;
        this.service = service;
    }

    [HttpGet(Name = "GetCalendars")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingCalendar>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List calendars with search, sort and paging.")]
    public async Task<IActionResult> Get(
        [FromQuery] CalendarsQueryParams query, CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("{id}", Name = "GetCalendar")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingCalendar))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a calendar.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var calendar = await service.Get(id, cancellationToken);
        return calendar is null ? this.NotFoundProblem($"Calendar ID {id} not found.") : Ok(calendar);
    }

    [HttpPost(Name = "PostCalendar")]
    [Authorize(Policy = PermissionClaims.CalendarCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingCalendar))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a calendar.")]
    public async Task<IActionResult> Post(
        [FromBody] NewCalendar request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.Create(request, userId, cancellationToken);
        return CreatedAtRoute("GetCalendar", new { id = created.CalendarId }, created);
    }

    [HttpPut("{id}", Name = "PutCalendar")]
    [Authorize(Policy = PermissionClaims.CalendarUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingCalendar))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Rename, redescribe or recolour a calendar.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewCalendar request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Calendar ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteCalendar")]
    [Authorize(Policy = PermissionClaims.CalendarDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an empty calendar; a calendar with events or recurrence patterns returns 409.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Calendar ID {id} not found.");
    }
}
