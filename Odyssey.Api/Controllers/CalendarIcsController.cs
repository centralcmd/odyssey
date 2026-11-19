using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// ICS (RFC 5545) import/export for a calendar (issues #330, #516). Shares the <c>api/calendars</c> prefix
/// with <see cref="CalendarsController"/> (same sub-resource convention as e.g. Terms). Export
/// is a streamed GET with a summary beside it; import is a POST to the same URI with a multipart body.
/// </summary>
[ApiController]
[Route("api/calendars")]
public sealed class CalendarIcsController : ControllerBase
{
    private readonly CalendarIcsService service;

    public CalendarIcsController(CalendarIcsService service)
    {
        this.service = service;
    }

    [HttpGet("{id}/ics", Name = "ExportCalendarIcs")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    // Streams (issue #516 Goal 1) under the shared export permits and the progress guard.
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export a calendar's events as an RFC 5545 .ics file.")]
    public Task<IActionResult> Export(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default) =>
        CalendarIcsExportResponder.StreamAsync(
            this, service, CalendarIcsExportScope.ForCalendar(id), $"Calendar ID {id} not found.", cancellationToken);

    // What the calendar export would contain, under the same checks and the same permits (issue #516 §5, S4), so a
    // refusal or a 429 arrives before the browser starts a download. The permit is released when it returns.
    [HttpGet("{id}/ics/summary", Name = "SummarizeCalendarIcsExport")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CalendarIcsExportSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Summarize what a calendar's .ics export would contain.")]
    public Task<ActionResult<CalendarIcsExportSummary>> SummarizeExport(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default) =>
        CalendarIcsExportResponder.SummarizeAsync(
            this, service, CalendarIcsExportScope.ForCalendar(id), $"Calendar ID {id} not found.", cancellationToken);

    [HttpPost("{id}/ics", Name = "ImportCalendarIcs")]
    // Import can create or update depending on each VEVENT's UID match, so it requires BOTH claims;
    // stacked [Authorize] attributes are AND-combined.
    [Authorize(Policy = PermissionClaims.CalendarCreate)]
    [Authorize(Policy = PermissionClaims.CalendarUpdate)]
    [Consumes("multipart/form-data")]
    [ImportSizeLimit(ImportSurface.Calendars)]
    [EnableRateLimiting(ImportExportRateLimiting.ImportConcurrencyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IcsImportResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Import an RFC 5545 .ics file into an existing calendar.")]
    public async Task<IActionResult> Import(
        [FromRoute(Name = "id")] Guid id,
        IFormFile file,
        // Lets a component the file marks cancelled remove what it matches (issue #516 §8.4). It needs
        // calendar.delete, evaluated once here from the request principal: without it the whole import is a 403
        // before any byte is applied.
        [FromForm(Name = "allowRemovals")] bool allowRemovals = false,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return this.BadRequestProblem("An .ics file is required.");
        }

        if (!file.FileName.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return this.BadRequestProblem("The uploaded file must have a .ics extension.");
        }

        if (!CalendarIcsService.IsAcceptedContentType(file.ContentType))
        {
            return this.BadRequestProblem("The uploaded file must be a calendar file (text/calendar).");
        }

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        await using var stream = file.OpenReadStream();
        // Places link or are created only with locations.read + locations.create (issue #380 §8.4).
        // RequestAborted (the bound token) stops the import at the next batch boundary; committed batches stay.
        var result = await service.ImportAsync(
            id, stream, file.Length, file.ContentType, userId, cancellationToken, User.LocationClaims(),
            allowRemovals, canDelete: User.HasPermission(PermissionClaims.CalendarDelete));
        return Ok(result);
    }
}
