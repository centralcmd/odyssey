using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Moq;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="ImportNewsFeedsDialog"/> (issue #509 §3.2–§3.4, §6, §7.2, §8; amendments v4, v5). Test names carry the
/// acceptance criterion they prove. The Feeds page's half (the menu, the reload on close, the claims refresh's
/// redraw) is <see cref="NewsFeedsOpmlPageTests"/>.
/// </summary>
public class ImportNewsFeedsDialogTests
{
    static ImportNewsFeedsDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private const long Megabyte = 1L << 20;

    private sealed class Harness : IAsyncDisposable
    {
        public NewsPageTestContext Context { get; } = new();

        public IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut { get; private set; } = default!;

        public List<bool> Closed { get; } = [];

        public List<bool> OpenChanges { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Harness Start()
        {
            Cut = Context.Render<ImportNewsFeedsDialog>(new Dictionary<string, object?>
            {
                [nameof(ImportNewsFeedsDialog.Open)] = true,
                [nameof(ImportNewsFeedsDialog.OpenChanged)] = EventCallback.Factory.Create<bool>(this, v => OpenChanges.Add(v)),
                [nameof(ImportNewsFeedsDialog.Closed)] = EventCallback.Factory.Create<bool>(this, v => Closed.Add(v)),
                [nameof(ImportNewsFeedsDialog.ReturnFocusToId)] = "news-feeds-actions",
            });
            Cut.WaitForElement("#" + ImportNewsFeedsDialog.ImportButtonId);
            return this;
        }

        public void Answers(params Func<ApiResult<NewsOpmlImportResult>>[] answers)
        {
            var queue = new Queue<Func<ApiResult<NewsOpmlImportResult>>>(answers);
            Context.News.Setup(n => n.ImportOpmlAsync(It.IsAny<ApiUpload>(), It.IsAny<CancellationToken>()))
                .Returns((ApiUpload _, CancellationToken ct) => { Tokens.Add(ct); return Task.FromResult(queue.Dequeue()()); });
        }

        /// <summary>The next request stays in flight until the returned source completes.</summary>
        public TaskCompletionSource<ApiResult<NewsOpmlImportResult>> Holds()
        {
            var pending = new TaskCompletionSource<ApiResult<NewsOpmlImportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Context.News.Setup(n => n.ImportOpmlAsync(It.IsAny<ApiUpload>(), It.IsAny<CancellationToken>()))
                .Returns((ApiUpload _, CancellationToken ct) => { Tokens.Add(ct); return pending.Task; });
            return pending;
        }

        public Task Pick(string name, long size = 4_096) =>
            Cut.InvokeAsync(() => Cut.FindComponent<OdsFileUpload>().Instance.FilesChanged.InvokeAsync(
                [new OdsUploadFile { Uid = Guid.NewGuid().ToString(), Name = name, Kind = "Document", SizeBytes = size, Source = new FakeBrowserFile(name, size, "text/x-opml") }]));

        public IElement Import => Cut.Find("#" + ImportNewsFeedsDialog.ImportButtonId);

        public void ClickImport() => Import.Click();

        public IElement Footer(string label) => Cut.FindAll(".ods-modal-foot button").Single(b => Label(b) == label);

        public bool HasFooter(string label) => Cut.FindAll(".ods-modal-foot button").Any(b => Label(b) == label);

        /// <summary>A button's visible text without its icon ligature.</summary>
        private static string Label(IElement button)
        {
            var icons = string.Concat(button.QuerySelectorAll(".material-icons").Select(i => i.TextContent));
            return (icons.Length == 0 ? button.TextContent : button.TextContent.Replace(icons, string.Empty)).Trim();
        }

        public string Polite => string.Concat(Cut.FindAll(".ods-modal [role=status][aria-live=polite]").Select(e => e.TextContent));

        public IElement Dropzone => Cut.Find($"#{ImportNewsFeedsDialog.PickerId} .odc-upload-drop");

        public ImportNewsFeedsDialog.Phase Phase => Cut.FindComponent<ImportNewsFeedsDialog>().Instance.CurrentPhase;

        private readonly Dictionary<string, int> mark = [];

        public IReadOnlyList<JSRuntimeInvocation> Focus(string identifier) =>
            [.. Context.Ctx.JSInterop.Invocations[identifier].Skip(mark.GetValueOrDefault(identifier))];

        public bool FocusedById(string id) => Focus("odsFocusById").Any(i => Equals(i.Arguments[0], id));

        public bool FocusedPicker() => Focus("odsFocusFirstIn").Any(i => Equals(i.Arguments[0], ImportNewsFeedsDialog.PickerId));

        /// <summary>Only invocations after this call count for <see cref="Focus"/>.</summary>
        public void ClearInvocations()
        {
            foreach (var identifier in new[] { "odsFocusById", "odsFocusFirstIn" })
            {
                mark[identifier] = Context.Ctx.JSInterop.Invocations[identifier].Count;
            }
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static NewsOpmlImportItem Item(int position, string url, string? title, NewsOpmlImportOutcome outcome, string? message = null, int dropped = 0) =>
        new() { Position = position, Url = url, Title = title, Outcome = outcome, Message = message, TagsDropped = dropped };

    private static NewsOpmlImportResult Mixed() => new()
    {
        Feeds = 14, MergedOutlines = 3, Created = 6, AlreadySubscribed = 2, PendingDeletion = 1, Invalid = 3, FeedLimit = 2,
        TagsCreated = ["Local", "Tech", "Økonomi"],
        TagsSkipped =
        [
            new() { Name = "Gardening", Reason = NewsOpmlTagSkipReason.NoPermission },
            new() { Name = "Old news", Reason = NewsOpmlTagSkipReason.Archived },
        ],
        TagsSkippedTotal = 2,
        Items =
        [
            Item(1, "https://harbour.example/rss", "Harbour Times", NewsOpmlImportOutcome.Created),
            Item(2, "https://circuit.example/feed.json", "Circuit Weekly", NewsOpmlImportOutcome.Created),
            Item(3, "https://okonomi.example/atom", "Økonomi i dag", NewsOpmlImportOutcome.Created, dropped: 4),
            Item(4, "https://a.example/rss", "Alpha", NewsOpmlImportOutcome.Created),
            Item(5, "https://b.example/rss", "Beta", NewsOpmlImportOutcome.Created),
            Item(6, "https://c.example/rss", "Gamma", NewsOpmlImportOutcome.Created),
            Item(7, "https://gazette.example/feed", "Harbour Gazette", NewsOpmlImportOutcome.AlreadySubscribed),
            Item(8, "https://byavisa.example/atom", "Byavisa", NewsOpmlImportOutcome.AlreadySubscribed),
            Item(9, "http://10.0.0.5/feed", "NAS", NewsOpmlImportOutcome.Invalid, "This address cannot be fetched."),
            Item(10, "https://reader:s3cret@private.example:8443/feed?x=1", "Private feed", NewsOpmlImportOutcome.Invalid, "Feed addresses cannot contain a user name or password."),
            Item(11, "ftp://files.example/feed", "Old FTP feed", NewsOpmlImportOutcome.Invalid, "Use an http:// or https:// address."),
            Item(12, "https://later.example/rss", "Later One", NewsOpmlImportOutcome.FeedLimit),
            Item(13, "https://later2.example/rss", "Later Two", NewsOpmlImportOutcome.FeedLimit),
            Item(14, "https://gone.example/rss", "Being removed", NewsOpmlImportOutcome.PendingDeletion),
        ],
    };

    private static async Task<Harness> ImportedAsync(NewsOpmlImportResult result, string file = "subscriptions.opml")
    {
        var h = new Harness();
        h.Answers(() => Ok(result));
        h.Start();
        await h.Pick(file);
        h.ClickImport();
        h.Cut.WaitForElement("#" + ImportNewsFeedsDialog.ResultHeadingId);
        return h;
    }

    private static IElement Group(Harness h, string id) => h.Cut.Find($"#{id}-list").ParentElement!;

    private static IElement GroupButton(Harness h, string id) => Group(h, id).QuerySelector("button.cal-imp-skip-head")!;

    private static string Name(IElement button) =>
        System.Text.RegularExpressions.Regex.Replace(button.TextContent.Replace("chevron_right", string.Empty), @"\s+", " ").Trim();

    // ── Compose ────────────────────────────────────────────────────────────────────────────────

    [Fact] // AC 6
    public async Task Opens_on_the_picker_with_the_live_limits_in_the_hint()
    {
        await using var h = new Harness();
        h.Context.CurrentLimits = ImportLimitsCacheFallbackWith(megabytes: 12, entries: 2_500);

        h.Start();

        Assert.True(h.FocusedPicker());
        Assert.Contains("OPML (.opml or .xml) · one file · up to 12 MB · up to 2,500 feeds in the file", h.Dropzone.TextContent, StringComparison.Ordinal);
        Assert.Contains("Folders and categories become tags. Feeds you already follow are left unchanged.", h.Cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Add feeds from an OPML file exported by Odyssey or another feed reader.", h.Cut.Markup, StringComparison.Ordinal);
        Assert.Single(h.Cut.FindAll(".ods-modal-x"));
        Assert.True(h.HasFooter("Cancel"));
    }

    [Fact] // AC 6: a Settings save changes the limits the next open shows
    public async Task Each_open_reads_the_limits_again()
    {
        await using var h = new Harness();
        h.Start();
        Assert.Contains("up to 64 MB", h.Dropzone.TextContent, StringComparison.Ordinal);
        h.Context.CurrentLimits = ImportLimitsCacheFallbackWith(megabytes: 5, entries: 100);

        h.Cut.FindComponent<ImportNewsFeedsDialog>().Render(p => p.Add(d => d.Open, false));
        h.Cut.FindComponent<ImportNewsFeedsDialog>().Render(p => p.Add(d => d.Open, true));

        h.Cut.WaitForAssertion(() => Assert.Contains("up to 5 MB · up to 100 feeds", h.Dropzone.TextContent, StringComparison.Ordinal));
    }

    [Fact] // AC 7 (v4 §2): Import is enabled with no file and explains itself
    public async Task Import_with_no_file_shows_the_field_error_and_sends_nothing()
    {
        await using var h = new Harness().Start();
        Assert.Null(h.Import.GetAttribute("aria-disabled"));
        Assert.Null(h.Import.GetAttribute("disabled"));
        h.ClearInvocations();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal("true", h.Dropzone.GetAttribute("aria-invalid")));
        var described = h.Dropzone.GetAttribute("aria-describedby")!;
        Assert.Equal("Choose an OPML file to import.", h.Cut.Find($"#{described}").TextContent.Trim());
        Assert.True(h.FocusedPicker());
        h.Context.News.Verify(n => n.ImportOpmlAsync(It.IsAny<ApiUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // AC 7
    public async Task A_text_file_is_refused_before_sending()
    {
        await using var h = new Harness().Start();
        await h.Pick("feeds.txt");
        h.ClearInvocations();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal("true", h.Dropzone.GetAttribute("aria-invalid")));
        Assert.Contains("That isn't an OPML file. Use a .opml or .xml file.", h.Cut.Markup, StringComparison.Ordinal);
        Assert.True(h.FocusedPicker());
        h.Context.News.Verify(n => n.ImportOpmlAsync(It.IsAny<ApiUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory] // §8.2: case-insensitive extension
    [InlineData("FEEDS.OPML")]
    [InlineData("export.Xml")]
    public async Task An_opml_or_xml_name_in_any_case_is_sent(string name)
    {
        await using var h = new Harness();
        h.Answers(() => Ok(Mixed()));
        h.Start();
        await h.Pick(name);

        h.ClickImport();

        h.Cut.WaitForElement("#" + ImportNewsFeedsDialog.ResultHeadingId);
    }

    [Fact] // AC 7: oversize, after one re-read of the limit
    public async Task An_oversize_file_rereads_the_limit_once_then_refuses_with_the_live_number()
    {
        await using var h = new Harness();
        h.Context.CurrentLimits = ImportLimitsCacheFallbackWith(megabytes: 2, entries: 100);
        h.Start();
        await h.Pick("big.opml", 3 * Megabyte);
        h.ClearInvocations();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Contains("That file is too large for this server. The import limit is 2 MB.", h.Cut.Markup, StringComparison.Ordinal));
        Assert.Equal("true", h.Dropzone.GetAttribute("aria-invalid"));
        Assert.True(h.FocusedPicker());
        h.Context.Limits.Verify(l => l.Invalidate(), Times.Once);
        h.Context.News.Verify(n => n.ImportOpmlAsync(It.IsAny<ApiUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // AC 7 (S4): a limit raised elsewhere is honoured after the re-read
    public async Task A_stale_cached_limit_is_replaced_before_refusing()
    {
        await using var h = new Harness();
        h.Context.CurrentLimits = ImportLimitsCacheFallbackWith(megabytes: 2, entries: 100);
        h.Answers(() => Ok(Mixed()));
        h.Start();
        await h.Pick("big.opml", 3 * Megabyte);
        h.Context.Limits.Setup(l => l.Invalidate()).Callback(() => h.Context.CurrentLimits = ImportLimitsCacheFallbackWith(megabytes: 4, entries: 100));

        h.ClickImport();

        h.Cut.WaitForElement("#" + ImportNewsFeedsDialog.ResultHeadingId);
        h.Context.News.Verify(n => n.ImportOpmlAsync(It.Is<ApiUpload>(u => u.FileName == "big.opml"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Importing ──────────────────────────────────────────────────────────────────────────────

    [Fact] // AC 8, AC 32
    public async Task Importing_is_not_dismissible_keeps_focus_on_import_and_announces_once()
    {
        await using var h = new Harness();
        var pending = h.Holds();
        h.Start();
        await h.Pick("feeds.opml");

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Importing, h.Phase));
        Assert.Equal("true", h.Import.GetAttribute("aria-disabled"));
        Assert.Null(h.Import.GetAttribute("disabled"));
        Assert.Empty(h.Cut.FindAll(".ods-modal-x"));
        Assert.False(h.HasFooter("Cancel"));
        Assert.False(h.HasFooter("Stop waiting"));
        h.Cut.WaitForAssertion(() => Assert.False(((IMudDialogInstance)h.Cut.FindComponent<MudDialogContainer>().Instance).Options.CloseOnEscapeKey));
        Assert.False(((IMudDialogInstance)h.Cut.FindComponent<MudDialogContainer>().Instance).Options.BackdropClick);
        h.Cut.Find(".mud-overlay").Click();
        Assert.Empty(h.Closed);
        h.Cut.WaitForAssertion(() => Assert.Equal("Importing feeds…", h.Polite.Trim()));
        Assert.Contains("Checking each address and matching tags.", h.Cut.Find(".cp-imp-busy").TextContent, StringComparison.Ordinal);

        pending.SetResult(Ok(Mixed()));
        h.Cut.WaitForElement("#" + ImportNewsFeedsDialog.ResultHeadingId);
    }

    [Fact] // AC 32 (v4 §1, v5 §2): only the picker block is inert
    public async Task Only_the_picker_block_is_inert_while_importing()
    {
        await using var h = new Harness();
        var pending = h.Holds();
        h.Start();
        await h.Pick("feeds.opml");
        Assert.Null(h.Cut.Find(".nw-opml-picker").GetAttribute("inert"));

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.NotNull(h.Cut.Find(".nw-opml-picker").GetAttribute("inert")));
        var picker = h.Cut.Find(".nw-opml-picker");
        Assert.NotNull(picker.QuerySelector(".odc-upload-drop"));
        Assert.NotNull(picker.QuerySelector("button.odc-upload-file-x"));
        Assert.Single(h.Cut.FindAll("[inert]"));
        // The busy block, the announcer and the footer sit outside it, in the accessibility tree.
        Assert.Null(h.Cut.Find(".cp-imp-busy").Closest("[inert]"));
        Assert.Null(h.Cut.Find(".ods-modal [role=status]").Closest("[inert]"));
        Assert.Null(h.Import.Closest("[inert]"));
        Assert.Null(h.Cut.Find(".cp-imp-busy").Closest("[aria-hidden=true]"));

        pending.SetResult(Fail<NewsOpmlImportResult>(HttpStatusCode.Conflict, NewsErrorCodes.OpmlConflict));
        h.Cut.WaitForAssertion(() => Assert.Empty(h.Cut.FindAll("[inert]")));
        // An alert is exposed too.
        Assert.Null(h.Cut.Find(".ods-modal [role=alert]").Closest("[inert]"));
    }

    [Fact] // AC 8 (v4 §3): Stop waiting after 30 s, cancelling into Stopped with focus moved first
    public async Task After_30_seconds_stop_waiting_appears_and_cancels_into_stopped()
    {
        await using var h = new Harness();
        h.Holds();
        h.Start();
        await h.Pick("feeds.opml");
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Importing, h.Phase));

        h.Context.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(h.HasFooter("Stop waiting"));
        h.Context.Time.Advance(TimeSpan.FromSeconds(1));

        h.Cut.WaitForAssertion(() => Assert.True(h.HasFooter("Stop waiting")));
        h.Cut.WaitForAssertion(() => Assert.Contains("Still importing. You can stop waiting.", h.Polite, StringComparison.Ordinal));
        h.ClearInvocations();

        h.Footer("Stop waiting").Click();

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Stopped, h.Phase));
        Assert.True(h.Tokens.Single().IsCancellationRequested);
        Assert.Contains("Stopped waiting. The import may still finish on the server. Check the Feeds list after closing.",
            h.Cut.Find(".ods-modal [role=alert]").TextContent, StringComparison.Ordinal);
        Assert.True(h.FocusedById(ImportNewsFeedsDialog.ImportButtonId));
        Assert.Null(h.Import.GetAttribute("aria-disabled"));
        Assert.False(h.HasFooter("Stop waiting"));
        Assert.Single(h.Cut.FindAll(".ods-modal-x"));
        // The chosen file is kept for a retry.
        Assert.Single(h.Cut.FindAll(".odc-upload-file"));
    }

    [Fact] // v4 §10, implementation note 2: Stopped, then an immediate retry that meets 429
    public async Task A_retry_right_after_stopping_can_meet_429_and_says_so()
    {
        await using var h = new Harness();
        var first = h.Holds();
        h.Start();
        await h.Pick("feeds.opml");
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Importing, h.Phase));
        h.Context.Time.Advance(ImportNewsFeedsDialog.StopWaitingAfter);
        h.Cut.WaitForAssertion(() => Assert.True(h.HasFooter("Stop waiting")));
        h.Footer("Stop waiting").Click();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Stopped, h.Phase));
        h.Answers(() => Fail<NewsOpmlImportResult>(HttpStatusCode.TooManyRequests));
        h.ClearInvocations();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Contains("Another import is running. Try again in a moment.", h.Cut.Find(".ods-modal [role=alert]").TextContent, StringComparison.Ordinal));
        Assert.True(h.FocusedById(ImportNewsFeedsDialog.ImportButtonId));
        // The stopped request answering late changes nothing.
        first.SetResult(Ok(Mixed()));
        // Drain the renderer's queue twice: any continuation of the stopped request runs before these turns.
        await h.Cut.InvokeAsync(() => { });
        await h.Cut.InvokeAsync(() => { });
        Assert.Equal(ImportNewsFeedsDialog.Phase.Rejected, h.Phase);
        Assert.Empty(h.Cut.FindAll("#" + ImportNewsFeedsDialog.ResultHeadingId));
    }

    // ── Rejected (§7.2) ────────────────────────────────────────────────────────────────────────

    public static TheoryData<HttpStatusCode, string?, string> AlertCases => new()
    {
        { HttpStatusCode.BadRequest, NewsErrorCodes.OpmlUnreadable, "This file isn't an OPML document, or part of it couldn't be read. Nothing was imported." },
        { HttpStatusCode.Conflict, NewsErrorCodes.OpmlConflict, "The feed list changed while importing. No feeds were imported, but new tags may have been added. Try again." },
        { HttpStatusCode.ServiceUnavailable, NewsErrorCodes.Busy, "News is busy, and the import may or may not have finished. Close to check the Feeds list, or try again." },
        { HttpStatusCode.ServiceUnavailable, null, "News is busy, and the import may or may not have finished. Close to check the Feeds list, or try again." },
        { HttpStatusCode.TooManyRequests, null, "Another import is running. Try again in a moment." },
        { HttpStatusCode.Forbidden, null, "You don't have permission to import feeds." },
        { HttpStatusCode.InternalServerError, null, "Couldn't import the file. Server said 500" },
    };

    [Theory] // AC 16, AC 17
    [MemberData(nameof(AlertCases))]
    public async Task Each_refusal_of_the_whole_file_shows_its_alert_and_keeps_focus_on_import(HttpStatusCode status, string? code, string text)
    {
        await using var h = new Harness();
        h.Answers(() => Fail<NewsOpmlImportResult>(status, code));
        h.Start();
        await h.Pick("feeds.opml");
        h.ClearInvocations();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Rejected, h.Phase));
        var alert = h.Cut.Find(".ods-modal [role=alert]");
        Assert.Equal(text, alert.TextContent.Trim());
        // Above the form, never instead of it; the file stays chosen and Import stays enabled (A8).
        Assert.True(alert.Closest(".cal-imp-form") is not null);
        Assert.Single(h.Cut.FindAll(".odc-upload-file"));
        Assert.Null(h.Import.GetAttribute("aria-disabled"));
        Assert.True(h.FocusedById(ImportNewsFeedsDialog.ImportButtonId));
        Assert.False(h.FocusedPicker());
        Assert.Null(h.Dropzone.GetAttribute("aria-invalid"));
    }

    [Fact] // AC 16: a non-problem body adds no detail
    public async Task A_failure_without_a_problem_document_shows_the_bare_text()
    {
        await using var h = new Harness();
        h.Answers(() => ApiResult<NewsOpmlImportResult>.Failure(HttpStatusCode.BadGateway, new ApiProblem { Status = 502, ReasonFallback = "Bad Gateway" }));
        h.Start();
        await h.Pick("feeds.opml");

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal("Couldn't import the file.", h.Cut.Find(".ods-modal [role=alert]").TextContent.Trim()));
    }

    [Fact] // AC 16
    public async Task A_dropped_connection_says_the_outcome_is_unknown()
    {
        await using var h = new Harness();
        h.Answers(() => ApiResult<NewsOpmlImportResult>.Failure(new HttpRequestException("reset")));
        h.Start();
        await h.Pick("feeds.opml");

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal(
            "The upload didn't finish, and the import may or may not have run. Close to check the Feeds list, or try again.",
            h.Cut.Find(".ods-modal [role=alert]").TextContent.Trim()));
    }

    [Theory] // AC 16: too many entries names the problem's limit, else the live one
    [InlineData(5_000, "5,000")]
    [InlineData(0, "100,000")]
    [InlineData(null, "100,000")]
    public async Task Too_many_entries_names_the_limit(int? stated, string shown)
    {
        await using var h = new Harness();
        var extensions = stated is { } s ? new Dictionary<string, object> { ["limit"] = s } : null;
        h.Answers(() => Fail<NewsOpmlImportResult>(HttpStatusCode.UnprocessableEntity, NewsErrorCodes.OpmlTooManyEntries, extensions: extensions));
        h.Start();
        await h.Pick("feeds.opml");

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal(
            $"This file lists more than {shown} feeds, the most one import accepts. Nothing was imported.",
            h.Cut.Find(".ods-modal [role=alert]").TextContent.Trim()));
    }

    public static TheoryData<HttpStatusCode, string?, string> FieldCases => new()
    {
        { HttpStatusCode.BadRequest, NewsErrorCodes.OpmlFileRequired, "Choose an OPML file to import." },
        { HttpStatusCode.UnsupportedMediaType, NewsErrorCodes.OpmlWrongType, "That isn't an OPML file. Use a .opml or .xml file." },
        { HttpStatusCode.RequestEntityTooLarge, null, "That file is too large for this server. The import limit is 64 MB." },
    };

    [Theory] // AC 16: field errors focus the picker
    [MemberData(nameof(FieldCases))]
    public async Task A_server_field_error_lands_on_the_picker(HttpStatusCode status, string? code, string text)
    {
        await using var h = new Harness();
        h.Answers(() => Fail<NewsOpmlImportResult>(status, code));
        h.Start();
        await h.Pick("feeds.opml");
        h.ClearInvocations();

        h.ClickImport();

        h.Cut.WaitForAssertion(() => Assert.Equal("true", h.Dropzone.GetAttribute("aria-invalid")));
        Assert.Equal(text, h.Cut.Find($"#{h.Dropzone.GetAttribute("aria-describedby")}").TextContent.Trim());
        Assert.True(h.FocusedPicker());
        Assert.Empty(h.Cut.FindAll(".ods-modal [role=alert]"));
        Assert.Single(h.Cut.FindAll(".odc-upload-file"));
    }

    [Fact] // AC 16 (F4): reject, then retry the same chosen file
    public async Task The_chosen_file_is_still_readable_for_a_retry()
    {
        await using var h = new Harness();
        var uploads = new List<ApiUpload>();
        var answers = new Queue<ApiResult<NewsOpmlImportResult>>([Busy<NewsOpmlImportResult>(), Ok(Mixed())]);
        h.Context.News.Setup(n => n.ImportOpmlAsync(It.IsAny<ApiUpload>(), It.IsAny<CancellationToken>()))
            .Returns((ApiUpload u, CancellationToken _) =>
            {
                using var stream = u.OpenRead();
                Assert.True(stream.CanRead);
                uploads.Add(u);
                return Task.FromResult(answers.Dequeue());
            });
        h.Start();
        await h.Pick("feeds.opml");

        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Rejected, h.Phase));
        h.ClickImport();

        h.Cut.WaitForElement("#" + ImportNewsFeedsDialog.ResultHeadingId);
        Assert.Equal(2, uploads.Count);
        Assert.All(uploads, u => Assert.Equal("feeds.opml", u.FileName));
    }

    [Fact] // AC 18: a 403 asks for one claims refresh; the dialog stays open in Rejected
    public async Task A_403_refreshes_the_claims_and_stays_open()
    {
        await using var h = new Harness();
        h.Answers(() => Fail<NewsOpmlImportResult>(HttpStatusCode.Forbidden), () => Fail<NewsOpmlImportResult>(HttpStatusCode.Forbidden));
        h.Start();
        await h.Pick("feeds.opml");

        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Rejected, h.Phase));
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(2, h.Context.Refresher.Forbidden));

        Assert.Equal(1, h.Context.Refresher.Refreshes);
        Assert.Empty(h.Closed);
        Assert.Equal(ImportNewsFeedsDialog.Phase.Rejected, h.Phase);
    }

    [Fact] // §3.2: choosing another file after a refusal returns to Compose
    public async Task Choosing_a_file_after_a_refusal_clears_the_alert()
    {
        await using var h = new Harness();
        h.Answers(() => Busy<NewsOpmlImportResult>());
        h.Start();
        await h.Pick("feeds.opml");
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Rejected, h.Phase));

        await h.Pick("other.opml");

        Assert.Equal(ImportNewsFeedsDialog.Phase.Compose, h.Phase);
        Assert.Empty(h.Cut.FindAll(".ods-modal [role=alert]"));
    }

    // ── Result (§3.4) ──────────────────────────────────────────────────────────────────────────

    [Fact] // AC 9
    public async Task The_result_focuses_its_heading_inside_a_labelled_group_and_announces_once()
    {
        var h = await ImportedAsync(Mixed(), "subscriptions-from-old-reader.opml");
        await using var _ = h;

        var heading = h.Cut.Find("#" + ImportNewsFeedsDialog.ResultHeadingId);
        Assert.Equal("-1", heading.GetAttribute("tabindex"));
        Assert.Equal("Imported from subscriptions-from-old-reader.opml", System.Text.RegularExpressions.Regex.Replace(heading.TextContent, @"\s+", " ").Trim());
        Assert.NotNull(heading.QuerySelector("bdi"));
        var group = heading.ParentElement!;
        Assert.Equal("group", group.GetAttribute("role"));
        Assert.Equal(ImportNewsFeedsDialog.ResultHeadingId, group.GetAttribute("aria-labelledby"));
        Assert.True(h.FocusedById(ImportNewsFeedsDialog.ResultHeadingId));
        h.Cut.WaitForAssertion(() => Assert.Equal("6 added, 2 already subscribed, 6 not imported.", h.Polite.Trim()));

        var tiles = h.Cut.FindAll(".cal-imp-stat").Select(t => (
            N: t.QuerySelector(".cal-imp-stat-n")!.TextContent.Trim(),
            L: t.QuerySelector(".cal-imp-stat-l")!.TextContent.Trim(),
            Icon: t.QuerySelector(".material-icons")!)).ToList();
        Assert.Equal([("6", "added"), ("2", "already subscribed"), ("6", "not imported")], tiles.Select(t => (t.N, t.L)));
        Assert.Equal(["add_circle", "bookmark_added", "rule"], tiles.Select(t => t.Icon.TextContent.Trim()));
        Assert.All(tiles, t => Assert.Equal("true", t.Icon.GetAttribute("aria-hidden")));
        Assert.True(h.HasFooter("Import another file"));
        Assert.True(h.HasFooter("Done"));
        Assert.DoesNotContain("Add feeds from an OPML file", h.Cut.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC 10
    public async Task Groups_follow_the_spec_order_with_server_counts_and_a_named_unit()
    {
        var h = await ImportedAsync(Mixed());
        await using var _ = h;

        var buttons = h.Cut.FindAll(".cal-imp-skips button.cal-imp-skip-head").Select(Name).ToList();

        Assert.Equal(
        [
            "Added feeds, 6 feeds", "Already subscribed, 2 feeds", "Address can't be used, 3 feeds", "Over the feed limit, 2 feeds",
            "Being deleted, 1 feeds", "Tags not added, 2 tags",
        ], buttons);
        foreach (var button in h.Cut.FindAll(".cal-imp-skips button.cal-imp-skip-head"))
        {
            Assert.Equal("false", button.GetAttribute("aria-expanded"));
            var controls = button.GetAttribute("aria-controls")!;
            var list = h.Cut.Find($"#{controls}");
            Assert.NotNull(list.GetAttribute("hidden"));
            Assert.Empty(list.Children);
        }
    }

    [Fact] // AC 10: rows only when expanded; the count is the server's, and a short list ends "+n more"
    public async Task An_expanded_truncated_group_lists_its_rows_then_the_rest_as_more()
    {
        var result = new NewsOpmlImportResult
        {
            Feeds = 5_001, Created = 1, Invalid = 5_000, ItemsTruncated = true,
            Items = [Item(1, "https://new.example/rss", "New feed", NewsOpmlImportOutcome.Created),
                .. Enumerable.Range(0, 4).Select(i => Item(2 + i, $"http://192.168.1.{20 + i}/feed", $"Home feed {i + 1}", NewsOpmlImportOutcome.Invalid, "This address cannot be fetched."))],
        };
        var h = await ImportedAsync(result);
        await using var _ = h;
        Assert.Equal("Address can't be used, 5,000 feeds", Name(GroupButton(h, "news-opml-g-invalid")));

        GroupButton(h, "news-opml-g-invalid").Click();

        var list = h.Cut.Find("#news-opml-g-invalid-list");
        Assert.Null(list.GetAttribute("hidden"));
        Assert.Equal("true", GroupButton(h, "news-opml-g-invalid").GetAttribute("aria-expanded"));
        Assert.Equal(4, list.QuerySelectorAll("li.nw-opml-row").Length);
        Assert.Equal("+4,996 more", list.QuerySelector("li.cal-imp-skip-more")!.TextContent.Trim());
        Assert.Contains("This address cannot be fetched.", list.QuerySelector(".nw-opml-msg")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("nw-opml-list", list.ClassList);
        Assert.Contains("Some lists are shortened. The counts include every feed.", h.Cut.Markup, StringComparison.Ordinal);
        h.Cut.WaitForAssertion(() => Assert.Equal("1 added, 0 already subscribed, 5,000 not imported.", h.Polite.Trim()));
    }

    [Fact] // AC 10: no "+n more" when every row is present
    public async Task A_complete_group_has_no_more_line()
    {
        var h = await ImportedAsync(Mixed());
        await using var _ = h;

        GroupButton(h, "news-opml-g-limit").Click();

        Assert.Empty(h.Cut.FindAll("#news-opml-g-limit-list .cal-imp-skip-more"));
        Assert.Equal(["Later One", "Later Two"], h.Cut.FindAll("#news-opml-g-limit-list .nw-opml-title").Select(e => e.TextContent));
    }

    [Fact] // AC 11
    public async Task Unknown_outcomes_and_tag_reasons_are_named_generically()
    {
        var result = new NewsOpmlImportResult
        {
            Feeds = 2, Created = 0, Invalid = 1,
            Items = [Item(1, "https://x.example/rss", "Future", (NewsOpmlImportOutcome)42), Item(2, "https://y.example/rss", "Old", NewsOpmlImportOutcome.Unknown)],
            TagsSkipped = [new() { Name = "Mystery", Reason = (NewsOpmlTagSkipReason)99 }, new() { Name = "Long", Reason = NewsOpmlTagSkipReason.NameTooLong }],
            TagsSkippedTotal = 2,
        };
        var h = await ImportedAsync(result);
        await using var _ = h;

        Assert.Equal("Not imported, 2 feeds", Name(GroupButton(h, "news-opml-g-unknown")));
        GroupButton(h, "news-opml-g-unknown").Click();
        Assert.Equal(["Future", "Old"], h.Cut.FindAll("#news-opml-g-unknown-list .nw-opml-title").Select(e => e.TextContent));
        GroupButton(h, "news-opml-g-tags").Click();
        Assert.Equal(["Not added", $"Name too long (over {NewsLimits.MaxTagNameLength} characters)"],
            h.Cut.FindAll("#news-opml-g-tags-list .nw-opml-msg").Select(e => e.TextContent));
    }

    [Theory] // AC 11: the known tag reasons
    [InlineData(NewsOpmlTagSkipReason.NoPermission, "You can't create tags")]
    [InlineData(NewsOpmlTagSkipReason.TagLimit, "The tag limit is reached")]
    [InlineData(NewsOpmlTagSkipReason.Archived, "The tag is archived")]
    [InlineData(NewsOpmlTagSkipReason.Unknown, "Not added")]
    public void Tag_reasons_read_as_text(NewsOpmlTagSkipReason reason, string text) =>
        Assert.Equal(text, ImportNewsFeedsDialog.TagReason(reason));

    [Fact] // AC 12
    public async Task Each_note_appears_when_its_field_says_so()
    {
        var h = await ImportedAsync(Mixed());
        await using var _ = h;
        var notes = h.Cut.FindAll(".cal-imp-note").Select(n => System.Text.RegularExpressions.Regex.Replace(n.TextContent, @"\s+", " ").Trim()).ToList();

        Assert.Contains(notes, n => n.EndsWith("3 repeated entries for the same feed were combined. An Odyssey export lists a feed once per tag.", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.EndsWith("Added 3 tags: Local, Tech, Økonomi", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.EndsWith($"Some feeds named more than {NewsLimits.MaxTagIdsPerRequest} tags; the extra ones were left off.", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.EndsWith("New feeds show as Pending until their first fetch. Paused ones are listed under Status: Paused.", StringComparison.Ordinal));
        Assert.DoesNotContain(notes, n => n.Contains("Some lists are shortened", StringComparison.Ordinal));
        Assert.Equal(3, h.Cut.FindAll(".cal-imp-note .nw-opml-wrap bdi").Count);
    }

    [Fact] // AC 12: none of the notes without their fields
    public async Task No_note_appears_without_its_field()
    {
        var result = new NewsOpmlImportResult
        {
            Feeds = 1, AlreadySubscribed = 1, Items = [Item(1, "https://a.example/rss", "A", NewsOpmlImportOutcome.AlreadySubscribed)],
        };
        var h = await ImportedAsync(result);
        await using var _ = h;

        Assert.Empty(h.Cut.FindAll(".cal-imp-result .cal-imp-note"));
        Assert.Empty(h.Cut.FindAll("#news-opml-g-tags-list"));
    }

    [Fact] // AC 12: one merged entry
    public void One_merged_entry_reads_in_the_singular() =>
        Assert.Equal("1 repeated entry for the same feed was combined. An Odyssey export lists a feed once per tag.", ImportNewsFeedsDialog.MergedNote(1));

    [Fact] // AC 13: hostile text renders as literal text in <bdi>; no URL is a link; user-info is masked
    public async Task Report_values_are_isolated_text_never_markup_or_links()
    {
        var result = new NewsOpmlImportResult
        {
            Feeds = 3, Created = 2, Invalid = 1,
            TagsCreated = ["<b>x</b>", "‮evil"],
            Items =
            [
                Item(1, "https://markup.example/rss", "<b>x</b> Markup", NewsOpmlImportOutcome.Created),
                Item(2, "https://rtl.example/feed", "‮deef desrever‬ Mirror", NewsOpmlImportOutcome.Created),
                Item(3, "https://reader:s3cret@private.example:8443/feed?x=1#top", "Private feed", NewsOpmlImportOutcome.Invalid, "No user names."),
            ],
        };
        var h = await ImportedAsync(result, "<i>file</i>‮.opml");
        await using var _ = h;
        GroupButton(h, "news-opml-g-created").Click();
        GroupButton(h, "news-opml-g-invalid").Click();

        var titles = h.Cut.FindAll(".nw-opml-row bdi.nw-opml-title").Select(e => e.TextContent).ToList();
        Assert.Contains("<b>x</b> Markup", titles);
        Assert.Contains("‮deef desrever‬ Mirror", titles);
        Assert.Empty(h.Cut.FindAll(".cal-imp-result b"));
        Assert.Empty(h.Cut.FindAll(".cal-imp-result i"));
        Assert.Empty(h.Cut.FindAll(".cal-imp-result a"));
        Assert.Equal("<i>file</i>‮.opml", h.Cut.Find($"#{ImportNewsFeedsDialog.ResultHeadingId} bdi").TextContent);
        Assert.Equal(["<b>x</b>", "‮evil"], h.Cut.FindAll(".nw-opml-wrap bdi").Select(e => e.TextContent));
        Assert.All(h.Cut.FindAll("bdi.nw-opml-url"), u => Assert.Equal("ltr", u.GetAttribute("dir")));

        var masked = h.Cut.Find("#news-opml-g-invalid-list bdi.nw-opml-url");
        Assert.DoesNotContain("s3cret", masked.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("reader", masked.TextContent, StringComparison.Ordinal);
        Assert.Equal("•••", masked.QuerySelector("[aria-hidden=true]")!.TextContent);
        Assert.Equal("credentials hidden", masked.QuerySelector(".odc-sr-only")!.TextContent);
        Assert.Equal("https://•••credentials hidden@private.example:8443/feed?x=1#top", masked.TextContent.Trim());
        Assert.DoesNotContain("s3cret", h.Cut.Markup, StringComparison.Ordinal);
    }

    [Theory] // AC 13 (v4 §4): the mask, and an unparseable URL
    [InlineData("https://harbour.example/rss", "https://harbour.example/rss", null)]
    [InlineData("https://user:pw@host.example/x", "https://", "@host.example/x")]
    [InlineData("http://only-user@host.example:8080/p?q=1", "http://", "@host.example:8080/p?q=1")]
    public void Urls_are_masked_from_the_parsed_address(string url, string head, string? tail)
    {
        var masked = ImportNewsFeedsDialog.Mask(url);

        Assert.NotNull(masked);
        Assert.Equal(head, masked!.Head);
        Assert.Equal(tail, masked.Tail);
    }

    [Theory] // AC 13 (v4 §4)
    [InlineData("not a url")]
    [InlineData("/relative/feed")]
    [InlineData("user:pass@host.example/feed")] // scheme "user", empty user-info (security review, PR #513)
    [InlineData("feed://u:p@host.example/f")]
    [InlineData("ftp://host.example/feed")]
    [InlineData("file:///etc/feeds")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unparseable_url_is_hidden(string? url) => Assert.Null(ImportNewsFeedsDialog.Mask(url));

    [Fact] // AC 13 (v4 §4)
    public async Task An_unparseable_url_shows_address_hidden()
    {
        var result = new NewsOpmlImportResult
        {
            Feeds = 1, Invalid = 1, Items = [Item(1, "::not a url::", null, NewsOpmlImportOutcome.Invalid, "Not a web address.")],
        };
        var h = await ImportedAsync(result);
        await using var _ = h;

        GroupButton(h, "news-opml-g-invalid").Click();

        Assert.Equal("(address hidden)", h.Cut.Find("#news-opml-g-invalid-list bdi.nw-opml-url").TextContent.Trim());
        Assert.Equal("Untitled feed", h.Cut.Find("#news-opml-g-invalid-list bdi.nw-opml-title").TextContent);
        Assert.DoesNotContain("::not a url::", h.Cut.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC 19
    public async Task A_file_with_no_feeds_says_so_and_has_no_groups()
    {
        var h = await ImportedAsync(new NewsOpmlImportResult());
        await using var _ = h;

        Assert.Contains("This file doesn't list any feeds.", h.Cut.Find(".cal-imp-result").TextContent, StringComparison.Ordinal);
        Assert.Empty(h.Cut.FindAll(".cal-imp-skip"));
        h.Cut.WaitForAssertion(() => Assert.Equal("This file doesn't list any feeds.", h.Polite.Trim()));
    }

    [Fact] // AC 19
    public async Task When_every_feed_was_added_the_groups_give_way_to_one_line()
    {
        var result = new NewsOpmlImportResult
        {
            Feeds = 2, Created = 2,
            Items = [Item(1, "https://a.example/rss", "A", NewsOpmlImportOutcome.Created), Item(2, "https://b.example/rss", "B", NewsOpmlImportOutcome.Created)],
        };
        var h = await ImportedAsync(result);
        await using var _ = h;

        Assert.Contains("Every feed in the file was added.", h.Cut.Find(".cal-imp-clean").TextContent, StringComparison.Ordinal);
        Assert.Empty(h.Cut.FindAll(".cal-imp-skip"));
        Assert.Equal("check_circle", h.Cut.FindAll(".cal-imp-stat .material-icons")[2].TextContent.Trim());
    }

    [Fact] // AC 19
    public async Task When_nothing_was_added_the_announcement_says_so_first()
    {
        var result = new NewsOpmlImportResult
        {
            Feeds = 3, AlreadySubscribed = 2, FeedLimit = 1,
            Items = [Item(1, "https://a.example/rss", "A", NewsOpmlImportOutcome.AlreadySubscribed), Item(2, "https://b.example/rss", "B", NewsOpmlImportOutcome.AlreadySubscribed),
                Item(3, "https://c.example/rss", "C", NewsOpmlImportOutcome.FeedLimit)],
        };
        var h = await ImportedAsync(result);
        await using var _ = h;

        h.Cut.WaitForAssertion(() => Assert.Equal("No feeds were added. 0 added, 2 already subscribed, 1 not imported.", h.Polite.Trim()));
        Assert.Equal(2, h.Cut.FindAll(".cal-imp-skips > .cal-imp-skip").Count);
        Assert.DoesNotContain("Pending until their first fetch", h.Cut.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC 15
    public async Task Import_another_file_returns_to_compose_with_focus_on_the_picker()
    {
        var h = await ImportedAsync(Mixed());
        await using var _ = h;
        h.ClearInvocations();

        h.Footer("Import another file").Click();

        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Compose, h.Phase));
        Assert.Empty(h.Cut.FindAll(".odc-upload-file"));
        Assert.Empty(h.Cut.FindAll(".cal-imp-result"));
        Assert.True(h.FocusedPicker());
        Assert.Empty(h.Closed);
    }

    // ── Close ──────────────────────────────────────────────────────────────────────────────────

    [Fact] // AC 14: Done after a request reports that one was sent
    public async Task Done_closes_and_reports_a_sent_request()
    {
        var h = await ImportedAsync(Mixed());
        await using var _ = h;

        h.Footer("Done").Click();

        h.Cut.WaitForAssertion(() => Assert.Equal([true], h.Closed));
        Assert.Equal([false], h.OpenChanges);
    }

    [Fact] // §3.3: Cancel in Compose sends nothing and asks for no reload
    public async Task Cancel_without_a_request_reports_none_sent()
    {
        await using var h = new Harness().Start();

        h.Footer("Cancel").Click();

        h.Cut.WaitForAssertion(() => Assert.Equal([false], h.Closed));
    }

    [Fact] // AC 14: a refusal still counts as a sent request
    public async Task Closing_after_a_refusal_reports_a_sent_request()
    {
        await using var h = new Harness();
        h.Answers(() => Fail<NewsOpmlImportResult>(HttpStatusCode.Conflict, NewsErrorCodes.OpmlConflict));
        h.Start();
        await h.Pick("feeds.opml");
        h.ClickImport();
        h.Cut.WaitForAssertion(() => Assert.Equal(ImportNewsFeedsDialog.Phase.Rejected, h.Phase));

        h.Footer("Cancel").Click();

        h.Cut.WaitForAssertion(() => Assert.Equal([true], h.Closed));
    }

    [Fact] // AC 14 / §3.3 step 5: the modal returns focus to the menu root by id
    public async Task The_modal_returns_focus_to_the_menu_root()
    {
        await using var h = new Harness().Start();

        Assert.Equal("news-feeds-actions", h.Cut.FindComponent<OdsModal>().Instance.ReturnFocusToId);
    }

    // ── AC 31: report values stay in the dialog ────────────────────────────────────────────────

    [Fact] // AC 31
    public async Task No_report_value_reaches_the_announcer_or_page_state()
    {
        var h = await ImportedAsync(Mixed(), "secret-name.opml");
        await using var _ = h;
        h.Cut.WaitForAssertion(() => Assert.NotEmpty(h.Polite.Trim()));

        var announced = h.Polite;
        foreach (var value in new[] { "secret-name", "Harbour", "harbour.example", "Local", "Gardening", "cannot be fetched" })
        {
            Assert.DoesNotContain(value, announced, StringComparison.Ordinal);
        }

        Assert.Empty(h.Cut.FindAll(".mud-snackbar"));
        h.Context.State.VerifyNoOtherCalls();
    }

    private static Odyssey.Dtos.Application.ImportLimitsDto ImportLimitsCacheFallbackWith(int megabytes, int entries) =>
        Odyssey.Client.Services.ImportLimitsCache.Fallback with { NewsOpmlMaxImportMegabytes = megabytes, NewsOpmlMaxImportEntries = entries };
}
