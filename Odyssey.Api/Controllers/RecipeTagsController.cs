using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Core.Recipes;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Api.Controllers;

/// <summary>Recipe tags (issue #525 §5 items 6–10): the <c>/api/photo-tags</c> contract plus a recipe count.</summary>
[ApiController]
[Route("api/recipe-tags")]
public sealed class RecipeTagsController(RecipeTagService service) : ControllerBase
{
    [HttpGet(Name = "GetRecipeTags")]
    [Authorize(Policy = PermissionClaims.RecipeTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingRecipeTag>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [EndpointSummary("List recipe tags; an omitted status returns active and archived tags.")]
    public async Task<IActionResult> Get([FromQuery] RecipeTagsQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id}", Name = "GetRecipeTag")]
    [Authorize(Policy = PermissionClaims.RecipeTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingRecipeTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("Get a recipe tag.")]
    public async Task<IActionResult> Get([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await service.GetAsync(id, cancellationToken);
        return tag is null ? NotFound(id) : Ok(tag);
    }

    [HttpPost(Name = "PostRecipeTag")]
    [Authorize(Policy = PermissionClaims.RecipeTagsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingRecipeTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Create a recipe tag; a name colliding with any existing tag (incl. archived) returns 409.")]
    public async Task<IActionResult> Post([FromBody] NewRecipeTag request, CancellationToken cancellationToken = default)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtRoute("GetRecipeTag", new { id = created.RecipeTagId }, created);
    }

    [HttpPut("{id}", Name = "PutRecipeTag")]
    [Authorize(Policy = PermissionClaims.RecipeTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingRecipeTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Update a recipe tag and toggle its archived state.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateRecipeTag request,
        CancellationToken cancellationToken = default)
    {
        var updated = await service.UpdateAsync(id, request, cancellationToken);
        return updated is null ? NotFound(id) : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteRecipeTag")]
    [Authorize(Policy = PermissionClaims.RecipeTagsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Delete an unused recipe tag; a tag any recipe carries returns 409 (archive it instead).")]
    public async Task<IActionResult> Delete([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default) =>
        await service.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound(id);

    private IActionResult NotFound(Guid id) => this.NotFoundProblem($"Recipe tag ID {id} not found.");
}
