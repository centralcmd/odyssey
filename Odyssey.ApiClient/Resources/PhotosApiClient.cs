using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed read clients for the Photos module endpoints (issue #321). Writes go through
/// <see cref="IOdysseyApi"/> at the call sites, as the sibling Finance/Journal surfaces do.
/// Cross-claim links (tags, people, files, albums) come back as ids only — pages hydrate names via the
/// respective claim-gated endpoints (spec §10.5).
/// </summary>
public interface IPhotosApiClient
{
    /// <summary>Server-side paged photo list (issue #277) with search + tag/person/album/date-range/archival/favourites
    /// filters and sort. The result distinguishes Empty (a success with no items) from Error.</summary>
    Task<ApiResult<PagedResult<PhotoSummary>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? personIds = null,
        IReadOnlyCollection<string>? albumIds = null,
        DateTime? from = null,
        DateTime? to = null,
        bool favouritesOnly = false,
        string? status = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Loads one photo with its full metadata + link id sets. Null on failure.</summary>
    Task<ExistingPhoto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Aggregate library counts for the Overview panel (server-computed; no full-library fetch).</summary>
    Task<ApiResult<PhotoLibraryStats>> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Promotes an already-uploaded file into a library photo. A <c>409</c> means the file is already
    /// a photo — callers treat that as a benign no-op, so the status is left on the result rather than
    /// being folded into a generic failure. Returns the created photo (the endpoint answers
    /// <c>201</c> with a body), which the album-create flow needs to collect the new ids.
    /// </summary>
    Task<ApiResult<ExistingPhoto>> CreateAsync(NewPhoto photo, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, UpdatePhoto photo, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IPhotosApiClient" />
public sealed class PhotosApiClient(IOdysseyApi api) : IPhotosApiClient
{
    private const string Base = "api/photos";

    public Task<ApiResult<PagedResult<PhotoSummary>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? personIds = null,
        IReadOnlyCollection<string>? albumIds = null,
        DateTime? from = null,
        DateTime? to = null,
        bool favouritesOnly = false,
        string? status = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<PhotoSummary>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .AddMany("tagIds", tagIds)
                .AddMany("personIds", personIds)
                .AddMany("albumIds", albumIds)
                .Add("from", from)
                .Add("to", to)
                .AddBool("favouritesOnly", favouritesOnly ? true : null)
                .Add("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public async Task<ExistingPhoto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingPhoto>($"{Base}/{id}", ct)).Value;

    public Task<ApiResult<PhotoLibraryStats>> GetStatsAsync(CancellationToken ct = default) =>
        api.GetAsync<PhotoLibraryStats>($"{Base}/stats", ct);

    public Task<ApiResult<ExistingPhoto>> CreateAsync(NewPhoto photo, CancellationToken ct = default) =>
        api.SendAsync<ExistingPhoto>(HttpMethod.Post, Base, photo, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdatePhoto photo, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", photo, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}

/// <summary>Typed read client for the photo-tags endpoints (issue #321).</summary>
public interface IPhotoTagsApiClient
{
    /// <summary>All photo tags (single large window) — for filter options and the tag picker.</summary>
    Task<ApiResult<List<ExistingPhotoTag>>> ListAllAsync(string? status = null, CancellationToken ct = default);

    /// <summary>Server-side paged photo-tag list for the admin page.</summary>
    Task<ApiResult<PagedResult<ExistingPhotoTag>>> ListAsync(
        int page, int pageSize, string? search = null, string? status = null, string? sortBy = null,
        string? sortDir = null, CancellationToken ct = default);
}

/// <inheritdoc cref="IPhotoTagsApiClient" />
public sealed class PhotoTagsApiClient(IOdysseyApi api) : IPhotoTagsApiClient
{
    private const string Base = "api/photo-tags";

    public Task<ApiResult<List<ExistingPhotoTag>>> ListAllAsync(string? status = null, CancellationToken ct = default) =>
        api.GetAllAsync<ExistingPhotoTag>(PagedQuery.For(Base).Add("status", status).Build(), ct);

    public Task<ApiResult<PagedResult<ExistingPhotoTag>>> ListAsync(
        int page, int pageSize, string? search = null, string? status = null, string? sortBy = null,
        string? sortDir = null, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingPhotoTag>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .Add("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);
}

/// <summary>Typed read client for the albums endpoints (issue #321).</summary>
public interface IAlbumsApiClient
{
    /// <summary>All albums (single large window) — for the albums grid and the album picker.</summary>
    Task<ApiResult<List<PhotoAlbumSummary>>> ListAllAsync(string? status = null, CancellationToken ct = default);

    /// <summary>Loads one album with its ordered member photo ids + cover. Null on failure.</summary>
    Task<ExistingPhotoAlbum?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewPhotoAlbum album, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, UpdatePhotoAlbum album, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IAlbumsApiClient" />
public sealed class AlbumsApiClient(IOdysseyApi api) : IAlbumsApiClient
{
    private const string Base = "api/albums";

    public Task<ApiResult<List<PhotoAlbumSummary>>> ListAllAsync(string? status = null, CancellationToken ct = default) =>
        api.GetAllAsync<PhotoAlbumSummary>(PagedQuery.For(Base).Add("status", status).Build(), ct);

    public async Task<ExistingPhotoAlbum?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingPhotoAlbum>($"{Base}/{id}", ct)).Value;

    public Task<ApiResult> CreateAsync(NewPhotoAlbum album, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, album, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdatePhotoAlbum album, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", album, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}
