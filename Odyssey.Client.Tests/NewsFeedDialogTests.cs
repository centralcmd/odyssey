using System.Net;
using Bunit;
using Moq;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// New feed / Edit feed (issue #473 §3.3, §7.2, §8.1; AC 13, 36, 41): client validation through NewsLimits with the
/// server winning, the duplicate's Go to feed, the feed limit, and the edit conflict's Reload flow.
/// </summary>
public class NewsFeedDialogTests
{
    private sealed class Probe
    {
        public List<ExistingNewsFeed> Saved { get; } = [];
        public List<Guid> GoneTo { get; } = [];
        public List<Guid> Deleted { get; } = [];
    }

    private static (IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut, Probe Probe) Render(NewsPageTestContext context, ExistingNewsFeed? feed = null,
        bool canCreateTags = false, IReadOnlyList<ExistingNewsTag>? tags = null)
    {
        var probe = new Probe();
        var cut = context.Ctx.Render(b =>
        {
            b.OpenComponent<MudBlazor.MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudBlazor.MudPopoverProvider>(1);
            b.CloseComponent();
            b.OpenComponent<NewsFeedDialog>(2);
            b.AddComponentParameter(3, nameof(NewsFeedDialog.Open), true);
            b.AddComponentParameter(4, nameof(NewsFeedDialog.Feed), feed);
            b.AddComponentParameter(5, nameof(NewsFeedDialog.Saved),
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<ExistingNewsFeed>(probe, f => probe.Saved.Add(f)));
            b.AddComponentParameter(6, nameof(NewsFeedDialog.GoToFeed),
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Guid>(probe, id => probe.GoneTo.Add(id)));
            b.AddComponentParameter(7, nameof(NewsFeedDialog.Deleted),
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Guid>(probe, id => probe.Deleted.Add(id)));
            b.AddComponentParameter(8, nameof(NewsFeedDialog.CanCreateTags), canCreateTags);
            b.AddComponentParameter(9, nameof(NewsFeedDialog.Tags), tags ?? []);
            b.CloseComponent();
        });
        return (cut, probe);
    }

    private static AngleSharp.Dom.IElement Input(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string id) =>
        cut.Find($"input#{id}, #{id} input");

