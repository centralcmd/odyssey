using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.ExchangeRateSync;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The exchange-rate sync (issue #349 §5): per-currency configuration, status, the run log, run-now and
/// the purge. Everything here is under the System settings area and Admin-only through the existing
/// claims — reads on <c>system-settings.read</c>, writes on <c>system-settings.update</c>. No new claim.
/// </summary>
[ApiController]
[Route("api/system-settings/exchange-rate-sync")]
[Authorize(Policy = PermissionClaims.SystemSettingsRead)]
public sealed class ExchangeRateSyncController(
    ExchangeRateSyncCurrencyService currencies,
    ExchangeRateSyncService sync,
    IExchangeRateSyncDispatcher dispatcher) : ControllerBase
{
    [HttpGet("currencies")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingExchangeRateSyncCurrency>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Every currency with its sync configuration.")]
    public async Task<IActionResult> ListCurrencies(
        [FromQuery] ExchangeRateSyncCurrenciesQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await currencies.ListAsync(query, cancellationToken));

    [HttpPut("currencies/{currencyCode}")]
    [Authorize(Policy = PermissionClaims.SystemSettingsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingExchangeRateSyncCurrency))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Turn sync on or off for one currency and set its start-date override.")]
    public async Task<IActionResult> PutCurrency(
        [FromRoute] string currencyCode,
        [FromBody] ExchangeRateSyncCurrencyUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return Ok(await currencies.UpdateAsync(currencyCode, update, userId, cancellationToken));
    }

    /// <summary>Gated on BOTH claims, the same pair as deleting one fetched rate.</summary>
    [HttpDelete("currencies/{currencyCode}/rates")]
    [Authorize(Policy = PermissionClaims.SystemSettingsUpdate)]
    [Authorize(Policy = PermissionClaims.ExchangeRatesDelete)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExchangeRatePurgeResult))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete every fetched rate of one disabled currency.")]
    public async Task<IActionResult> PurgeCurrency(
        [FromRoute] string currencyCode, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return Ok(await currencies.PurgeAsync(currencyCode, userId, cancellationToken));
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExchangeRateSyncStatus))]
    [SwaggerOperation(Summary = "The sync's state, schedule and last run.")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default) =>
        Ok(await sync.GetStatusAsync(cancellationToken));

    /// <remarks>
    /// Refresh status. Under the read claim because it changes no setting: it re-fetches the provider's list
    /// of published currencies into the stored copy every reader uses, at most once every 5 minutes, so the
    /// outbound requests it can cause are bounded whoever calls it.
    /// </remarks>
    [HttpPost("status/refresh")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExchangeRateSyncStatus))]
    [SwaggerOperation(Summary = "Fetch the provider's currency list, then read the status.")]
    public async Task<IActionResult> RefreshStatus(CancellationToken cancellationToken = default) =>
        Ok(await sync.RefreshStatusAsync(cancellationToken));

    [HttpGet("runs")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingExchangeRateSyncRun>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The run log, newest first.")]
    public async Task<IActionResult> ListRuns(
        [FromQuery] ExchangeRateSyncRunsQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await sync.ListRunsAsync(query, cancellationToken));

    [HttpPost("runs")]
    [Authorize(Policy = PermissionClaims.SystemSettingsUpdate)]
    [ProducesResponseType(StatusCodes.Status202Accepted, Type = typeof(ExchangeRateSyncRunStarted))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Run the sync now.")]
    public async Task<IActionResult> PostRun(CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var run = await sync.StartManualAsync(userId, cancellationToken);
        if (run.Status == ExchangeRateSyncRunStatus.Running)
        {
            // Not the request's token: the run must not die with the HTTP connection.
            await dispatcher.DispatchAsync(run.ExchangeRateSyncRunId, CancellationToken.None);
        }

        return Accepted(new ExchangeRateSyncRunStarted { RunId = run.ExchangeRateSyncRunId });
    }
}
