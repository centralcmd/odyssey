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
[Route("api/journal-entries")]
public class JournalEntriesController : ControllerBase
{
    private readonly ILogger<JournalEntriesController> logger;
    private readonly JournalEntryService service;
    private readonly JournalEntryIcsService icsService;
    private readonly IUserDisplayNameResolver displayNames;

    public JournalEntriesController(
        ILogger<JournalEntriesController> logger,
        JournalEntryService service,
        JournalEntryIcsService icsService,
        IUserDisplayNameResolver displayNames)
    {
        this.logger = logger;
        this.service = service;
        this.icsService = icsService;
        this.displayNames = displayNames;
    }

    [HttpGet(Name = "GetJournalEntries")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<JournalEntrySummary>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List journal entries with search, tag/contact/date-range filters, sort and paging.")]
    public async Task<IActionResult> Get(
        [FromQuery] JournalEntriesQueryParams query,
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

    [HttpGet("{id}", Name = "GetJournalEntry")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalEntry))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a journal entry with its tag, contact, photo and attachment link IDs.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var entry = await service.Get(id, cancellationToken);
        if (entry is null)
        {
            return this.NotFoundProblem($"Journal entry ID {id} not found.");
        }

        await EnrichAuthorsAsync(entry, cancellationToken);
        return Ok(entry);
    }

    [HttpPost(Name = "PostJournalEntry")]
    [Authorize(Policy = PermissionClaims.JournalCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingJournalEntry))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a journal entry.")]
    public async Task<IActionResult> Post(
        [FromBody] NewJournalEntry request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        if (LinksFiles(request.PhotoFileIds, request.AttachmentFileIds) && !CanLinkFiles())
        {
            return this.ForbiddenProblem("Linking files requires the files.read permission.");
        }

        var created = await service.Create(request, userId, cancellationToken);
        await EnrichAuthorsAsync(created, cancellationToken);
        return CreatedAtRoute("GetJournalEntry", new { id = created.JournalEntryId }, created);
    }

    [HttpPut("{id}", Name = "PutJournalEntry")]
    [Authorize(Policy = PermissionClaims.JournalUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalEntry))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a journal entry and replace its tag, contact, photo and attachment links.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateJournalEntry request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        if (LinksFiles(request.PhotoFileIds, request.AttachmentFileIds) && !CanLinkFiles())
        {
            return this.ForbiddenProblem("Linking files requires the files.read permission.");
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Journal entry ID {id} not found.");
        }

        await EnrichAuthorsAsync(updated, cancellationToken);
        return Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteJournalEntry")]
    [Authorize(Policy = PermissionClaims.JournalDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Permanently delete a journal entry and its owned photo/attachment rows.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Journal entry ID {id} not found.");
    }

    // ---------------------------------------------------------------- VJOURNAL export/import (issue #339)

    // Single-entry export. The literal "vjournal" collection route below and this "{id}/vjournal" route
    // are distinct from the "{id}" GET; ASP.NET Core prefers the literal segment for the collection route.
    [HttpGet("{id}/vjournal", Name = "ExportJournalEntryVJournal")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export a single journal entry as an RFC 5545 VJOURNAL .ics file.")]
    public async Task<IActionResult> ExportSingleVJournal(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var export = await icsService.ExportSingleAsync(id, CanReadContacts(), cancellationToken);
        if (export is null)
        {
            return this.NotFoundProblem($"Journal entry ID {id} not found.");
        }

        return IcsFile(export);
    }

    // Collection export of the current filtered view (or everything). Unlike the list endpoint, archived
    // entries are included when no status is supplied (§5). A matched set larger than the export cap is a
    // 400 carrying the machine-readable "code" discriminator (§11), surfaced by the global handler.
    [HttpGet("vjournal", Name = "ExportJournalEntriesVJournal")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export journal entries matching the given filters as an RFC 5545 VJOURNAL .ics file.")]
    public async Task<IActionResult> ExportVJournal(
        [FromQuery] JournalEntriesQueryParams query, CancellationToken cancellationToken = default)
    {
        await icsService.ExportStreamingAsync(query, CanReadContacts(), Response.Body, (fileName, rowCount) =>
        {
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.ContentType = "text/calendar; charset=utf-8";
            var contentDisposition = new ContentDispositionHeaderValue("attachment");
            contentDisposition.SetHttpFileName(fileName);
            Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
            Response.Headers["X-Odyssey-Export-Rows"] = rowCount.ToString(CultureInfo.InvariantCulture);
        }, cancellationToken);

        return new EmptyResult();
    }

    // VJOURNAL import. Creates or updates by UID match, so it requires BOTH claims; stacked [Authorize]
    // attributes are AND-combined. contacts.read / files.read are evaluated per-reference inside the
    // service (never a hard gate on the endpoint), so a caller lacking them can still import.
    [HttpPost("vjournal", Name = "ImportJournalEntriesVJournal")]
    [Authorize(Policy = PermissionClaims.JournalCreate)]
    [Authorize(Policy = PermissionClaims.JournalUpdate)]
    [Consumes("multipart/form-data")]
    [ImportSizeLimit(ImportSurface.JournalEntries)]
    [EnableRateLimiting(ImportExportRateLimiting.ImportConcurrencyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(JournalEntryIcsImportResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Import an RFC 5545 VJOURNAL .ics file into the shared journal-entries board.")]
    public async Task<IActionResult> ImportVJournal(
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

        if (!JournalEntryIcsService.IsAcceptedContentType(file.ContentType))
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
            stream, file.Length, file.ContentType, userId, CanLinkFiles(), CanReadContacts(), cancellationToken);
        return Ok(result);
    }

    // nosniff mirrors the file-download surface: the browser must not re-interpret the body as anything
    // other than the declared text/calendar (matches the Calendar/Task export headers).
    private FileContentResult IcsFile(JournalEntryIcsExport export)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(Encoding.UTF8.GetBytes(export.Content), "text/calendar; charset=utf-8", export.FileName);
    }

    private async Task EnrichAuthorsAsync(ExistingJournalEntry entry, CancellationToken cancellationToken)
    {
        var names = await displayNames.ResolveAsync(User, [entry.CreatedByUserId, entry.UpdatedByUserId], cancellationToken);
        entry.CreatedByName = names.NameForAuthor(entry.CreatedByUserId);
        entry.UpdatedByName = names.NameForOptional(entry.UpdatedByUserId);
    }

    private static bool LinksFiles(Guid[] photoFileIds, Guid[] attachmentFileIds) =>
        photoFileIds.Length > 0 || attachmentFileIds.Length > 0;

    private bool CanLinkFiles() => User.HasClaim(PermissionClaims.Type, PermissionClaims.FilesRead);

    private bool CanReadContacts() => User.HasClaim(PermissionClaims.Type, PermissionClaims.ContactsRead);
}
