using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The property estimate endpoints (issue #167) — behaviourally identical to
/// <see cref="AccountEstimatesController"/>: same statuses, same rules, same null-body convention on
/// <c>current</c>. No request body carries an owner id; the property comes from the route.
/// </summary>
[ApiController]
[Route("api/properties")]
public class PropertyEstimatesController : ControllerBase
{
    private readonly PropertyEstimateService propertyEstimateService;

    public PropertyEstimatesController(PropertyEstimateService propertyEstimateService)
    {
        this.propertyEstimateService = propertyEstimateService;
    }

    [HttpGet("{propertyId}/estimates", Name = "GetPropertyEstimates")]
    [Authorize(Policy = PermissionClaims.PropertiesEstimatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingPropertyEstimate>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the estimated-value history for a property.",
        Description = @"Lists the full estimate history for the property, newest effective date first.
                        Optionally cut off at an as-of date.")]
    public async Task<IActionResult> GetPropertyEstimates(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var estimates = await propertyEstimateService.GetHistory(propertyId, asOf, cancellationToken);
        if (estimates is null)
            return this.NotFoundProblem($"Property ID {propertyId} not found.");

        return Ok(estimates);
    }

    [HttpGet("{propertyId}/estimates/current", Name = "GetCurrentPropertyEstimate")]
    [Authorize(Policy = PermissionClaims.PropertiesEstimatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CurrentPropertyEstimate))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the currently-effective estimated value for a property.",
        Description = @"Returns the in-force estimate as of now or the supplied as-of date, or a null
                        body when the property has no estimate in force.")]
    public async Task<IActionResult> GetCurrentPropertyEstimate(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        if (!await propertyEstimateService.PropertyExists(propertyId, cancellationToken))
            return this.NotFoundProblem($"Property ID {propertyId} not found.");

        var estimate = await propertyEstimateService.GetCurrent(propertyId, asOf, cancellationToken);

        // Always 200: a null body means "no estimate in force" (Ok(null) would 204). JsonResult keeps
        // the status and serializes null — the account endpoint's contract exactly.
        return new JsonResult(estimate) { StatusCode = StatusCodes.Status200OK };
    }

    [HttpPost("{propertyId}/estimates", Name = "PostPropertyEstimate")]
    [Authorize(Policy = PermissionClaims.PropertiesEstimatesWrite)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPropertyEstimate))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Record a new estimated-value entry on a property.")]
    public async Task<IActionResult> PostPropertyEstimate(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromBody] NewPropertyEstimate newEstimate, CancellationToken cancellationToken = default)
    {
        var estimate = await propertyEstimateService.Create(propertyId, newEstimate, cancellationToken);
        return CreatedAtRoute("GetPropertyEstimates", new { propertyId }, estimate);
    }

    [HttpPut("{propertyId}/estimates/{estimateId}", Name = "PutPropertyEstimate")]
    [Authorize(Policy = PermissionClaims.PropertiesEstimatesWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update an estimated-value entry on a property.")]
    public async Task<IActionResult> PutPropertyEstimate(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "estimateId")] Guid estimateId,
        [FromBody] NewPropertyEstimate putEstimate, CancellationToken cancellationToken = default)
    {
        var updated = await propertyEstimateService.Update(propertyId, estimateId, putEstimate, cancellationToken);
        if (!updated)
            return this.NotFoundProblem($"Estimate ID {estimateId} is not attached to property ID {propertyId}.");

        return NoContent();
    }

    [HttpDelete("{propertyId}/estimates/{estimateId}", Name = "DeletePropertyEstimate")]
    [Authorize(Policy = PermissionClaims.PropertiesEstimatesWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an estimated-value entry from a property.")]
    public async Task<IActionResult> DeletePropertyEstimate(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "estimateId")] Guid estimateId, CancellationToken cancellationToken = default)
    {
        var deleted = await propertyEstimateService.Delete(propertyId, estimateId, cancellationToken);
        if (!deleted)
            return this.NotFoundProblem($"Estimate ID {estimateId} is not attached to property ID {propertyId}.");

        return NoContent();
    }
}
