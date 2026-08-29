using System.Security.Claims;
using System.Text;
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
/// ICS (RFC 5545) import/export for a calendar (issue #330). Shares the <c>api/calendars</c> prefix
/// with <see cref="CalendarsController"/> (same sub-resource convention as e.g. AccountTerms). Export
/// is a plain GET; import is a POST to the same URI with a multipart body.
/// </summary>
[ApiController]
[Route("api/calendars")]
public class CalendarIcsController : ControllerBase
{
    private readonly CalendarIcsService service;

    public CalendarIcsController(CalendarIcsService service)
    {
        this.service = service;
    }

    [HttpGet("{id}/ics", Name = "ExportCalendarIcs")]
    [Authorize(Policy = PermissionClaims.CalendarRead)]
    // Reachable at User tier via CalendarRead alone, and — unlike the other four Goal 8 surfaces —
    // fully-buffered with no cap or chunking (issue #343 §2 Non-Goal 9, out of scope, tracked
    // separately under #340). It still carries the same availability controls as the four in-scope
    // surfaces because those bound an instance-wide resource risk this feature introduces regardless
    // of whether this endpoint's own row count is capped (§5, §10 item 5, sec Z1).
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export a calendar's events as an RFC 5545 .ics file.")]
    public async Task<IActionResult> Export(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var export = await service.ExportAsync(id, cancellationToken);
        if (export is null)
        {
            return this.NotFoundProblem($"Calendar ID {id} not found.");
        }

        // nosniff mirrors the file-download surface: the browser must not re-interpret the body as
        // anything other than the declared text/calendar. File(...) sets an attachment disposition
        // with the (framework-sanitized) filename.
        Response.Headers.XContentTypeOptions = "nosniff";
        var bytes = Encoding.UTF8.GetBytes(export.Content);
        return File(bytes, "text/calendar; charset=utf-8", export.FileName);
    }

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
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Import an RFC 5545 .ics file into an existing calendar.")]
    public async Task<IActionResult> Import(
        [FromRoute(Name = "id")] Guid id,
        IFormFile file,
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

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return this.UnauthorizedProblem("User identity is missing from the request.");
        }

        await using var stream = file.OpenReadStream();
        var result = await service.ImportAsync(id, stream, file.Length, file.ContentType, userId, cancellationToken);
        return Ok(result);
    }
}
