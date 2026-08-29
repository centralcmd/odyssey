using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Identity;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/photos")]
public class PhotosController : ControllerBase
{
    private readonly ILogger<PhotosController> logger;
    private readonly PhotoService service;
    private readonly FileService files;
    private readonly IUserDisplayNameResolver displayNames;

    public PhotosController(
        ILogger<PhotosController> logger,
        PhotoService service,
        FileService files,
        IUserDisplayNameResolver displayNames)
    {
        this.logger = logger;
        this.service = service;
        this.files = files;
        this.displayNames = displayNames;
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
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken = default) =>
        Ok(await service.GetStatsAsync(cancellationToken));

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
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPhoto))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a library photo from an image file; runs EXIF/IPTC/XMP extraction.")]
    public async Task<IActionResult> Post(
        [FromBody] NewPhoto request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        // A photo always links a Files-store image, so creating one requires files.read (mirrors the
        // Journal file-link guard). Auto-creating tags from extracted keywords additionally needs
        // photos.tags.create; absent it, only existing keyword tags are linked (§7/§10.6).
        if (!HasClaim(PermissionClaims.FilesRead))
        {
            return this.ForbiddenProblem("Linking a file requires the files.read permission.");
        }

        var created = await service.Create(request, userId, HasClaim(PermissionClaims.PhotoTagsCreate), cancellationToken);
        await EnrichAsync(created, cancellationToken);
        return CreatedAtRoute("GetPhoto", new { id = created.PhotoId }, created);
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
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        // Renaming the backing file is a Files-store write, so it requires files.update — the same claim
        // the Files page's rename enforces. Check before applying anything so the photo update isn't
        // half-committed. A blank/omitted FileName leaves the file name untouched.
        var wantsRename = !string.IsNullOrWhiteSpace(request.FileName);
        if (wantsRename && !HasClaim(PermissionClaims.FilesUpdate))
        {
            return this.ForbiddenProblem("Renaming the backing file requires the files.update permission.");
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Photo ID {id} not found.");
        }

        if (wantsRename)
        {
            await RenameBackingFileAsync(updated.FileId, request.FileName!.Trim(), cancellationToken);
        }

        await EnrichAsync(updated, cancellationToken);
        return Ok(updated);
    }

    // Rename the photo's Files-store record in place, preserving its description (UpdateFileMetadataAsync
    // rewrites description too, so pass the current value through). No-op when the name is unchanged.
    private async Task RenameBackingFileAsync(Guid fileId, string newName, CancellationToken cancellationToken)
    {
        var meta = await files.GetFileMetadataAsync(fileId, cancellationToken);
        if (meta is not null && !string.Equals(meta.FileName, newName, StringComparison.Ordinal))
        {
            await files.UpdateFileMetadataAsync(
                fileId, new UpdateFileMetadataRequest(meta.Description, newName), cancellationToken);
        }
    }

    [HttpDelete("{id}", Name = "DeletePhoto")]
    [Authorize(Policy = PermissionClaims.PhotosDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Permanently delete a library photo and the file it wraps. "
        + "Returns 409 when that file is also attached elsewhere, naming the other holders.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Photo ID {id} not found.");
    }

    // Enrich the read model with data owned by other domains: author display names (identity) and the
    // backing file's name (Files store). Both are resolved at the API edge on the single-photo reads.
    private async Task EnrichAsync(ExistingPhoto photo, CancellationToken cancellationToken)
    {
        var names = await displayNames.ResolveAsync(User, [photo.CreatedByUserId, photo.UpdatedByUserId], cancellationToken);
        photo.CreatedByName = names.NameForAuthor(photo.CreatedByUserId);
        photo.UpdatedByName = names.NameForOptional(photo.UpdatedByUserId);

        var meta = await files.GetFileMetadataAsync(photo.FileId, cancellationToken);
        photo.FileName = meta?.FileName;
    }

    private bool HasClaim(string claimValue) => User.HasClaim(PermissionClaims.Type, claimValue);
}
