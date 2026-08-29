using Odyssey.Dtos.Finance;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the subscriptions endpoints (issue #293). Writes go through
/// <see cref="IOdysseyApi"/> at the call sites, as the sibling contracts/insurance surfaces do. Dates
/// are exchanged as <see cref="DateOnly"/> on the DTOs (native <c>System.Text.Json</c> support); the
/// UI ⇄ <see cref="DateOnly"/> conversion happens at the page.
/// </summary>
public interface ISubscriptionApiClient
{
    /// <summary>
    /// Lists subscriptions (lean projection) with server-side search, interval filter, derived
    /// lifecycle-status filter (Active/Paused/Ended/Archived) and sort (issue #277 / #293).
    /// </summary>
    Task<ApiResult<List<SubscriptionListItem>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? intervals = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Loads one subscription (incl. notes + the minimised contact reference). Null on failure.</summary>
    Task<ExistingSubscription?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Loads the server-computed summary rollup (status/interval counts, multi-currency run-rate and
    /// derived upcoming renewals). <paramref name="baseCurrency"/> is the display currency the run-rate
    /// is blended into. Returns null on failure.
    /// </summary>
    Task<SubscriptionSummary?> GetSummaryAsync(string? baseCurrency = null, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewSubscription subscription, CancellationToken ct = default);

    /// <summary>
    /// Updates a subscription. Pause / resume / end are expressed through the update DTO's own fields
    /// — the lifecycle status is derived server-side, so there is no separate transition endpoint.
    /// </summary>
    Task<ApiResult> UpdateAsync(Guid id, UpdateSubscription subscription, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="ISubscriptionApiClient" />
public sealed class SubscriptionApiClient(IOdysseyApi api) : ISubscriptionApiClient
{
    private const string Base = "api/subscriptions";

    public Task<ApiResult<List<SubscriptionListItem>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? intervals = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<SubscriptionListItem>(
            PagedQuery.For(Base)
                .Add("search", search)
                .AddMany("intervals", intervals)
                .AddMany("statuses", statuses)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public async Task<ExistingSubscription?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingSubscription>($"{Base}/{id}", ct)).Value;

    public async Task<SubscriptionSummary?> GetSummaryAsync(string? baseCurrency = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(baseCurrency)
            ? $"{Base}/summary"
            : $"{Base}/summary?baseCurrency={Uri.EscapeDataString(baseCurrency)}";
        return (await api.GetAsync<SubscriptionSummary>(url, ct)).Value;
    }

    public Task<ApiResult> CreateAsync(NewSubscription subscription, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, subscription, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdateSubscription subscription, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", subscription, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}
