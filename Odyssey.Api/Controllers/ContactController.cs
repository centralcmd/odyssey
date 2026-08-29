using System.Globalization;
using System.Text;
using Odyssey.Dtos.Authorization;
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

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/contacts")]
public class ContactController : ControllerBase
{
    private readonly ILogger<ContactController> logger;
    private readonly ContactService contactService;
    private readonly ContactVCardService vCardService;

    public ContactController(
        ILogger<ContactController> logger, ContactService contactService, ContactVCardService vCardService)
    {
        this.logger = logger;
        this.contactService = contactService;
        this.vCardService = vCardService;
    }

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
        var contact = await contactService.Update(id, newContact, cancellationToken);
        return contact is null ? await Post(newContact, cancellationToken) : NoContent();
    }

    [HttpDelete("{id}", Name = "DeleteContact")]
    [Authorize(Policy = PermissionClaims.ContactsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the contact to delete.")] Guid id, CancellationToken cancellationToken = default)
    {
        await contactService.Delete(id, cancellationToken);
        return NoContent();
    }

    // ── vCard import/export (issue #338) ──────────────────────────────────────

    [HttpGet("{id}/vcard", Name = "ExportContactVCard")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [Produces("text/vcard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export a single contact as an RFC 6350 vCard 4.0 .vcf file.")]
    public async Task<IActionResult> ExportVCard(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var export = await vCardService.ExportOneAsync(id, cancellationToken);
        if (export is null)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        return VCardFile(export);
    }

    [HttpGet("vcard", Name = "ExportContactsVCard")]
    [Authorize(Policy = PermissionClaims.ContactsRead)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/vcard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export every contact matching the supplied filters as a multi-entry RFC 6350 vCard 4.0 .vcf file.",
        Description = "Omit all filters to export everything; pass the page's current filter state to export exactly the filtered set.")]
    public async Task<IActionResult> ExportVCards(
        [FromQuery] ContactsQueryParams query, CancellationToken cancellationToken = default)
    {
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
        }, cancellationToken);

        return new EmptyResult();
    }

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

    // nosniff mirrors the file-download surface: the browser must not re-interpret the body as
    // anything other than the declared text/vcard (matches CalendarIcsController.Export).
    private IActionResult VCardFile(VCardExport export)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        var bytes = Encoding.UTF8.GetBytes(export.Content);
        return File(bytes, "text/vcard; charset=utf-8", export.FileName);
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
