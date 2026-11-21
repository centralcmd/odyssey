using Odyssey.Dtos;
using Odyssey.Dtos.Books;

namespace Odyssey.ApiClient.Resources;

/// <summary>Filters for <see cref="IBooksApiClient.ListAsync"/>; every member maps to one query-string key.</summary>
public sealed record BooksListFilter
{
    public string? Search { get; init; }

    public IReadOnlyCollection<string>? Statuses { get; init; }

    public IReadOnlyCollection<string>? Formats { get; init; }

    public IReadOnlyCollection<Guid>? TagIds { get; init; }

    public Guid? ContactId { get; init; }

    public bool? HasCover { get; init; }

    public bool? HasAttachments { get; init; }

    public string? SortBy { get; init; }

    public string? SortDir { get; init; }
}

/// <summary>
/// Typed client for the book catalogue (issue #527). <c>PUT</c> is not an upsert, and a stale <c>version</c> answers
/// <c>409</c> with code <c>book-version-conflict</c>; callers branch on <see cref="ApiResult.Status"/> and the problem.
/// </summary>
public interface IBooksApiClient
{
    Task<ApiResult<PagedResult<BookSummary>>> ListAsync(int page, int pageSize, BooksListFilter? filter = null, CancellationToken ct = default);

    Task<ApiResult<ExistingBook>> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<ExistingBook>> CreateAsync(NewBook book, CancellationToken ct = default);

    Task<ApiResult<ExistingBook>> UpdateAsync(Guid id, UpdateBook book, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IBooksApiClient" />
public sealed class BooksApiClient(IOdysseyApi api) : IBooksApiClient
{
    private const string BasePath = "api/books";

    public Task<ApiResult<PagedResult<BookSummary>>> ListAsync(
        int page, int pageSize, BooksListFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new BooksListFilter();
        var url = PagedQuery.For(BasePath)
            .Window(page, pageSize)
            .Add("search", filter.Search)
            .AddMany("status", filter.Statuses)
            .AddMany("format", filter.Formats)
            .AddMany("tagIds", filter.TagIds?.Select(id => id.ToString()))
            .Add("contactId", filter.ContactId?.ToString())
            .AddBool("hasCover", filter.HasCover)
            .AddBool("hasAttachments", filter.HasAttachments)
            .Add("sortBy", filter.SortBy)
            .Add("sortDir", filter.SortDir)
            .Build();
        return api.GetPagedAsync<BookSummary>(url, ct);
    }

    public Task<ApiResult<ExistingBook>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingBook>($"{BasePath}/{id}", ct);

    public Task<ApiResult<ExistingBook>> CreateAsync(NewBook book, CancellationToken ct = default) =>
        api.SendAsync<ExistingBook>(HttpMethod.Post, BasePath, book, ct);

    public Task<ApiResult<ExistingBook>> UpdateAsync(Guid id, UpdateBook book, CancellationToken ct = default) =>
        api.SendAsync<ExistingBook>(HttpMethod.Put, $"{BasePath}/{id}", book, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{BasePath}/{id}", body: null, ct);
}
