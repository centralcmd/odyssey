using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Services;

/// <summary>
/// The news feed catalogue (<c>GET /api/news-feeds/catalogue</c>) and the news tag list (<c>GET /api/news-tags</c>),
/// shared by the three Internet pages (issue #473 §4.1, §8.3). It holds no <c>management</c> data: the catalogue
/// carries no feed URL, and the tag list none either.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the signed-in user's id and cleared on every <see cref="AuthenticationStateProvider.AuthenticationStateChanged"/>,
/// so one user's feeds and tags never show to the next, even without a reload (AC 15). A read made under one user is
/// discarded when the key no longer matches, which also covers a change that raced the event.
/// </para>
/// <para>
/// A failure is never cached. Every feed write calls <see cref="Invalidate"/>; every tag write
/// <see cref="InvalidateTags"/> (a tag write changes no feed, so it leaves the catalogue alone). The tag list is read
/// through the shared tag contract (<see cref="ITagsApiClient{TTag}.ListAllAsync"/>), as every tag picker reads its tags.
/// </para>
/// </remarks>
public interface INewsCatalogueCache
{
    Task<ApiResult<List<NewsFeedCatalogueEntry>>> GetCatalogueAsync(CancellationToken ct = default);

    Task<ApiResult<List<ExistingNewsTag>>> GetTagsAsync(CancellationToken ct = default);

    /// <summary>Drops both lists.</summary>
    void Invalidate();

    /// <summary>Drops the tag list only.</summary>
    void InvalidateTags();
}

/// <inheritdoc cref="INewsCatalogueCache" />
public sealed class NewsCatalogueCache : INewsCatalogueCache, IDisposable
{
    private readonly INewsApiClient news;
    private readonly ITagsApiClient<ExistingNewsTag> newsTags;
    private readonly AuthenticationStateProvider auth;
    private string? owner;
    private Task<ApiResult<List<NewsFeedCatalogueEntry>>>? catalogue;
    private Task<ApiResult<List<ExistingNewsTag>>>? tags;

    public NewsCatalogueCache(INewsApiClient news, ITagsApiClient<ExistingNewsTag> newsTags, AuthenticationStateProvider auth)
    {
        this.news = news;
        this.newsTags = newsTags;
        this.auth = auth;
        auth.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    public async Task<ApiResult<List<NewsFeedCatalogueEntry>>> GetCatalogueAsync(CancellationToken ct = default)
    {
        await EnsureOwnerAsync();
        var task = catalogue ??= news.GetCatalogueAsync(CancellationToken.None);
        var result = await task.WaitAsync(ct);
        if (!result.IsSuccess && ReferenceEquals(catalogue, task))
        {
            catalogue = null;
        }

        return result;
    }

    public async Task<ApiResult<List<ExistingNewsTag>>> GetTagsAsync(CancellationToken ct = default)
    {
        await EnsureOwnerAsync();
        var task = tags ??= newsTags.ListAllAsync(ct: CancellationToken.None);
        var result = await task.WaitAsync(ct);
        if (!result.IsSuccess && ReferenceEquals(tags, task))
        {
            tags = null;
        }

        return result;
    }

    public void Invalidate()
    {
        catalogue = null;
        tags = null;
    }

    public void InvalidateTags() => tags = null;

    private async Task EnsureOwnerAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        var id = UserKey(state.User);
        if (!string.Equals(id, owner, StringComparison.Ordinal))
        {
            Invalidate();
            owner = id;
        }
    }

    /// <summary>The cache key for a principal: its id claim, else its name, else empty for an anonymous one.</summary>
    internal static string UserKey(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? user.Identity?.Name ?? string.Empty;

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        Invalidate();
        owner = null;
    }

    public void Dispose() => auth.AuthenticationStateChanged -= OnAuthenticationStateChanged;
}
