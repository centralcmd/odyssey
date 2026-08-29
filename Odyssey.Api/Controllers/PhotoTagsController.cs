using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/photo-tags")]
public class PhotoTagsController : ControllerBase
{
    private readonly ILogger<PhotoTagsController> logger;
    private readonly PhotoTagService service;

    public PhotoTagsController(
        ILogger<PhotoTagsController> logger,
        PhotoTagService service)
    {
        this.logger = logger;
        this.service = service;
    }

    [HttpGet(Name = "GetPhotoTags")]
    [Authorize(Policy = PermissionClaims.PhotoTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingPhotoTag>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List photo tags with search, archival-status filter, sort and paging.")]
    public async Task<IActionResult> Get(
        [FromQuery] PhotoTagsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("{id}", Name = "GetPhotoTag")]
    [Authorize(Policy = PermissionClaims.PhotoTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingPhotoTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a photo tag.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await service.Get(id, cancellationToken);
        return tag is null ? this.NotFoundProblem($"Photo tag ID {id} not found.") : Ok(tag);
    }

    [HttpPost(Name = "PostPhotoTag")]
    [Authorize(Policy = PermissionClaims.PhotoTagsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPhotoTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a photo tag; a name colliding with an existing (incl. archived) tag returns 409.")]
    public async Task<IActionResult> Post(
        [FromBody] NewPhotoTag request, CancellationToken cancellationToken = default)
    {
        var created = await service.Create(request, cancellationToken);
        return CreatedAtRoute("GetPhotoTag", new { id = created.PhotoTagId }, created);
    }

    [HttpPut("{id}", Name = "PutPhotoTag")]
    [Authorize(Policy = PermissionClaims.PhotoTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingPhotoTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a photo tag and toggle its archival state.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdatePhotoTag request, CancellationToken cancellationToken = default)
    {
        var updated = await service.Update(id, request, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Photo tag ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeletePhotoTag")]
    [Authorize(Policy = PermissionClaims.PhotoTagsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an unused photo tag; a tag in use returns 409 (archive it instead).")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Photo tag ID {id} not found.");
    }
}
