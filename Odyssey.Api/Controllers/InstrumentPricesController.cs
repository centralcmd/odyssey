using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using Odyssey.Core.Finance.Instruments;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Api.Controllers;

/// <summary>Instrument closing prices (issue #446 §5 endpoints 8–12). A fetched row is read-only.</summary>
[ApiController]
[Route("api/instrument-prices")]
public sealed class InstrumentPricesController(InstrumentPriceService prices) : ControllerBase
{
    [HttpGet(Name = "GetInstrumentPrices")]
    [Authorize(Policy = PermissionClaims.InstrumentPricesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingInstrumentPrice>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Get([FromQuery] InstrumentPricesQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await prices.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}", Name = "GetInstrumentPrice")]
    [Authorize(Policy = PermissionClaims.InstrumentPricesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingInstrumentPrice))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default) =>
        await prices.GetAsync(id, cancellationToken) is { } price ? Ok(price) : this.NotFoundProblem($"Price {id} not found.");

    [HttpPost(Name = "PostInstrumentPrice")]
    [Authorize(Policy = PermissionClaims.InstrumentPricesCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingInstrumentPrice))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add a manual closing price.")]
    public async Task<IActionResult> Post([FromBody] NewInstrumentPrice request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await prices.CreateAsync(request, userId, cancellationToken);
        return CreatedAtRoute("GetInstrumentPrice", new { id = created.InstrumentPriceId }, created);
    }

    [HttpPut("{id:guid}", Name = "PutInstrumentPrice")]
    [Authorize(Policy = PermissionClaims.InstrumentPricesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingInstrumentPrice))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Correct a manual price's date and close. A fetched row is refused with 422 keyed source.")]
    public async Task<IActionResult> Put(Guid id, [FromBody] UpdateInstrumentPrice request, CancellationToken cancellationToken = default) =>
        await prices.UpdateAsync(id, request, User.ActingUserId(), cancellationToken) is { } updated
            ? Ok(updated)
            : this.NotFoundProblem($"Price {id} not found.");

    [HttpDelete("{id:guid}", Name = "DeleteInstrumentPrice")]
    [Authorize(Policy = PermissionClaims.InstrumentPricesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default) =>
        await prices.DeleteAsync(id, User.ActingUserId(), cancellationToken) ? NoContent() : this.NotFoundProblem($"Price {id} not found.");
}
