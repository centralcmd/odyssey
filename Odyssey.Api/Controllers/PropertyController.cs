using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

/// <summary>
/// Property CRUD and list (issue #167). Estimates and smart tags have their own controllers on the same
/// route prefix, the way <see cref="AccountEstimatesController"/> and
/// <see cref="AccountSmartTagsController"/> sit beside <see cref="AccountController"/>.
///
/// <para>
/// Unlike <c>PUT /api/accounts/{id}</c>, <c>PUT</c> here is <b>not an upsert</b>: an unknown id is a
/// <c>404</c> and creates nothing.
/// </para>
/// </summary>
[ApiController]
[Route("api/properties")]
public class PropertyController : ControllerBase
{
    private readonly PropertyService propertyService;

    public PropertyController(PropertyService propertyService)
    {
        this.propertyService = propertyService;
    }

    [HttpGet(Name = "GetProperties")]
    [Authorize(Policy = PermissionClaims.PropertiesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingProperty>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List properties.",
        Description = @"List properties with search, subtype and status filters, sorting and pagination.
                        sortBy=value orders by the estimate in force now, in each property's own
                        currency WITHOUT conversion (a mixed-currency list sorts by raw magnitude),
                        with properties that have no estimate last in both directions.")]
    public async Task<IActionResult> Get(
        [FromQuery] PropertiesQueryParams query, CancellationToken cancellationToken = default)
    {
        var result = await propertyService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}", Name = "GetProperty")]
    [Authorize(Policy = PermissionClaims.PropertiesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingProperty))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a property, with its detail sub-object.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var property = await propertyService.Get(id, cancellationToken);
        if (property is null)
            return this.NotFoundProblem($"Property ID {id} not found.");

        return Ok(property);
    }

    [HttpPost(Name = "PostProperty")]
    [Authorize(Policy = PermissionClaims.PropertiesCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingProperty))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a property.",
        Description = @"Creates a property with exactly one detail sub-object matching its type.")]
    public async Task<IActionResult> Post(
        [FromBody] NewProperty newProperty, CancellationToken cancellationToken = default)
    {
        var property = await propertyService.Create(newProperty, cancellationToken);
        return CreatedAtRoute("GetProperty", new { id = property.PropertyId }, property);
    }

    [HttpPut("{id}", Name = "PutProperty")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Replace a property.",
        Description = @"Full replace; not an upsert. The type is fixed at creation: a body whose type
                        differs from the stored one is refused with 422.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewProperty putProperty, CancellationToken cancellationToken = default)
    {
        var property = await propertyService.Update(id, putProperty, cancellationToken);
        if (property is null)
            return this.NotFoundProblem($"Property ID {id} not found.");

        return NoContent();
    }

    [HttpDelete("{id}", Name = "DeleteProperty")]
    [Authorize(Policy = PermissionClaims.PropertiesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete a property.",
        Description = @"Hard delete. Cascades the detail row, the estimate history and the smart-tag
                        links; the tags themselves are untouched.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await propertyService.Delete(id, cancellationToken);
        if (!deleted)
            return this.NotFoundProblem($"Property ID {id} not found.");

        return NoContent();
    }
}
