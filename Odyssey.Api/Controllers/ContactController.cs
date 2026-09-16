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

[ApiController]
[Route("api/contacts")]
public class ContactController : ControllerBase
{
    private readonly ILogger<ContactController> logger;
    private readonly ContactService contactService;
    private readonly ContactVCardService vCardService;
    private readonly ContactAvatarService avatarService;
    private readonly IContactReferenceGuard referenceGuard;

    public ContactController(
        ILogger<ContactController> logger,
        ContactService contactService,
        ContactVCardService vCardService,
        ContactAvatarService avatarService,
        IContactReferenceGuard referenceGuard)
    {
        this.logger = logger;
        this.contactService = contactService;
        this.vCardService = vCardService;
        this.avatarService = avatarService;
        this.referenceGuard = referenceGuard;
    }

    /// <summary>The house claim check — the same shape PhotosController and JournalEntriesController use.</summary>
    private bool HasClaim(string claimValue) => User.HasClaim(PermissionClaims.Type, claimValue);

    private string ActorUserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

    /// <summary>
    /// A structured, <b>value-free</b> audit event for a change to the personal data issue #48 adds
    /// (§10.9). <c>Contact.UpdatedAt</c> records <i>that</i> something changed and never <i>who</i> or
    /// <i>what</i>, which cannot answer "who recorded this?" or, after an incident, "whose maiden
    /// names were read?" — precisely what GDPR Art. 33 breach scoping and Art. 5(2) accountability
    /// require.
    ///
    /// <para>
    /// It carries the actor, the contact id and the action, and <b>never the alias value, the label
    /// or the date</b>, so §10.6's no-echo rule is unchanged. It lives in the controller because the
    /// domain service has no <c>ClaimsPrincipal</c>.
    /// </para>
    /// </summary>
    private void AuditContactChange(Guid contactId, string action) =>
        logger.LogInformation(
            "Contact {ContactId} {Action} by {ActorUserId}.", contactId, action, ActorUserId);

    /// <summary>
    /// The bulk-read counterpart (§10.11). A <c>contacts.read</c> holder — <b>Guest included</b> — can
    /// download the whole corpus, maiden names and dates of death with it, in one request. Row count
    /// and whether filters were applied; no names, no values.
    /// </summary>
    private void AuditVCardExport(int rowCount, bool filtered) =>
        logger.LogInformation(
            "Contacts vCard export of {RowCount} contact(s) ({Scope}) by {ActorUserId}.",
            rowCount, filtered ? "filtered" : "all", ActorUserId);

    [HttpGet(Name = "GetContacts")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingContact>))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List contacts.", Description = @"List contacts with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] ContactsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await contactService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}", Name = "GetContact")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContact))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the contact to get.")] Guid id, CancellationToken cancellationToken = default)
    {
        var contact = await contactService.Get(id, cancellationToken);
        if (contact is null)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        return Ok(contact);
    }

    [HttpPost(Name = "PostContact")]
    [Authorize(Policy = PermissionClaims.ContactsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewContact", Required = true,
            Description = @"The new contact to create.")] NewContact newContact, CancellationToken cancellationToken = default)
    {
        var contact = await contactService.Create(newContact, cancellationToken);
        if (contact.PersonDetails?.DateOfDeath is not null)
        {
            AuditContactChange(contact.ContactId, "dateOfDeath.set");
        }

        return CreatedAtRoute("GetContact", new { id = contact.ContactId }, "");
    }

    [HttpPut("{id}", Name = "PutContact")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the contact to update.")] Guid id,
        [FromBody] [SwaggerParameter("NewContact", Required = true,
            Description = @"The contact with the updated values.")] NewContact newContact, CancellationToken cancellationToken = default)
    {
        // Read BEFORE the write: the audit event distinguishes recording a death from clearing one,
        // and only the prior value can say which happened (§10.9). Value-free either way — the event
        // names the transition, never the date.
        var before = (await contactService.Get(id, cancellationToken))?.PersonDetails?.DateOfDeath;

        var contact = await contactService.Update(id, newContact, cancellationToken);
        if (contact is null)
        {
            return await Post(newContact, cancellationToken);
        }

        var after = contact.PersonDetails?.DateOfDeath;
        if (before != after)
        {
            AuditContactChange(id, after is null ? "dateOfDeath.cleared" : "dateOfDeath.set");
        }

        return NoContent();
    }

    [HttpDelete("{id}", Name = "DeleteContact")]
    [Authorize(Policy = PermissionClaims.ContactsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DetachedInsuranceLinks))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a contact.",
        Description = @"409 when the contact is named as an insurer, an insured contact or a beneficiary
