using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the properties endpoints (issue #167) and their sub-resources — value estimates and
/// smart tags. Those live on their own server-side controllers but are routed under
/// <c>api/properties/{propertyId}/…</c>, so they belong here, as the account client's do.
/// </summary>
/// <remarks>
/// Every sub-resource route is built from its parent property id, so an estimate can never be addressed
/// by its own id alone. <c>PUT</c> is <b>not</b> an upsert: an unknown id answers <c>404</c>, and a
/// changed type answers <c>422</c> — callers branch on <see cref="ApiResult.Status"/>.
/// </remarks>
public interface IPropertiesApiClient
{
    /// <summary>One page of properties with the server-side filter surface.</summary>
    Task<ApiResult<PagedResult<ExistingProperty>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Every matching property in one window, with the same filter surface as <see cref="ListAsync"/>.</summary>
    Task<ApiResult<List<ExistingProperty>>> ListAllAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    Task<ApiResult<ExistingProperty>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The header overview. Blank <paramref name="baseCurrency"/> lets the server pick the currency most
    /// owned properties use; <see cref="PropertySummary.Value"/> is <c>null</c> without
    /// <c>properties.estimates.read</c>.
    /// </summary>
    Task<ApiResult<PropertySummary>> GetSummaryAsync(string? baseCurrency = null, CancellationToken ct = default);

    Task<ApiResult<ExistingProperty>> CreateAsync(NewProperty property, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, NewProperty property, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    // ── Estimates ────────────────────────────────────────────────────────────

    Task<ApiResult<List<ExistingPropertyEstimate>>> ListEstimatesAsync(Guid propertyId, CancellationToken ct = default);

    /// <summary>The estimate in force now; a successful result with a null value means "none in force".</summary>
    Task<ApiResult<CurrentPropertyEstimate>> GetCurrentEstimateAsync(Guid propertyId, CancellationToken ct = default);

    Task<ApiResult> AddEstimateAsync(Guid propertyId, NewPropertyEstimate estimate, CancellationToken ct = default);

    Task<ApiResult> UpdateEstimateAsync(Guid propertyId, Guid estimateId, NewPropertyEstimate estimate, CancellationToken ct = default);

    Task<ApiResult> DeleteEstimateAsync(Guid propertyId, Guid estimateId, CancellationToken ct = default);

    // ── Smart tags ───────────────────────────────────────────────────────────

    Task<ApiResult<List<ExistingTransactionTag>>> ListSmartTagsAsync(Guid propertyId, CancellationToken ct = default);

    Task<ApiResult> AddSmartTagAsync(Guid propertyId, Guid tagId, CancellationToken ct = default);

    Task<ApiResult> RemoveSmartTagAsync(Guid propertyId, Guid tagId, CancellationToken ct = default);
}

/// <inheritdoc cref="IPropertiesApiClient" />
public sealed class PropertiesApiClient(IOdysseyApi api) : IPropertiesApiClient
{
    private const string Base = "api/properties";

    public Task<ApiResult<PagedResult<ExistingProperty>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingProperty>(
            Query(search, types, statuses, sortBy, sortDir).Window(page, pageSize).Build(), ct);

    public Task<ApiResult<List<ExistingProperty>>> ListAllAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<ExistingProperty>(Query(search, types, statuses, sortBy, sortDir).Build(), ct);

    // Types and statuses are arrays on PropertiesQueryParams, so they bind as repeated pairs.
    private static PagedQuery Query(
        string? search,
        IReadOnlyCollection<string>? types,
        IReadOnlyCollection<string>? statuses,
        string? sortBy,
        string? sortDir) =>
        PagedQuery.For(Base)
            .Add("search", search)
            .AddMany("types", types)
            .AddMany("statuses", statuses)
            .Add("sortBy", sortBy)
            .Add("sortDir", sortDir);

    public Task<ApiResult<PropertySummary>> GetSummaryAsync(string? baseCurrency = null, CancellationToken ct = default) =>
        api.GetAsync<PropertySummary>(
            string.IsNullOrWhiteSpace(baseCurrency)
                ? $"{Base}/summary"
                : $"{Base}/summary?baseCurrency={Uri.EscapeDataString(baseCurrency)}",
            ct);

    public Task<ApiResult<ExistingProperty>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingProperty>($"{Base}/{id}", ct);

    public Task<ApiResult<ExistingProperty>> CreateAsync(NewProperty property, CancellationToken ct = default) =>
        api.SendAsync<ExistingProperty>(HttpMethod.Post, Base, property, ct);

    public Task<ApiResult> UpdateAsync(Guid id, NewProperty property, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", property, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    // ── Estimates ────────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingPropertyEstimate>>> ListEstimatesAsync(Guid propertyId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingPropertyEstimate>>(Estimates(propertyId), ct);

    public Task<ApiResult<CurrentPropertyEstimate>> GetCurrentEstimateAsync(Guid propertyId, CancellationToken ct = default) =>
        api.GetAsync<CurrentPropertyEstimate>($"{Estimates(propertyId)}/current", ct);

    public Task<ApiResult> AddEstimateAsync(Guid propertyId, NewPropertyEstimate estimate, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Estimates(propertyId), estimate, ct);

    public Task<ApiResult> UpdateEstimateAsync(Guid propertyId, Guid estimateId, NewPropertyEstimate estimate, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Estimates(propertyId)}/{estimateId}", estimate, ct);

    public Task<ApiResult> DeleteEstimateAsync(Guid propertyId, Guid estimateId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Estimates(propertyId)}/{estimateId}", null, ct);

    // ── Smart tags ───────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingTransactionTag>>> ListSmartTagsAsync(Guid propertyId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingTransactionTag>>(SmartTags(propertyId), ct);

    public Task<ApiResult> AddSmartTagAsync(Guid propertyId, Guid tagId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{SmartTags(propertyId)}/{tagId}", null, ct);

    public Task<ApiResult> RemoveSmartTagAsync(Guid propertyId, Guid tagId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{SmartTags(propertyId)}/{tagId}", null, ct);

    private static string SmartTags(Guid propertyId) => $"{Base}/{propertyId}/smart-tags";
    private static string Estimates(Guid propertyId) => $"{Base}/{propertyId}/estimates";
}
