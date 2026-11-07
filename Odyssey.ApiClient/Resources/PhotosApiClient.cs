using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed clients for the Photos module endpoints (issue #321; the upload and the original since issue #428).
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
    /// The absolute URL of a photo's WebP thumbnail (issue #362), for an <c>&lt;img src&gt;</c> only — never fetched
    /// through <see cref="HttpClient"/>. No cache-busting parameter: the endpoint's strong ETag revalidates it.
    /// </summary>
    string ThumbnailUrl(Guid photoId, ThumbnailSize size);

    /// <summary>
    /// The absolute URL of a photo's original (issue #428 §5.2), for an <c>&lt;img src&gt;</c> or a download link.
    /// </summary>
    string ContentUrl(Guid photoId);

    /// <summary>
    /// Uploads a photo (issue #428 §5.1): the image as part <c>file</c> plus the optional fields. Returns the created
    /// photo; a <c>409</c> (duplicate) is left on the result with its problem, whose <c>existingPhotoId</c> extension
    /// names the existing photo for a caller holding <c>photos.read</c>.
    /// </summary>
    Task<ApiResult<ExistingPhoto>> UploadAsync(ApiUpload file, NewPhotoUpload? fields = null, CancellationToken ct = default);

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

    public string ThumbnailUrl(Guid photoId, ThumbnailSize size) =>
        new Uri(api.BaseAddress!, $"{Base}/{photoId}/thumbnail?size={size}").ToString();

    public Task<ApiResult<PhotoLibraryStats>> GetStatsAsync(CancellationToken ct = default) =>
        api.GetAsync<PhotoLibraryStats>($"{Base}/stats", ct);

    public string ContentUrl(Guid photoId) =>
        new Uri(api.BaseAddress!, $"{Base}/{photoId}/content").ToString();

    public Task<ApiResult<ExistingPhoto>> UploadAsync(ApiUpload file, NewPhotoUpload? fields = null, CancellationToken ct = default) =>
        api.UploadAsync<ExistingPhoto>(Base, file, ct: ct, fields: FormFields(fields));

    // Each id list is sent as the repeated field name without brackets, which ASP.NET binds to an array.
    private static List<KeyValuePair<string, string>> FormFields(NewPhotoUpload? fields)
    {
        var form = new List<KeyValuePair<string, string>>();
        if (fields is null)
        {
            return form;
        }

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                form.Add(new(name, value));
            }
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Add("title", fields.Title);
        Add("caption", fields.Caption);
        Add("takenAt", fields.TakenAt?.ToString("O", invariant));
        Add("capturedLatitude", fields.CapturedLatitude?.ToString("R", invariant));
        Add("capturedLongitude", fields.CapturedLongitude?.ToString("R", invariant));
        Add("locationName", fields.LocationName);
        Add("pixelWidth", fields.PixelWidth?.ToString(invariant));
        Add("pixelHeight", fields.PixelHeight?.ToString(invariant));
        if (fields.Favourite)
        {
            Add("favourite", "true");
        }

        form.AddRange(fields.TagIds.Select(id => new KeyValuePair<string, string>("tagIds", id.ToString())));
        form.AddRange(fields.PersonContactIds.Select(id => new KeyValuePair<string, string>("personContactIds", id.ToString())));
        form.AddRange(fields.AlbumIds.Select(id => new KeyValuePair<string, string>("albumIds", id.ToString())));
        return form;
    }

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
