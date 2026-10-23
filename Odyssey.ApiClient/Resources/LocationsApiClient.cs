using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the household locations endpoints (issue #350). There is no delete: a location is
/// archived by <see cref="UpdateAsync"/> with <see cref="UpdateLocation.Archived"/> set.
/// </summary>
/// <remarks>
/// <see cref="SearchAsync"/> is a <c>POST</c> with the text in the body, never a query string, so it does
/// not appear in any URL-based log. Its failures carry meaning callers branch on: <c>429</c> (per-user or
/// instance-wide limit) and <c>503</c> with the <c>geocoding-unavailable</c> code.
/// </remarks>
public interface ILocationsApiClient
{
    /// <summary>
    /// One page of locations. <paramref name="statuses"/> is the page's Active / Archived multi-select:
    /// exactly one value sends <c>status</c>, none or both sends nothing and the server returns every row.
    /// </summary>
    Task<ApiResult<PagedResult<ExistingLocation>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    Task<ApiResult<ExistingLocation>> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<ExistingLocation>> CreateAsync(NewLocation location, CancellationToken ct = default);

    /// <summary>A full replacement — not an upsert; an unknown id answers <c>404</c>.</summary>
    Task<ApiResult<ExistingLocation>> UpdateAsync(Guid id, UpdateLocation location, CancellationToken ct = default);

    Task<ApiResult<LocationSearchResponse>> SearchAsync(LocationSearchRequest request, CancellationToken ct = default);
}

/// <inheritdoc cref="ILocationsApiClient" />
public sealed class LocationsApiClient(IOdysseyApi api) : ILocationsApiClient
{
    private const string Base = "api/locations";

    public Task<ApiResult<PagedResult<ExistingLocation>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingLocation>(
            PagedQuery.For(Base)
                .Add("search", search)
                .AddSingle("status", statuses)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Window(page, pageSize)
                .Build(),
            ct);

    public Task<ApiResult<ExistingLocation>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingLocation>($"{Base}/{id}", ct);

    public Task<ApiResult<ExistingLocation>> CreateAsync(NewLocation location, CancellationToken ct = default) =>
        api.SendAsync<ExistingLocation>(HttpMethod.Post, Base, location, ct);

    public Task<ApiResult<ExistingLocation>> UpdateAsync(Guid id, UpdateLocation location, CancellationToken ct = default) =>
        api.SendAsync<ExistingLocation>(HttpMethod.Put, $"{Base}/{id}", location, ct);

    public Task<ApiResult<LocationSearchResponse>> SearchAsync(LocationSearchRequest request, CancellationToken ct = default) =>
        api.SendAsync<LocationSearchResponse>(HttpMethod.Post, $"{Base}/search", request, ct);
}
