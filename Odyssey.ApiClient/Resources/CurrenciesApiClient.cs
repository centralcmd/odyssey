using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the currencies endpoints. Currencies are reference data keyed by their ISO
/// <c>code</c> rather than a surrogate id, so the single-row routes take a <see cref="string"/>.
/// </summary>
/// <remarks>
/// <see cref="ListAllAsync"/> is by far the most-used call — every amount-entry dialog loads it to
/// populate a currency picker. It previously appeared as a hand-written
/// <c>"api/currencies?offset=0&amp;limit=1000"</c> at more than a dozen call sites.
/// </remarks>
public interface ICurrenciesApiClient
{
    /// <summary>One page of currencies for the admin list.</summary>
    Task<ApiResult<PagedResult<ExistingCurrency>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? status = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    /// <summary>Every currency in one window — the currency pickers.</summary>
    Task<ApiResult<List<ExistingCurrency>>> ListAllAsync(
        IReadOnlyCollection<string>? status = null, CancellationToken ct = default);

    /// <summary>Loads one currency by ISO code. Null on failure.</summary>
    Task<ExistingCurrency?> GetAsync(string code, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewCurrency currency, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(string code, NewCurrency currency, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(string code, CancellationToken ct = default);
}

/// <inheritdoc cref="ICurrenciesApiClient" />
public sealed class CurrenciesApiClient(IOdysseyApi api) : ICurrenciesApiClient
{
    private const string Base = "api/currencies";

    public Task<ApiResult<PagedResult<ExistingCurrency>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? status = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingCurrency>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .AddSingle("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult<List<ExistingCurrency>>> ListAllAsync(
        IReadOnlyCollection<string>? status = null, CancellationToken ct = default) =>
        api.GetAllAsync<ExistingCurrency>(PagedQuery.For(Base).AddSingle("status", status).Build(), ct);

    public async Task<ExistingCurrency?> GetAsync(string code, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingCurrency>($"{Base}/{Uri.EscapeDataString(code)}", ct)).Value;

    public Task<ApiResult> CreateAsync(NewCurrency currency, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, currency, ct);

    public Task<ApiResult> UpdateAsync(string code, NewCurrency currency, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{Uri.EscapeDataString(code)}", currency, ct);

    public Task<ApiResult> DeleteAsync(string code, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{Uri.EscapeDataString(code)}", null, ct);
}

/// <summary>Typed client for the exchange-rate endpoints.</summary>
public interface IExchangeRatesApiClient
{
    /// <summary>One page of rates with search, target-currency and current/historical filters.</summary>
    Task<ApiResult<PagedResult<ExistingExchangeRate>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? toCurrencies = null,
        IReadOnlyCollection<string>? status = null, string? sortBy = null, string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>
    /// Every rate in one window. The card's summary tiles are derived from the whole set, so they are
    /// computed from this rather than from the filtered display slice.
    /// </summary>
    Task<ApiResult<List<ExistingExchangeRate>>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// The most recent rate for a directed pair. The conversion service does no inversion or
    /// triangulation, so <paramref name="from"/>/<paramref name="to"/> must match a stored direction.
    /// Null when no rate exists (the endpoint answers <c>404</c>).
    /// </summary>
    Task<ExistingExchangeRate?> GetLatestAsync(string from, string to, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewExchangeRate rate, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, UpdateExchangeRate rate, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IExchangeRatesApiClient" />
public sealed class ExchangeRatesApiClient(IOdysseyApi api) : IExchangeRatesApiClient
{
    private const string Base = "api/exchange-rates";

    public Task<ApiResult<PagedResult<ExistingExchangeRate>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? toCurrencies = null,
        IReadOnlyCollection<string>? status = null, string? sortBy = null, string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingExchangeRate>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .AddMany("toCurrencies", toCurrencies)
                .AddSingle("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult<List<ExistingExchangeRate>>> ListAllAsync(CancellationToken ct = default) =>
        api.GetAllAsync<ExistingExchangeRate>(PagedQuery.For(Base).Build(), ct);

    public async Task<ExistingExchangeRate?> GetLatestAsync(string from, string to, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingExchangeRate>(
            $"{Base}/latest?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}", ct)).Value;

    public Task<ApiResult> CreateAsync(NewExchangeRate rate, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, rate, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdateExchangeRate rate, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", rate, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}