    private static AngleSharp.Dom.IElement ImagesSwitch(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindAll("label").First(l => l.TextContent.Contains("Download images", StringComparison.Ordinal)).QuerySelector("input")!;

    private static Task<bool> Submit(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.InvokeAsync(() => cut.FindComponent<OdsFormDialog>().Instance.OnSubmit!());

    [Fact]
    public async Task New_feed_says_required_in_words_and_checks_the_address_on_the_client()
    {
        await using var context = new NewsPageTestContext();
        var (cut, _) = Render(context);

        Assert.Contains("RSS, Atom or JSON Feed address. Odyssey checks it on the first fetch.", cut.Markup, StringComparison.Ordinal);
        foreach (var bad in new[] { "", "ftp://example.com/feed", "javascript:alert(1)", "https://user:pw@example.com/feed", "/relative" })
        {
            Input(cut, NewsFeedDialog.UrlId).Input(bad);
            Assert.False(await Submit(cut));
            cut.WaitForAssertion(() => Assert.Contains(NewsFeedForm.UrlMessage, cut.Markup, StringComparison.Ordinal));
        }

        context.News.Verify(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_interval_and_title_limits_come_from_news_limits()
    {
        await using var context = new NewsPageTestContext();
        var (cut, _) = Render(context);
        Input(cut, NewsFeedDialog.UrlId).Input("https://example.com/feed.xml");
        Input(cut, NewsFeedDialog.IntervalId).Input((NewsLimits.MinFetchIntervalMinutes - 1).ToString());
        Input(cut, NewsFeedDialog.TitleFieldId).Input(new string('t', NewsLimits.MaxCustomTitleLength + 1));

        Assert.False(await Submit(cut));

        cut.WaitForAssertion(() => Assert.Contains(
            $"Use {NewsLimits.MinFetchIntervalMinutes} to {NewsLimits.MaxFetchIntervalMinutes} minutes, or leave empty.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains($"Keep it under {NewsLimits.MaxCustomTitleLength} characters.", cut.Markup, StringComparison.Ordinal);
        context.News.Verify(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_duplicate_shows_the_inline_error_with_go_to_feed()
    {
        var existing = Guid.NewGuid();
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.Conflict, NewsErrorCodes.FeedDuplicate,
                extensions: new Dictionary<string, object> { ["existingFeedId"] = existing }));
        var (cut, probe) = Render(context);
        Input(cut, NewsFeedDialog.UrlId).Input("https://example.com/feed.xml");

        Assert.False(await Submit(cut));

        cut.WaitForAssertion(() => Assert.Contains("You already follow this feed.", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent.Contains("Go to feed", StringComparison.Ordinal)).Click();
        Assert.Equal([existing], probe.GoneTo);
    }

    [Fact]
    public async Task The_server_url_verdict_wins_over_the_client()
    {
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedUrlRejected,
                errors: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["url"] = ["That address points at a private network."] }));
        var (cut, _) = Render(context);
        Input(cut, NewsFeedDialog.UrlId).Input("https://nas.lan/feed.xml");

        Assert.False(await Submit(cut));

        cut.WaitForAssertion(() => Assert.Contains("That address points at a private network.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_feed_limit_is_an_alert_naming_settings()
    {
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedLimit));
        var (cut, _) = Render(context);
        Input(cut, NewsFeedDialog.UrlId).Input("https://example.com/feed.xml");

        Assert.False(await Submit(cut));

        cut.WaitForAssertion(() => Assert.Contains("The limit has been reached. An administrator can raise it in Settings.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_create_sends_the_trimmed_form_and_reports_the_servers_feed()
    {
        var created = NewsFeedsPageTests.Feed("Example");
        await using var context = new NewsPageTestContext();
        NewNewsFeed? sent = null;
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>()))
            .Callback((NewNewsFeed f, CancellationToken _) => sent = f)
            .ReturnsAsync(Ok(created));
        var (cut, probe) = Render(context);
        Input(cut, NewsFeedDialog.UrlId).Input("  https://example.com/feed.xml ");
        Input(cut, NewsFeedDialog.IntervalId).Input("30");

        Assert.True(await Submit(cut));

        Assert.Equal("https://example.com/feed.xml", sent!.Url);
        Assert.Equal(30, sent.FetchIntervalMinutes);
        Assert.Null(sent.CustomTitle);
        Assert.Equal([created], probe.Saved);
    }

    // ── Inline tag create ─────────────────────────────────────────────────────────────────────────

    private static void SearchTags(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string search)
    {
        cut.FindComponent<OdsTagMultiSelect>().Find("button.odc-tagms-trigger").Click();
        cut.WaitForElement(".odc-tagms-search input").Input(search);
    }

    [Fact]
    public async Task A_tag_created_inline_is_posted_and_the_new_feed_carries_its_real_id()
    {
        var tagId = Guid.NewGuid();
        await using var context = new NewsPageTestContext();
        context.NewsTags.Setup(t => t.CreateAsync(It.IsAny<TagWrite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created, new Uri($"http://localhost/api/news-tags/{tagId}")));
        NewNewsFeed? sent = null;
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>()))
            .Callback((NewNewsFeed f, CancellationToken _) => sent = f)
            .ReturnsAsync(Ok(NewsFeedsPageTests.Feed("Example")));
        var (cut, _) = Render(context, canCreateTags: true);
        Input(cut, NewsFeedDialog.UrlId).Input("https://example.com/feed.xml");

        SearchTags(cut, "Climate");
        var create = cut.Find("button.odc-tagms-create");
        Assert.Contains("News tag", create.TextContent, StringComparison.Ordinal);
        create.Click();

        Assert.True(await Submit(cut));
        Assert.Equal([tagId], sent!.TagIds);
        context.NewsTags.Verify(t => t.CreateAsync(It.Is<TagWrite>(w => w.Name == "Climate"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Without_the_create_claim_the_feed_tags_field_only_picks_and_an_archived_name_is_refused()
    {
        var archived = new ExistingNewsTag { NewsTagId = Guid.NewGuid(), Name = "Old", Archived = DateTime.UtcNow };
        await using var context = new NewsPageTestContext();
        var (without, _) = Render(context, canCreateTags: false, tags: [archived]);
        SearchTags(without, "Climate");
        Assert.Empty(without.FindAll("button.odc-tagms-create"));

        await using var other = new NewsPageTestContext();
        var (with, _) = Render(other, canCreateTags: true, tags: [archived]);
        SearchTags(with, "old");
        Assert.Empty(with.FindAll("button.odc-tagms-create"));
        Assert.Contains("Restore it on the News tags page", with.Find(".odc-tagms-blocked").TextContent, StringComparison.Ordinal);
    }

    // ── Edit and the version (AC 36, 41) ─────────────────────────────────────────────────────────

    private static ExistingNewsFeed Editable() =>
        NewsFeedsPageTests.Feed("Example", f => { f.CustomTitle = "Mine"; f.FetchIntervalMinutes = 60; });

    [Fact]
    public async Task Edit_sends_the_version_from_the_read()
    {
        var feed = Editable();
        await using var context = new NewsPageTestContext();
        UpdateNewsFeed? sent = null;
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateNewsFeed u, CancellationToken _) => sent = u)
            .ReturnsAsync(Ok(feed));
        var (cut, _) = Render(context, feed);

        Assert.True(await Submit(cut));

        Assert.Equal(feed.Version, sent!.Version);
        Assert.Equal(feed.Management!.Url, sent.Url);
    }

    [Fact]
    public async Task Edit_has_no_active_switch_keeps_is_active_and_carries_the_tags_in_the_same_put()
    {
        var tag = Guid.NewGuid();
        var feed = NewsFeedsPageTests.Feed("Example", f => { f.IsActive = false; f.Health = NewsFeedHealth.Paused; f.TagIds = [tag]; });
        await using var context = new NewsPageTestContext();
        UpdateNewsFeed? sent = null;
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateNewsFeed u, CancellationToken _) => sent = u)
            .ReturnsAsync(Ok(feed));
        var (cut, _) = Render(context, feed);

        Assert.DoesNotContain(cut.FindAll("label"), l => l.TextContent.Trim() == "Active");
        Assert.Single(cut.FindComponents<OdsTagMultiSelect>());
        Assert.True(await Submit(cut));

        Assert.False(sent!.IsActive);
        Assert.Equal([tag], sent.TagIds);
        context.News.Verify(n => n.SetFeedTagsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_409_puts_the_conflict_alert_up_and_moves_focus_to_it()
    {
        var feed = Editable();
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.Conflict, NewsErrorCodes.FeedChanged));
        var (cut, _) = Render(context, feed);

        Assert.False(await Submit(cut));

        cut.WaitForAssertion(() => Assert.Contains("This feed was changed elsewhere. Reload it to see the latest settings.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("-1", cut.Find($"#{NewsFeedDialog.ConflictId}").GetAttribute("tabindex"));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && Equals(i.Arguments[0], NewsFeedDialog.ConflictId));
    }

    [Fact]
    public async Task Reload_keeps_user_only_fields_lets_the_server_win_and_lists_what_it_changed()
    {
        var feed = Editable();
        var server = feed with { Version = Guid.NewGuid(), FetchIntervalMinutes = 30, CustomTitle = "Server title" };
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.Conflict, NewsErrorCodes.FeedChanged));
        context.News.Setup(n => n.GetFeedAsync(feed.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(server));
        var (cut, _) = Render(context, feed);

        // The reader changed the title and turned images off; the server changed the title and the interval.
        Input(cut, NewsFeedDialog.TitleFieldId).Input("Reader title");
        ImagesSwitch(cut).Change(false);
        Assert.False(await Submit(cut));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll($"#{NewsFeedDialog.ReloadId}")));
        cut.Find($"#{NewsFeedDialog.ReloadId}").Click();

        cut.WaitForAssertion(() => Assert.Contains("Reloaded. Updated from the server: Title, Interval.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("Server title", Input(cut, NewsFeedDialog.TitleFieldId).GetAttribute("value"));
        Assert.Equal("30", Input(cut, NewsFeedDialog.IntervalId).GetAttribute("value"));
        // The reader's own change survives the reload.
        Assert.False(cut.FindComponents<OdsSwitch>().Single(s => s.Instance.Label == "Download images").Instance.Checked);
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && Equals(i.Arguments[0], NewsFeedDialog.TitleFieldId));
    }

    [Fact]
    public async Task Reload_with_nothing_changed_says_so()
    {
        var feed = Editable();
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.Conflict, NewsErrorCodes.FeedChanged));
        context.News.Setup(n => n.GetFeedAsync(feed.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(feed with { Version = Guid.NewGuid() }));
        var (cut, _) = Render(context, feed);

        Assert.False(await Submit(cut));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll($"#{NewsFeedDialog.ReloadId}")));
        cut.Find($"#{NewsFeedDialog.ReloadId}").Click();

        cut.WaitForAssertion(() => Assert.Contains("Reloaded. Nothing changed on the server.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_reload_keeps_the_alert_and_focus_on_reload()
    {
        var feed = Editable();
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.Conflict, NewsErrorCodes.FeedChanged));
        context.News.Setup(n => n.GetFeedAsync(feed.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.InternalServerError));
        var (cut, _) = Render(context, feed);

        Assert.False(await Submit(cut));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll($"#{NewsFeedDialog.ReloadId}")));
        cut.Find($"#{NewsFeedDialog.ReloadId}").Click();

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t reload this feed. Try again.", cut.Markup, StringComparison.Ordinal));
        Assert.Null(cut.Find($"#{NewsFeedDialog.ReloadId}").GetAttribute("aria-disabled"));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && Equals(i.Arguments[0], NewsFeedDialog.ReloadId));
    }

    [Fact]
    public async Task A_404_on_reload_disables_save_and_moves_focus_to_close()
    {
        var feed = Editable();
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.Conflict, NewsErrorCodes.FeedChanged));
        context.News.Setup(n => n.GetFeedAsync(feed.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.NotFound));
        var (cut, probe) = Render(context, feed);

        Assert.False(await Submit(cut));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll($"#{NewsFeedDialog.ReloadId}")));
        cut.Find($"#{NewsFeedDialog.ReloadId}").Click();

        cut.WaitForAssertion(() => Assert.Contains("This feed was deleted.", cut.Markup, StringComparison.Ordinal));
        var form = cut.FindComponent<OdsFormDialog>();
        Assert.True(form.Instance.SubmitDisabled);
        Assert.Equal("Close", form.Instance.CancelLabel);
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusDialogCancel");
        Assert.Equal([feed.Id], probe.Deleted);
    }
}
