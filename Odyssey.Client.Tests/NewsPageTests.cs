using System.Net;
using Bunit;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>/news</c> (issue #473 §3.2): the remote article list and its paging rules, the filter bar, the cards and their
/// read state, the header's overview and Attention, Hide read, and the shared fixtures the other /news tests use.
/// </summary>
public class NewsPageTests
{
    internal static readonly Guid FeedId = Guid.NewGuid();

    internal static readonly string[] ReaderOnly = [PermissionClaims.NewsRead];

    internal static NewsArticleSummary Article(string title, Action<NewsArticleSummary>? edit = null)
    {
        var article = new NewsArticleSummary
        {
            Id = Guid.NewGuid(), FeedId = FeedId, FeedTitle = "Example News", Title = title, Author = "Kari",
            Excerpt = "An excerpt.", Link = "https://example.org/a", PublishedAtUtc = Now.UtcDateTime.AddHours(-3),
            SortAtUtc = Now.UtcDateTime.AddHours(-3), FirstSeenAtUtc = Now.UtcDateTime.AddHours(-2),
        };
        edit?.Invoke(article);
        return article;
    }

    internal static NewsFeedCatalogueEntry CatalogueFeed(string title, Action<NewsFeedCatalogueEntry>? edit = null, Guid? id = null)
    {
        var feed = new NewsFeedCatalogueEntry
        {
            Id = id ?? Guid.NewGuid(), DisplayTitle = title, Health = NewsFeedHealth.Healthy, State = NewsFeedState.Active,
            UnreadCount = 4, ArticleCount = 10,
        };
        edit?.Invoke(feed);
        return feed;
    }

    internal static NewsArticlePage Page(IEnumerable<NewsArticleSummary> items, int? total = null, string? next = null, bool capped = false)
    {
        var list = items.ToList();
        return new NewsArticlePage
        {
            Items = list, TotalCount = total ?? list.Count, TotalCountCapped = capped, NextCursor = next,
            AsOfUtc = Now.UtcDateTime,
        };
    }

    internal static NewsArticleDetail DetailOf(NewsArticleSummary article, Action<NewsArticleDetail>? edit = null)
    {
        var detail = new NewsArticleDetail
        {
            Id = article.Id, FeedId = article.FeedId, FeedTitle = article.FeedTitle, Title = article.Title, Author = article.Author,
            Link = article.Link, PublishedAtUtc = article.PublishedAtUtc, FirstSeenAtUtc = article.FirstSeenAtUtc,
            ContentChangedAtUtc = article.ContentChangedAtUtc, Language = article.Language, Body = "First paragraph.\n\nSecond paragraph.",
        };
        edit?.Invoke(detail);
        return detail;
    }

    /// <summary>
    /// A context whose catalogue holds one feed (<see cref="FeedId"/>) unless given, whose tags and counts are given or
    /// empty, and whose list answers every first page with <paramref name="articles"/>.
    /// </summary>
    internal static NewsPageTestContext Context(IReadOnlyList<NewsArticleSummary> articles, IReadOnlyList<string>? claims = null,
        List<NewsFeedCatalogueEntry>? catalogue = null, List<ExistingNewsTag>? tags = null, NewsUnreadCounts? counts = null,
        NewsArticlePage? page = null)
    {
        var context = new NewsPageTestContext(claims);
        context.News.Setup(n => n.GetCatalogueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(catalogue ?? [CatalogueFeed("Example News", id: FeedId)]));
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(tags ?? []));
        context.News.Setup(n => n.GetUnreadCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(counts ?? new NewsUnreadCounts { Total = articles.Count(a => !a.IsRead) }));
        context.News.Setup(n => n.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new NewsStatus()));
        context.News.Setup(n => n.ListArticlesAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Ok(page ?? Page(articles)));
        context.News.Setup(n => n.SetReadAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        context.News.Setup(n => n.GetArticleAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => Ok(DetailOf(articles.FirstOrDefault(a => a.Id == id) ?? Article("Gone"))));
        context.News.Setup(n => n.ImageUrl(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns((Guid a, Guid i) => $"http://api.test/api/news-articles/{a}/images/{i}/content");
        return context;
    }

    internal static NewsPage PageOf(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) => cut.FindComponent<NewsPage>().Instance;

    internal static List<string> CardNames(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        [.. cut.FindAll(".nw-list .odc-record-name").Select(e => e.TextContent.Trim())];

    internal static void Expand(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, NewsArticleSummary article) =>
        cut.Find($"#nwa-{article.Id} .odc-record-trigger").Click();

    internal static string Polite(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        string.Join(" | ", cut.FindAll("[role=status][aria-live=polite]").Select(e => e.TextContent.Trim()).Where(t => t.Length > 0));

    internal static List<string> MenuLabels(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, NewsArticleSummary article)
    {
        var trigger = $"#nwa-{article.Id} .odc-record-ctl button[aria-label='Actions for {NewsArticleCard.TitleOf(article)}']";
        cut.Find(trigger).Click();
        cut.WaitForElement(".mud-menu-item");
        var labels = cut.FindAll(".mud-menu-item .odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim()).Where(t => t.Length > 0).ToList();
        cut.Find(trigger).Click();
        return labels;
    }

    internal static void ClickMenu(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, NewsArticleSummary article, string label)
    {
        cut.Find($"#nwa-{article.Id} .odc-record-ctl button[aria-label='Actions for {NewsArticleCard.TitleOf(article)}']").Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".mud-menu-item"), i => i.TextContent.Contains(label, StringComparison.Ordinal)));
        cut.FindAll(".mud-menu-item").First(i => i.TextContent.Contains(label, StringComparison.Ordinal)).Click();
    }

    // ── AC 2, 53, 59: the happy path ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lists_unread_articles_newest_first_as_h3_cards_under_an_sr_only_h2()
    {
        var a = Article("First", x => x.ImageCount = 2);
        var b = Article("Second");
        NewsArticleListFilter? sent = null;
        int? limit = null;
        await using var context = Context([a, b]);
        context.News.Setup(n => n.ListArticlesAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .Callback((NewsArticleListFilter f, int l, string? _, CancellationToken _) => { sent = f; limit = l; })
            .ReturnsAsync(Ok(Page([a, b])));

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["First", "Second"], CardNames(cut)));
        Assert.Equal(false, sent!.IsRead);
        Assert.Equal(NewsArticleSortBy.Newest, sent.SortBy);
        Assert.Equal(OdsPageSizes.Batch[1], limit);
        Assert.Equal("Articles", cut.Find("h2.odc-sr-only").TextContent);
        Assert.Null(cut.Find("h2.odc-sr-only").GetAttribute("tabindex"));
        Assert.All(cut.FindAll(".nw-list [role=heading]"), h => Assert.Equal("3", h.GetAttribute("aria-level")));
        var first = cut.Find($"#nwa-{a.Id}");
        Assert.Contains("Unread", first.QuerySelector(".odc-record-namerow")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Example News", first.QuerySelector(".odc-record-meta")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("2 images", first.QuerySelector(".odc-record-meta")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(first.QuerySelector(".odc-record-meta time[datetime]"));
        Assert.Empty(first.QuerySelectorAll(".odc-record-trigger button, .odc-record-trigger a"));
        cut.WaitForAssertion(() => Assert.Contains("2 articles", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_visible_divider_or_alert_renders_above_the_list()
    {
        await using var context = Context([Article("One")], catalogue:
        [
            CatalogueFeed("Example News", id: FeedId),
            CatalogueFeed("Broken", f => f.Health = NewsFeedHealth.Failing),
        ]);

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Assert.Empty(cut.FindAll(".odc-section-divider"));
        Assert.Empty(cut.FindAll(".nw-page > .mud-stack > .mud-alert"));
    }
}
