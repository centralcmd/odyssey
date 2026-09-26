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

    // ── Contracts ────────────────────────────────────────────────────────────

    /// <summary>
    /// The contracts naming the property as a party, one row per contract with every role it holds
    /// there (issue #208). Needs <c>contracts.read</c> as well as <c>properties.read</c>.
    /// </summary>
    Task<ApiResult<List<PropertyContractLink>>> ListContractsAsync(Guid propertyId, CancellationToken ct = default);

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

    // ── Documents (issue #210) ───────────────────────────────────────────────

    /// <summary>The property's documents, oldest attachment first; an empty list when it has none.</summary>
    Task<ApiResult<List<ExistingPropertyFile>>> ListFilesAsync(Guid propertyId, CancellationToken ct = default);

    /// <summary>Attaches a file already uploaded through the Files API. Needs properties.update + files.read.</summary>
    Task<ApiResult> AttachFileAsync(Guid propertyId, AttachPropertyFileRequest request, CancellationToken ct = default);

    /// <summary>A full replacement of the type and validity metadata — a null date or issuer clears it.</summary>
    Task<ApiResult> UpdateFileAsync(
        Guid propertyId, Guid fileId, UpdatePropertyFileRequest request, CancellationToken ct = default);

    Task<ApiResult<ApiFile>> DownloadFileAsync(Guid propertyId, Guid fileId, CancellationToken ct = default);

    /// <summary>Removes the link only; the file stays in the Files store.</summary>
    Task<ApiResult> DetachFileAsync(Guid propertyId, Guid fileId, CancellationToken ct = default);

    // ── Events (issue #209) ──────────────────────────────────────────────────
    //
    // Property-scoped like the estimate routes: an event is addressed as {propertyId}/events/{eventId},
    // never by event id alone, so the owner is always named by the route.

    /// <summary>
    /// One page of the property's event log. Newest first by default; the search term spans the title,
    /// the description <b>and</b> the notes. <paramref name="source"/> restricts to hand-written or
    /// server-recorded rows; omitted returns both.
    /// </summary>
    /// <param name="page">1-based page number, paired with <paramref name="pageSize"/>.</param>
    /// <param name="pageSize">Rows per page; <see cref="PagedQuery.SizeAll"/> requests the whole log.</param>
    Task<ApiResult<PagedResult<ExistingPropertyEvent>>> ListEventsAsync(
        Guid propertyId,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        DateTime? from = null,
        DateTime? to = null,
        string? sortBy = null,
        string? sortDir = null,
        int page = 1,
        int pageSize = PagedQuery.SizeAll,
        ContractEventSource? source = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records one event. <c>occurredAt</c> more than a minute ahead of the server clock, or a type illegal
    /// for the property's type or recorded only by the server, answers <c>422</c>.
    /// </summary>
    Task<ApiResult<ExistingPropertyEvent>> CreateEventAsync(
        Guid propertyId, NewPropertyEvent request, CancellationToken ct = default);

    /// <summary>
    /// Replaces one event — a <b>full replacement</b>: <c>null</c> clears <c>description</c>/<c>notes</c>
    /// and an omitted <c>type</c> resets to <see cref="PropertyEventType.Other"/>. A system-only type may
    /// be kept on a row that already carries it, never introduced.
    /// </summary>
    Task<ApiResult<ExistingPropertyEvent>> UpdateEventAsync(
        Guid propertyId, Guid eventId, UpdatePropertyEvent request, CancellationToken ct = default);

    /// <summary>Removes one event from the property's log. The property itself is untouched.</summary>
    Task<ApiResult> DeleteEventAsync(Guid propertyId, Guid eventId, CancellationToken ct = default);
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

    // ── Contracts ────────────────────────────────────────────────────────────

    public Task<ApiResult<List<PropertyContractLink>>> ListContractsAsync(Guid propertyId, CancellationToken ct = default) =>
        api.GetAsync<List<PropertyContractLink>>($"{Base}/{propertyId}/contracts", ct);

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

    // ── Documents ────────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingPropertyFile>>> ListFilesAsync(Guid propertyId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingPropertyFile>>(Files(propertyId), ct);

    public Task<ApiResult> AttachFileAsync(Guid propertyId, AttachPropertyFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Files(propertyId), request, ct);

    public Task<ApiResult> UpdateFileAsync(
        Guid propertyId, Guid fileId, UpdatePropertyFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Files(propertyId)}/{fileId}", request, ct);

    public Task<ApiResult<ApiFile>> DownloadFileAsync(Guid propertyId, Guid fileId, CancellationToken ct = default) =>
        api.GetFileAsync($"{Files(propertyId)}/{fileId}", "property-file", ct: ct);

    public Task<ApiResult> DetachFileAsync(Guid propertyId, Guid fileId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Files(propertyId)}/{fileId}", null, ct);

    private static string Files(Guid propertyId) => $"{Base}/{propertyId}/files";

    // ── Events ───────────────────────────────────────────────────────────────

    public Task<ApiResult<PagedResult<ExistingPropertyEvent>>> ListEventsAsync(
        Guid propertyId,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        DateTime? from = null,
        DateTime? to = null,
        string? sortBy = null,
        string? sortDir = null,
        int page = 1,
        int pageSize = PagedQuery.SizeAll,
        ContractEventSource? source = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingPropertyEvent>(
            PagedQuery.For(Events(propertyId))
                .Window(page, pageSize)
                .Add("search", search)
                .AddMany("types", types)
                .Add("from", from)
                .Add("to", to)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Add("source", source?.ToString())
                .Build(),
            ct);

    public Task<ApiResult<ExistingPropertyEvent>> CreateEventAsync(
        Guid propertyId, NewPropertyEvent request, CancellationToken ct = default) =>
        api.SendAsync<ExistingPropertyEvent>(HttpMethod.Post, Events(propertyId), request, ct);

    public Task<ApiResult<ExistingPropertyEvent>> UpdateEventAsync(
        Guid propertyId, Guid eventId, UpdatePropertyEvent request, CancellationToken ct = default) =>
        api.SendAsync<ExistingPropertyEvent>(HttpMethod.Put, $"{Events(propertyId)}/{eventId}", request, ct);

    public Task<ApiResult> DeleteEventAsync(Guid propertyId, Guid eventId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Events(propertyId)}/{eventId}", null, ct);

    private static string Events(Guid propertyId) => $"{Base}/{propertyId}/events";

    private static string SmartTags(Guid propertyId) => $"{Base}/{propertyId}/smart-tags";
    private static string Estimates(Guid propertyId) => $"{Base}/{propertyId}/estimates";
}
