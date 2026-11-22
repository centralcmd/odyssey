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

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/recurrence-patterns")]
public sealed class RecurrencePatternsController : ControllerBase
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
    [EndpointSummary("List recurrence patterns, optionally filtered by calendar.")]
    public async Task<IActionResult> Get(
        [FromQuery] RecurrencePatternsQueryParams query, CancellationToken cancellationToken = default)
    {
        var page = await service.ListAsync(query, cancellationToken, User.LocationClaims());
        User.ApplyLinkVisibility(page.Items);
        return Ok(page);
    }

    [HttpGet("{id}", Name = "GetRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingRecurrencePattern))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("Get a recurrence pattern.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var pattern = await service.Get(id, cancellationToken, User.LocationClaims());
        User.ApplyLinkVisibility(pattern);
        return pattern is null ? this.NotFoundProblem($"Recurrence pattern ID {id} not found.") : Ok(pattern);
    }

    [HttpGet("{id}/events", Name = "GetRecurrencePatternEvents")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingCalendarEvent>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("List all generated events (past and future) for a recurrence pattern, paged.")]
    public async Task<IActionResult> GetEvents(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery][Range(0, int.MaxValue)] int offset = 0,
        [FromQuery][Range(1, ListDefaults.MaxLimit)] int limit = ListDefaults.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var result = await service.ListEventsAsync(id, offset, limit, cancellationToken, User.LocationClaims());
        if (result is not null)
        {
            User.ApplyLinkVisibility(result.Items);
        }

        return result is null ? this.NotFoundProblem($"Recurrence pattern ID {id} not found.") : Ok(result);
    }

    [HttpGet("{id}/ics", Name = "ExportRecurrencePatternIcs")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    // Under the shared export permits and progress guard like every calendar export (issue #516 §5).
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [EndpointSummary("Export a recurring series as one RRULE VEVENT (or its per-occurrence fallback).")]
    public Task<IActionResult> ExportIcs(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery(Name = "attachments")][EnumDataType(typeof(IcsAttachmentsMode))] IcsAttachmentsMode attachments = IcsAttachmentsMode.Include,
        CancellationToken cancellationToken = default) =>
        CalendarIcsExportResponder.StreamAsync(
            this, icsService, CalendarIcsExportScope.ForPattern(id), $"Recurrence pattern ID {id} not found.", cancellationToken, attachments);

    [HttpGet("{id}/ics/summary", Name = "SummarizeRecurrencePatternIcsExport")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CalendarIcsExportSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [EndpointSummary("Summarize what a recurring series' .ics export would contain.")]
    public Task<ActionResult<CalendarIcsExportSummary>> SummarizeIcsExport(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery(Name = "attachments")][EnumDataType(typeof(IcsAttachmentsMode))] IcsAttachmentsMode attachments = IcsAttachmentsMode.Include,
        CancellationToken cancellationToken = default) =>
        CalendarIcsExportResponder.SummarizeAsync(
            this, icsService, CalendarIcsExportScope.ForPattern(id), $"Recurrence pattern ID {id} not found.", cancellationToken, attachments);

    [HttpPost(Name = "PostRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingRecurrencePattern))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("Create a recurring event series; eagerly generates its bounded set of CalendarEvent occurrences in one transaction.")]
    public async Task<IActionResult> Post(
        [FromBody] NewRecurrencePattern request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await service.Create(request, userId, cancellationToken, User.LocationClaims(), User.LinkClaims());
        User.ApplyLinkVisibility(created);
        return CreatedAtRoute("GetRecurrencePattern", new { id = created.RecurrencePatternId }, created);
    }

    [HttpPut("{id}", Name = "PutRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RecurrencePatternUpdateResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Update a series' template, rule and zone; reconciles its future rows in place by occurrence key, keeping every override and exclusion whose key survives, and reports what it dropped.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewRecurrencePattern request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var updated = await service.Update(id, request, userId, cancellationToken, User.LocationClaims(), User.LinkClaims());
        User.ApplyLinkVisibility(updated?.Pattern);
        return updated is null ? this.NotFoundProblem($"Recurrence pattern ID {id} not found.") : Ok(updated);
    }

    // What the PUT with the same body would drop (issue #519 §5 endpoint 4). It saves nothing and takes no lock, and runs
    // under the summaries' per-user limiter (v3 §F).
    [HttpPost("{id}/preview", Name = "PreviewRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarUpdate)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RecurrencePatternPreview))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [EndpointSummary("Preview a series edit: the overrides and exclusions it would drop, without saving.")]
    public async Task<IActionResult> Preview(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewRecurrencePattern request, CancellationToken cancellationToken = default)
    {
        var preview = await service.PreviewAsync(id, request, cancellationToken, User.LocationClaims());
        return preview is null ? this.NotFoundProblem($"Recurrence pattern ID {id} not found.") : Ok(preview);
    }

    [HttpDelete("{id}", Name = "DeleteRecurrencePattern")]
    [Authorize(Policy = PermissionClaims.CalendarDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Delete a series: hard-deletes its future generated events, detaches past/current ones and removes its exclusions.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Recurrence pattern ID {id} not found.");
    }
}
