using System.Net;
using Bunit;
using Moq;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>/news-tags</c> (issue #473 §3.4; AC 16, 17, 18, 32, 46, 61, 64): the page-held, page-sorted window in an
/// OdsRecordTable with the footer-only OdsInfiniteList after it, the claim-gated menu, create and edit with the
/// server's verdict winning, archive by the dedicated endpoints, and the in-use delete.
/// </summary>
public class NewsTagsPageTests
{
    private static ExistingNewsTag Tag(string name, int feeds = 0, int articles = 0, bool archived = false, string? description = null) => new()
    {
        Id = Guid.NewGuid(), Name = name, Description = description, FeedCount = feeds, ArticleCount = articles, Archived = archived,
        CreatedAtUtc = Now.UtcDateTime, UpdatedAtUtc = Now.UtcDateTime,
    };

    private static NewsPageTestContext Context(List<ExistingNewsTag> tags, IReadOnlyList<string>? claims = null)
    {
        var context = new NewsPageTestContext(claims);
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(tags));
        return context;
    }

    private static List<string> RowNames(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        [.. cut.FindAll("tbody tr .nt-name > :first-child").Select(e => e.TextContent.Trim())];

    private static void OpenMenuAndClick(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, Guid id, string label)
    {
        cut.Find($"#nt-menu-{id} button").Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".mud-menu-item"), i => i.TextContent.Contains(label, StringComparison.Ordinal)));
        cut.FindAll(".mud-menu-item").First(i => i.TextContent.Contains(label, StringComparison.Ordinal)).Click();
    }

    private static List<string> MenuLabels(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, Guid id)
    {
        cut.Find($"#nt-menu-{id} button").Click();
        cut.WaitForElement(".mud-menu-item");
        return [.. cut.FindAll(".mud-menu-item .odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim()).Where(t => t.Length > 0)];
    }

    [Fact]
    public async Task Lists_every_tag_with_counts_status_and_the_caption()
    {
        await using var context = Context([Tag("Tech", 3, 41, description: "Gadgets"), Tag("Old", archived: true)]);
        var cut = context.Render<NewsTagsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["Old", "Tech"], RowNames(cut)));
        Assert.Equal("News tags", cut.Find("table caption").TextContent.Trim());
        Assert.Equal("Tags", cut.Find("h2#nt-h2").TextContent.Trim());
        Assert.Contains("1 active · 1 archived", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Gadgets", cut.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Contains("Archived", cut.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".odc-pager"));
    }

    [Fact]
    public async Task Sorting_orders_the_whole_set_and_the_window_is_its_head()
    {
        // 30 tags, a window of 25: the 30th by name ("Tag 29") is past the window until the sort flips it to the top.
        var tags = Enumerable.Range(0, 30).Select(i => Tag($"Tag {i:00}")).ToList();
        await using var context = Context(tags);
        context.State.Setup(s => s.LoadAsync<NewsTagsPage.NewsTagsPageState>(NewsTagsPage.PageStateKey))
            .ReturnsAsync(new NewsTagsPage.NewsTagsPageState { BatchSize = OdsPageSizes.Batch[0] });
        var cut = context.Render<NewsTagsPage>();

        cut.WaitForAssertion(() => Assert.Equal(OdsPageSizes.Batch[0], RowNames(cut).Count));
        Assert.Equal("Tag 00", RowNames(cut)[0]);
        Assert.DoesNotContain("Tag 29", RowNames(cut));

        // The header sorts the WHOLE set (ServerSort): descending by name puts Tag 29 first.
        var table = cut.FindComponent<OdsRecordTable<ExistingNewsTag>>();
        await cut.InvokeAsync(() => table.Instance.SortChanged.InvokeAsync(new OdsTableSort("name", OdsSortDirection.Desc)));

        cut.WaitForAssertion(() => Assert.Equal("Tag 29", RowNames(cut)[0]));
        Assert.True(table.Instance.ServerSort);
        Assert.Equal(OdsPageSizes.Batch[0], RowNames(cut).Count);
    }

    [Fact]
    public async Task Growing_the_window_keeps_the_sort_order()
    {
        var tags = Enumerable.Range(0, 30).Select(i => Tag($"Tag {i:00}")).ToList();
        await using var context = Context(tags);
        context.State.Setup(s => s.LoadAsync<NewsTagsPage.NewsTagsPageState>(NewsTagsPage.PageStateKey))
            .ReturnsAsync(new NewsTagsPage.NewsTagsPageState { BatchSize = OdsPageSizes.Batch[0], SortDirection = OdsSortDirection.Desc, SortField = "name" });
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Equal("Tag 29", RowNames(cut)[0]));

        var footer = cut.FindComponent<OdsInfiniteList<ExistingNewsTag>>();
        Assert.True(footer.Instance.FooterOnly);
        await cut.InvokeAsync(() => footer.Instance.WindowSizeChanged.InvokeAsync(30));

        cut.WaitForAssertion(() => Assert.Equal(30, RowNames(cut).Count));
        Assert.Equal([.. tags.Select(t => t.Name).OrderByDescending(n => n, StringComparer.Ordinal)], RowNames(cut));
    }

    [Fact]
    public async Task The_table_is_not_nested_in_the_infinite_list()
    {
        await using var context = Context([Tag("Tech")]);
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));

        Assert.Empty(cut.FindAll(".odc-infinite table, .odc-ilist table"));
        Assert.Single(cut.FindComponents<OdsInfiniteList<ExistingNewsTag>>());
    }

    [Fact]
    public async Task Status_filter_and_search_narrow_the_set()
    {
        await using var context = Context([Tag("Tech"), Tag("Travel", description: "Trips"), Tag("Old", archived: true)]);
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Equal(3, RowNames(cut).Count));

        var status = cut.FindComponents<OdsMultiSelect>().Single(m => m.Instance.Label == "Status");
        await cut.InvokeAsync(() => status.Instance.ValuesChanged.InvokeAsync(["archived"]));
        cut.WaitForAssertion(() => Assert.Equal(["Old"], RowNames(cut)));

        await cut.InvokeAsync(() => status.Instance.ValuesChanged.InvokeAsync([]));
        var search = cut.FindComponent<OdsSearchField>();
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("trips"));
        cut.WaitForAssertion(() => Assert.Equal(["Travel"], RowNames(cut)));
    }

    [Fact]
    public async Task The_menu_holds_only_what_the_caller_may_do()
    {
        var tag = Tag("Tech");
        await using (var admin = Context([tag]))
        {
            var cut = admin.Render<NewsTagsPage>();
            cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));
            Assert.Equal(["Edit", "Archive", "Delete…"], MenuLabels(cut, tag.Id));
        }

        await using (var readOnly = Context([tag], [PermissionClaims.NewsRead]))
        {
            var cut = readOnly.Render<NewsTagsPage>();
            cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));
            Assert.Empty(cut.FindAll($"#nt-menu-{tag.Id}"));
            Assert.Empty(cut.FindAll(".nt-namebtn"));
            Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "New tag");
        }
    }

    [Fact]
    public async Task Archive_calls_the_dedicated_endpoint_and_announces_it()
    {
        var tag = Tag("Tech");
        await using var context = Context([tag]);
        context.News.Setup(n => n.ArchiveTagAsync(tag.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(tag with { Archived = true }));
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));

        OpenMenuAndClick(cut, tag.Id, "Archive");

        cut.WaitForAssertion(() => Assert.Contains("Tech archived", cut.Markup, StringComparison.Ordinal));
        context.News.Verify(n => n.ArchiveTagAsync(tag.Id, It.IsAny<CancellationToken>()), Times.Once);
        context.News.Verify(n => n.UpdateTagAsync(It.IsAny<Guid>(), It.IsAny<UpdateNewsTag>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("0 active · 1 archived", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_validates_on_the_client_then_lets_the_server_duplicate_win()
    {
        await using var context = Context([Tag("Tech")]);
        context.News.Setup(n => n.CreateTagAsync(It.IsAny<NewNewsTag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsTag>(HttpStatusCode.Conflict, NewsErrorCodes.TagDuplicate));
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "New tag").Click();
        var dialog = cut.FindComponents<OdsFormDialog>().Single();

        // Empty name: refused on the client, nothing sent.
        Assert.False(await cut.InvokeAsync(() => dialog.Instance.OnSubmit!()));
        cut.WaitForAssertion(() => Assert.Contains("Give the tag a name.", cut.Markup, StringComparison.Ordinal));
        context.News.Verify(n => n.CreateTagAsync(It.IsAny<NewNewsTag>(), It.IsAny<CancellationToken>()), Times.Never);

        // Too long: the shared limit, interpolated.
        cut.Find("input#nt-form-name, #nt-form-name input").Input(new string('x', NewsLimits.MaxTagNameLength + 1));
        Assert.False(await cut.InvokeAsync(() => dialog.Instance.OnSubmit!()));
        cut.WaitForAssertion(() => Assert.Contains($"Keep it under {NewsLimits.MaxTagNameLength} characters.", cut.Markup, StringComparison.Ordinal));

        // A server duplicate lands on Name.
        cut.Find("input#nt-form-name, #nt-form-name input").Input("Tech");
        Assert.False(await cut.InvokeAsync(() => dialog.Instance.OnSubmit!()));
        cut.WaitForAssertion(() => Assert.Contains("A news tag with this name exists.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_tag_limit_is_an_alert_without_a_settings_sentence()
    {
        await using var context = Context([]);
        context.News.Setup(n => n.CreateTagAsync(It.IsAny<NewNewsTag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsTag>(HttpStatusCode.UnprocessableEntity, NewsErrorCodes.TagLimit));
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Contains("No news tags yet", cut.Markup, StringComparison.Ordinal));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "New tag").Click();
        var dialog = cut.FindComponents<OdsFormDialog>().Single();
        cut.Find("input#nt-form-name, #nt-form-name input").Input("Favourites");
        Assert.False(await cut.InvokeAsync(() => dialog.Instance.OnSubmit!()));

        cut.WaitForAssertion(() => Assert.Contains("The limit has been reached.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("raise it in Settings", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_created_tag_joins_the_list()
    {
        var created = Tag("Favourites");
        await using var context = Context([Tag("Tech")]);
        NewNewsTag? sent = null;
        context.News.Setup(n => n.CreateTagAsync(It.IsAny<NewNewsTag>(), It.IsAny<CancellationToken>()))
            .Callback((NewNewsTag t, CancellationToken _) => sent = t)
            .ReturnsAsync(Ok(created));
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "New tag").Click();
        var dialog = cut.FindComponents<OdsFormDialog>().Single();
        cut.Find("input#nt-form-name, #nt-form-name input").Input("  Favourites ");
        Assert.True(await cut.InvokeAsync(() => dialog.Instance.OnSubmit!()));

        Assert.Equal("Favourites", sent!.Name);
        cut.WaitForAssertion(() => Assert.Equal(["Favourites", "Tech"], RowNames(cut)));
    }

    [Fact]
    public async Task An_unused_tag_deletes_after_a_plain_confirmation()
    {
        var tag = Tag("Tech");
        await using var context = Context([tag, Tag("Zed")]);
        context.News.Setup(n => n.DeleteTagAsync(tag.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, RowNames(cut).Count));

        OpenMenuAndClick(cut, tag.Id, "Delete…");
        cut.WaitForAssertion(() => Assert.Contains("No feed or article uses this tag. This cannot be undone.", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent.Contains("Delete tag", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(["Zed"], RowNames(cut)));
        context.News.Verify(n => n.DeleteTagAsync(tag.Id, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_in_use_tag_offers_remove_from_all_behind_the_acknowledgement()
    {
        var tag = Tag("Tech", feeds: 3, articles: 41);
        await using var context = Context([tag]);
        context.News.Setup(n => n.DeleteTagAsync(tag.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));

        OpenMenuAndClick(cut, tag.Id, "Delete…");
        cut.WaitForAssertion(() => Assert.Contains("Used by 3 feeds and 41 articles.", cut.Markup, StringComparison.Ordinal));
        var detach = cut.Find("#nt-del-detach");
        Assert.Equal("true", detach.GetAttribute("aria-disabled"));
        detach.Click();
        context.News.Verify(n => n.DeleteTagAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.Find("#nt-del-ack").Change(true);
        cut.Find("#nt-del-detach").Click();

        cut.WaitForAssertion(() => Assert.Empty(RowNames(cut)));
        context.News.Verify(n => n.DeleteTagAsync(tag.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Without_the_feed_claim_the_dialog_names_the_missing_permission()
    {
        var tag = Tag("Tech", feeds: 2);
        await using var context = Context([tag], [PermissionClaims.NewsRead, PermissionClaims.NewsTagsDelete, PermissionClaims.NewsUpdate]);
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));

        OpenMenuAndClick(cut, tag.Id, "Delete…");

        cut.WaitForAssertion(() => Assert.Contains("Used by 2 feeds.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("You need permission to edit feeds to remove this tag from them.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#nt-del-detach"));
    }

    [Fact]
    public async Task A_stale_count_409_turns_the_dialog_into_the_in_use_explanation()
    {
        var tag = Tag("Tech");
        await using var context = Context([tag]);
        context.News.Setup(n => n.DeleteTagAsync(tag.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail(HttpStatusCode.Conflict, NewsErrorCodes.TagInUse, new Dictionary<string, object> { ["feedCount"] = 1, ["articleCount"] = 4 }));
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Single(RowNames(cut)));

        OpenMenuAndClick(cut, tag.Id, "Delete…");
        cut.WaitForElement("button.mud-button-filled-error, button");
        cut.FindAll("button").First(b => b.TextContent.Contains("Delete tag", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Used by 1 feed and 4 articles.", cut.Markup, StringComparison.Ordinal));
        Assert.Single(cut.FindAll("#nt-del-detach"));
    }

    [Fact]
    public async Task A_busy_first_load_shows_the_region_state_and_retries_once_by_itself()
    {
        await using var context = new NewsPageTestContext();
        var calls = 0;
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1 ? Busy<List<ExistingNewsTag>>() : Ok(new List<ExistingNewsTag> { Tag("Tech") }));
        var cut = context.Render<NewsTagsPage>();

        cut.WaitForAssertion(() => Assert.Contains("News is busy. Retrying in a moment.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".mud-snackbar"));

        context.Time.Advance(TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() => Assert.Equal(["Tech"], RowNames(cut)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_second_busy_answer_gives_retry_back()
    {
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Busy<List<ExistingNewsTag>>());
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Contains("News is busy. Retrying in a moment.", cut.Markup, StringComparison.Ordinal));

        context.Time.Advance(TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() => Assert.Contains("News is busy. Try again in a moment.", cut.Markup, StringComparison.Ordinal));
        var retry = cut.Find("#nt-load-retry");
        Assert.Null(retry.GetAttribute("aria-disabled"));
        Assert.Contains("Retry", retry.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_before_retry_after_stays_enabled_and_schedules_the_retry()
    {
        await using var context = new NewsPageTestContext();
        var calls = 0;
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1
                ? Fail<List<ExistingNewsTag>>(HttpStatusCode.ServiceUnavailable, NewsErrorCodes.Busy, TimeSpan.FromSeconds(25))
                : Ok(new List<ExistingNewsTag> { Tag("Tech") }));
        var cut = context.Render<NewsTagsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Available in 25 s", cut.Markup, StringComparison.Ordinal));

        cut.Find("#nt-load-retry").Click();

        cut.WaitForAssertion(() => Assert.Contains("Retrying in 25 seconds", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(1, calls);
        context.Time.Advance(TimeSpan.FromSeconds(25));
        cut.WaitForAssertion(() => Assert.Equal(["Tech"], RowNames(cut)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Any_other_first_load_failure_is_the_error_state_with_retry()
    {
        await using var context = new NewsPageTestContext();
        var calls = 0;
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1 ? Fail<List<ExistingNewsTag>>(HttpStatusCode.InternalServerError) : Ok(new List<ExistingNewsTag>()));
        var cut = context.Render<NewsTagsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load news tags.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("alert", cut.Find(".nw-region-problem .odc-empty").GetAttribute("role"));
        cut.Find("#nt-load-retry").Click();
        cut.WaitForAssertion(() => Assert.Contains("No news tags yet", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void The_page_and_state_keys_are_the_spec_ones()
    {
        Assert.Equal("news-tags-page", NewsTagsPage.PageStateKey);
        var route = typeof(NewsTagsPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Single();
        Assert.Equal("/news-tags", route.Template);
        var policy = typeof(NewsTagsPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(PermissionClaims.NewsRead, policy.Policy);
    }
}