on any insurance policy. With detachInsuranceLinks=true those link rows are removed and the contact
deleted in ONE transaction, which needs insurance.update in addition to contacts.delete; the response is
then 200 with a summary of what was destroyed.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the contact to delete.")] Guid id,
        [FromQuery] [SwaggerParameter(Description = @"Remove the contact's insurance link rows and delete
it in one transaction, instead of refusing with a 409. Requires insurance.update.")] bool detachInsuranceLinks = false,
        CancellationToken cancellationToken = default)
    {
        if (detachInsuranceLinks)
        {
            // Composed from two existing claims rather than a third one — no RolePermissions change, no
            // role-claim reconciliation, no sign-out/sign-in. A caller holding only contacts.delete gets
            // a 403 here, never a silent downgrade to the refused delete.
            if (!HasClaim(PermissionClaims.InsuranceUpdate))
            {
                return this.ForbiddenProblem(
                    "Detaching insurance links requires permission to update insurance policies.");
            }

            var detached = await contactService.Delete(id, detachInsuranceLinks: true, cancellationToken);
            if (detached is null)
            {
                // The contact did not exist; nothing was detached and nothing was deleted.
                return NoContent();
            }

            // Ids only, never names — the caller asked to erase a contact. This one line exists because
            // the detach path's blast radius (every link across every policy, in one request) is
            // materially larger than an ordinary per-policy edit; it is NOT an audit trail and §10 #12
            // does not claim it is.
            logger.LogInformation(
                "Detached {LinkCount} insurance link(s) across {PolicyCount} policy/policies for contact {ContactId} ({Kinds}) and deleted the contact.",
                detached.TotalLinks,
                detached.AffectedPolicyIds.Count,
                id,
                string.Join(", ", detached.Kinds.Select(k => $"{k.Kind}={k.Count}")));

            return Ok(detached);
        }

        // The claim conditional lives HERE, not in the service: DomainConflictException carries a
        // message and nothing else, and the domain service has no ClaimsPrincipal — so neither it nor
        // GlobalExceptionHandler could shape a claim-conditional payload. The service keeps its own
        // unconditional guard as defence-in-depth for non-HTTP callers.
        var blockers = await referenceGuard.GetInsuranceLinkBlockersAsync(id, cancellationToken);
        if (blockers.Any)
        {
            var canReadInsurance = HasClaim(PermissionClaims.InsuranceRead);
            var payload = new ContactInsuranceLinkBlockers
            {
                Kinds = [.. blockers.Kinds],
                TotalLinks = blockers.TotalLinks,
                PolicyCount = blockers.Policies.Count,
                // Names and ids only for a caller that could read them from the insurance surface
                // anyway. The boundary costs nothing today (every shipped role holding contacts.delete
                // also holds insurance.read, asserted by a guard test) and is kept for a future role.
                Policies = canReadInsurance ? [.. blockers.Policies] : [],
            };

            return this.ConflictProblem(
                "This contact is named on one or more insurance policies and cannot be deleted. "
                + "Retry with detachInsuranceLinks=true to remove those links and delete it in one "
                + "transaction, or remove it from those policies first.",
                new Dictionary<string, object?> { ["insuranceLinks"] = payload });
        }

        await contactService.Delete(id, detachInsuranceLinks: false, cancellationToken);
        return NoContent();
    }

    // ── Contact image (issue #86 §7) ──────────────────────────────────────────
    //
    // Three endpoints, gated by ONE claim each — contacts.read to look at a contact's image,
    // contacts.update to set or clear it. Deliberately NOT also files.read / files.create /
    // files.delete.
    //
    // The read side: the image IS contact data, and the endpoint takes no file id, so the claim buys
    // that contact's image and nothing else. Requiring files.read as well would mirror, on the read
    // path, exactly the claim-coupling §10.4 refuses.
    //
    // The write side rests on BOUNDEDNESS, not on consistency. What contacts.update confers here is
    // "<= 2 MB, one of three allow-listed still image types, magic-checked, <= 1024 square,
    // non-animated, stripped and re-validated, with a server-generated filename and description, at one
    // row per contact" — a far weaker primitive than files.create, which admits PDFs, Office documents
    // and archives at the global cap with caller-controlled metadata.
    //
    // THAT IS AN INVARIANT, NOT A ONE-TIME JUDGEMENT (§10.17): a later change that widens what an
    // avatar upload may be re-opens the claim-gating question and does not inherit this answer.

    [HttpGet("{id}/avatar", Name = "GetContactAvatar")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Stream a contact's image.",
        Description = @"404 when the contact does not exist, has no image, or its stored content type is
not a permitted contact image. Served inline with a strong ETag and no-cache, so a replaced or deleted
image is never served from a stale cache.")]
    public async Task<IActionResult> GetAvatar(
        [FromRoute(Name = "id")] Guid id,
        CancellationToken cancellationToken = default)
    {
        // Metadata only — no Include(fm => fm.FileBlob). no-cache makes revalidation the hot path, so
        // materialising the LONGBLOB to answer a conditional request would make every return visit pay
        // for a response that carries no body.
        var descriptor = await avatarService.GetDescriptorAsync(id, cancellationToken);
        if (descriptor is null)
        {
            return this.NotFoundProblem($"Contact ID {id} has no image.");
        }

        ApplyAvatarHeaders(descriptor.ContentType);

        var etag = new EntityTagHeaderValue($"\"{descriptor.Sha256Hash}\"");
        if (IfNoneMatches(etag))
        {
            // The 304 that never reads the blob — the figure that matters most under no-cache.
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var content = await avatarService.GetContentAsync(descriptor.FileId, cancellationToken);
        if (content is null)
        {
            return this.NotFoundProblem($"Contact ID {id} has no image.");
        }

        // The FileResult overload that TAKES an EntityTagHeaderValue, so ASP.NET Core performs
        // conditional-request handling itself. A manually assigned Response.Headers.ETag — the shape
        // FilesController.DownloadFile uses — does not engage that machinery at all, so copying it
        // would return 200 every time and the 304 above would be the only one that ever fired.
        return File(content, descriptor.ContentType, lastModified: null, entityTag: etag);
    }

    [HttpPost("{id}/avatar", Name = "PostContactAvatar")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [Consumes("multipart/form-data")]
    // The transport gate resolves the GLOBAL cap (64 MB ceiling), not this surface's min — so it is the
    // coarse gate and the action's own check is the binding one.
    [UploadSizeLimit]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContact))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Attach or replace a contact's image.",
        Description = @"Takes the image BYTES as multipart/form-data under the part name 'file' — never a
fileId. A caller-supplied id would let a contacts.update holder point a contact at any row in the file
store and read its bytes back through the contacts.read-gated endpoint above.

The bytes are validated against a narrower allow-list than a general upload (PNG, JPEG or WebP; magic
bytes checked; at most 1024 x 1024; not animated), stripped of all embedded metadata and re-validated
before storage. The previous image's file is released in the SAME transaction.")]
    public async Task<IActionResult> PostAvatar(
        [FromRoute(Name = "id")] Guid id,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        // Only `file` is bound. Any other form field — a fileId, a nested file object — is not a
        // parameter of this action and reaches nothing (AC 15).
        if (file is null || file.Length == 0)
        {
            return this.BadRequestProblem("An image file is required.");
        }

        var effectiveMaxBytes = await avatarService.GetEffectiveMaxBytesAsync(cancellationToken);
        if (file.Length > effectiveMaxBytes)
        {
            // Rejected before the body is buffered into memory, and the message names the number
            // actually in force rather than a compiled-in one.
            return this.BadRequestProblem(
                $"The image must be {FormatMegabytes(effectiveMaxBytes)} or smaller. "
                + "Crop a smaller area, or choose a different file.");
        }

        var bytes = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        // The RAW claim, not ActorUserId: that falls back to the literal "unknown" for audit lines, and
        // FileMetadata.UploadedByUserId is a real foreign key to AspNetUsers — so storing the fallback
        // would fail the constraint and surface as a 500. An absent claim stores NULL, which is the
        // healthy state the column and its ON DELETE SET NULL are already built around.
        var attached = await avatarService.AttachAsync(
            id, bytes, file.ContentType, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);

        if (!attached)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        AuditContactChange(id, "image set");

        var contact = await contactService.Get(id, cancellationToken);
        return contact is null ? this.NotFoundProblem($"Contact ID {id} not found.") : Ok(contact);
    }

    [HttpDelete("{id}/avatar", Name = "DeleteContactAvatar")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove a contact's image.",
        Description = @"Deletes the underlying file as well as the reference — the targeted route for a
GDPR Art. 17 erasure request — unless the file is not a permitted contact image, in which case only the
reference is released and the file is left for an operator to investigate. The contact itself is
unchanged: it is not archived, and it falls back to its type glyph.")]
    public async Task<IActionResult> DeleteAvatar(
        [FromRoute(Name = "id")] Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!await avatarService.RemoveAsync(id, cancellationToken))
        {
            return this.NotFoundProblem($"Contact ID {id} has no image.");
        }

        AuditContactChange(id, "image removed");
        return NoContent();
    }

    /// <summary>
    /// The response headers for the product's <b>first inline-served untrusted-bytes surface</b> — every
    /// other download is forced <c>attachment</c>, which is why this carries headers the Files endpoint
    /// does not.
    ///
    /// <para>
    /// <b><c>Cross-Origin-Resource-Policy: same-site</c>, not <c>same-origin</c>.</b> The API is consumed
    /// cross-origin by design — CORS runs with <c>AllowCredentials()</c> and a production origin list,
    /// and the dev client serves from 5199 while the API listens on 5188. CORP is enforced on no-cors
    /// subresource loads, which is exactly how an <c>&lt;img src&gt;</c> loads, and it compares scheme,
    /// host <b>and port</b>. <c>same-origin</c> would therefore have blocked every avatar outside
    /// Docker — and silently, since a load failure degrades to the type glyph with nothing surfaced.
    /// </para>
    ///
    /// <para>
    /// <c>no-cache</c> (revalidate every time), not <c>no-store</c>: it is what makes revocation and
    /// erasure immediate — a deleted image 404s on the next revalidation rather than hiding behind a
    /// cached 200 — while still allowing the 304 that carries no body.
    /// </para>
    /// </summary>
    private void ApplyAvatarHeaders(string contentType)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentDisposition = "inline";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";
        Response.Headers.CacheControl = "private, no-cache";
        Response.ContentType = contentType;
    }

    /// <summary>
    /// Whether the request's <c>If-None-Match</c> matches the avatar's strong <c>ETag</c>. Checked here,
    /// before the blob query, so a revalidation costs the metadata read and nothing else.
    /// </summary>
    private bool IfNoneMatches(EntityTagHeaderValue etag)
    {
        var candidates = Request.GetTypedHeaders().IfNoneMatch;
        if (candidates is null || candidates.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: true))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatMegabytes(long bytes) =>
        $"{Math.Round(bytes / (1024d * 1024d), 1)} MB";

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

        AuditVCardExport(rowCount: 1, filtered: false);
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
            AuditVCardExport(rowCount, filtered: HasAnyFilter(query));
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

    // ── Aliases (issue #48 §7) ────────────────────────────────────────────────
    // Four sub-resource actions gated by the SIBLING claims — contacts.read/.create/.update/.delete.
    // No new claim, so no RolePermissions change, no RoleClaimSeeder reconciliation and no forced
    // sign-out/sign-in (§10.8).
    //
    // Containment holds on all four verbs: the service resolves an aliasId SCOPED to contactId, so an
    // alias belonging to another contact is a 404 — not a 403, which would confirm the row exists
    // under a different parent (ASVS V4.1.5, WSTG-ATHZ-04).

    [HttpGet("{contactId}/aliases", Name = "GetContactAliases")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingContactAlias>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List a contact's alternative names.")]
    public async Task<IActionResult> GetAliases([FromRoute] Guid contactId, CancellationToken cancellationToken = default)
    {
        var aliases = await contactService.GetAliases(contactId, cancellationToken);
        return aliases is null ? this.NotFoundProblem($"Contact ID {contactId} not found.") : Ok(aliases);
    }

    [HttpPost("{contactId}/aliases", Name = "PostContactAlias")]
    [Authorize(Policy = PermissionClaims.ContactsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContactAlias))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add one alternative name to a contact.",
        Description = @"409 when the contact already carries that value (compared case- AND
accent-insensitively, on the value alone — two labels cannot smuggle in a second ""Hansen""); 422 when
the contact is already at its 32-alias cap. Both name the field `value` in the problem-details
`errors` dictionary, and neither echoes the submitted value or label.")]
    public async Task<IActionResult> PostAlias(
        [FromRoute] Guid contactId, [FromBody] NewContactAlias request, CancellationToken cancellationToken = default)
    {
        var created = await contactService.CreateAlias(contactId, request, cancellationToken);
        if (created is null)
        {
            return this.NotFoundProblem($"Contact ID {contactId} not found.");
        }

        AuditContactChange(contactId, "alias.created");
        // CreatedAtRoute points at the collection: the siblings expose no per-item GET either.
        return CreatedAtRoute("GetContactAliases", new { contactId }, created);
    }

    [HttpPut("{contactId}/aliases/{aliasId}", Name = "PutContactAlias")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace one of a contact's alternative names.",
        Description = @"A replace, not a patch: an omitted or blank `label` CLEARS a previously-set
one, matching the sibling sub-resources. There is deliberately no 422 here — a replace cannot grow the
collection, so the cap is unreachable on this verb.")]
    public async Task<IActionResult> PutAlias(
        [FromRoute] Guid contactId, [FromRoute] Guid aliasId, [FromBody] NewContactAlias request,
        CancellationToken cancellationToken = default)
    {
        var updated = await contactService.UpdateAlias(contactId, aliasId, request, cancellationToken);
        if (!updated)
        {
            return this.NotFoundProblem($"Alias ID {aliasId} is not attached to contact ID {contactId}.");
        }

        AuditContactChange(contactId, "alias.updated");
        return NoContent();
    }

    [HttpDelete("{contactId}/aliases/{aliasId}", Name = "DeleteContactAlias")]
    [Authorize(Policy = PermissionClaims.ContactsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeleteAlias(
        [FromRoute] Guid contactId, [FromRoute] Guid aliasId, CancellationToken cancellationToken = default)
    {
        var deleted = await contactService.DeleteAlias(contactId, aliasId, cancellationToken);
        if (!deleted)
        {
            return this.NotFoundProblem($"Alias ID {aliasId} is not attached to contact ID {contactId}.");
        }

        AuditContactChange(contactId, "alias.deleted");
        return NoContent();
    }

    // ── Addresses (issue #325 §7) ─────────────────────────────────────────────

    [HttpGet("{contactId}/addresses", Name = "GetContactAddresses")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingAddress>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetAddresses([FromRoute] Guid contactId, CancellationToken cancellationToken = default)
    {
        var addresses = await contactService.GetAddresses(contactId, cancellationToken);
        return addresses is null ? this.NotFoundProblem($"Contact ID {contactId} not found.") : Ok(addresses);
    }

    [HttpPost("{contactId}/addresses", Name = "PostContactAddress")]
    [Authorize(Policy = PermissionClaims.ContactsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingAddress))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PostAddress([FromRoute] Guid contactId, [FromBody] NewAddress request, CancellationToken cancellationToken = default)
    {
        var created = await contactService.CreateAddress(contactId, request, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Contact ID {contactId} not found.")
            : CreatedAtRoute("GetContactAddresses", new { contactId }, created);
    }

    [HttpPut("{contactId}/addresses/{addressId}", Name = "PutContactAddress")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PutAddress([FromRoute] Guid contactId, [FromRoute] Guid addressId, [FromBody] NewAddress request, CancellationToken cancellationToken = default)
    {
        var updated = await contactService.UpdateAddress(contactId, addressId, request, cancellationToken);
        return updated ? NoContent() : this.NotFoundProblem($"Address ID {addressId} is not attached to contact ID {contactId}.");
    }

    [HttpDelete("{contactId}/addresses/{addressId}", Name = "DeleteContactAddress")]
    [Authorize(Policy = PermissionClaims.ContactsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeleteAddress([FromRoute] Guid contactId, [FromRoute] Guid addressId, CancellationToken cancellationToken = default)
    {
        var deleted = await contactService.DeleteAddress(contactId, addressId, cancellationToken);
        return deleted ? NoContent() : this.NotFoundProblem($"Address ID {addressId} is not attached to contact ID {contactId}.");
    }

    // ── Emails (issue #325 §7) ────────────────────────────────────────────────

    [HttpGet("{contactId}/emails", Name = "GetContactEmails")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingEmailAddress>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetEmails([FromRoute] Guid contactId, CancellationToken cancellationToken = default)
    {
        var emails = await contactService.GetEmails(contactId, cancellationToken);
        return emails is null ? this.NotFoundProblem($"Contact ID {contactId} not found.") : Ok(emails);
    }

    [HttpPost("{contactId}/emails", Name = "PostContactEmail")]
    [Authorize(Policy = PermissionClaims.ContactsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingEmailAddress))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PostEmail([FromRoute] Guid contactId, [FromBody] NewEmailAddress request, CancellationToken cancellationToken = default)
    {
        var created = await contactService.CreateEmail(contactId, request, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Contact ID {contactId} not found.")
            : CreatedAtRoute("GetContactEmails", new { contactId }, created);
    }

    [HttpPut("{contactId}/emails/{emailId}", Name = "PutContactEmail")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PutEmail([FromRoute] Guid contactId, [FromRoute] Guid emailId, [FromBody] NewEmailAddress request, CancellationToken cancellationToken = default)
    {
        var updated = await contactService.UpdateEmail(contactId, emailId, request, cancellationToken);
        return updated ? NoContent() : this.NotFoundProblem($"Email ID {emailId} is not attached to contact ID {contactId}.");
    }

    [HttpDelete("{contactId}/emails/{emailId}", Name = "DeleteContactEmail")]
    [Authorize(Policy = PermissionClaims.ContactsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeleteEmail([FromRoute] Guid contactId, [FromRoute] Guid emailId, CancellationToken cancellationToken = default)
    {
        var deleted = await contactService.DeleteEmail(contactId, emailId, cancellationToken);
        return deleted ? NoContent() : this.NotFoundProblem($"Email ID {emailId} is not attached to contact ID {contactId}.");
    }

    // ── Phone numbers (issue #325 §7) ─────────────────────────────────────────

    [HttpGet("{contactId}/phones", Name = "GetContactPhones")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingPhoneNumber>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetPhones([FromRoute] Guid contactId, CancellationToken cancellationToken = default)
    {
        var phones = await contactService.GetPhones(contactId, cancellationToken);
        return phones is null ? this.NotFoundProblem($"Contact ID {contactId} not found.") : Ok(phones);
    }

    [HttpPost("{contactId}/phones", Name = "PostContactPhone")]
    [Authorize(Policy = PermissionClaims.ContactsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPhoneNumber))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PostPhone([FromRoute] Guid contactId, [FromBody] NewPhoneNumber request, CancellationToken cancellationToken = default)
    {
        var created = await contactService.CreatePhone(contactId, request, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Contact ID {contactId} not found.")
            : CreatedAtRoute("GetContactPhones", new { contactId }, created);
    }

    [HttpPut("{contactId}/phones/{phoneId}", Name = "PutContactPhone")]
    [Authorize(Policy = PermissionClaims.ContactsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> PutPhone([FromRoute] Guid contactId, [FromRoute] Guid phoneId, [FromBody] NewPhoneNumber request, CancellationToken cancellationToken = default)
    {
        var updated = await contactService.UpdatePhone(contactId, phoneId, request, cancellationToken);
        return updated ? NoContent() : this.NotFoundProblem($"Phone ID {phoneId} is not attached to contact ID {contactId}.");
    }

    [HttpDelete("{contactId}/phones/{phoneId}", Name = "DeleteContactPhone")]
    [Authorize(Policy = PermissionClaims.ContactsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeletePhone([FromRoute] Guid contactId, [FromRoute] Guid phoneId, CancellationToken cancellationToken = default)
    {
        var deleted = await contactService.DeletePhone(contactId, phoneId, cancellationToken);
        return deleted ? NoContent() : this.NotFoundProblem($"Phone ID {phoneId} is not attached to contact ID {contactId}.");
    }
}
