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
[Route("api/currencies")]
public class CurrencyController : ControllerBase
{
    private readonly ILogger<CurrencyController> logger;
    private readonly CurrencyService currencyService;

    public CurrencyController(ILogger<CurrencyController> logger, CurrencyService currencyService)
    {
        this.logger = logger;
        this.currencyService = currencyService;
    }

    [HttpGet(Name = "GetCurrencies")]
    [Authorize(Policy = PermissionClaims.CurrenciesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingCurrency>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List currencies.", Description = @"List currencies with search, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] CurrenciesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await currencyService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{code}", Name = "GetCurrency")]
    [Authorize(Policy = PermissionClaims.CurrenciesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingCurrency))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "code")] [SwaggerParameter("Code", Required = true,
            Description = @"The code for the currency to get.")] string code, CancellationToken cancellationToken = default)
    {
        var currency = await currencyService.Get(code, cancellationToken);
        if (currency is null)
        {
            return this.NotFoundProblem($"Currency code {code} not found.");
        }

        return Ok(currency);
    }

    [HttpPost(Name = "PostCurrency")]
    [Authorize(Policy = PermissionClaims.CurrenciesCreate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewCurrency", Required = true,
            Description = @"The new currency to create.")] NewCurrency newCurrency, CancellationToken cancellationToken = default)
    {
        var currency = await currencyService.Create(newCurrency, cancellationToken);
        return CreatedAtRoute("GetCurrency", new { code = currency.CurrencyCode }, currency);
    }

    [HttpPut("{code}", Name = "PutCurrency")]
    [Authorize(Policy = PermissionClaims.CurrenciesUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "code")] [SwaggerParameter("Code", Required = true,
            Description = @"The code for the currency to update.")] string code,
        [FromBody] [SwaggerParameter("NewCurrency", Required = true,
            Description = @"The currency with the updated values.")] NewCurrency newCurrency, CancellationToken cancellationToken = default)
    {
        var currency = await currencyService.Update(code, newCurrency, cancellationToken);
        return currency is null ? await Post(newCurrency, cancellationToken) : NoContent();
    }

    [HttpDelete("{code}", Name = "DeleteCurrency")]
    [Authorize(Policy = PermissionClaims.CurrenciesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "code")] [SwaggerParameter("Code", Required = true,
            Description = @"The code for the currency to delete.")] string code, CancellationToken cancellationToken = default)
    {
        await currencyService.Delete(code, cancellationToken);
        return NoContent();
    }
}
