using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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
    private readonly PropertySummaryService summaryService;
    private readonly ContractService contractService;

    public PropertyController(
        PropertyService propertyService, PropertySummaryService summaryService, ContractService contractService)
    {
        this.propertyService = propertyService;
        this.summaryService = summaryService;
        this.contractService = contractService;
    }

    /// <summary>
    /// The estimate figures on a property row and in the summary are the estimate history's data, so
    /// they follow its claim rather than <c>properties.read</c> — the same shape as
    /// <c>ExistingAccount.ContractCount</c> following <c>contracts.read</c>.
    /// </summary>
    private bool CanReadEstimates() =>
        User.HasClaim(PermissionClaims.Type, PermissionClaims.PropertiesEstimatesRead);

    /// <summary>
    /// A count of contracts is contract data, so <see cref="ExistingProperty.ContractCount"/> follows
    /// <c>contracts.read</c> (issue #208 §5.6) — decided here because the service has no principal.
    /// </summary>
    private bool CanReadContracts() =>
        User.HasClaim(PermissionClaims.Type, PermissionClaims.ContractsRead);

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
        // Ordering by value discloses how the properties rank by worth, which is estimate data.
        if (query.SortBy == PropertySortBy.Value && !CanReadEstimates())
            return Forbid();

        var result = await propertyService.ListAsync(
            query, includeEstimates: CanReadEstimates(), cancellationToken,
            includeContractCount: CanReadContracts());
        return Ok(result);
    }

    [HttpGet("summary", Name = "GetPropertySummary")]
    [Authorize(Policy = PermissionClaims.PropertiesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PropertySummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Summarise the properties.",
        Description = @"Counts by type (unarchived only) and by derived status. For a caller holding
                        properties.estimates.read, also the in-force estimates of the owned properties
                        summed per currency, and a total converted to baseCurrency at the latest rate;
                        a currency with no rate is named in unconvertedCurrencies and left out of the
                        total. Blank baseCurrency picks the currency most owned properties use.")]
    public async Task<IActionResult> GetSummary(
        [FromQuery, StringLength(3)] string? baseCurrency, CancellationToken cancellationToken = default)
    {
        var summary = await summaryService.GetAsync(baseCurrency, CanReadEstimates(), cancellationToken);
        return Ok(summary);
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
        var property = await propertyService.Get(
            id, includeEstimates: CanReadEstimates(), cancellationToken,
            includeContractCount: CanReadContracts());
        if (property is null)
            return this.NotFoundProblem($"Property ID {id} not found.");

        return Ok(property);
    }

    [HttpGet("{propertyId}/contracts", Name = "GetPropertyContracts")]
    [Authorize(Policy = PermissionClaims.PropertiesRead)]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<PropertyContractLink>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the contracts that name a property as a party.",
        Description = @"One row per contract, with every role the property holds on it. Requires both
                        properties.read and contracts.read: the rows name contracts, which
                        properties.read alone does not license.")]
    public async Task<IActionResult> GetPropertyContracts(
        [FromRoute(Name = "propertyId")] Guid propertyId, CancellationToken cancellationToken = default)
    {
        var contracts = await contractService.ListForPropertyAsync(propertyId, cancellationToken);
        if (contracts is null)
            return this.NotFoundProblem($"Property ID {propertyId} not found.");

        return Ok(contracts);
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
        Description = @"Hard delete. Cascades the detail row, the estimate history, the smart-tag
                        links and every contract party naming the property (each removal recorded as
                        a PartyRemoved event on its contract); the tags and contracts are untouched.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var deleted = await propertyService.Delete(id, userId, cancellationToken);
        if (!deleted)
            return this.NotFoundProblem($"Property ID {id} not found.");

        return NoContent();
    }
}
