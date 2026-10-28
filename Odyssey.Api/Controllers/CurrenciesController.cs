using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;
using Odyssey.Core.Finance.ExchangeRateSync;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/currencies")]
public sealed class CurrenciesController : ControllerBase
{
    private readonly ILogger<CurrenciesController> logger;
    private readonly CurrencyService currencyService;
    private readonly ExchangeRateSyncCurrencyService syncCurrencies;

    public CurrenciesController(
        ILogger<CurrenciesController> logger, CurrencyService currencyService, ExchangeRateSyncCurrencyService syncCurrencies)
    {
        this.logger = logger;
        this.currencyService = currencyService;
        this.syncCurrencies = syncCurrencies;
    }

    /// <summary>
    /// A currency's sync projection is system-settings data, so <see cref="ExistingCurrency.Sync"/>
    /// follows <c>system-settings.read</c> — decided here because the service has no principal. The
    /// derived <see cref="ExistingCurrency.Status"/> is everyone's.
    /// </summary>
    private bool CanReadSync() =>
        User.HasPermission(PermissionClaims.SystemSettingsRead);

    [HttpGet(Name = "GetCurrencies")]
    [Authorize(Policy = PermissionClaims.CurrenciesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingCurrency>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List currencies.",
        Description = @"List currencies with search, sorting and pagination. Every row carries its derived
                        status (Synced, FetchingHistory, NoLongerPublished, Manual, NotPublished,
                        Archived), which statuses= filters and sortBy=status orders, for every caller.
                        A caller holding system-settings.read also gets each non-archived row's
                        exchange-rate sync projection (sync). status=active|archived is the older
                        archival filter, kept for the currency pickers.")]
    public async Task<IActionResult> Get(
        [FromQuery] CurrenciesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        // The status needs the projection for everyone; only the detail (coverage, counts) is the claim's.
        var canReadSync = CanReadSync();
        var sync = await syncCurrencies.ProjectAllAsync(cancellationToken, withExtents: canReadSync);
        var result = await currencyService.ListAsync(query, sync, attachSync: canReadSync, cancellationToken);
        if (canReadSync)
        {
            await syncCurrencies.FillFetchedRowCountsAsync(
                result.Items.Select(item => item.Sync).OfType<ExistingExchangeRateSyncCurrency>().ToList(), cancellationToken);
        }

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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update the details for a currency.",
        Description = @"Update the details for a currency. Not an upsert: an unknown code is a 404 and
                        nothing is created. Use POST to create. Archiving a currency switches its
                        exchange-rate sync off, and restoring it does not switch it back on. Archiving
                        the sync's base currency while any currency is synced is a 422.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "code")] [SwaggerParameter("Code", Required = true,
            Description = @"The code for the currency to update.")] string code,
        [FromBody] [SwaggerParameter("NewCurrency", Required = true,
            Description = @"The currency with the updated values.")] NewCurrency newCurrency, CancellationToken cancellationToken = default)
    {
        var updated = await currencyService.Update(code, newCurrency, cancellationToken);
        if (updated is null)
            return this.NotFoundProblem($"Currency {code} not found.");

        // After the save: an audit line must not record a change that did not commit.
        if (updated.SyncStopped)
            syncCurrencies.LogStoppedByArchive(updated.Currency.CurrencyCode, User.ActingUserId());

        return NoContent();
    }

    [HttpDelete("{code}", Name = "DeleteCurrency")]
    [Authorize(Policy = PermissionClaims.CurrenciesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete a currency based on its code.",
        Description = @"Delete a currency based on its code. A currency still used by an account, transaction,
                        budget, tax statement, property, estimate, contract term or exchange rate is refused
                        with a 409 naming how many of each reference it.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "code")] [SwaggerParameter("Code", Required = true,
            Description = @"The code for the currency to delete.")] string code, CancellationToken cancellationToken = default)
    {
        await currencyService.Delete(code, cancellationToken);
        return NoContent();
    }
}
