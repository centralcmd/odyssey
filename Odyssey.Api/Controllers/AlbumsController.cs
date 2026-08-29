using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Identity;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/albums")]
public class AlbumsController : ControllerBase
{
    private readonly ILogger<AlbumsController> logger;
    private readonly PhotoAlbumService service;
    private readonly IUserDisplayNameResolver displayNames;

    public AlbumsController(
        ILogger<AlbumsController> logger,
        PhotoAlbumService service,
        IUserDisplayNameResolver displayNames)
    {
        this.logger = logger;
        this.service = service;
        this.displayNames = displayNames;
    }

    [HttpGet(Name = "GetAlbums")]
    [Authorize(Policy = PermissionClaims.PhotoAlbumsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<PhotoAlbumSummary>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List albums with search, archival-status filter, sort and paging.")]
    public async Task<IActionResult> Get(
        [FromQuery] AlbumsQueryParams query,
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

    [HttpGet("{id}", Name = "GetAlbum")]
    [Authorize(Policy = PermissionClaims.PhotoAlbumsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingPhotoAlbum))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get an album with its ordered member photo IDs and cover.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var album = await service.Get(id, cancellationToken);
        if (album is null)
        {
            return this.NotFoundProblem($"Album ID {id} not found.");
        }

        await EnrichAuthorsAsync(album, cancellationToken);
        return Ok(album);
    }

    [HttpPost(Name = "PostAlbum")]
    [Authorize(Policy = PermissionClaims.PhotoAlbumsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPhotoAlbum))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create an album with optional initial member photos.")]
    public async Task<IActionResult> Post(
        [FromBody] NewPhotoAlbum request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.Create(request, userId, cancellationToken);
        await EnrichAuthorsAsync(created, cancellationToken);
        return CreatedAtRoute("GetAlbum", new { id = created.PhotoAlbumId }, created);
    }

    [HttpPut("{id}", Name = "PutAlbum")]
    [Authorize(Policy = PermissionClaims.PhotoAlbumsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingPhotoAlbum))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update an album: rename/describe, replace ordered membership, set cover, archive.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdatePhotoAlbum request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Album ID {id} not found.");
        }

        await EnrichAuthorsAsync(updated, cancellationToken);
        return Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteAlbum")]
    [Authorize(Policy = PermissionClaims.PhotoAlbumsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an album and its membership rows; member photos are untouched.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Album ID {id} not found.");
    }

    private async Task EnrichAuthorsAsync(ExistingPhotoAlbum album, CancellationToken cancellationToken)
    {
        var names = await displayNames.ResolveAsync(User, [album.CreatedByUserId, album.UpdatedByUserId], cancellationToken);
        album.CreatedByName = names.NameForAuthor(album.CreatedByUserId);
        album.UpdatedByName = names.NameForOptional(album.UpdatedByUserId);
    }
}
