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
/// A contact's single image. Split out of <c>ContactController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contacts")]
public sealed class ContactAvatarController : ControllerBase
{
    private readonly ContactService contactService;
    private readonly ContactAvatarService avatarService;
    private readonly ContactAuditLog audit;

    public ContactAvatarController(
        ContactService contactService,
        ContactAvatarService avatarService,
        ContactAuditLog audit)
    {
        this.contactService = contactService;
        this.avatarService = avatarService;
        this.audit = audit;
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

        // FileMetadata.UploadedByUserId is a real foreign key to AspNetUsers, so only a real id may be
        // stored; a principal without one is refused before the body is buffered.
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var bytes = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        var attached = await avatarService.AttachAsync(
            id, bytes, file.ContentType, userId, cancellationToken);

        if (!attached)
        {
            return this.NotFoundProblem($"Contact ID {id} not found.");
        }

        audit.ContactChanged(User, id, "image set");

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

        audit.ContactChanged(User, id, "image removed");
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
}
