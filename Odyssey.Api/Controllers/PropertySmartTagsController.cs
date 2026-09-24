using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The property smart-tag endpoints (issue #167) — a separate controller on the <c>api/properties</c>
/// route prefix beside <see cref="PropertyController"/>, mirroring
/// <see cref="ContractSmartTagsController"/>. Behaviourally identical to it and to
/// <see cref="AccountSmartTagsController"/>.
///
/// <para>
/// <strong>Neither write takes a request body.</strong> Both the property and the tag are named by
/// route <see cref="Guid"/>s, so mass assignment is closed structurally rather than by a check: there
/// is no DTO through which a nested <c>TransactionTag</c> or <c>Property</c> object could be
/// over-posted.
/// </para>
/// </summary>
[ApiController]
[Route("api/properties")]
public class PropertySmartTagsController : ControllerBase
{
    private readonly PropertySmartTagService propertySmartTagService;

    public PropertySmartTagsController(PropertySmartTagService propertySmartTagService)
    {
        this.propertySmartTagService = propertySmartTagService;
    }

    [HttpGet("{propertyId}/smart-tags", Name = "GetPropertySmartTags")]
    [Authorize(Policy = PermissionClaims.PropertiesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingTransactionTag>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the smart tags configured for a property.",
        Description = @"Lists the transaction tags currently associated with the property as smart
                        tags, oldest association first. An empty list is a healthy 200, never a 404.")]
    public async Task<IActionResult> GetPropertySmartTags(
        [FromRoute(Name = "propertyId")] Guid propertyId, CancellationToken cancellationToken = default)
    {
        var tags = await propertySmartTagService.GetSmartTags(propertyId, cancellationToken);
        if (tags is null)
            return this.NotFoundProblem($"Property ID {propertyId} not found.");

        return Ok(tags);
    }

    [HttpPost("{propertyId}/smart-tags/{tagId}", Name = "AddPropertySmartTag")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTransactionTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Associate an existing tag with a property as a smart tag.")]
    public async Task<IActionResult> AddPropertySmartTag(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "tagId")] Guid tagId, CancellationToken cancellationToken = default)
    {
        var tag = await propertySmartTagService.AddSmartTag(propertyId, tagId, cancellationToken);
        return CreatedAtRoute("GetPropertySmartTags", new { propertyId }, tag);
    }

    [HttpDelete("{propertyId}/smart-tags/{tagId}", Name = "RemovePropertySmartTag")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove a smart-tag association from a property.")]
    public async Task<IActionResult> RemovePropertySmartTag(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "tagId")] Guid tagId, CancellationToken cancellationToken = default)
    {
        var removed = await propertySmartTagService.RemoveSmartTag(propertyId, tagId, cancellationToken);
        if (!removed)
            return this.NotFoundProblem(
                $"Tag ID {tagId} is not a smart tag for property ID {propertyId}.");

        return NoContent();
    }
}
