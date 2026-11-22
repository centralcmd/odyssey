using System.Net;
using System.Security.Claims;
using System.Text;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
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
/// The Feeds page’s OPML half (issue #509 §3.1, §3.2, §3.3, §7.1, §7.3; amendments v4, v5): the "Feed actions" menu
/// by claim and by list state, both exports, the import dialog's reload on close, and the redraw after a claims
/// refresh. Test names carry the acceptance criterion they prove; the dialog itself is <see cref="ImportNewsFeedsDialogTests"/>.
/// </summary>
public class NewsFeedsOpmlPageTests
{
    static NewsFeedsOpmlPageTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] Opml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><opml version=\"2.0\"><head/><body><outline/></body></opml>\n");

    private static readonly string[] ReaderClaims = [PermissionClaims.NewsRead, PermissionClaims.NewsUpdate];

    private static List<ExistingNewsFeed> Feeds(int count, Action<int, ExistingNewsFeed>? edit = null) =>
        [.. Enumerable.Range(0, count).Select(i => NewsFeedsPageTests.Feed($"Feed {i:00}", f => edit?.Invoke(i, f)))];

    private sealed class ChangeableClaims(IReadOnlyList<string> claims) : AuthenticationStateProvider
    {
        private IReadOnlyList<string> current = claims;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(State());

        public void Set(IReadOnlyList<string> claims)
        {
            current = claims;
            NotifyAuthenticationStateChanged(Task.FromResult(State()));
        }

        private AuthenticationState State() => new(new ClaimsPrincipal(new ClaimsIdentity(
            [.. current.Select(c => new Claim(PermissionClaims.Type, c))], "test")));
    }

    private sealed class Page : IAsyncDisposable
    {
        public Page(IReadOnlyList<ExistingNewsFeed> feeds, IReadOnlyList<string>? claims = null)
        {
            Claims = new ChangeableClaims(claims ?? AllNewsClaims);
            Context = new NewsPageTestContext(claims);
            Context.Ctx.Services.AddSingleton<AuthenticationStateProvider>(Claims);
            Context.News.Setup(n => n.ListFeedsAsync(1, NewsLimits.MaxPageSize, null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Ok(new PagedResult<ExistingNewsFeed> { Items = [.. Current], TotalCount = Current.Count }));
            Context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));
            Current = [.. feeds];
        }

        public NewsPageTestContext Context { get; }

        public ChangeableClaims Claims { get; }

        public List<ExistingNewsFeed> Current { get; set; } = [];

        public IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut { get; private set; } = default!;

        public Page Start()
        {
            Cut = Context.Render<NewsFeedsPage>();
            Cut.WaitForAssertion(() => Assert.Empty(Cut.FindAll(".nw-loading")));
            return this;
        }

        public NewsFeedsPage Instance => Cut.FindComponent<NewsFeedsPage>().Instance;

        public bool HasMenu => Cut.FindAll($"#{NewsFeedsPage.ActionMenuId}").Count == 1;

        public IReadOnlyList<string> MenuItems()
        {
            OpenMenu();
            var labels = Cut.FindAll(".mud-menu-item").Select(i => i.TextContent.Trim()).Where(t => t.Length > 0).ToList();
            Cut.Find($"#{NewsFeedsPage.ActionMenuId} button").Click();
            return labels;
        }

        public void OpenMenu()
        {
            // Re-find and retry: right after MenuItems() closes the menu, a re-render still in flight can replace the
            // button's handler, and clicking the element found before it lands throws an unknown-event-handler error.
            Cut.WaitForAssertion(() => Cut.Find($"#{NewsFeedsPage.ActionMenuId} button").Click());
            Cut.WaitForElement(".mud-menu-item");
        }

        public void Choose(string label)
        {
            if (!Cut.FindAll(".mud-menu-item").Any(i => i.TextContent.Trim() == label))
            {
                OpenMenu();
            }

            Cut.WaitForAssertion(() => Assert.Contains(Cut.FindAll(".mud-menu-item"), i => i.TextContent.Trim() == label));
            // Re-found on each attempt: a re-render between the find and the click retires the handler id.
            Cut.WaitForAssertion(() => Cut.FindAll(".mud-menu-item").First(i => i.TextContent.Trim() == label).Click());
        }

        public string Polite => string.Concat(Cut.FindAll("[role=status][aria-live=polite]").Select(e => e.TextContent));

        public IReadOnlyList<JSRuntimeInvocation> Downloads => [.. Context.Ctx.JSInterop.Invocations["downloadFileFromBytes"]];

        public void Exports(ApiResult<NewsOpmlExportFile> answer) =>
            Context.News.Setup(n => n.ExportOpmlAsync(It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(answer);

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static ApiResult<NewsOpmlExportFile> Export(int? rows, byte[]? bytes = null, string name = "odyssey-news-feeds-2026-11-17.opml") =>
        Ok(new NewsOpmlExportFile(new ApiFile(bytes ?? Opml, name, "text/x-opml"), rows));

    // ── The menu ───────────────────────────────────────────────────────────────────────────────

    [Fact] // AC 1, AC 29 (the id on an existing page's menu root, not its button)
    public async Task With_both_claims_the_menu_offers_export_and_import_under_its_name_and_id()
    {
        await using var page = new Page(Feeds(3)).Start();

        var root = page.Cut.Find($"#{NewsFeedsPage.ActionMenuId}");
        Assert.Contains("mud-menu", root.ClassList);
        Assert.Null(root.GetAttribute("tabindex"));
        Assert.Equal("Feed actions", root.QuerySelector("button")!.GetAttribute("aria-label"));
        Assert.Equal(["Export all as OPML", "Import from OPML…"], page.MenuItems());
    }

    [Fact] // AC 1: the divider sits between an export item and the import item only
    public async Task The_divider_separates_export_from_import()
    {
        await using var page = new Page(Feeds(3)).Start();

        page.OpenMenu();

        Assert.Single(page.Cut.FindAll(".mud-popover .mud-divider"));
    }

    [Fact] // AC 1: import only
    public async Task Create_alone_offers_only_import_and_no_divider()
    {
        await using var page = new Page(Feeds(3), [PermissionClaims.NewsRead, PermissionClaims.NewsFeedsCreate]).Start();

        Assert.Equal(["Import from OPML…"], page.MenuItems());
        page.OpenMenu();
        Assert.Empty(page.Cut.FindAll(".mud-popover .mud-divider"));
    }

    [Fact] // AC 2
    public async Task A_reader_gets_no_menu_button()
    {
        await using var page = new Page(Feeds(3), ReaderClaims).Start();

        Assert.False(page.HasMenu);
        Assert.Empty(page.Cut.FindAll(".ph-action-menu"));
    }

    [Fact] // AC 3: no feeds → no export item; update-only then has no menu at all
    public async Task With_no_feeds_neither_export_item_is_shown()
    {
        await using var page = new Page([]).Start();
        Assert.Equal(["Import from OPML…"], page.MenuItems());

        await using var updateOnly = new Page([], [PermissionClaims.NewsRead, PermissionClaims.NewsFeedsUpdate]).Start();
        Assert.False(updateOnly.HasMenu);
    }

    [Fact] // AC 26, AC 27: N is the whole filtered set; Status defaults to Active
    public async Task Export_filtered_counts_the_filtered_set()
    {
        // 40 feeds, 12 of them paused: the default Active filter shows 28.
        await using var page = new Page(Feeds(40, (i, f) => f.IsActive = i >= 12)).Start();

        Assert.Equal(["Export all as OPML", "Export filtered (28) as OPML", "Import from OPML…"], page.MenuItems());
    }

    [Fact] // AC 26: exactly those ids, including feeds the batch loader has not rendered yet
    public async Task Export_filtered_sends_exactly_the_filtered_ids()
    {
        var feeds = Feeds(140, (i, f) => f.IsActive = i % 2 == 0);
        await using var page = new Page(feeds).Start();
        var expected = feeds.Where(f => f.IsActive).Select(f => f.Id).ToHashSet();
        Assert.True(page.Cut.FindAll(".odc-record").Count < expected.Count);
        IReadOnlyCollection<Guid>? sent = null;
        page.Context.News.Setup(n => n.ExportOpmlAsync(It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<Guid>? ids, CancellationToken _) => sent = ids)
            .ReturnsAsync(Export(70));

        page.Choose("Export filtered (70) as OPML");

        page.Cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal(expected, sent!.ToHashSet());
        Assert.Equal(70, sent!.Count);
    }

    [Fact] // AC 27: all shown, or none shown → absent
    public async Task Export_filtered_is_absent_when_the_filters_show_all_or_none()
    {
        await using var page = new Page(Feeds(5)).Start();
        Assert.DoesNotContain(page.MenuItems(), l => l.StartsWith("Export filtered", StringComparison.Ordinal));

        // "Feed 0" matches all five ("Feed 00" … "Feed 04"): still the whole list.
        await page.Cut.InvokeAsync(() => page.Cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("Feed 0"));
        Assert.DoesNotContain(page.MenuItems(), l => l.StartsWith("Export filtered", StringComparison.Ordinal));

        await page.Cut.InvokeAsync(() => page.Cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("no such feed"));
        Assert.Equal(["Export all as OPML", "Import from OPML…"], page.MenuItems());
    }

    [Fact] // AC 27: a filter change updates N live
    public async Task A_filter_change_updates_n()
    {
        await using var page = new Page(Feeds(12)).Start();

        // "Feed 1" matches "Feed 10" and "Feed 11" of "Feed 00" … "Feed 11".
        await page.Cut.InvokeAsync(() => page.Cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("Feed 1"));

        page.Cut.WaitForAssertion(() => Assert.Contains("Export filtered (2) as OPML", page.MenuItems()));

        await page.Cut.InvokeAsync(() => page.Cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("nothing matches"));
        page.Cut.WaitForAssertion(() => Assert.DoesNotContain(page.MenuItems(), l => l.StartsWith("Export filtered", StringComparison.Ordinal)));
    }

    [Fact] // v5 §4: a list that did not load in full offers no export at all
    public async Task A_partially_loaded_list_offers_no_export()
    {
        await using var page = new Page(Feeds(3));
        page.Context.News.Setup(n => n.ListFeedsAsync(It.IsAny<int>(), NewsLimits.MaxPageSize, null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int p, int _, string? _, Guid? _, NewsFeedState? _, IReadOnlyCollection<NewsFeedHealth>? _, NewsFeedSortBy? _, bool? _, CancellationToken _) =>
                p == 1 ? Ok(new PagedResult<ExistingNewsFeed> { Items = Feeds(NewsLimits.MaxPageSize), TotalCount = 999 })
                       : Fail<PagedResult<ExistingNewsFeed>>(HttpStatusCode.InternalServerError));
        page.Start();

        Assert.False(page.Instance.ShowsExportAll);
        Assert.False(page.Instance.ShowsExportFiltered);
        Assert.Equal(["Import from OPML…"], page.MenuItems());
    }

    // ── Export ─────────────────────────────────────────────────────────────────────────────────

    [Fact] // AC 4
    public async Task Export_all_downloads_the_server_file_and_names_its_rows()
    {
        await using var page = new Page(Feeds(3, (i, f) => f.IsActive = i > 0)).Start();
        IReadOnlyCollection<Guid>? sent = [Guid.Empty];
        page.Context.News.Setup(n => n.ExportOpmlAsync(It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<Guid>? ids, CancellationToken _) => sent = ids)
            .ReturnsAsync(Export(3));

        page.Choose("Export all as OPML");

        page.Cut.WaitForAssertion(() => Assert.Single(page.Downloads));
        Assert.Null(sent);
        var download = page.Downloads.Single();
        Assert.Equal(Opml, download.Arguments[0]);
        Assert.Equal("odyssey-news-feeds-2026-11-17.opml", download.Arguments[1]);
        Assert.Equal("text/x-opml", download.Arguments[2]);
        page.Cut.WaitForAssertion(() => Assert.Contains("Exported 3 feeds to odyssey-news-feeds-2026-11-17.opml.", page.Cut.Markup, StringComparison.Ordinal));
        page.Cut.WaitForAssertion(() => Assert.Contains("Preparing the OPML export…", page.Polite, StringComparison.Ordinal));
    }

    [Fact] // AC 4: no usable row count
    public async Task Without_a_row_count_the_snackbar_names_the_file_alone()
    {
        await using var page = new Page(Feeds(2)).Start();
        page.Exports(Export(null));

        page.Choose("Export all as OPML");

        page.Cut.WaitForAssertion(() => Assert.Contains("Exported odyssey-news-feeds-2026-11-17.opml.", page.Cut.Markup, StringComparison.Ordinal));
        Assert.Single(page.Downloads);
    }

    [Fact] // AC 4 (v5 §3): a second press while one runs is ignored and says so
    public async Task A_second_export_press_while_one_runs_is_announced_and_ignored()
    {
        await using var page = new Page(Feeds(4, (i, f) => f.IsActive = i > 0)).Start();
        var pending = new TaskCompletionSource<ApiResult<NewsOpmlExportFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Context.News.Setup(n => n.ExportOpmlAsync(It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>())).Returns(pending.Task);

        page.Choose("Export all as OPML");
        page.Cut.WaitForAssertion(() => Assert.Contains("Preparing the OPML export…", page.Polite, StringComparison.Ordinal));
        page.Choose("Export filtered (3) as OPML");

        page.Cut.WaitForAssertion(() => Assert.Contains("An export is already running.", page.Polite, StringComparison.Ordinal));
        page.Context.News.Verify(n => n.ExportOpmlAsync(It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<CancellationToken>()), Times.Once);
        pending.SetResult(Export(4));
        page.Cut.WaitForAssertion(() => Assert.Single(page.Downloads));
    }

    public static TheoryData<HttpStatusCode, string?, string> ExportFailures => new()
    {
        { HttpStatusCode.TooManyRequests, null, "An export is already running. Try again in a moment." },
        { HttpStatusCode.ServiceUnavailable, NewsErrorCodes.Busy, "Busy. Try again in a moment." },
        { HttpStatusCode.Forbidden, null, "You don’t have permission to export feeds." },
        { HttpStatusCode.InternalServerError, null, "Couldn’t export the feeds." },
    };

    [Theory] // AC 5
    [MemberData(nameof(ExportFailures))]
    public async Task Export_failures_show_their_snackbar_and_download_nothing(HttpStatusCode status, string? code, string text)
    {
        await using var page = new Page(Feeds(2)).Start();
        page.Exports(Fail<NewsOpmlExportFile>(status, code));

        page.Choose("Export all as OPML");

        page.Cut.WaitForAssertion(() => Assert.Contains(text, page.Cut.Find(".mud-snackbar").TextContent, StringComparison.Ordinal));
        Assert.Empty(page.Downloads);
        Assert.DoesNotContain("Server said", page.Cut.Find(".mud-snackbar").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC 5 (S6): a body not ending in </opml> is a failed export
    public async Task An_incomplete_body_downloads_nothing()
    {
        await using var page = new Page(Feeds(2)).Start();
        page.Exports(Export(2, Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><opml version=\"2.0\"><body><outline")));

        page.Choose("Export all as OPML");

        page.Cut.WaitForAssertion(() => Assert.Contains("The export didn’t complete. Try again.", page.Cut.Markup, StringComparison.Ordinal));
        Assert.Empty(page.Downloads);
    }

    [Theory] // §5 (S6, v4 §8): the completeness check ignores trailing whitespace and accepts an empty body
    [InlineData("<opml version=\"2.0\"><head/><body/></opml>", true)]
    [InlineData("<opml version=\"2.0\"><body/></opml>  \r\n\t", true)]
    [InlineData("<opml version=\"2.0\"><body/></opml", false)]
    [InlineData("<opml version=\"2.0\"><body/></OPML>", false)]
    [InlineData("", false)]
    public void The_completeness_check_reads_the_closing_tag(string text, bool complete) =>
        Assert.Equal(complete, NewsFeedsPage.EndsWithOpmlClose(Encoding.UTF8.GetBytes(text)));

    [Fact] // AC 28
    public async Task A_filtered_export_of_zero_rows_says_so_and_downloads_nothing()
    {
        await using var page = new Page(Feeds(3, (i, f) => f.IsActive = i > 0)).Start();
        page.Exports(Export(0, Encoding.UTF8.GetBytes("<opml version=\"2.0\"><head/><body/></opml>")));

        page.Choose("Export filtered (2) as OPML");

        page.Cut.WaitForAssertion(() => Assert.Contains("No feeds to export: the filtered feeds no longer exist.", page.Cut.Markup, StringComparison.Ordinal));
        Assert.Empty(page.Downloads);
    }

    [Fact] // v4 §8: Export all of zero rows, checked before the completeness check
    public async Task Export_all_of_zero_rows_says_so_and_downloads_nothing()
    {
        await using var page = new Page(Feeds(2)).Start();
        page.Exports(Export(0, Encoding.UTF8.GetBytes("not even xml")));

        page.Choose("Export all as OPML");

        page.Cut.WaitForAssertion(() => Assert.Contains("No feeds to export.", page.Cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("didn’t complete", page.Cut.Markup, StringComparison.Ordinal);
        Assert.Empty(page.Downloads);
    }

    // ── 403 → claims refresh (§7.3) ────────────────────────────────────────────────────────────

    [Fact] // AC 18
    public async Task A_403_refreshes_the_claims_and_the_lost_items_leave_the_menu()
    {
        await using var page = new Page(Feeds(2));
        var refresher = new FakeClaimsRefresher(() =>
        {
            page.Claims.Set([PermissionClaims.NewsRead, PermissionClaims.NewsFeedsCreate]);
            return Task.CompletedTask;
        });
        page.Context.Ctx.Services.AddSingleton<Odyssey.Client.Services.IClaimsRefresher>(refresher);
        page.Start();
        page.Exports(Fail<NewsOpmlExportFile>(HttpStatusCode.Forbidden));

        page.Choose("Export all as OPML");

        page.Cut.WaitForAssertion(() => Assert.Equal(1, refresher.Refreshes));
        page.Cut.WaitForAssertion(() => Assert.Equal(["Import from OPML…"], page.MenuItems()));
    }

    [Fact] // AC 18 / AC 2: losing every OPML claim removes the ⋯ button
    public async Task Losing_both_claims_removes_the_menu_button()
    {
        await using var page = new Page(Feeds(2)).Start();
        Assert.True(page.HasMenu);

        await page.Cut.InvokeAsync(() => page.Claims.Set(ReaderClaims));

        page.Cut.WaitForAssertion(() => Assert.False(page.HasMenu));
    }

    // ── Import (§3.3) ──────────────────────────────────────────────────────────────────────────

    private static async Task<(Page Page, IRenderedComponent<ImportNewsFeedsDialog> Dialog)> OpenImportAsync(Page page)
    {
        page.Choose("Import from OPML…");
        page.Cut.WaitForElement("#" + ImportNewsFeedsDialog.ImportButtonId);
        var dialog = page.Cut.FindComponent<ImportNewsFeedsDialog>();
        await page.Cut.InvokeAsync(() => page.Cut.FindComponent<OdsFileUpload>().Instance.FilesChanged.InvokeAsync(
            [new OdsUploadFile { Uid = "f", Name = "feeds.opml", Kind = "Document", SizeBytes = 100, Source = new FakeBrowserFile("feeds.opml", 100, "text/x-opml") }]));
        return (page, dialog);
    }

    private static IElement FooterButton(Page page, string label) =>
        page.Cut.FindAll(".ods-modal-foot button").Single(b => b.TextContent.Trim().EndsWith(label, StringComparison.Ordinal));

    public static TheoryData<string> SentOutcomes => ["result", "conflict", "busy", "dropped", "stopped"];

    [Theory] // AC 14: after any sent request, closing invalidates the catalogue and reloads the list
    [MemberData(nameof(SentOutcomes))]
    public async Task Closing_after_a_sent_request_reloads_the_list(string outcome)
    {
        await using var page = new Page(Feeds(2)).Start();
        var pending = new TaskCompletionSource<ApiResult<NewsOpmlImportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Context.News.Setup(n => n.ImportOpmlAsync(It.IsAny<ApiUpload>(), It.IsAny<CancellationToken>())).Returns(outcome switch
        {
            "result" => Task.FromResult(Ok(new NewsOpmlImportResult { Feeds = 1, Created = 1, Items = [new() { Position = 1, Url = "https://n.example/rss", Outcome = NewsOpmlImportOutcome.Created }] })),
            "conflict" => Task.FromResult(Fail<NewsOpmlImportResult>(HttpStatusCode.Conflict, NewsErrorCodes.OpmlConflict)),
            "busy" => Task.FromResult(Busy<NewsOpmlImportResult>()),
            "dropped" => Task.FromResult(ApiResult<NewsOpmlImportResult>.Failure(new HttpRequestException("reset"))),
            _ => pending.Task,
        });
        var (_, dialog) = await OpenImportAsync(page);
        page.Cut.Find("#" + ImportNewsFeedsDialog.ImportButtonId).Click();
        if (outcome == "stopped")
        {
            page.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Importing, dialog.Instance.CurrentPhase));
            page.Context.Time.Advance(ImportNewsFeedsDialog.StopWaitingAfter);
            page.Cut.WaitForAssertion(() => FooterButton(page, "Stop waiting").Click());
            page.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Stopped, dialog.Instance.CurrentPhase));
        }
        else
        {
            page.Cut.WaitForAssertion(() => Assert.NotEqual(ImportNewsFeedsDialog.Phase.Importing, dialog.Instance.CurrentPhase));
        }

        page.Current = [.. page.Current, NewsFeedsPageTests.Feed("Imported")];

        FooterButton(page, outcome == "result" ? "Done" : "Cancel").Click();

        page.Cut.WaitForAssertion(() => Assert.Contains("Imported", page.Cut.Markup, StringComparison.Ordinal));
        page.Context.News.Verify(n => n.ListFeedsAsync(1, NewsLimits.MaxPageSize, null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        page.Context.NewsTags.Verify(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        page.Cut.WaitForAssertion(() => Assert.Contains("Feed list updated.", page.Polite, StringComparison.Ordinal));
        Assert.Equal(NewsFeedsPage.ActionMenuId, dialog.FindComponent<OdsModal>().Instance.ReturnFocusToId);
    }

    [Fact] // §3.3: Cancel with nothing sent reloads nothing
    public async Task Cancelling_without_a_request_reloads_nothing()
    {
        await using var page = new Page(Feeds(2)).Start();
        await OpenImportAsync(page);

        FooterButton(page, "Cancel").Click();

        page.Cut.WaitForAssertion(() => Assert.False(page.Cut.FindComponent<ImportNewsFeedsDialog>().Instance.Open));
        page.Context.News.Verify(n => n.ListFeedsAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain("Feed list updated.", page.Polite, StringComparison.Ordinal);
    }

    [Fact] // AC 31: report values reach no snackbar, page state or page announcer
    public async Task Report_values_stay_inside_the_dialog()
    {
        await using var page = new Page(Feeds(2)).Start();
        page.Context.News.Setup(n => n.ImportOpmlAsync(It.IsAny<ApiUpload>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new NewsOpmlImportResult
        {
            Feeds = 2,
            Created = 1,
            Invalid = 1,
            TagsCreated = ["SecretTag"],
            Items =
            [
                new() { Position = 1, Url = "https://private-host.example/rss", Title = "SecretTitle", Outcome = NewsOpmlImportOutcome.Created },
                new() { Position = 2, Url = "http://10.0.0.5/feed", Title = "Nas", Outcome = NewsOpmlImportOutcome.Invalid, Message = "SecretMessage" },
            ],
        }));
        await OpenImportAsync(page);
        page.Cut.Find("#" + ImportNewsFeedsDialog.ImportButtonId).Click();
        page.Cut.WaitForElement("#" + ImportNewsFeedsDialog.ResultHeadingId);
        page.Context.State.Invocations.Clear();

        FooterButton(page, "Done").Click();
        page.Cut.WaitForAssertion(() => Assert.Contains("Feed list updated.", page.Polite, StringComparison.Ordinal));

        var outside = page.Polite + string.Concat(page.Cut.FindAll(".mud-snackbar").Select(s => s.TextContent));
        foreach (var value in new[] { "SecretTag", "SecretTitle", "private-host", "SecretMessage", "feeds.opml" })
        {
            Assert.DoesNotContain(value, outside, StringComparison.Ordinal);
        }

        foreach (var call in page.Context.State.Invocations)
        {
            var args = string.Join(" ", call.Arguments.Select(a => System.Text.Json.JsonSerializer.Serialize(a)));
            Assert.DoesNotContain("Secret", args, StringComparison.Ordinal);
            Assert.DoesNotContain("feeds.opml", args, StringComparison.Ordinal);
        }
    }
}
