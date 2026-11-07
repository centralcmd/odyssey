using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Identity;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Originals;
using Odyssey.Core.Journal.Thumbnails;
using Microsoft.Net.Http.Headers;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;
using Odyssey.Core.Identity;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/photos")]
public sealed class PhotosController : ControllerBase
{
    private readonly ILogger<PhotosController> logger;
    private readonly PhotoService service;
    private readonly IUserDisplayNameResolver displayNames;
    private readonly PhotoThumbnailService thumbnails;
    private readonly PhotoOriginalSource originals;
    private readonly IObjectStore store;

    public PhotosController(
        ILogger<PhotosController> logger,
        PhotoService service,
        IUserDisplayNameResolver displayNames,
        PhotoThumbnailService thumbnails,
        PhotoOriginalSource originals,
        IObjectStore store)
    {
        this.logger = logger;
        this.service = service;
        this.displayNames = displayNames;
        this.thumbnails = thumbnails;
        this.originals = originals;
        this.store = store;
    }

    [HttpGet(Name = "GetPhotos")]
    [Authorize(Policy = PermissionClaims.PhotosRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<PhotoSummary>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List photos with search, tag/person/album/date-range filters, sort and paging.")]
    public async Task<IActionResult> Get(
        [FromQuery] PhotosQueryParams query,
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

    [HttpGet("stats", Name = "GetPhotoStats")]
    [Authorize(Policy = PermissionClaims.PhotosRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PhotoLibraryStats))]
    [SwaggerOperation(Summary = "Aggregate library counts (total, favourites, per-tag, per-person) for the Overview panel.")]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken = default)
    {
        var stats = await service.GetStatsAsync(cancellationToken);

        // The worker's state is operational detail for administrators only (issue #362 §3.6).
        if (!User.HasPermission(PermissionClaims.SystemSettingsRead))
        {
            stats.ThumbnailWorkerState = null;
            stats.ThumbnailWorkerResumesAtUtc = null;
        }

        return Ok(stats);
    }

    [HttpGet("{id}/thumbnail", Name = "GetPhotoThumbnail")]
    [Authorize(Policy = PermissionClaims.PhotosRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Stream a photo's WebP thumbnail (Small 200 px or Medium 400 px).",
        Description = @"404 photo-thumbnail-not-found for an unknown photo, a thumbnail not ready yet and a released one
alike. Served inline with a strong ETag and private, no-cache, so revalidation costs one 304 with no body.")]
    public async Task<IActionResult> GetThumbnail(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery] PhotoThumbnailQuery query,
        CancellationToken cancellationToken = default)
    {
        // Metadata only: a conditional request never opens the file (§5).
        if (await thumbnails.GetDescriptorAsync(id, query.Size!.Value, cancellationToken) is not { } descriptor)
        {
            throw new PhotoThumbnailNotFoundException();
        }

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.ContentDisposition = "inline";
        Response.Headers.XContentTypeOptions = "nosniff";
        // same-site, not same-origin: CORP compares the port too, and the dev stack's client and API differ in it.
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";

        var etag = new EntityTagHeaderValue($"\"{descriptor.Sha256Hash}\"");
        if (IfNoneMatches(etag))
        {
            Response.Headers.ETag = etag.ToString();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var content = await store.OpenReadAsync(PhotoThumbnailStorage.FeatureKey, descriptor.StoredObjectId, cancellationToken);

        // The overload that takes an EntityTagHeaderValue, so the framework handles the conditional request too; a
        // hand-set ETag header would never produce a 304.
        return File(content.Content, "image/webp", lastModified: null, entityTag: etag);
    }

    [HttpGet("{id}/content", Name = "GetPhotoContent")]
    [Authorize(Policy = PermissionClaims.PhotosRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Stream a photo's original, from the object store or (not yet moved) its legacy file.",
        Description = @"Served inline under photos.read with nosniff, CORP same-site, a strong ETag (the SHA-256) and range
processing; private, no-cache, so revalidation costs one 304 with no body (issue #428 §5.2).")]
    public async Task<IActionResult> GetContent(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        // Metadata only first: a conditional request never opens the file.
        if (await originals.DescribeAsync(id, cancellationToken) is not { Sha256Hash: { } hash })
        {
            return this.NotFoundProblem($"Photo ID {id} not found.");
        }

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        // same-site, not same-origin: CORP compares the port too, and the dev stack's client and API differ in it.
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";

        var etag = new EntityTagHeaderValue($"\"{hash}\"");
        if (IfNoneMatches(etag))
        {
            Response.Headers.ETag = etag.ToString();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        if (await originals.OpenAsync(id, cancellationToken) is not { } content)
        {
            return this.NotFoundProblem($"Photo ID {id} not found.");
        }

        // Built by the framework, never by hand: a stored name holding a quote, CR/LF or non-ASCII text cannot break
        // the header (issue #247).
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(content.FileName);
        Response.Headers.ContentDisposition = disposition.ToString();

        return File(content.Content, content.ContentType, lastModified: null, entityTag: etag, enableRangeProcessing: true);
    }

    private bool IfNoneMatches(EntityTagHeaderValue etag)
    {
        var candidates = Request.GetTypedHeaders().IfNoneMatch;
        return candidates is { Count: > 0 }
            && candidates.Any(c => c.Equals(EntityTagHeaderValue.Any) || c.Compare(etag, useStrongComparison: true));
    }

    [HttpGet("{id}", Name = "GetPhoto")]
    [Authorize(Policy = PermissionClaims.PhotosRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingPhoto))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a photo with its tag/person/album link IDs and extracted metadata.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var photo = await service.Get(id, cancellationToken);
        if (photo is null)
        {
            return this.NotFoundProblem($"Photo ID {id} not found.");
        }

        await EnrichAsync(photo, cancellationToken);
        return Ok(photo);
    }

    [HttpPost(Name = "PostPhoto")]
    [Authorize(Policy = PermissionClaims.PhotosCreate)]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(PhotoUploadRateLimiting.UploadPolicy)]
    // No per-request [UploadSizeLimit]: under it the multipart reader refuses a file just over the admin cap inside
    // model binding, which surfaces as a 400 with no cap in it. The transport ceiling (FileStorage:MaxFileSizeBytes,
    // fixed at startup) still bounds the body; the service checks the admin cap and answers 413 naming it (#428 §5.1).
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPhoto))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status507InsufficientStorage, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Upload a photo: writes the original to the photo store and creates the library photo.",
        Description = @"multipart/form-data: the image as part 'file', plus the optional NewPhotoUpload fields. Runs
EXIF/IPTC/XMP extraction. 409 photo-duplicate when the same bytes are already a photo (issue #428 §5.1).")]
    public async Task<IActionResult> Post(
        IFormFile? file,
        [FromForm] NewPhotoUpload request,
        CancellationToken cancellationToken = default)
    {
        if (file is null)
        {
            return this.BadRequestProblem("A photo file is required.");
        }

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        try
        {
            // Auto-creating tags from extracted keywords needs photos.tags.create; absent it, only existing keyword
            // tags are linked (§7/§10.6).
            var created = await service.UploadAsync(
                file.ToFileUpload(), request, userId, User.HasPermission(PermissionClaims.PhotoTagsCreate), cancellationToken);
            await EnrichAsync(created, cancellationToken);
            return CreatedAtRoute("GetPhoto", new { id = created.PhotoId }, created);
        }
        catch (DuplicatePhotoException duplicate)
        {
            // Claim-conditional (issue #428 §5.1, §7.7): which photo it is goes only to a caller who may read photos.
            return User.HasPermission(PermissionClaims.PhotosRead)
                ? this.ConflictProblem(duplicate.Message, new Dictionary<string, object?>
                {
                    ["code"] = duplicate.Code,
                    ["existingPhotoId"] = duplicate.ExistingPhotoId,
                    ["existingArchived"] = duplicate.ExistingArchived,
                })
                : this.ConflictProblem(duplicate.Message, new Dictionary<string, object?> { ["code"] = duplicate.Code });
        }
    }

    [HttpPut("{id}", Name = "PutPhoto")]
    [Authorize(Policy = PermissionClaims.PhotosUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingPhoto))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a photo's metadata and replace its tag/person/album links; set/clear archive.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdatePhoto request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        // A rename is photo data since issue #428 §5.4: the service writes Photo.FileName under photos.update alone.
        var updated = await service.Update(id, request, userId, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Photo ID {id} not found.");
        }

        await EnrichAsync(updated, cancellationToken);
        return Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeletePhoto")]
    [Authorize(Policy = PermissionClaims.PhotosDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Permanently delete a library photo; its original and thumbnails are released after "
        + "the deletion grace. A legacy photo (not yet moved to the photo store) deletes the file it wraps, and returns "
        + "409 when that file is also attached elsewhere, naming the other holders.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Photo ID {id} not found.");
    }

    // Enrich the read model with author display names (identity), resolved at the API edge on the single-photo reads.
    // The file name is the photo's own effective name, filled by the service (issue #428 §5.4).
    private async Task EnrichAsync(ExistingPhoto photo, CancellationToken cancellationToken)
    {
        var names = await displayNames.ResolveAsync(User, [photo.CreatedByUserId, photo.UpdatedByUserId], cancellationToken);
        photo.CreatedByName = names.NameForAuthor(photo.CreatedByUserId);
        photo.UpdatedByName = names.NameForOptional(photo.UpdatedByUserId);
    }

}
