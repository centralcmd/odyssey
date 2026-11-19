using System.Net;
using System.Text.RegularExpressions;
using Bunit;
using Moq;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The news client's source lints and the hostile-payload test (issue #473 §8.2, §8.5; AC 7, 26, 39). Scoped to
/// <c>Pages/Internet/**</c>, the news components and the News status panel; the /news page (F2b) lands in the same
/// folder and so inherits every check here.
/// </summary>
public class NewsSourceLintTests
{
    private static IEnumerable<string> NewsFiles()
    {
        var internet = Path.Combine(ClientSource.Root, "Pages", "Internet");
        var files = Directory.EnumerateFiles(internet, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal));
        var components = new[] { "OdsImageCarousel.razor", "OdsImageCarousel.razor.cs", "OdsExternalLink.razor", "NewsFeedHealthChip.razor", "OdsRelativeTime.razor", "NewsArticleBody.razor" }
            .Select(name => Path.Combine(ClientSource.Root, "Components", name))
            .Concat(Directory.EnumerateFiles(Path.Combine(ClientSource.Root, "Pages", "JobRuns"), "News*.razor*"));
        return files.Concat(components).Where(File.Exists).ToList();
    }

    [Fact]
    public void The_scan_set_is_not_empty() => Assert.True(NewsFiles().Count() >= 8);

    [Fact]
    public void No_news_file_renders_markup_from_a_string()
    {
        var pattern = new Regex(@"MarkupString|AddMarkupContent|innerHTML", RegexOptions.Compiled);
        var offenders = NewsFiles()
            .Where(f => pattern.IsMatch(ClientSource.WithoutComments(File.ReadAllText(f))))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0, "Publisher text must be encoded, never markup: " + string.Join(", ", offenders));
    }

    /// <summary>The NewsLimits values a page could be tempted to restate (§5.5): field limits, the interval range, the caps.</summary>
    private static readonly (string Name, int Value)[] RestatableLimits =
    [
        (nameof(NewsLimits.MaxUrlLength), NewsLimits.MaxUrlLength),
        (nameof(NewsLimits.MaxCustomTitleLength), NewsLimits.MaxCustomTitleLength),
        (nameof(NewsLimits.MinFetchIntervalMinutes), NewsLimits.MinFetchIntervalMinutes),
        (nameof(NewsLimits.MaxFetchIntervalMinutes), NewsLimits.MaxFetchIntervalMinutes),
        (nameof(NewsLimits.MaxPageSize), NewsLimits.MaxPageSize),
        (nameof(NewsLimits.MaxRevisions), NewsLimits.MaxRevisions),
        (nameof(NewsLimits.MaxTags), NewsLimits.MaxTags),
        (nameof(NewsLimits.MaxTagIdsPerRequest), NewsLimits.MaxTagIdsPerRequest),
        (nameof(NewsLimits.MaxFilterIds), NewsLimits.MaxFilterIds),
        (nameof(NewsLimits.MaxTagNameLength), NewsLimits.MaxTagNameLength),
        (nameof(NewsLimits.MaxTagDescriptionLength), NewsLimits.MaxTagDescriptionLength),
        (nameof(NewsLimits.MaxMarkReadPerRequest), NewsLimits.MaxMarkReadPerRequest),
    ];

    [Fact]
    public void No_news_file_restates_a_news_limit_as_a_literal()
    {
        var offenders = new List<string>();
        foreach (var file in NewsFiles().Where(f => f.Contains($"{Path.DirectorySeparatorChar}Internet{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var source = ClientSource.WithoutComments(File.ReadAllText(file));
            foreach (var (name, value) in RestatableLimits)
            {
                if (Regex.IsMatch(source, $@"(?<![\w.#-]){value}(?![\w.%-]|px)"))
                {
                    offenders.Add($"{ClientSource.Relative(file)}: {value} ({name})");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Name the NewsLimits constant instead of its value: " + string.Join("; ", offenders));
    }

    [Fact]
    public void No_news_page_holds_an_admin_editable_cap()
    {
        var caps = new[] { "NewsTextStoreMaxGiB", "NewsImageStoreMaxGiB", "NewsMaxFeeds", "NewsFeedMaxArticlesPerDay", "NewsFeedMaxImageMbPerDay" };
        var offenders = NewsFiles()
            .Where(f => caps.Any(c => ClientSource.WithoutComments(File.ReadAllText(f)).Contains(c, StringComparison.Ordinal)))
            .Select(ClientSource.Relative)
            .ToList();

        Assert.True(offenders.Count == 0, "The quota figures come from GET /api/news-status only: " + string.Join(", ", offenders));
    }

    private const string Hostile = "<img src=x onerror=alert(1)>";
    private const string HostileLink = "javascript:alert(1)";

    [Fact]
    public async Task A_hostile_feed_renders_as_text_and_creates_no_element()
    {
        var feed = NewsFeedsPageTests.Feed(Hostile, f =>
        {
            f.Title = Hostile;
            f.Description = Hostile;
            f.SiteUrl = HostileLink;
            f.Language = Hostile;
            f.Management!.LastErrorMessage = Hostile;
            f.Management.SuggestedUrl = HostileLink;
        });
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.ListFeedsAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new PagedResult<ExistingNewsFeed> { Items = [feed], TotalCount = 1 }));
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>
        {
            new() { NewsTagId = Guid.NewGuid(), Name = Hostile, Description = Hostile },
        }));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".odc-record-name")));
        cut.Find(".odc-record-trigger").Click();

        cut.WaitForAssertion(() => Assert.Contains(Hostile, cut.Find(".odc-record-name").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll($"a[href^='javascript']"));
        Assert.Contains(HostileLink, cut.Markup.Replace("&amp;", "&"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hostile_tag_renders_as_text_and_creates_no_element()
    {
        await using var context = new NewsPageTestContext();
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>
        {
            new() { NewsTagId = Guid.NewGuid(), Name = Hostile, Description = Hostile, Icon = Hostile, Color = Hostile },
        }));
        // The tags page is the shared OdsTagAdmin, which loads only in a browser; the hostile name is pinned where the
        // news reader renders tags itself — the feeds page and its tag breakdown.
        context.News.Setup(n => n.ListFeedsAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new PagedResult<ExistingNewsFeed> { Items = [], TotalCount = 0 }));
        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Contains(Hostile, string.Concat(cut.Nodes.Select(n => n.TextContent)), StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("img"));
    }
}
