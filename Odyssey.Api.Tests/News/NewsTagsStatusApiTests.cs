using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Internet.News;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Api.Tests.News.NewsApiTestSupport;

namespace Odyssey.Api.Tests.News;

/// <summary>News tags, the status endpoint, the manual run and the claim gate of every news endpoint (issue #470 §5.4, §5.5).</summary>
public sealed class NewsTagsStatusApiTests
{
    private const string Tags = "/api/news-tags";

    [Fact]
    public async Task Tags_are_created_updated_archived_and_a_duplicate_name_in_any_case_is_409()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(Tags, new NewNewsTag { Name = "  World ", Icon = null, Color = "#a1b2c3" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tag = await created.ReadAsync<ExistingNewsTag>();
        Assert.Equal(("World", "#A1B2C3"), (tag.Name, tag.Color));

        var duplicate = await client.PostAsJsonAsync(Tags, new NewNewsTag { Name = "WORLD" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(NewsErrorCodes.TagDuplicate, (await duplicate.ProblemAsync()).Code());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Tags, new NewNewsTag { Name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Tags, new NewNewsTag { Name = "X", Color = "red" })).StatusCode);

        var updated = await (await client.PutAsJsonAsync($"{Tags}/{tag.Id}", new UpdateNewsTag { Name = "Earth", Description = "Planet" })).ReadAsync<ExistingNewsTag>();
        Assert.Equal(("Earth", "Planet", (string?)null), (updated.Name, updated.Description, updated.Color));
        var archived = await (await client.PostAsync($"{Tags}/{tag.Id}/archive", null)).ReadAsync<ExistingNewsTag>();
        Assert.True(archived.Archived);
        Assert.False((await (await client.PostAsync($"{Tags}/{tag.Id}/unarchive", null)).ReadAsync<ExistingNewsTag>()).Archived);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Tags}/{Guid.NewGuid()}", new UpdateNewsTag { Name = "Y" })).StatusCode);
    }

