using System.Globalization;
using System.Security.Claims;
using System.Text;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Swashbuckle.AspNetCore.Annotations;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Avatar;

namespace Odyssey.Api.Controllers;

/// <summary>
/// Contact vCard import and export. Split out of <c>ContactsController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contacts")]
public sealed class ContactVCardController : ControllerBase
{
    private readonly ILogger<ContactVCardController> logger;
    private readonly ContactVCardService vCardService;
    private readonly ContactAuditLog audit;

    public ContactVCardController(
        ILogger<ContactVCardController> logger,
        ContactVCardService vCardService,
        ContactAuditLog audit)
    {
        this.logger = logger;
        this.vCardService = vCardService;
        this.audit = audit;
    }

    // ── vCard import/export (issue #338) ──────────────────────────────────────

    [HttpGet("{id}/vcard", Name = "ExportContactVCard")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [Produces("text/vcard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export a single contact as an RFC 6350 vCard 4.0 .vcf file.",
        Description = ExportDisclosure)]
    public async Task<IActionResult> ExportVCard(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery] [SwaggerParameter(Description = @"Embed the contact's image as a PHOTO (person) or
LOGO (organization) data URI. Opt-in and default false on BOTH export endpoints: flipping the
single-contact default would silently change what an existing caller receives from a shipped endpoint.")]
        bool includeImages = false,
        CancellationToken cancellationToken = default)
    {
        var export = await vCardService.ExportOneAsync(id, includeImages, cancellationToken);
        if (export is null)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        audit.VCardExported(User, rowCount: 1, filtered: false);
        return VCardFile(export);
    }

    /// <summary>
    /// What leaves the deployment when a contact is exported (issue #48 §10.11). Stated on the
    /// operation because issue #48 widened it: the file now also carries alternative names — which
    /// include maiden and former names — middle names and the lifecycle dates.
    /// </summary>
    private const string ExportDisclosure = @"The exported card carries the contact's names, aliases
(including maiden and former names, with their free-text labels), middle name, dates of birth and
death, an organization's establishment and dissolution dates, addresses, email addresses, phone
numbers and notes.";

    [HttpGet("vcard", Name = "ExportContactsVCard")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/vcard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export every contact matching the supplied filters as a multi-entry RFC 6350 vCard 4.0 .vcf file.",
        Description = "Omit all filters to export everything; pass the page's current filter state to export exactly the filtered set.\n\n"
            + ExportDisclosure)]
    public async Task<IActionResult> ExportVCards(
        [FromQuery] ContactsQueryParams query,
        [FromQuery] [SwaggerParameter(Description = @"Embed each contact's image. Opt-in, default false.
With images on, the output byte cap makes truncation the common case rather than a corner, so a
truncated response reports itself rather than returning a silently short document.")]
        bool includeImages = false,
        CancellationToken cancellationToken = default)
    {
        // Declared BEFORE the body starts, because a trailer that was not declared up front is dropped.
        // It is how the truncation below reaches the caller at all: X-Odyssey-Export-Rows has already
        // been flushed with the PROMISED count by the time truncation is known.
        if (Response.SupportsTrailers())
        {
            Response.DeclareTrailer(ExportTruncatedHeader);
            Response.DeclareTrailer(ExportDeliveredRowsHeader);
        }

        await vCardService.ExportManyStreamingAsync(query, Response.Body, (fileName, rowCount) =>
        {
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.ContentType = "text/vcard; charset=utf-8";
            var contentDisposition = new ContentDispositionHeaderValue("attachment");
            contentDisposition.SetHttpFileName(fileName);
            Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
            // Written before any body byte, per the completeness-signal contract (issue #343 §11):
            // Odyssey.ApiClient compares the parsed entry count in the downloaded body against this
            // header and treats a short count as a failed download rather than a smaller-but-valid one.
            Response.Headers["X-Odyssey-Export-Rows"] = rowCount.ToString(CultureInfo.InvariantCulture);
            audit.VCardExported(User, rowCount, filtered: HasAnyFilter(query));
        },
        includeImages,
        onTruncated: (deliveredRows, totalRows) =>
        {
            // A trailer rather than a header: by the time truncation is known the response headers are
            // long gone, and the document itself is not the place for it — a stray property outside a
            // VCARD block breaks strict parsers, and a fabricated card would be a fabricated contact.
            if (Response.SupportsTrailers())
            {
                Response.AppendTrailer(ExportTruncatedHeader, "true");
                Response.AppendTrailer(ExportDeliveredRowsHeader, deliveredRows.ToString(CultureInfo.InvariantCulture));
            }

            // The floor under the trailer, and the reason a truncation is never SILENT even where the
            // transport drops trailers: X-Odyssey-Export-Rows still promises `totalRows`, and the API
            // client's completeness check already treats a body carrying fewer BEGIN:VCARD markers than
            // the header promised as a failed download rather than a smaller-but-valid one.
            logger.LogWarning(
                "Contacts vCard export delivered {DeliveredRows} of {TotalRows} contact(s) before the "
                + "output cap; the response was reported as truncated.",
                deliveredRows, totalRows);
        },
        cancellationToken);

        return new EmptyResult();
    }

    /// <summary>Set when the export stopped at the output byte cap before every matched row was written.</summary>
    private const string ExportTruncatedHeader = "X-Odyssey-Export-Truncated";

    /// <summary>How many contacts the truncated document actually carries.</summary>
    private const string ExportDeliveredRowsHeader = "X-Odyssey-Export-Delivered-Rows";

    [HttpPost("vcard", Name = "ImportContactsVCard")]
    // Import can create or update depending on each vCard entry's UID match, so it requires BOTH
    // claims; stacked [Authorize] attributes are AND-combined (mirrors CalendarIcsController.Import).
    [Authorize(Policy = PermissionClaims.ContactsCreate)]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [Consumes("multipart/form-data")]
    // The transport-level cap is applied by ImportSizeLimitMiddleware from the configured, admin-
    // editable limit (issue #343 §5) — [RequestSizeLimit] is gone because it only raised the Kestrel
    // body limit, never the global multipart limit, so it never actually worked (§1, §5).
    [ImportSizeLimit(ImportSurface.Contacts)]
    [EnableRateLimiting(ImportExportRateLimiting.ImportConcurrencyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(VCardImportResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Import an RFC 6350 vCard 4.0 .vcf file, creating or updating contacts matched by ExternalUid.")]
    public async Task<IActionResult> ImportVCard(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return this.BadRequestProblem("A .vcf file is required.");
        }

        if (!file.FileName.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
        {
            return this.BadRequestProblem("The uploaded file must have a .vcf extension.");
        }

        if (!ContactVCardService.IsAcceptedContentType(file.ContentType))
        {
            return this.BadRequestProblem("The uploaded file must be a vCard file (text/vcard).");
        }

        await using var stream = file.OpenReadStream();
        var result = await vCardService.ImportAsync(stream, file.Length, file.ContentType, cancellationToken);
        return Ok(result);
    }

    private static bool HasAnyFilter(ContactsQueryParams query) =>
        !string.IsNullOrWhiteSpace(query.Search) || query.Types is { Length: > 0 } || query.Status is not null;

    // nosniff mirrors the file-download surface: the browser must not re-interpret the body as
    // anything other than the declared text/vcard (matches CalendarIcsController.Export).
    private IActionResult VCardFile(VCardExport export)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        var bytes = Encoding.UTF8.GetBytes(export.Content);
        return File(bytes, "text/vcard; charset=utf-8", export.FileName);
    }
}
