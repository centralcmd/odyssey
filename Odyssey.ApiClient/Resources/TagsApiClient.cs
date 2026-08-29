using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>The wire shape every tag resource accepts on create and update.</summary>
/// <remarks>
/// Transaction tags, journal tags, task tags and photo tags are four separate server-side resources
/// with identical contracts — name, optional description, archival flag. The shared admin component
/// already treats them uniformly; this makes that uniformity explicit rather than leaving each page to
/// post an anonymous object.
///
/// The limits mirror the per-resource DTOs this stands in for (<c>NewTransactionTag</c>,
/// <c>NewJournalTag</c>, <c>NewJournalTaskTag</c>, <c>NewPhotoTag</c> and their <c>Update*</c>
/// siblings), all of which agree on 64 / 256. If one resource ever diverges, this record can no longer
/// represent all four and the generic client should be split — the annotations are what will surface
/// that.
/// </remarks>
public sealed record TagWrite(
    [property: Required]
    [property: StringLength(TagWrite.MaxNameLength)]
    string Name,
    [property: StringLength(TagWrite.MaxDescriptionLength)]
    string? Description,
    bool Archived)
{
    /// <summary>Matches <c>PhotoLimits.MaxTagNameLength</c> and its journal/task equivalents.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Matches <c>PhotoLimits.MaxTagDescriptionLength</c> and its journal/task equivalents.</summary>
    public const int MaxDescriptionLength = 256;
}

/// <summary>
/// Typed client for a tag resource. Closed over the concrete tag type and bound to its route at
/// registration, so <c>ITagsApiClient&lt;ExistingJournalTag&gt;</c> talks to <c>api/journal-tags</c>
/// and its three siblings to theirs.
/// </summary>
public interface ITagsApiClient<TTag>
{
    /// <summary>Every tag in one window — the pickers and filter options.</summary>
    Task<ApiResult<List<TTag>>> ListAllAsync(string? status = null, CancellationToken ct = default);

    /// <summary>One page for the admin list.</summary>
    Task<ApiResult<PagedResult<TTag>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? status = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(TagWrite tag, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, TagWrite tag, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="ITagsApiClient{TTag}" />
public sealed class TagsApiClient<TTag>(IOdysseyApi api, string basePath) : ITagsApiClient<TTag>
{
    public Task<ApiResult<List<TTag>>> ListAllAsync(string? status = null, CancellationToken ct = default) =>
        api.GetAllAsync<TTag>(PagedQuery.For(basePath).Add("status", status).Build(), ct);

    public Task<ApiResult<PagedResult<TTag>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? status = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default) =>
        api.GetPagedAsync<TTag>(
            PagedQuery.For(basePath)
                .Window(page, pageSize)
                .Add("search", search)
                .AddSingle("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult> CreateAsync(TagWrite tag, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, basePath, tag, ct);

    public Task<ApiResult> UpdateAsync(Guid id, TagWrite tag, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{basePath}/{id}", tag, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{basePath}/{id}", null, ct);
}
