using System.Globalization;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.ApiClient.Resources;

/// <summary>The instrument-prices list's filters and sort (issue #446 §5 endpoint 8). Empty collections mean "all".</summary>
public sealed record InstrumentPricesFilter
{
    public string? Search { get; init; }

    public IReadOnlyCollection<Guid>? InstrumentIds { get; init; }

    public IReadOnlyCollection<InstrumentPriceStatus>? Statuses { get; init; }

    public IReadOnlyCollection<InstrumentPriceSource>? Sources { get; init; }

    /// <summary>Trading date, inclusive.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Trading date, inclusive.</summary>
    public DateOnly? To { get; init; }

    public InstrumentPriceSortBy? SortBy { get; init; }

    public bool Ascending { get; init; }
}

/// <summary>
/// Typed client for the instrument-prices endpoints (issue #446 §5 endpoints 8–12). Only manual rows are writable:
/// editing or deleting a fetched row is <c>422</c> keyed <c>source</c>.
/// </summary>
public interface IInstrumentPricesApiClient
{
    /// <summary>
    /// One page of prices. The purge dialog's count is <c>ListAsync(1, 1, filter)</c> read off
    /// <see cref="PagedResult{T}.TotalCount"/>.
    /// </summary>
    Task<ApiResult<PagedResult<ExistingInstrumentPrice>>> ListAsync(
        int page, int pageSize, InstrumentPricesFilter? filter = null, CancellationToken ct = default);

    Task<ApiResult<ExistingInstrumentPrice>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a manual price. <c>409</c> <c>price-duplicate</c> carries <c>existingInstrumentPriceId</c>.</summary>
    Task<ApiResult<ExistingInstrumentPrice>> CreateAsync(NewInstrumentPrice price, CancellationToken ct = default);

    Task<ApiResult<ExistingInstrumentPrice>> UpdateAsync(Guid id, UpdateInstrumentPrice price, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IInstrumentPricesApiClient" />
public sealed class InstrumentPricesApiClient(IOdysseyApi api) : IInstrumentPricesApiClient
{
    private const string Base = "api/instrument-prices";

    public Task<ApiResult<PagedResult<ExistingInstrumentPrice>>> ListAsync(
        int page, int pageSize, InstrumentPricesFilter? filter = null, CancellationToken ct = default)
    {
        var query = PagedQuery.For(Base).Window(page, pageSize);
        if (filter is not null)
        {
            query
                .Add("search", filter.Search)
                .AddMany("instrumentIds", filter.InstrumentIds?.Select(id => id.ToString()))
                .AddMany("status", filter.Statuses?.Select(status => status.ToString()))
                .AddMany("sources", filter.Sources?.Select(source => source.ToString()))
                .Add("from", filter.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Add("to", filter.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Sort(filter.SortBy?.ToString(), filter.Ascending);
        }

        return api.GetPagedAsync<ExistingInstrumentPrice>(query.Build(), ct);
    }

    public Task<ApiResult<ExistingInstrumentPrice>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingInstrumentPrice>($"{Base}/{id}", ct);

    public Task<ApiResult<ExistingInstrumentPrice>> CreateAsync(NewInstrumentPrice price, CancellationToken ct = default) =>
        api.SendAsync<ExistingInstrumentPrice>(HttpMethod.Post, Base, price, ct);

    public Task<ApiResult<ExistingInstrumentPrice>> UpdateAsync(Guid id, UpdateInstrumentPrice price, CancellationToken ct = default) =>
        api.SendAsync<ExistingInstrumentPrice>(HttpMethod.Put, $"{Base}/{id}", price, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}
