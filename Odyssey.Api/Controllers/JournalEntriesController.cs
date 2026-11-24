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
using Odyssey.Core.Journal.Interop;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Odyssey.Core.Identity;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/journal-entries")]
public sealed class JournalEntriesController : ControllerBase
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
    [EndpointSummary("List journal entries with search, tag/contact/date-range/attachment-presence filters, sort and paging.")]
    public async Task<IActionResult> Get(
        [FromQuery] JournalEntriesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await service.ListAsync(query, cancellationToken, User.LocationClaims());
        // The card states who last edited an entry, so the row needs the editor resolved too — both
        // ids go into the one batched lookup rather than a second round trip for the second column.
        var authorIds = result.Items.Select(i => i.CreatedByUserId)
            .Concat(result.Items.Select(i => i.UpdatedByUserId));
        var names = await displayNames.ResolveAsync(User, authorIds, cancellationToken);
        foreach (var item in result.Items)
        {
            item.CreatedByName = names.NameForAuthor(item.CreatedByUserId);
            item.UpdatedByName = names.NameForOptional(item.UpdatedByUserId);
            ApplyThumbnailRule(item.Photos);
        }

        return Ok(result);
    }

    [HttpGet("{id}", Name = "GetJournalEntry")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalEntry))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("Get a journal entry with its tag, contact, photo and attachment link IDs.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var entry = await service.Get(id, cancellationToken, User.LocationClaims());
        if (entry is null)
        {
            return this.NotFoundProblem($"Journal entry ID {id} not found.");
        }

        await EnrichAuthorsAsync(entry, cancellationToken);
        ApplyThumbnailRule(entry.Photos);
        return Ok(entry);
    }

    [HttpPost(Name = "PostJournalEntry")]
    [Authorize(Policy = PermissionClaims.JournalCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingJournalEntry))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [EndpointSummary("Create a journal entry.")]
    public async Task<IActionResult> Post(
        [FromBody] NewJournalEntry request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        if (request.AttachmentFileIds.Length > 0 && !CanLinkFiles())
        {
            return this.ForbiddenProblem("Linking files requires the files.read permission.");
        }

        // Linking a photo the entry does not hold needs photos.read (issue #428 §3.3), decided change-only by the service.
        var created = await service.Create(request, userId, cancellationToken, User.LocationClaims(), CanReadPhotos());
        await EnrichAuthorsAsync(created, cancellationToken);
        ApplyThumbnailRule(created.Photos);
        return CreatedAtRoute("GetJournalEntry", new { id = created.JournalEntryId }, created);
    }

    [HttpPut("{id}", Name = "PutJournalEntry")]
    [Authorize(Policy = PermissionClaims.JournalUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalEntry))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [EndpointSummary("Update a journal entry and replace its tag, contact, photo and attachment links.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateJournalEntry request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        if (request.AttachmentFileIds.Length > 0 && !CanLinkFiles())
        {
            return this.ForbiddenProblem("Linking files requires the files.read permission.");
        }

        var updated = await service.Update(id, request, userId, cancellationToken, User.LocationClaims(), CanReadPhotos());
        if (updated is null)
        {
            return this.NotFoundProblem($"Journal entry ID {id} not found.");
        }

        await EnrichAuthorsAsync(updated, cancellationToken);
        ApplyThumbnailRule(updated.Photos);
        return Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteJournalEntry")]
    [Authorize(Policy = PermissionClaims.JournalDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("Permanently delete a journal entry and its owned photo/attachment rows.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Journal entry ID {id} not found.");
    }

    // ---------------------------------------------------------------- VJOURNAL export/import (issue #339)

    // Single-entry export. The literal "vjournal" collection route below and this "{id}/vjournal" route
    // are distinct from the "{id}" GET; ASP.NET Core prefers the literal segment for the collection route. It streams
    // like the collection export, because the entry's attached files and photos travel inline.
    [HttpGet("{id}/vjournal", Name = "ExportJournalEntryVJournal")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    // One entry can carry gigabytes of files and photos, so it shares the collection export's permits and deadline.
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("Export a single journal entry as an RFC 5545 VJOURNAL .ics file, attached files and photos inline.")]
    public async Task<IActionResult> ExportSingleVJournal(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        if (await icsService.SummarizeExportAsync(new JournalEntriesQueryParams(), CanLinkFiles(), CanReadPhotos(), id, cancellationToken) is null)
        {
            return this.NotFoundProblem($"Journal entry ID {id} not found.");
        }

        return await StreamVJournalAsync(new JournalEntriesQueryParams(), id, cancellationToken);
    }

    // What the single-entry export would contain, under the same checks; 404 for an unknown entry.
    [HttpGet("{id}/vjournal/summary", Name = "SummarizeJournalEntryVJournalExport")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(JournalEntryIcsExportSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("Summarize what a single journal entry's VJOURNAL export would contain.")]
    public async Task<ActionResult<JournalEntryIcsExportSummary>> SummarizeSingleVJournalExport(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default) =>
        await icsService.SummarizeExportAsync(new JournalEntriesQueryParams(), CanLinkFiles(), CanReadPhotos(), id, cancellationToken)
            is { } summary
            ? summary
            : this.NotFoundProblem($"Journal entry ID {id} not found.");

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
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [EndpointSummary("Export journal entries matching the given filters as an RFC 5545 VJOURNAL .ics file, attached files and photos inline.")]
    public async Task<IActionResult> ExportVJournal(
        [FromQuery] JournalEntriesQueryParams query, CancellationToken cancellationToken = default) =>
        await StreamVJournalAsync(query, null, cancellationToken);

    // What the collection export would contain, under the same checks (entry cap, byte cap, files.read when an entry
    // carries an attached file, photos.read when it carries a photo). The client asks first, so a refusal is an
    // ordinary error and not a failed browser download.
    [HttpGet("vjournal/summary", Name = "SummarizeJournalEntriesVJournalExport")]
    [Authorize(Policy = PermissionClaims.JournalRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(JournalEntryIcsExportSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [EndpointSummary("Summarize what a VJOURNAL export of the filtered journal view would contain.")]
    public async Task<ActionResult<JournalEntryIcsExportSummary>> SummarizeVJournalExport(
        [FromQuery] JournalEntriesQueryParams query, CancellationToken cancellationToken = default) =>
        (await icsService.SummarizeExportAsync(query, CanLinkFiles(), CanReadPhotos(), null, cancellationToken))!;

    private async Task<IActionResult> StreamVJournalAsync(
        JournalEntriesQueryParams query, Guid? onlyId, CancellationToken cancellationToken)
    {
        try
        {
            await icsService.ExportStreamingAsync(query, CanReadContacts(), Response.Body, (fileName, rowCount) =>
            {
                Response.Headers.XContentTypeOptions = "nosniff";
                Response.ContentType = "text/calendar; charset=utf-8";
                var contentDisposition = new ContentDispositionHeaderValue("attachment");
                contentDisposition.SetHttpFileName(fileName);
                Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
                Response.Headers["X-Odyssey-Export-Rows"] = rowCount.ToString(CultureInfo.InvariantCulture);
            }, cancellationToken, includePlaceNames: User.HasPermission(PermissionClaims.LocationsRead),
                canReadFiles: CanLinkFiles(), canReadPhotos: CanReadPhotos(), onlyId: onlyId);
        }
        catch (IcsExportSizeExceededException)
        {
            // The body has started, so no status can be sent: abort, so the download fails rather than ending early.
            HttpContext.Abort();
        }

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
    [EndpointSummary("Import an RFC 5545 VJOURNAL .ics file into the shared journal-entries board.")]
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

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        await using var stream = file.OpenReadStream();
        var result = await icsService.ImportAsync(
            stream, file.Length, file.ContentType, userId, CanLinkFiles(), CanReadContacts(), cancellationToken,
            User.LocationClaims(), CanReadPhotos(),
            canCreateFiles: User.HasPermission(PermissionClaims.FilesCreate),
            canCreatePhotos: User.HasPermission(PermissionClaims.PhotosCreate));
        return Ok(result);
    }

    /// <summary>
    /// The journal rule (issue #362 §7.3), in one place for every action that returns a photo link: a caller without
    /// <c>photos.read</c> never sees <c>ThumbnailsReady</c> true, so its client never requests a thumbnail it would be
    /// refused, nor the library photo's favourite and archive state, which is photo data. Core services carry no
    /// <c>ClaimsPrincipal</c>, so the rule lives at the controller.
    /// </summary>
    private void ApplyThumbnailRule(IEnumerable<JournalEntryPhotoDto>? photos)
    {
        if (photos is null || User.HasPermission(PermissionClaims.PhotosRead))
        {
            return;
        }

        foreach (var photo in photos)
        {
            photo.ThumbnailsReady = false;
            photo.Favourited = null;
            photo.Archived = null;
        }
    }

    private async Task EnrichAuthorsAsync(ExistingJournalEntry entry, CancellationToken cancellationToken)
    {
        var names = await displayNames.ResolveAsync(User, [entry.CreatedByUserId, entry.UpdatedByUserId], cancellationToken);
        entry.CreatedByName = names.NameForAuthor(entry.CreatedByUserId);
        entry.UpdatedByName = names.NameForOptional(entry.UpdatedByUserId);
    }

    private bool CanLinkFiles() => User.HasPermission(PermissionClaims.FilesRead);

    private bool CanReadPhotos() => User.HasPermission(PermissionClaims.PhotosRead);

    private bool CanReadContacts() => User.HasPermission(PermissionClaims.ContactsRead);
}
