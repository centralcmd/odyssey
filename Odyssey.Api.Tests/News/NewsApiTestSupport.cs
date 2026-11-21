using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;

namespace Odyssey.Api.Tests.News;

/// <summary>
/// Seeding for the news API tests (issue #470). Feeds are created through the API where the create path is the
/// subject; articles, files and worker state are written straight to the store, because only the worker writes them.
/// </summary>
internal static class NewsApiTestSupport
{
    public static readonly string[] AllNewsClaims =
    [
        PermissionClaims.NewsRead, PermissionClaims.NewsUpdate,
        PermissionClaims.NewsFeedsCreate, PermissionClaims.NewsFeedsUpdate, PermissionClaims.NewsFeedsDelete,
        PermissionClaims.NewsTagsCreate, PermissionClaims.NewsTagsUpdate, PermissionClaims.NewsTagsDelete,
        PermissionClaims.FileStorageRead,
    ];

    public static readonly string[] ReadOnly = [PermissionClaims.NewsRead];

    public static readonly DateTime Base = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    public static string? Code(this JsonElement problem) =>
        problem.TryGetProperty("code", out var code) ? code.GetString() : null;

    public static bool HasError(this JsonElement problem, string field) =>
        problem.TryGetProperty("errors", out var errors)
        && errors.EnumerateObject().Any(e => string.Equals(e.Name, field, StringComparison.OrdinalIgnoreCase));

