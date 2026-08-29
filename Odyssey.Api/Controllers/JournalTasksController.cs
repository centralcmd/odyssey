using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Odyssey.Api.Identity;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public class JournalTasksController : ControllerBase
{
    private readonly ILogger<JournalTasksController> logger;
    private readonly JournalTaskService service;
    private readonly TaskIcsService icsService;
    private readonly IUserDisplayNameResolver displayNames;

    public JournalTasksController(
        ILogger<JournalTasksController> logger,
        JournalTaskService service,
        TaskIcsService icsService,
        IUserDisplayNameResolver displayNames)
    {
        this.logger = logger;
        this.service = service;
        this.icsService = icsService;
        this.displayNames = displayNames;
    }

    [HttpGet(Name = "GetTasks")]
    [Authorize(Policy = PermissionClaims.TasksRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<JournalTaskSummary>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List tasks (search/tag/status filter, sort, paginate).")]
    public async Task<IActionResult> Get(
        [FromQuery] JournalTasksQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await service.ListAsync(query, cancellationToken);
        var names = await displayNames.ResolveAsync(User, result.Items.Select(i => i.CreatedByUserId), cancellationToken);
        foreach (var item in result.Items)
        {
            item.CreatedByName = names.NameForAuthor(item.CreatedByUserId);
        }

        return Ok(result);
    }

    [HttpGet("{id}", Name = "GetTask")]
    [Authorize(Policy = PermissionClaims.TasksRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalTask))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a task with its tag IDs and attachment metadata.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var item = await service.Get(id, cancellationToken);
        if (item is null)
        {
            return this.NotFoundProblem($"Task ID {id} not found.");
        }

        await EnrichAuthorsAsync(item, cancellationToken);
        return Ok(item);
    }

    [HttpPost(Name = "PostTask")]
    [Authorize(Policy = PermissionClaims.TasksCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingJournalTask))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a task.")]
    public async Task<IActionResult> Post(
        [FromBody] NewJournalTask request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        if (request.AttachmentFileIds.Length > 0 && !CanLinkFiles())
        {
            return this.ForbiddenProblem("Linking files requires the files.read permission.");
        }

        var created = await service.Create(request, userId, cancellationToken);
        await EnrichAuthorsAsync(created, cancellationToken);
        return CreatedAtRoute("GetTask", new { id = created.JournalTaskId }, created);
    }

    [HttpPut("{id}", Name = "PutTask")]
    [Authorize(Policy = PermissionClaims.TasksUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalTask))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a task's fields, links, lifecycle status and board position.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateJournalTask request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        if (request.AttachmentFileIds.Length > 0 && !CanLinkFiles())
        {
            return this.ForbiddenProblem("Linking files requires the files.read permission.");
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Task ID {id} not found.");
        }

        await EnrichAuthorsAsync(updated, cancellationToken);
        return Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteTask")]
    [Authorize(Policy = PermissionClaims.TasksDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a task (cascades its owned attachment links).")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Task ID {id} not found.");
    }

    // VTODO/.ics export (issue #337). Literal "ics" segment takes precedence over the {id} route above
    // (ASP.NET Core prefers literal over parameter segments), mirroring CalendarIcsController's {id}/ics.
    [HttpGet("ics", Name = "ExportTasksIcs")]
    [Authorize(Policy = PermissionClaims.TasksRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export the current filtered task view as an RFC 5545 VTODO .ics file.")]
    public async Task<IActionResult> ExportIcs(
        [FromQuery] JournalTasksQueryParams query, CancellationToken cancellationToken = default)
    {
        await icsService.ExportStreamingAsync(query, Response.Body, (fileName, rowCount) =>
        {
            // nosniff mirrors the file-download surface: the browser must not re-interpret the body as
            // anything other than the declared text/calendar (matches CalendarIcsController's export headers).
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.ContentType = "text/calendar; charset=utf-8";
            var contentDisposition = new ContentDispositionHeaderValue("attachment");
            contentDisposition.SetHttpFileName(fileName);
            Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
            Response.Headers["X-Odyssey-Export-Rows"] = rowCount.ToString(CultureInfo.InvariantCulture);
        }, cancellationToken);

        return new EmptyResult();
    }

    // VTODO/.ics import (issue #337). Creates or updates by UID match, so it requires BOTH claims;
    // stacked [Authorize] attributes are AND-combined.
    [HttpPost("ics", Name = "ImportTasksIcs")]
    [Authorize(Policy = PermissionClaims.TasksCreate)]
    [Authorize(Policy = PermissionClaims.TasksUpdate)]
    [Consumes("multipart/form-data")]
    [ImportSizeLimit(ImportSurface.Tasks)]
    [EnableRateLimiting(ImportExportRateLimiting.ImportConcurrencyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TaskIcsImportResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Import an RFC 5545 VTODO .ics file into the shared task board.")]
    public async Task<IActionResult> ImportIcs(
        IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return this.BadRequestProblem("An .ics file is required.");
        }

        if (!file.FileName.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return this.BadRequestProblem("The uploaded file must have a .ics extension.");
        }

        if (!TaskIcsService.IsAcceptedContentType(file.ContentType))
        {
            return this.BadRequestProblem("The uploaded file must be a calendar file (text/calendar).");
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return this.UnauthorizedProblem("User identity is missing from the request.");
        }

        await using var stream = file.OpenReadStream();
        var result = await icsService.ImportAsync(
            stream, file.Length, file.ContentType, userId, CanLinkFiles(), cancellationToken);
        return Ok(result);
    }

    private async Task EnrichAuthorsAsync(ExistingJournalTask task, CancellationToken cancellationToken)
    {
        var names = await displayNames.ResolveAsync(User, [task.CreatedByUserId, task.UpdatedByUserId], cancellationToken);
        task.CreatedByName = names.NameForAuthor(task.CreatedByUserId);
        task.UpdatedByName = names.NameForOptional(task.UpdatedByUserId);
    }

    private bool CanLinkFiles() => User.HasClaim(PermissionClaims.Type, PermissionClaims.FilesRead);
}
