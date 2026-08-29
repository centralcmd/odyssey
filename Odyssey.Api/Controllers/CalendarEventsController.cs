using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/calendar-events")]
public class CalendarEventsController : ControllerBase
{
    private readonly ILogger<CalendarEventsController> logger;
    private readonly CalendarEventService service;
    private readonly CalendarIcsService icsService;

    public CalendarEventsController(
        ILogger<CalendarEventsController> logger,
        CalendarEventService service,
        CalendarIcsService icsService)
    {
        this.logger = logger;
        this.service = service;
        this.icsService = icsService;
    }

    [HttpGet(Name = "GetCalendarEvents")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingCalendarEvent>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List calendar events overlapping a required From/To window (max 92 days), filterable by calendar.")]
    public async Task<IActionResult> Get(
        [FromQuery] CalendarEventsQueryParams query, CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    // Aggregate/filtered .ics export (issue #340). Literal "ics" segment takes precedence over the
    // {id} route below (ASP.NET Core prefers literal over parameter segments), mirroring
    // JournalTasksController's ExportTasksIcs / CalendarIcsController's {id}/ics.
    [HttpGet("ics", Name = "ExportCalendarEventsIcs")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export every matching event, across every calendar, as a single multi-VEVENT .ics file.")]
    public async Task<IActionResult> ExportIcs(
        [FromQuery] CalendarEventsIcsExportQueryParams query, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        await icsService.ExportAggregateStreamingAsync(query, userId, Response.Body, (fileName, rowCount) =>
        {
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.CacheControl = "no-store";
            Response.ContentType = "text/calendar; charset=utf-8";
            var contentDisposition = new ContentDispositionHeaderValue("attachment");
            contentDisposition.SetHttpFileName(fileName);
            Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
            Response.Headers["X-Odyssey-Export-Rows"] = rowCount.ToString(CultureInfo.InvariantCulture);
        }, cancellationToken);

        return new EmptyResult();
    }

    [HttpGet("{id}", Name = "GetCalendarEvent")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingCalendarEvent))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a calendar event.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var calendarEvent = await service.Get(id, cancellationToken);
        return calendarEvent is null ? this.NotFoundProblem($"Calendar event ID {id} not found.") : Ok(calendarEvent);
    }

    [HttpGet("{id}/ics", Name = "ExportCalendarEventIcs")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export a single event as a standalone RFC 5545 .ics VEVENT.")]
    public async Task<IActionResult> ExportEventIcs(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var export = await icsService.ExportEventAsync(id, cancellationToken);
        if (export is null)
        {
            return this.NotFoundProblem($"Calendar event ID {id} not found.");
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store";
        var bytes = Encoding.UTF8.GetBytes(export.Content);
        return File(bytes, "text/calendar; charset=utf-8", export.FileName);
    }

    [HttpPost(Name = "PostCalendarEvent")]
    [Authorize(Policy = PermissionClaims.CalendarCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingCalendarEvent))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a standalone (non-recurring) calendar event.")]
    public async Task<IActionResult> Post(
        [FromBody] NewCalendarEvent request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.Create(request, userId, cancellationToken);
        return CreatedAtRoute("GetCalendarEvent", new { id = created.CalendarEventId }, created);
    }

    [HttpPut("{id}", Name = "PutCalendarEvent")]
    [Authorize(Policy = PermissionClaims.CalendarUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingCalendarEvent))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update this occurrence only. RecurrencePatternId is immutable — use PUT /api/recurrence-patterns/{id} to edit a series.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewCalendarEvent request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Calendar event ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteCalendarEvent")]
    [Authorize(Policy = PermissionClaims.CalendarDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Hard-delete this occurrence only.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Calendar event ID {id} not found.");
    }
}
