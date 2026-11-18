using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/calendar-events")]
public sealed class CalendarEventsController : ControllerBase
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
        var page = await service.ListAsync(query, cancellationToken, User.LocationClaims());
        User.ApplyLinkVisibility(page.Items);
        return Ok(page);
    }

    // Aggregate/filtered .ics export (issue #340). Literal "ics" segment takes precedence over the
    // {id} route below (ASP.NET Core prefers literal over parameter segments), mirroring
    // JournalTasksController's ExportTasksIcs / CalendarIcsController's {id}/ics. Both paths stream since
    // issue #516, and both abort at the byte cap instead of truncating or refusing late.
    [HttpGet("ics", Name = "ExportCalendarEventsIcs")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export every matching event, across every calendar, as a single multi-VEVENT .ics file.")]
    public Task<IActionResult> ExportIcs(
        [FromQuery] CalendarEventsIcsExportQueryParams query, CancellationToken cancellationToken = default) =>
        CalendarIcsExportResponder.StreamAsync(
            this, icsService, CalendarIcsExportScope.ForAggregate(query), "Not found.", cancellationToken);

    // What the aggregate export would contain for the same query, under the same checks and permits (issue #516 §5).
    [HttpGet("ics/summary", Name = "SummarizeCalendarEventsIcsExport")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CalendarIcsExportSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Summarize what the aggregate .ics export would contain for the same query.")]
    public Task<ActionResult<CalendarIcsExportSummary>> SummarizeIcsExport(
        [FromQuery] CalendarEventsIcsExportQueryParams query, CancellationToken cancellationToken = default) =>
        CalendarIcsExportResponder.SummarizeAsync(
            this, icsService, CalendarIcsExportScope.ForAggregate(query), "Not found.", cancellationToken);

    [HttpGet("{id}", Name = "GetCalendarEvent")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingCalendarEvent))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a calendar event.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var calendarEvent = await service.Get(id, cancellationToken, User.LocationClaims());
        User.ApplyLinkVisibility(calendarEvent);
        return calendarEvent is null ? this.NotFoundProblem($"Calendar event ID {id} not found.") : Ok(calendarEvent);
    }

    [HttpGet("{id}/ics", Name = "ExportCalendarEventIcs")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    // Under the shared export permits and progress guard like every calendar export (issue #516 §5).
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export a single event as a standalone RFC 5545 .ics VEVENT.")]
    public Task<IActionResult> ExportEventIcs(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery(Name = "attachments")][EnumDataType(typeof(IcsAttachmentsMode))] IcsAttachmentsMode attachments = IcsAttachmentsMode.Include,
        CancellationToken cancellationToken = default) =>
        CalendarIcsExportResponder.StreamAsync(
            this, icsService, CalendarIcsExportScope.ForEvent(id), $"Calendar event ID {id} not found.", cancellationToken, attachments);

    [HttpGet("{id}/ics/summary", Name = "SummarizeCalendarEventIcsExport")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CalendarIcsExportSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Summarize what a single event's .ics export would contain.")]
    public Task<ActionResult<CalendarIcsExportSummary>> SummarizeEventIcsExport(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery(Name = "attachments")][EnumDataType(typeof(IcsAttachmentsMode))] IcsAttachmentsMode attachments = IcsAttachmentsMode.Include,
        CancellationToken cancellationToken = default) =>
        CalendarIcsExportResponder.SummarizeAsync(
            this, icsService, CalendarIcsExportScope.ForEvent(id), $"Calendar event ID {id} not found.", cancellationToken, attachments);

    [HttpPost(Name = "PostCalendarEvent")]
    [Authorize(Policy = PermissionClaims.CalendarCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingCalendarEvent))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a standalone (non-recurring) calendar event.")]
    public async Task<IActionResult> Post(
        [FromBody] NewCalendarEvent request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await service.Create(request, userId, cancellationToken, User.LocationClaims(), User.LinkClaims());
        User.ApplyLinkVisibility(created);
        return CreatedAtRoute("GetCalendarEvent", new { id = created.CalendarEventId }, created);
    }

    [HttpPut("{id}", Name = "PutCalendarEvent")]
    [Authorize(Policy = PermissionClaims.CalendarUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingCalendarEvent))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update this occurrence only; on a series row it becomes an override that a series edit keeps. Times are wall-clock in timeZoneId. RecurrencePatternId is immutable — use PUT /api/recurrence-patterns/{id} to edit a series.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewCalendarEvent request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var updated = await service.Update(id, request, userId, cancellationToken, User.LocationClaims(), User.LinkClaims());
        User.ApplyLinkVisibility(updated);
        return updated is null ? this.NotFoundProblem($"Calendar event ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteCalendarEvent")]
    [Authorize(Policy = PermissionClaims.CalendarDelete)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CalendarEventDeleteResult))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete this occurrence only. On a series row it also records an exclusion for the occurrence's key, so a later series edit or re-import does not bring it back. Always 200 with what keying a legacy series detached (zero and false otherwise).")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var result = await service.Delete(id, cancellationToken, User.ActingUserId());
        if (result is null)
        {
            return this.NotFoundProblem($"Calendar event ID {id} not found.");
        }

        // One shape on every success (PR #528 review A2): the detached report is zero and false on an ordinary delete.
        return Ok(result);
    }
}
