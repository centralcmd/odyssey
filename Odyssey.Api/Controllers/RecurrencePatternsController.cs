using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
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
[Route("api/recurrence-patterns")]
public class RecurrencePatternsController : ControllerBase
{
    private readonly ILogger<RecurrencePatternsController> logger;
    private readonly RecurrencePatternService service;
    private readonly CalendarIcsService icsService;

    public RecurrencePatternsController(
        ILogger<RecurrencePatternsController> logger,
        RecurrencePatternService service,
        CalendarIcsService icsService)
    {
        this.logger = logger;
        this.service = service;
        this.icsService = icsService;
    }

    [HttpGet(Name = "GetRecurrencePatterns")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingRecurrencePattern>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List recurrence patterns, optionally filtered by calendar.")]
    public async Task<IActionResult> Get(
        [FromQuery] RecurrencePatternsQueryParams query, CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("{id}", Name = "GetRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingRecurrencePattern))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a recurrence pattern.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var pattern = await service.Get(id, cancellationToken);
        return pattern is null ? this.NotFoundProblem($"Recurrence pattern ID {id} not found.") : Ok(pattern);
    }

    [HttpGet("{id}/events", Name = "GetRecurrencePatternEvents")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingCalendarEvent>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List all generated events (past and future) for a recurrence pattern, paged.")]
    public async Task<IActionResult> GetEvents(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery][Range(0, int.MaxValue)] int offset = 0,
        [FromQuery][Range(1, ListDefaults.MaxLimit)] int limit = ListDefaults.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var result = await service.ListEventsAsync(id, offset, limit, cancellationToken);
        return result is null ? this.NotFoundProblem($"Recurrence pattern ID {id} not found.") : Ok(result);
    }

    [HttpGet("{id}/ics", Name = "ExportRecurrencePatternIcs")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export a recurring series as one RRULE VEVENT (or its per-occurrence fallback).")]
    public async Task<IActionResult> ExportIcs(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var export = await icsService.ExportPatternAsync(id, cancellationToken);
        if (export is null)
        {
            return this.NotFoundProblem($"Recurrence pattern ID {id} not found.");
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store";
        var bytes = Encoding.UTF8.GetBytes(export.Content);
        return File(bytes, "text/calendar; charset=utf-8", export.FileName);
    }

    [HttpPost(Name = "PostRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingRecurrencePattern))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a recurring event series; eagerly generates its bounded set of CalendarEvent occurrences in one transaction.")]
    public async Task<IActionResult> Post(
        [FromBody] NewRecurrencePattern request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.Create(request, userId, cancellationToken);
        return CreatedAtRoute("GetRecurrencePattern", new { id = created.RecurrencePatternId }, created);
    }

    [HttpPut("{id}", Name = "PutRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingRecurrencePattern))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a series' template/rule fields; regenerates its future (>= now) generated events only.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewRecurrencePattern request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Recurrence pattern ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a series: hard-deletes its future generated events, detaches past/current ones.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Recurrence pattern ID {id} not found.");
    }
}
