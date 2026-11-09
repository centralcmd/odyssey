using System.Globalization;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.ApiClient.Resources;

/// <summary>The instruments list's filters and sort (issue #446 §5 endpoint 1). Empty collections mean "all".</summary>
public sealed record InstrumentsFilter
{
    public string? Search { get; init; }

    public IReadOnlyCollection<InstrumentType>? Types { get; init; }

    public IReadOnlyCollection<string>? CurrencyCodes { get; init; }

    public bool IncludeArchived { get; init; }

    /// <summary>Active-only or archived-only; when set the server ignores <see cref="IncludeArchived"/>.</summary>
    public ArchivalStatus? Status { get; init; }

    /// <summary><see cref="InstrumentSortBy.LatestDate"/> is a <c>403</c> without <c>instrument-prices.read</c>.</summary>
    public InstrumentSortBy? SortBy { get; init; }

    public bool Ascending { get; init; } = true;
}

/// <summary>
/// Typed client for the instruments endpoints (issue #446 §5 endpoints 1–7, 13–15): the watchlist entries, their
/// summary, the dashboard watchlist read, an instrument's price series and the per-instrument price purge.
/// </summary>
/// <remarks>
/// Price-derived fields on <see cref="ExistingInstrument"/> and <see cref="InstrumentSummary"/> are <c>null</c>
/// without <c>instrument-prices.read</c>. <c>PUT</c> is not an upsert: an unknown id answers <c>404</c>.
/// </remarks>
public interface IInstrumentsApiClient
{
    /// <summary>One page of instruments; <paramref name="pageSize"/> may be <see cref="PagedQuery.SizeAll"/>.</summary>
    Task<ApiResult<PagedResult<ExistingInstrument>>> ListAsync(
        int page, int pageSize, InstrumentsFilter? filter = null, CancellationToken ct = default);

    Task<ApiResult<ExistingInstrument>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The unfiltered counts by type, followed and archived; newest date and attributions for price readers.</summary>
    Task<ApiResult<InstrumentSummary>> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Creates an instrument. <c>409</c> <c>instrument-duplicate</c> carries <c>existingInstrumentId</c>.</summary>
    Task<ApiResult<ExistingInstrument>> CreateAsync(NewInstrument instrument, CancellationToken ct = default);

    Task<ApiResult<ExistingInstrument>> UpdateAsync(Guid id, NewInstrument instrument, CancellationToken ct = default);

    Task<ApiResult> ArchiveAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult> UnarchiveAsync(Guid id, CancellationToken ct = default);

    /// <summary>Deletes an instrument; <c>409</c> while it has prices.</summary>
    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Deletes the instrument's prices in <paramref name="scope"/>. <paramref name="expectedCount"/> is the number
    /// the user confirmed; a stale count is <c>409 purge-count-mismatch</c> and deletes nothing (its <c>actualCount</c> extension is the new count).
    /// </summary>
    Task<ApiResult<InstrumentPricePurgeResult>> PurgePricesAsync(
        Guid id, InstrumentPricePurgeScope scope, int expectedCount, CancellationToken ct = default);

    /// <summary>One closing price per date, for a chart. Both bounds optional; at most 3 660 days.</summary>
    Task<ApiResult<InstrumentPriceSeries>> GetSeriesAsync(
        Guid id, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default);

    /// <summary>
    /// The dashboard watchlist in <paramref name="mainCurrency"/> (blank lets the server use NOK). A display currency
    /// the server cannot use is <c>422</c> keyed <c>mainCurrency</c>.
    /// </summary>
    Task<ApiResult<InstrumentWatchlist>> GetWatchlistAsync(
        string? mainCurrency, int sparklineDays = 0, CancellationToken ct = default);
}

/// <inheritdoc cref="IInstrumentsApiClient" />
public sealed class InstrumentsApiClient(IOdysseyApi api) : IInstrumentsApiClient
{
    private const string Base = "api/instruments";

    public Task<ApiResult<PagedResult<ExistingInstrument>>> ListAsync(
        int page, int pageSize, InstrumentsFilter? filter = null, CancellationToken ct = default)
    {
        var query = PagedQuery.For(Base).Window(page, pageSize);
        if (filter is not null)
        {
            query
                .Add("search", filter.Search)
                .AddMany("types", filter.Types?.Select(type => type.ToString()))
                .AddMany("currencyCodes", filter.CurrencyCodes)
                .AddBool("includeArchived", filter.IncludeArchived ? true : null)
                .Add("status", filter.Status?.ToString())
                .Sort(filter.SortBy?.ToString(), filter.Ascending);
        }

        return api.GetPagedAsync<ExistingInstrument>(query.Build(), ct);
    }

    public Task<ApiResult<ExistingInstrument>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingInstrument>($"{Base}/{id}", ct);

    public Task<ApiResult<InstrumentSummary>> GetSummaryAsync(CancellationToken ct = default) =>
        api.GetAsync<InstrumentSummary>($"{Base}/summary", ct);

    public Task<ApiResult<ExistingInstrument>> CreateAsync(NewInstrument instrument, CancellationToken ct = default) =>
        api.SendAsync<ExistingInstrument>(HttpMethod.Post, Base, instrument, ct);

    public Task<ApiResult<ExistingInstrument>> UpdateAsync(Guid id, NewInstrument instrument, CancellationToken ct = default) =>
        api.SendAsync<ExistingInstrument>(HttpMethod.Put, $"{Base}/{id}", instrument, ct);

    public Task<ApiResult> ArchiveAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{id}/archive", null, ct);

    public Task<ApiResult> UnarchiveAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{id}/unarchive", null, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    public Task<ApiResult<InstrumentPricePurgeResult>> PurgePricesAsync(
        Guid id, InstrumentPricePurgeScope scope, int expectedCount, CancellationToken ct = default) =>
        api.SendAsync<InstrumentPricePurgeResult>(
            HttpMethod.Delete,
            $"{Base}/{id}/prices?source={scope}&expectedCount={expectedCount.ToString(CultureInfo.InvariantCulture)}",
            null,
            ct);

    public Task<ApiResult<InstrumentPriceSeries>> GetSeriesAsync(
        Guid id, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        var parts = new List<string>(2);
        if (from is { } f)
        {
            parts.Add($"from={f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        if (to is { } t)
        {
            parts.Add($"to={t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        var url = $"{Base}/{id}/prices/series";
        return api.GetAsync<InstrumentPriceSeries>(parts.Count == 0 ? url : $"{url}?{string.Join('&', parts)}", ct);
    }

    public Task<ApiResult<InstrumentWatchlist>> GetWatchlistAsync(
        string? mainCurrency, int sparklineDays = 0, CancellationToken ct = default)
    {
        var url = $"{Base}/watchlist?sparklineDays={sparklineDays.ToString(CultureInfo.InvariantCulture)}";
        return api.GetAsync<InstrumentWatchlist>(
            string.IsNullOrWhiteSpace(mainCurrency) ? url : $"{url}&mainCurrency={Uri.EscapeDataString(mainCurrency.Trim())}",
            ct);
    }
}
