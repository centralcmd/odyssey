using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Core.Books;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Books;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>The book tags (issue #527), the <c>/api/photo-tags</c> contract under <c>books.tags.*</c>.</summary>
[ApiController]
[Route("api/book-tags")]
public sealed class BookTagsController(BookTagService service) : ControllerBase
{
    [HttpGet(Name = "GetBookTags")]
    [Authorize(Policy = PermissionClaims.BookTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingBookTag>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List book tags with search, archival-status filter, sort and paging.")]
    public async Task<IActionResult> Get([FromQuery] BookTagsQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id}", Name = "GetBookTag")]
    [Authorize(Policy = PermissionClaims.BookTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingBookTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a book tag.")]
    public async Task<IActionResult> Get([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await service.GetAsync(id, cancellationToken);
        return tag is null ? NotFound(id) : Ok(tag);
    }

    [HttpPost(Name = "PostBookTag")]
    [Authorize(Policy = PermissionClaims.BookTagsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingBookTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a book tag; a name colliding with an existing (incl. archived) tag returns 409.")]
    public async Task<IActionResult> Post([FromBody] NewBookTag request, CancellationToken cancellationToken = default)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtRoute("GetBookTag", new { id = created.BookTagId }, created);
    }

    [HttpPut("{id}", Name = "PutBookTag")]
    [Authorize(Policy = PermissionClaims.BookTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingBookTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a book tag and toggle its archival state.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id, [FromBody] UpdateBookTag request, CancellationToken cancellationToken = default)
    {
        var updated = await service.UpdateAsync(id, request, cancellationToken);
        return updated is null ? NotFound(id) : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteBookTag")]
    [Authorize(Policy = PermissionClaims.BookTagsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an unused book tag; a tag in use returns 409 (archive it instead).")]
    public async Task<IActionResult> Delete([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default) =>
        await service.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound(id);

    private ObjectResult NotFound(Guid id) => this.NotFoundProblem($"Book tag ID {id} not found.");
}
