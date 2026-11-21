using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Identity;
using Odyssey.Core.Identity;
using Odyssey.Core.Recipes;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The household's recipes (issue #525 §5). Every action returning a recipe read model is gated by
/// <c>recipes.read</c>, <c>.create</c> or <c>.update</c> alone, which the tag-icon pin in
/// <c>AuthorizationPolicyTests</c> holds it to (issue #345). The caller's <c>photos.read</c> and
/// <c>recipes.tags.read</c> are passed to the service, which decides what they unlock.
/// </summary>
[ApiController]
[Route("api/recipes")]
public sealed class RecipesController(RecipeService service, IUserDisplayNameResolver displayNames) : ControllerBase
{
    [HttpGet(Name = "GetRecipes")]
    [Authorize(Policy = PermissionClaims.RecipesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<RecipeSummary>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List recipes with search (title or ingredient), tag, time, photo and status filters, sort and paging.")]
    public async Task<IActionResult> Get([FromQuery] RecipesQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await service.ListAsync(query, CanReadPhotos(), cancellationToken));

    [HttpGet("{id}", Name = "GetRecipe")]
    [Authorize(Policy = PermissionClaims.RecipesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingRecipe))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a recipe, optionally with its amounts scaled to ?servings= (read-only, nothing is stored).")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery] RecipeReadQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await service.GetAsync(id, query.Servings, CanReadPhotos(), cancellationToken);
        return result is null ? NotFound(id) : Ok(await WithAuthorsAsync(result, cancellationToken));
    }

    [HttpPost(Name = "PostRecipe")]
    [Authorize(Policy = PermissionClaims.RecipesCreate)]
    [RequestSizeLimit(RecipeLimits.MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingRecipe))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a recipe. Linking photos needs photos.read; linking tags needs recipes.tags.read.")]
    public async Task<IActionResult> Post([FromBody] NewRecipe request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await service.CreateAsync(request, userId, CanReadPhotos(), CanReadTags(), cancellationToken);
        var dto = await WithAuthorsAsync(created, cancellationToken);
        return CreatedAtRoute("GetRecipe", new { id = dto.RecipeId }, dto);
    }

    [HttpPut("{id}", Name = "PutRecipe")]
    [Authorize(Policy = PermissionClaims.RecipesUpdate)]
    [RequestSizeLimit(RecipeLimits.MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingRecipe))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace a recipe (not an upsert). A stale version is 409 recipe-version-conflict; photoIds null leaves photos unchanged.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateRecipe request,
        CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var updated = await service.UpdateAsync(id, request, userId, CanReadPhotos(), CanReadTags(), cancellationToken);
        return updated is null ? NotFound(id) : Ok(await WithAuthorsAsync(updated, cancellationToken));
    }

    [HttpDelete("{id}", Name = "DeleteRecipe")]
    [Authorize(Policy = PermissionClaims.RecipesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a recipe with its ingredients, steps and links; library photos and tags stay. A delete that loses a race to an edit is 409.")]
    public async Task<IActionResult> Delete([FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.DeleteAsync(id, userId, cancellationToken) ? NoContent() : NotFound(id);
    }

    private IActionResult NotFound(Guid id) => this.NotFoundProblem($"Recipe ID {id} not found.");

    private bool CanReadPhotos() => User.HasPermission(PermissionClaims.PhotosRead);

    private bool CanReadTags() => User.HasPermission(PermissionClaims.RecipeTagsRead);

    // Names only (§7.3): a raw user id never reaches the wire, and a nulled attribution stays null.
    private async Task<ExistingRecipe> WithAuthorsAsync(RecipeResult result, CancellationToken cancellationToken)
    {
        var names = await displayNames.ResolveAsync(User, [result.CreatedByUserId, result.UpdatedByUserId], cancellationToken);
        result.Recipe.CreatedByName = names.NameForOptional(result.CreatedByUserId);
        result.Recipe.UpdatedByName = names.NameForOptional(result.UpdatedByUserId);
        return result.Recipe;
    }
}
