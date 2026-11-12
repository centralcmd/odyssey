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
public sealed class ExchangeRatesController : ControllerBase
{
    private readonly ILogger<ExchangeRatesController> logger;
    private readonly ExchangeRateService exchangeRateService;

    public ExchangeRatesController(
        ILogger<ExchangeRatesController> logger,
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
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get the latest rate for a currency pair.", Description = @"Returns the newest row in force for from -> to: a stored from -> to row, or a fetched to -> from row returned as its inverse. An inverted response has inverted = true, the codes swapped and rate = 1 / the stored rate, unrounded, so it can carry more than 8 decimals (a JavaScript Number keeps about 15-17 significant digits). Its exchangeRateId, asOf, createdAt and updatedAt are the STORED row's, so GET /api/exchange-rates/{id} with that id returns the to -> from row; a caller that follows ids must check inverted. No NOK hop: use /api/exchange-rates/resolved for the rate a conversion would use. 400 for a code that is not exactly three letters; 404 when nothing is in force, a currency does not exist, or from equals to.")]
    public async Task<IActionResult> GetLatest(
        [FromQuery] CurrencyPairQuery query, CancellationToken cancellationToken = default)
    {
        var rate = await exchangeRateService.GetLatest(query.From!, query.To!, cancellationToken);
        if (rate is null)
        {
            return this.NotFoundProblem($"No exchange rate found for {query.From!.ToUpperInvariant()} -> {query.To!.ToUpperInvariant()}.");
        }

        return Ok(rate);
    }

    [HttpGet("resolved", Name = "GetResolvedExchangeRate")]
    [Authorize(Policy = PermissionClaims.ExchangeRatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ResolvedExchangeRate))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get the rate a conversion would use right now, and how it was built.", Description = @"The same resolution the account totals use: a direct row, a fetched reverse row inverted, or a hop through NOK (method 0 SameCurrency, 1 Direct, 2 Inverted, 3 ViaNok). Each leg is shown in the logical direction with the rate actually multiplied in; its exchangeRateId names the stored row, whose own pair is the reverse when inverted is true. Rates are unrounded and can carry more than 8 decimals (a JavaScript Number keeps about 15-17 significant digits). asOf is the older leg's date for a hop. 400 for a code that is not exactly three letters; 404 when a currency does not exist or nothing resolves.")]
    public async Task<IActionResult> GetResolved(
        [FromQuery] CurrencyPairQuery query, CancellationToken cancellationToken = default)
    {
        var resolved = await exchangeRateService.ResolveAsync(query.From!, query.To!, cancellationToken);
        if (resolved is null)
        {
            return this.NotFoundProblem($"No exchange rate resolves {query.From!.ToUpperInvariant()} -> {query.To!.ToUpperInvariant()}.");
        }

        return Ok(resolved);
    }

    [HttpGet("summary", Name = "GetExchangeRateSummary")]
    [Authorize(Policy = PermissionClaims.ExchangeRatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExchangeRateSummary))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Counts by status and source, the currencies in use and the attribution.")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default) =>
        Ok(await exchangeRateService.GetSummaryAsync(cancellationToken));

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
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Correct a rate's Rate/AsOf.", Description = @"Corrects the Rate and AsOf of an existing manual exchange rate record. The currency pair is the record's identity and can't be changed. A fetched rate is refused with 422 keyed source.")]
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
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove a mistaken manual exchange rate row. A fetched row is refused; the per-currency purge removes those.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID of the exchange rate record to delete.")] Guid id, CancellationToken cancellationToken = default)
    {
        await exchangeRateService.Delete(id, cancellationToken);
        return NoContent();
    }
}
