using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Api.Tests.News.NewsApiTestSupport;

namespace Odyssey.Api.Tests.News;

/// <summary>
/// The file-reading article endpoints (issue #470 §5.2 items 13–15a): the detail body, the revisions and the image
/// content, each read from the per-factory volume through the object store.
/// </summary>
public sealed class NewsContentApiTests
{
    private const string Articles = "/api/news-articles";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public async Task AC27_The_detail_reads_its_body_from_the_plain_text_file_and_lists_only_stored_images()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var id = (await factory.SeedArticlesAsync(feed, 1))[0];
        await factory.SeedTextAsync(feed, id, "The whole body.\n\nSecond paragraph.");
        var stored = await factory.SeedImageAsync(feed, id, Png);
        await factory.SeedImageAsync(feed, id, Png, NewsArticleImageState.Failed, displayOrder: 1);
        using var client = factory.CreateClient();

        var detail = await (await client.GetAsync($"{Articles}/{id}")).ReadAsync<NewsArticleDetail>();

        Assert.Equal("The whole body.\n\nSecond paragraph.", detail.Body);
        Assert.False(detail.BodyUnavailable);
        Assert.Null(detail.BodyOmittedReason);
        Assert.Equal(stored, Assert.Single(detail.Images).Id);
        Assert.Equal(1, detail.ImageCount);
        Assert.Equal("Example News", detail.FeedTitle);
    }

    [Fact]
    public async Task AC27_A_degraded_volume_is_503_on_the_detail_while_lists_counts_read_state_and_tags_keep_working()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var id = (await factory.SeedArticlesAsync(feed, 1))[0];
        await factory.SeedTextAsync(feed, id, "Body");
        var tag = await factory.SeedTagAsync("Kept");
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<FileStorageVolumeRegistry>();
        registry.MarkDegraded(registry.Volumes.Single(), new IOException("test"));

        var detail = await client.GetAsync($"{Articles}/{id}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, detail.StatusCode);
        Assert.Equal(NewsErrorCodes.StorageUnavailable, (await detail.ProblemAsync()).Code());
        Assert.Equal(TimeSpan.FromSeconds(30), detail.Headers.RetryAfter!.Delta);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"{Articles}/{id}/revisions")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Articles)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/news-feeds/unread-counts")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Articles}/{id}/tags/{tag}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Articles}/{id}/read-state", JsonBody(new { isRead = true }))).StatusCode);
        registry.MarkAvailable(registry.Volumes.Single());
    }

    [Fact]
    public async Task A_missing_body_file_is_200_with_bodyUnavailable_and_an_omitted_body_opens_no_file()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var missing = (await factory.SeedArticlesAsync(feed, 1))[0];
        var fileId = await factory.SeedTextAsync(feed, missing, "Body");
        var omitted = (await factory.SeedArticlesAsync(feed, 1, "Omitted", omitted: NewsBodyOmittedReason.TooComplex))[0];
        await DeleteFileAsync(factory, fileId);
        using var client = factory.CreateClient();

        var gone = await (await client.GetAsync($"{Articles}/{missing}")).ReadAsync<NewsArticleDetail>();
        Assert.Null(gone.Body);
        Assert.True(gone.BodyUnavailable);

        var refused = await (await client.GetAsync($"{Articles}/{omitted}")).ReadAsync<NewsArticleDetail>();
        Assert.Equal((null, false, NewsBodyOmittedReason.TooComplex), (refused.Body, refused.BodyUnavailable, refused.BodyOmittedReason));
    }

    [Fact]
    public async Task AC49_AC76_Revisions_return_no_bodies_and_one_revision_returns_its_body_or_its_omitted_reason()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var id = (await factory.SeedArticlesAsync(feed, 1))[0];
        var first = await AddRevisionAsync(factory, id, 1, null);
        await AddRevisionAsync(factory, id, 2, NewsBodyOmittedReason.TooLarge);
        await factory.SeedTextAsync(feed, id, "The original body.", first);
        using var client = factory.CreateClient();

        var listing = await client.GetStringAsync($"{Articles}/{id}/revisions");
        var revisions = System.Text.Json.JsonSerializer.Deserialize<List<NewsArticleRevisionSummary>>(listing, Json)!;
        Assert.Equal([2, 1], revisions.Select(r => r.RevisionNumber));
        Assert.DoesNotContain("original body", listing, StringComparison.Ordinal);

        var one = await (await client.GetAsync($"{Articles}/{id}/revisions/1")).ReadAsync<NewsArticleRevisionDetail>();
        Assert.Equal("The original body.", one.Body);
        var two = await (await client.GetAsync($"{Articles}/{id}/revisions/2")).ReadAsync<NewsArticleRevisionDetail>();
        Assert.Equal((null, NewsBodyOmittedReason.TooLarge), (two.Body, two.BodyOmittedReason));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Articles}/{id}/revisions/3")).StatusCode);
    }

    [Fact]
    public async Task AC26_The_image_is_scoped_to_its_article_and_served_with_its_headers_and_a_304_never_opens_the_file()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var ids = await factory.SeedArticlesAsync(feed, 2);
        var image = await factory.SeedImageAsync(feed, ids[0], Png);
        var failed = await factory.SeedImageAsync(feed, ids[0], Png, NewsArticleImageState.Failed, displayOrder: 1);
        using var client = factory.CreateClient();
        var url = $"{Articles}/{ids[0]}/images/{image}/content";

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-site", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.True(response.Headers.CacheControl!.Private && response.Headers.CacheControl.NoCache);
        var etag = response.Headers.ETag!;
        Assert.False(etag.IsWeak);
        Assert.Equal($"\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Png))}\"", etag.Tag);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Articles}/{ids[1]}/images/{image}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Articles}/{ids[0]}/images/{failed}/content")).StatusCode);

        // Revalidation is answered from the database: with the file gone, a matching ETag is still a 304.
        await DeleteFileAsync(factory, await factory.WithContextAsync(c =>
            c.NewsArticleImages.Where(i => i.NewsArticleImageId == image).Select(i => i.NewsArticleFileId!.Value).SingleAsync()));
        var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);

        var range = new HttpRequestMessage(HttpMethod.Get, $"{Articles}/{ids[0]}/images/{failed}/content");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(range)).StatusCode);
    }

    [Fact]
    public async Task AC54_A_missing_image_file_is_404_and_the_get_writes_nothing()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var id = (await factory.SeedArticlesAsync(feed, 1))[0];
        var image = await factory.SeedImageAsync(feed, id, Png);
        var fileId = await factory.WithContextAsync(c => c.NewsArticleImages.Where(i => i.NewsArticleImageId == image).Select(i => i.NewsArticleFileId!.Value).SingleAsync());
        await DeleteFileAsync(factory, fileId);
        var before = await factory.WithContextAsync(c => c.NewsArticleImages.AsNoTracking().SingleAsync(i => i.NewsArticleImageId == image));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Articles}/{id}/images/{image}/content")).StatusCode);

        var after = await factory.WithContextAsync(c => c.NewsArticleImages.AsNoTracking().SingleAsync(i => i.NewsArticleImageId == image));
        Assert.Equal((before.State, before.Attempts, before.LastRequeuedAtUtc, before.NewsArticleFileId), (after.State, after.Attempts, after.LastRequeuedAtUtc, after.NewsArticleFileId));
    }

    [Fact]
    public async Task AC46_Detail_revisions_and_images_of_a_feed_pending_deletion_are_404()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync(archived: true, pendingDeletion: true);
        var id = (await factory.SeedArticlesAsync(feed, 1))[0];
        await factory.SeedTextAsync(feed, id, "Body");
        var image = await factory.SeedImageAsync(feed, id, Png);
        await AddRevisionAsync(factory, id, 1, null);
        using var client = factory.CreateClient();

        foreach (var url in new[]
        {
            $"{Articles}/{id}", $"{Articles}/{id}/revisions", $"{Articles}/{id}/revisions/1", $"{Articles}/{id}/images/{image}/content",
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        }

        Assert.Empty((await (await client.GetAsync($"{Articles}?includeArchivedFeeds=true")).ReadAsync<NewsArticlePage>()).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"{Articles}/{id}/read-state", JsonBody(new { isRead = true }))).StatusCode);
    }

    private static Task<Guid> AddRevisionAsync(OdysseyApiFactory factory, Guid articleId, int number, NewsBodyOmittedReason? omitted) =>
        factory.WithContextAsync(async context =>
        {
            var id = Guid.NewGuid();
            context.NewsArticleRevisions.Add(new NewsArticleRevision
            {
                NewsArticleRevisionId = id,
                NewsArticleId = articleId,
                RevisionNumber = number,
                Title = $"Revision {number}",
                ContentHash = new byte[32],
                IdentityHash = new byte[32],
                BodyLength = omitted is null ? 18 : 0,
                BodyOmittedReason = omitted,
                ValidFromUtc = Base.AddHours(number - 1),
                ReplacedAtUtc = Base.AddHours(number),
            });
            await context.SaveChangesAsync();
            return id;
        });

    private static async Task DeleteFileAsync(OdysseyApiFactory factory, Guid newsArticleFileId)
    {
        var relativePath = await factory.WithContextAsync(c =>
            (from file in c.NewsArticleFiles
             join stored in c.StoredObjects on file.StoredObjectId equals stored.Id
             where file.NewsArticleFileId == newsArticleFileId
             select stored.RelativePath).SingleAsync());
        Assert.StartsWith(NewsArticleStorage.RootFolder + "/", relativePath, StringComparison.Ordinal);
        File.Delete(Path.Combine(factory.FileVolumeRoot, relativePath));
    }
}
