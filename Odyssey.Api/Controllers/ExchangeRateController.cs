using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/exchange-rates")]
public class ExchangeRateController : ControllerBase
{
    private readonly ILogger<ExchangeRateController> logger;
    private readonly ExchangeRateService exchangeRateService;

    public ExchangeRateController(
        ILogger<ExchangeRateController> logger,
        ExchangeRateService exchangeRateService)
    {
        this.logger = logger;
        this.exchangeRateService = exchangeRateService;
    }

    [HttpGet(Name = "GetExchangeRates")]
    [Authorize(Policy = PermissionClaims.ExchangeRatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingExchangeRate>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List exchange rates.", Description = @"List exchange rates with search, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] ExchangeRatesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await exchangeRateService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("latest", Name = "GetLatestExchangeRate")]
    [Authorize(Policy = PermissionClaims.ExchangeRatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingExchangeRate))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get the latest rate for a currency pair.")]
    public async Task<IActionResult> GetLatest(
        [FromQuery(Name = "from")] [SwaggerParameter("From", Required = true,
            Description = @"The source currency code.")] string from,
        [FromQuery(Name = "to")] [SwaggerParameter("To", Required = true,
            Description = @"The target currency code.")] string to, CancellationToken cancellationToken = default)
    {
        var rate = await exchangeRateService.GetLatest(from, to, cancellationToken);
        if (rate is null)
        {
            return this.NotFoundProblem($"No exchange rate found for {from?.ToUpperInvariant()} -> {to?.ToUpperInvariant()}.");
        }

        return Ok(rate);
    }

    [HttpGet("{id}", Name = "GetExchangeRate")]
    [Authorize(Policy = PermissionClaims.ExchangeRatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingExchangeRate))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a single exchange rate record.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID of the exchange rate record to get.")] Guid id, CancellationToken cancellationToken = default)
    {
        var rate = await exchangeRateService.Get(id, cancellationToken);
        if (rate is null)
        {
            return this.NotFoundProblem($"Exchange rate {id} not found.");
        }

        return Ok(rate);
    }

    [HttpPost(Name = "PostExchangeRate")]
    [Authorize(Policy = PermissionClaims.ExchangeRatesCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingExchangeRate))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add a new exchange rate. To correct an existing entry, use PUT instead.")]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewExchangeRate", Required = true,
            Description = @"The new exchange rate to record.")] NewExchangeRate newExchangeRate, CancellationToken cancellationToken = default)
    {
        var rate = await exchangeRateService.Create(newExchangeRate, cancellationToken);
        return CreatedAtRoute("GetExchangeRate", new { id = rate.ExchangeRateId }, rate);
    }

    [HttpPut("{id}", Name = "PutExchangeRate")]
    [Authorize(Policy = PermissionClaims.ExchangeRatesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingExchangeRate))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Correct a rate's Rate/AsOf.", Description = @"Corrects the Rate and AsOf of an existing exchange rate record. The currency pair is the record's identity and can't be changed.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID of the exchange rate record to update.")] Guid id,
        [FromBody] [SwaggerParameter("UpdateExchangeRate", Required = true,
            Description = @"The corrected Rate and AsOf.")] UpdateExchangeRate updateExchangeRate, CancellationToken cancellationToken = default)
    {
        var rate = await exchangeRateService.Update(id, updateExchangeRate, cancellationToken);
        if (rate is null)
        {
            return this.NotFoundProblem($"Exchange rate {id} not found.");
        }

        return Ok(rate);
    }

    [HttpDelete("{id}", Name = "DeleteExchangeRate")]
    [Authorize(Policy = PermissionClaims.ExchangeRatesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove a mistaken exchange rate row.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID of the exchange rate record to delete.")] Guid id, CancellationToken cancellationToken = default)
    {
        await exchangeRateService.Delete(id, cancellationToken);
        return NoContent();
    }
}