    public static async Task WithContextAsync(this OdysseyApiFactory factory, Func<OdysseyContext, Task> work)
    {
        using var scope = factory.Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    public static async Task<T> WithContextAsync<T>(this OdysseyApiFactory factory, Func<OdysseyContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    /// <summary>A feed as the API would create it, plus the worker-owned fields a test needs.</summary>
    public static Task<Guid> SeedFeedAsync(
        this OdysseyApiFactory factory,
        string url = "https://feeds.example.net/rss",
        string? title = "Example News",
        string? siteUrl = "https://www.example.com/",
        bool archived = false,
        bool active = true,
        bool pendingDeletion = false,
        Action<NewsFeedFetchState>? state = null,
        int articles = 0,
        int unread = 0) =>
        factory.WithContextAsync(async context =>
        {
            var id = Guid.NewGuid();
            context.NewsFeeds.Add(new NewsFeed
            {
                NewsFeedId = id,
                Url = url,
                UrlKeyHash = NewsUrlKey.Hash(NewsUrlKey.UrlKey(url) ?? url + id),
                IsActive = active,
                DownloadImages = true,
                CreatedAtUtc = Base,
                ArchivedAtUtc = archived ? Base : null,
                DeletionRequestedAtUtc = pendingDeletion ? Base : null,
                SettingsVersion = Guid.NewGuid(),
            });
            var fetch = new NewsFeedFetchState
            {
                NewsFeedId = id,
                FetchFence = Guid.NewGuid(),
                NextFetchAtUtc = Base,
                Title = title,
                SiteUrl = siteUrl,
                Language = "en",
                Format = NewsFeedFormat.Rss,
                LastSuccessAtUtc = Base,
                LastFetchAttemptAtUtc = Base,
            };
            state?.Invoke(fetch);
            context.NewsFeedFetchStates.Add(fetch);
            context.NewsFeedCounters.Add(new NewsFeedCounter { NewsFeedId = id, ArticleCount = articles, UnreadCount = unread });
            await context.SaveChangesAsync();
            return id;
        });

    public static Task<Guid> SeedTagAsync(this OdysseyApiFactory factory, string name, bool archived = false) =>
        factory.WithContextAsync(async context =>
        {
            var id = Guid.NewGuid();
            context.NewsTags.Add(new NewsTag
            {
                NewsTagId = id,
                Name = name,
                NameKey = name.ToLowerInvariant(),
                CreatedAtUtc = Base,
                UpdatedAtUtc = Base,
                ArchivedAtUtc = archived ? Base : null,
            });
            await context.SaveChangesAsync();
            return id;
        });

    public static Task TagFeedAsync(this OdysseyApiFactory factory, Guid feedId, Guid tagId) =>
        factory.WithContextAsync(async context =>
        {
            context.NewsFeedTags.Add(new NewsFeedTag { NewsFeedTagId = Guid.NewGuid(), NewsFeedId = feedId, NewsTagId = tagId });
            await context.SaveChangesAsync();
        });

    public static Task TagArticleAsync(this OdysseyApiFactory factory, Guid articleId, Guid tagId) =>
        factory.WithContextAsync(async context =>
        {
            var article = await context.NewsArticles.AsNoTracking().SingleAsync(a => a.NewsArticleId == articleId);
            context.NewsArticleTags.Add(new NewsArticleTag
            {
                NewsArticleTagId = Guid.NewGuid(),
                NewsArticleId = articleId,
                NewsTagId = tagId,
                SortAtUtc = article.SortAtUtc,
            });
            await context.SaveChangesAsync();
        });

    /// <summary>Articles <c>i = 0 … count-1</c>, the i-th sorted <paramref name="step"/> × i after <see cref="Base"/>.</summary>
    public static Task<List<Guid>> SeedArticlesAsync(
        this OdysseyApiFactory factory, Guid feedId, int count, string title = "Article", bool read = false,
        TimeSpan? step = null, DateTime? firstSeen = null, NewsBodyOmittedReason? omitted = null, DateTime? start = null) =>
        factory.WithContextAsync(async context =>
        {
            var ids = new List<Guid>(count);
            var spacing = step ?? TimeSpan.FromMinutes(1);
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                var sortAt = (start ?? Base) + spacing * i;
                context.NewsArticles.Add(new NewsArticle
                {
                    NewsArticleId = id,
                    NewsFeedId = feedId,
                    Bucket = 1,
                    Title = $"{title} {i}",
                    Excerpt = $"Excerpt of {title} {i}",
                    ContentHash = new byte[32],
                    IdentityHash = new byte[32],
                    FirstSeenAtUtc = firstSeen ?? sortAt,
                    LastSeenAtUtc = firstSeen ?? sortAt,
                    SortAtUtc = sortAt,
                    IsRead = read,
                    ReadAtUtc = read ? sortAt : null,
                    BodyOmittedReason = omitted,
                    Language = "en",
                });
                ids.Add(id);
            }

            await context.SaveChangesAsync();
            var counter = await context.NewsFeedCounters.SingleAsync(c => c.NewsFeedId == feedId);
            counter.ArticleCount += count;
            counter.UnreadCount += read ? 0 : count;
            await context.SaveChangesAsync();
            return ids;
        });

    /// <summary>Writes a real file on the per-factory volume and its <see cref="NewsArticleFile"/> row.</summary>
    public static Task<Guid> SeedFileAsync(
        this OdysseyApiFactory factory, Guid feedId, Guid articleId, NewsArticleFileKind kind, byte[] bytes, string contentType,
        Guid? revisionId = null) =>
        Task.Run(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
            await using var content = new MemoryStream(bytes);
            var stored = await store.PutAsync(
                NewsArticleStorage.FeatureKey, new NewsArticleLayout(feedId, 1, articleId), null, null, contentType, content, bytes.Length + 1,
                CancellationToken.None);
            var fileId = Guid.NewGuid();
            context.NewsArticleFiles.Add(new NewsArticleFile
            {
                NewsArticleFileId = fileId,
                NewsArticleId = articleId,
                NewsArticleRevisionId = revisionId,
                Kind = kind,
                StoredObjectId = stored.Id,
                ContentType = contentType,
                SizeBytes = bytes.Length,
                Sha256 = System.Security.Cryptography.SHA256.HashData(bytes),
                CreatedAtUtc = Base,
            });
            await context.SaveChangesAsync();
            return fileId;
        });

    public static Task<Guid> SeedTextAsync(this OdysseyApiFactory factory, Guid feedId, Guid articleId, string text, Guid? revisionId = null) =>
        factory.SeedFileAsync(feedId, articleId, NewsArticleFileKind.PlainText, Encoding.UTF8.GetBytes(text), "text/plain", revisionId);

    public static async Task<Guid> SeedImageAsync(
        this OdysseyApiFactory factory, Guid feedId, Guid articleId, byte[] bytes, NewsArticleImageState state = NewsArticleImageState.Stored,
        int displayOrder = 0)
    {
        var fileId = await factory.SeedFileAsync(feedId, articleId, NewsArticleFileKind.Image, bytes, "image/png");
        return await factory.WithContextAsync(async context =>
        {
            var id = Guid.NewGuid();
            context.NewsArticleImages.Add(new NewsArticleImage
            {
                NewsArticleImageId = id,
                NewsArticleId = articleId,
                NewsArticleFileId = fileId,
                Position = displayOrder,
                DisplayOrder = displayOrder,
                Origin = NewsArticleImageOrigin.Enclosure,
                SourceUrl = "https://cdn.example.com/a.png",
                AltText = "A picture",
                State = state,
            });
            await context.SaveChangesAsync();
            return id;
        });
    }

    public static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
}
