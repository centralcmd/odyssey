using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the exchange-rate sync administration endpoints (issue #349 §5): which currencies
/// Norges Bank's rates are fetched for, their start dates, the sync's status and its run log.
/// </summary>
public interface IExchangeRateSyncApiClient
{
    /// <summary>
    /// One page of currencies. <paramref name="supported"/> <c>true</c> drops only the KNOWN-unsupported
    /// rows, so a currency of unknown status stays listed while the provider's list is unreachable.
    /// </summary>
    Task<ApiResult<PagedResult<ExistingExchangeRateSyncCurrency>>> ListCurrenciesAsync(
        int page, int pageSize, string? search = null, bool? enabled = null, bool? supported = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    /// <summary>Switch one currency on or off and/or set its start-date override; returns the updated row.</summary>
    Task<ApiResult<ExistingExchangeRateSyncCurrency>> UpdateCurrencyAsync(
        string currencyCode, ExchangeRateSyncCurrencyUpdate update, CancellationToken ct = default);

    /// <summary>Delete every fetched rate of one switched-off currency. Manual rates are kept.</summary>
    Task<ApiResult<ExchangeRatePurgeResult>> PurgeCurrencyAsync(string currencyCode, CancellationToken ct = default);

    Task<ApiResult<ExchangeRateSyncStatus>> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Fetches the provider's currency list (at most every 5 minutes), then reads the status.</summary>
    Task<ApiResult<ExchangeRateSyncStatus>> RefreshStatusAsync(CancellationToken ct = default);

    /// <summary>One page of the run log, newest first.</summary>
    Task<ApiResult<PagedResult<ExistingExchangeRateSyncRun>>> ListRunsAsync(
        int page, int pageSize, CancellationToken ct = default);

    /// <summary>Run the sync now. <c>409</c> while a run is active, <c>429</c> inside the cooldown.</summary>
    Task<ApiResult<ExchangeRateSyncRunStarted>> RunNowAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IExchangeRateSyncApiClient" />
public sealed class ExchangeRateSyncApiClient(IOdysseyApi api) : IExchangeRateSyncApiClient
{
    private const string Base = "api/system-settings/exchange-rate-sync";

    public Task<ApiResult<PagedResult<ExistingExchangeRateSyncCurrency>>> ListCurrenciesAsync(
        int page, int pageSize, string? search = null, bool? enabled = null, bool? supported = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingExchangeRateSyncCurrency>(
            PagedQuery.For($"{Base}/currencies")
                .Window(page, pageSize)
                .Add("search", search)
                .Add("enabled", enabled?.ToString().ToLowerInvariant())
                .Add("supported", supported?.ToString().ToLowerInvariant())
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult<ExistingExchangeRateSyncCurrency>> UpdateCurrencyAsync(
        string currencyCode, ExchangeRateSyncCurrencyUpdate update, CancellationToken ct = default) =>
        api.SendAsync<ExistingExchangeRateSyncCurrency>(
            HttpMethod.Put, $"{Base}/currencies/{Uri.EscapeDataString(currencyCode)}", update, ct);

    public Task<ApiResult<ExchangeRatePurgeResult>> PurgeCurrencyAsync(string currencyCode, CancellationToken ct = default) =>
        api.SendAsync<ExchangeRatePurgeResult>(
            HttpMethod.Delete, $"{Base}/currencies/{Uri.EscapeDataString(currencyCode)}/rates", null, ct);

    public Task<ApiResult<ExchangeRateSyncStatus>> GetStatusAsync(CancellationToken ct = default) =>
        api.GetAsync<ExchangeRateSyncStatus>($"{Base}/status", ct);

    public Task<ApiResult<ExchangeRateSyncStatus>> RefreshStatusAsync(CancellationToken ct = default) =>
        api.SendAsync<ExchangeRateSyncStatus>(HttpMethod.Post, $"{Base}/status/refresh", null, ct);

    public Task<ApiResult<PagedResult<ExistingExchangeRateSyncRun>>> ListRunsAsync(
        int page, int pageSize, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingExchangeRateSyncRun>(PagedQuery.For($"{Base}/runs").Window(page, pageSize).Build(), ct);

    public Task<ApiResult<ExchangeRateSyncRunStarted>> RunNowAsync(CancellationToken ct = default) =>
        api.SendAsync<ExchangeRateSyncRunStarted>(HttpMethod.Post, $"{Base}/runs", null, ct);
}