    [Fact]
    public async Task AC71_All_500_tags_come_back_in_one_response_and_the_501st_is_422()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        await factory.WithContextAsync(async context =>
        {
            for (var i = 0; i < NewsLimits.MaxTags; i++)
            {
                context.NewsTags.Add(new NewsTag { NewsTagId = Guid.NewGuid(), Name = $"Tag {i}", NameKey = $"tag {i}", CreatedAtUtc = Base, UpdatedAtUtc = Base });
            }

            await context.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        Assert.Equal(NewsLimits.MaxTags, (await (await client.GetAsync(Tags)).ReadAsync<List<ExistingNewsTag>>()).Count);
        var refused = await client.PostAsJsonAsync(Tags, new NewNewsTag { Name = "One more" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(NewsErrorCodes.TagLimit, (await refused.ProblemAsync()).Code());
    }

    [Fact]
    public async Task AC9_A_tag_in_use_is_409_with_counts_and_a_detach_without_news_update_is_403_and_changes_nothing()
    {
        await using var admin = new OdysseyApiFactory(AllNewsClaims);
        var tag = await admin.SeedTagAsync("Used");
        var feed = await admin.SeedFeedAsync();
        var article = (await admin.SeedArticlesAsync(feed, 1))[0];
        await admin.TagFeedAsync(feed, tag);
        await admin.TagArticleAsync(article, tag);
        await using var tagsOnly = new OdysseyApiFactory(
            [PermissionClaims.NewsRead, PermissionClaims.NewsTagsDelete, PermissionClaims.NewsFeedsUpdate], sharingStoreWith: admin);
        using var limited = tagsOnly.CreateClient();
        using var client = admin.CreateClient();

        var inUse = await client.DeleteAsync($"{Tags}/{tag}");
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        var problem = await inUse.ProblemAsync();
        Assert.Equal(NewsErrorCodes.TagInUse, problem.Code());
        Assert.Equal((1, 1), (problem.GetProperty("feedCount").GetInt32(), problem.GetProperty("articleCount").GetInt32()));

        Assert.Equal(HttpStatusCode.Forbidden, (await limited.DeleteAsync($"{Tags}/{tag}?detachLinks=true")).StatusCode);
        Assert.Equal(2, await admin.WithContextAsync(async c => await c.NewsFeedTags.CountAsync() + await c.NewsArticleTags.CountAsync()));

        var version = await admin.WithContextAsync(c => c.NewsFeeds.Where(f => f.NewsFeedId == feed).Select(f => f.SettingsVersion).SingleAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Tags}/{tag}?detachLinks=true")).StatusCode);
        Assert.Equal(0, await admin.WithContextAsync(async c => await c.NewsTags.CountAsync() + await c.NewsFeedTags.CountAsync() + await c.NewsArticleTags.CountAsync()));
        Assert.NotEqual(version, await admin.WithContextAsync(c => c.NewsFeeds.Where(f => f.NewsFeedId == feed).Select(f => f.SettingsVersion).SingleAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Tags}/{tag}")).StatusCode);
    }

    [Fact]
    public async Task AC42_AC58_Status_answers_200_with_the_resolved_text_quota_its_reached_flag_and_feed_titles()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.NewsTextStoreMaxGiB, "1");
        var feed = await factory.SeedFeedAsync(state: s => s.QuarantinedAtUtc = Base, articles: 4);
        var id = (await factory.SeedArticlesAsync(feed, 1))[0];
        await factory.SeedImageAsync(feed, id, [1, 2, 3, 4]);
        await factory.WithContextAsync(async context =>
        {
            var totals = await context.NewsStorageTotals.SingleAsync();
            totals.TextAndSourceBytes = NewsTextQuota.BytesPerGiB;
            totals.ImageBytes = 4;
            await context.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        var status = await (await client.GetAsync("/api/news-status")).ReadAsync<NewsStatus>();

        Assert.Equal(NewsStorageState.Ready, status.Storage);
        Assert.Equal("photos", status.Volume);
        Assert.Equal(NewsTextQuota.BytesPerGiB, status.TextAndSourceQuotaBytes);
        Assert.True(status.TextAndSourceQuotaReached);
        Assert.False(status.ImageQuotaReached);
        Assert.Equal((1, 1, 5), (status.FeedCount, status.QuarantinedFeedCount, status.ArticleCount));
        var top = Assert.Single(status.TopFeedsByImageBytes);
        Assert.Equal(("Example News", 4L), (top.DisplayTitle, top.ImageBytes));
    }

    [Fact]
    public async Task AC42_Status_answers_200_Unconfigured_on_a_host_with_no_volume()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims, configuration: new Dictionary<string, string?> { [OdysseyApiFactory.NoFileVolumeKey] = "true" });
        using var client = factory.CreateClient();

        var status = await (await client.GetAsync("/api/news-status")).ReadAsync<NewsStatus>();

        Assert.Equal((NewsStorageState.Unconfigured, (string?)null), (status.Storage, status.Volume));
    }

    [Fact]
    public async Task The_manual_run_executes_inline_under_Testing_and_marks_every_eligible_feed_due()
    {
        await using var factory = new OdysseyApiFactory([.. AllNewsClaims, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        var due = await factory.SeedFeedAsync(url: "https://a.example.org/rss");
        var paused = await factory.SeedFeedAsync(url: "https://b.example.org/rss", active: false);
        var recent = await factory.SeedFeedAsync(url: "https://c.example.org/rss", state: s => s.LastFetchAttemptAtUtc = DateTime.UtcNow);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/background-jobs/NewsFeedFetch/runs", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = await factory.WithContextAsync(c => c.Set<NewsFeedJobRun>().AsNoTracking().SingleAsync());
        Assert.Equal((JobRunStatus.Succeeded, 1), (run.Status, run.FeedsMarked));
        var flags = await factory.WithContextAsync(c => c.NewsFeedFetchStates.ToDictionaryAsync(s => s.NewsFeedId, s => s.RefreshRequestedAtUtc));
        Assert.NotNull(flags[due]);
        Assert.Null(flags[paused]);
        Assert.Null(flags[recent]);
    }

    public static TheoryData<string, string, string, object?> Gates() => new()
    {
        { "GET", "/api/news-feeds", PermissionClaims.NewsRead, null },
        { "GET", "/api/news-feeds/catalogue", PermissionClaims.NewsRead, null },
        { "GET", "/api/news-feeds/unread-counts", PermissionClaims.NewsRead, null },
        { "GET", $"/api/news-feeds/{Guid.Empty}", PermissionClaims.NewsRead, null },
        { "POST", "/api/news-feeds", PermissionClaims.NewsFeedsCreate, new { url = "https://a.example.org/rss" } },
        { "PUT", $"/api/news-feeds/{Guid.Empty}", PermissionClaims.NewsFeedsUpdate, new { } },
        { "PUT", $"/api/news-feeds/{Guid.Empty}/tags", PermissionClaims.NewsFeedsUpdate, new { tagIds = Array.Empty<Guid>() } },
        { "POST", $"/api/news-feeds/{Guid.Empty}/refresh", PermissionClaims.NewsFeedsUpdate, null },
        { "POST", $"/api/news-feeds/{Guid.Empty}/archive", PermissionClaims.NewsFeedsUpdate, null },
        { "POST", $"/api/news-feeds/{Guid.Empty}/unarchive", PermissionClaims.NewsFeedsUpdate, null },
        { "POST", $"/api/news-feeds/{Guid.Empty}/release-quarantine", PermissionClaims.NewsFeedsUpdate, null },
        { "DELETE", $"/api/news-feeds/{Guid.Empty}", PermissionClaims.NewsFeedsDelete, null },
        { "GET", "/api/news-articles", PermissionClaims.NewsRead, null },
        { "GET", $"/api/news-articles/{Guid.Empty}", PermissionClaims.NewsRead, null },
        { "GET", $"/api/news-articles/{Guid.Empty}/revisions", PermissionClaims.NewsRead, null },
        { "GET", $"/api/news-articles/{Guid.Empty}/revisions/1", PermissionClaims.NewsRead, null },
        { "GET", $"/api/news-articles/{Guid.Empty}/images/{Guid.Empty}/content", PermissionClaims.NewsRead, null },
        { "PUT", $"/api/news-articles/{Guid.Empty}/read-state", PermissionClaims.NewsUpdate, new { isRead = true } },
        { "POST", "/api/news-articles/read-state", PermissionClaims.NewsUpdate, new { articleIds = new[] { Guid.Empty }, isRead = true } },
        { "POST", "/api/news-articles/mark-read", PermissionClaims.NewsUpdate, new { asOfUtc = "2026-11-01T00:00:00Z" } },
        { "PUT", $"/api/news-articles/{Guid.Empty}/tags", PermissionClaims.NewsUpdate, new { tagIds = Array.Empty<Guid>() } },
        { "PUT", $"/api/news-articles/{Guid.Empty}/tags/{Guid.Empty}", PermissionClaims.NewsUpdate, null },
        { "DELETE", $"/api/news-articles/{Guid.Empty}/tags/{Guid.Empty}", PermissionClaims.NewsUpdate, null },
        { "GET", "/api/news-tags", PermissionClaims.NewsRead, null },
        { "GET", $"/api/news-tags/{Guid.Empty}", PermissionClaims.NewsRead, null },
        { "POST", "/api/news-tags", PermissionClaims.NewsTagsCreate, new { name = "X" } },
        { "PUT", $"/api/news-tags/{Guid.Empty}", PermissionClaims.NewsTagsUpdate, new { name = "X" } },
        { "POST", $"/api/news-tags/{Guid.Empty}/archive", PermissionClaims.NewsTagsUpdate, null },
        { "POST", $"/api/news-tags/{Guid.Empty}/unarchive", PermissionClaims.NewsTagsUpdate, null },
        { "DELETE", $"/api/news-tags/{Guid.Empty}", PermissionClaims.NewsTagsDelete, null },
        { "GET", "/api/news-status", PermissionClaims.FileStorageRead, null },
    };

    /// <summary>AC 2: every endpoint is 403 without its claim and gets past authorization with it.</summary>
    [Theory]
    [MemberData(nameof(Gates))]
    public async Task AC2_Every_news_endpoint_is_403_without_its_claim(string method, string url, string claim, object? body)
    {
        await using var without = new OdysseyApiFactory([.. AllNewsClaims.Where(c => c != claim)]);
        await using var with = new OdysseyApiFactory([claim]);

        foreach (var (factory, forbidden) in new[] { (without, true), (with, false) })
        {
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = body is null ? null : JsonBody(body) };
            var status = (await client.SendAsync(request)).StatusCode;
            if (forbidden)
            {
                Assert.Equal(HttpStatusCode.Forbidden, status);
            }
            else
            {
                Assert.NotEqual(HttpStatusCode.Forbidden, status);
                Assert.NotEqual(HttpStatusCode.Unauthorized, status);
            }
        }
    }
}
