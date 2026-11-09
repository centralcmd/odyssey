using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Client.Pages.JobRuns;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Background jobs page rendered (issue #410 §11): regions, Run items per <c>canRunNow</c>, the links and stored
/// state through the allow-list, the run list's request handling (debounce, stale responses, the <c>tz</c> fallback),
/// expanding a row, and Run now with its follow.
/// </summary>
public class BackgroundJobsPageTests
{
    static BackgroundJobsPageTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static readonly string[] Admin = [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    private static BackgroundJobSummary Job(JobType type, Action<BackgroundJobSummary>? edit = null)
    {
        var job = new BackgroundJobSummary
        {
            JobType = type, Health = JobHealth.Healthy, CanRunNow = true, CanReadDetail = true, NotTickingAfterSeconds = 300,
            LastRun = new JobRunReference { Id = Guid.NewGuid(), Status = JobRunStatus.Succeeded, StartedAtUtc = Now.UtcDateTime.AddMinutes(-12), FinishedAtUtc = Now.UtcDateTime.AddMinutes(-11) },
        };
        edit?.Invoke(job);
        return job;
    }

    private static BackgroundJobsOverview Overview(params BackgroundJobSummary[] jobs) => new()
    {
        GeneratedAtUtc = Now.UtcDateTime,
        Jobs = jobs.Length > 0 ? [.. jobs] : [.. BackgroundJobPresentation.Jobs.Select(t => Job(t))],
    };

    private static ExistingJobRun Run(JobType type, JobRunStatus status = JobRunStatus.Succeeded, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), JobType = type, Status = status, Trigger = JobRunTrigger.Scheduled,
        StartedAtUtc = Now.UtcDateTime.AddMinutes(-30), FinishedAtUtc = status == JobRunStatus.Running ? null : Now.UtcDateTime.AddMinutes(-29),
        DurationMs = status == JobRunStatus.Running ? null : 1800, ItemsProcessed = 4, ItemsFailed = 0,
    };

    private static ApiResult<PagedResult<ExistingJobRun>> Page(params ExistingJobRun[] runs) =>
        ApiResult<PagedResult<ExistingJobRun>>.Success(new PagedResult<ExistingJobRun> { Items = runs, TotalCount = runs.Length }, HttpStatusCode.OK);

    private sealed class Fixture(BunitContext ctx, Mock<IBackgroundJobsApiClient> api, Mock<IPageStateService> state, FakeTimeProvider time)
        : IAsyncDisposable
    {
        public BunitContext Ctx { get; } = ctx;

        public Mock<IBackgroundJobsApiClient> Api { get; } = api;

        public Mock<IPageStateService> State { get; } = state;

        public FakeTimeProvider Time { get; } = time;

        public IRenderedComponent<MudPopoverProvider> Popover { get; set; } = default!;

        /// <summary>Every run-list request's filter, in order.</summary>
        public List<JobRunsFilter?> Requests { get; } = [];

        /// <summary>Answers every run-list request with <paramref name="answer"/>, recording each.</summary>
        public void ListRuns(Func<JobRunsFilter?, ApiResult<PagedResult<ExistingJobRun>>> answer) =>
            Api.Setup(a => a.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<JobRunsFilter?>(), It.IsAny<CancellationToken>()))
                .Returns((int _, int _, JobRunsFilter? filter, CancellationToken _) =>
                {
                    Requests.Add(filter);
                    return Task.FromResult(answer(filter));
                });

        public ValueTask DisposeAsync() => Ctx.DisposeAsync();
    }

    private static Fixture NewFixture(
        BackgroundJobsOverview? overview = null, string[]? claims = null, BackgroundJobsPageState? stored = null, ExistingJobRun[]? runs = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo"));
        ctx.Services.AddSingleton<TimeProvider>(time);

        var api = new Mock<IBackgroundJobsApiClient>();
        api.Setup(a => a.GetOverviewAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<BackgroundJobsOverview>.Success(overview ?? Overview(), HttpStatusCode.OK));
        api.Setup(a => a.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => ApiResult<JobRunDetail>.Success(
                new WeatherJobRunDetail { Id = id, JobType = JobType.Weather, Status = JobRunStatus.Succeeded, PointsAttempted = 6, StartedAtUtc = Now.UtcDateTime }, HttpStatusCode.OK));
        ctx.Services.AddSingleton(api.Object);

        var state = new Mock<IPageStateService>();
        state.Setup(s => s.LoadAsync<BackgroundJobsPageState>(BackgroundJobs.PageKey)).ReturnsAsync(stored);
        ctx.Services.AddSingleton(state.Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedInWith(claims ?? Admin));

        var fixture = new Fixture(ctx, api, state, time);
        var rows = runs ?? [Run(JobType.Weather), Run(JobType.ExchangeRateSync)];
        fixture.ListRuns(_ => Page(rows));
        fixture.Popover = ctx.Render<MudPopoverProvider>();
        return fixture;
    }

    private static IRenderedComponent<BackgroundJobs> Render(Fixture f, string? uri = null)
    {
        if (uri is not null)
            f.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var cut = f.Ctx.Render<BackgroundJobs>();
        cut.WaitForAssertion(() => Assert.NotEmpty(f.Requests));
        return cut;
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> OpenMenu(Fixture f, IRenderedComponent<BackgroundJobs> cut)
    {
        cut.Find("button[aria-label='More actions']").Click();
        f.Popover.WaitForAssertion(() => Assert.Contains("Refresh", f.Popover.Markup));
        return f.Popover.FindAll(".mud-menu-item");
    }

    // ── Regions (AC 1, 2) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_page_has_its_labelled_regions_and_attention_only_with_a_problem()
    {
        await using var f = NewFixture();
        var cut = Render(f);

        cut.WaitForAssertion(() => Assert.Equal(BackgroundJobPresentation.Jobs.Count, cut.FindAll("h3.bgj-tile-h").Count));
        Assert.NotNull(cut.Find("section[aria-label='Overview']"));
        Assert.NotNull(cut.Find("section[aria-label='Search']"));
        Assert.Equal(["Jobs", "Totals", "Running now", "Run history"], cut.FindAll("h2.bgj-h2").Select(h => h.TextContent.Trim()));
        Assert.Equal("-1", cut.Find($"#{BackgroundJobs.HistoryHeadingId}").GetAttribute("tabindex"));
        Assert.DoesNotContain("Attention", cut.Markup);
        Assert.Contains("Nothing running.", cut.Markup);
        Assert.Contains(RunTime.Full(Now.UtcDateTime.AddMinutes(-11)), cut.Markup);
        Assert.Contains("11 minutes ago", cut.Markup);
    }

    [Fact]
    public async Task A_failing_job_raises_attention_with_a_show_runs_button()
    {
        await using var f = NewFixture(Overview(
            Job(JobType.ExchangeRateSync, j => j.Health = JobHealth.Failing),
            Job(JobType.Weather), Job(JobType.FileStorageMaintenance), Job(JobType.PhotoThumbnails)));
        var cut = Render(f);

        cut.WaitForAssertion(() => Assert.Contains("Attention", cut.Markup));
        Assert.Contains($"Exchange-rate sync’s last run failed at {RunTime.Clock(Now.UtcDateTime.AddMinutes(-11))}.", cut.Markup);
        Assert.Contains("Show exchange-rate sync runs", cut.Markup);
    }

    [Fact]
    public async Task Zero_runs_show_a_dash_with_no_runs_as_its_text()
    {
        await using var f = NewFixture();
        var cut = Render(f);

        cut.WaitForAssertion(() => Assert.Equal(6, cut.FindAll(".odc-stat").Count));
        var success = cut.FindAll(".odc-stat").Single(s => s.TextContent.Contains("Success · 24 h"));
        Assert.Equal("true", success.QuerySelector("[aria-hidden]")!.GetAttribute("aria-hidden"));
        Assert.Contains("No runs", success.QuerySelector(".sr-only")!.TextContent);
    }

    /// <summary>Issue #428: the move's status card has its own h2, and is absent when its block is null (no file-storage.read).</summary>
    [Fact]
    public async Task The_photo_original_move_card_appears_only_with_its_status_block()
    {
        var withStatus = Overview();
        withStatus.WorkerHealth = new BackgroundJobsWorkerHealth { PhotoOriginalBackfill = new PhotoBackfillStatus { Remaining = 12 } };
        await using (var f = NewFixture(withStatus))
        {
            var cut = await RenderLoaded(f);
            Assert.Contains(cut.FindAll("h2.bgj-h2"), h => h.TextContent == "Photo original move");
            Assert.Single(cut.FindComponents<BackfillMoveCard>());
        }

        await using (var f = NewFixture())
        {
            var cut = await RenderLoaded(f);
            Assert.DoesNotContain(cut.FindAll("h2.bgj-h2"), h => h.TextContent == "Photo original move");
            Assert.Empty(cut.FindComponents<BackfillMoveCard>());
        }
    }

    // ── Run items per canRunNow (AC 12) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_items_follow_can_run_now_and_name_their_cooldown()
    {
        await using var f = NewFixture(Overview(
            Job(JobType.ExchangeRateSync),
            Job(JobType.Weather, j => j.CanRunNow = false),
            Job(JobType.FileStorageMaintenance, j => j.ManualRunAvailableAtUtc = Now.UtcDateTime.AddMinutes(52)),
            Job(JobType.PhotoThumbnails),
            Job(JobType.Unknown)));
        var cut = Render(f);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("h3.bgj-tile-h").Count));

        var items = OpenMenu(f, cut).Select(i => i.TextContent.Trim()).ToList();

        Assert.Equal(
            ["Run exchange-rate sync", $"Run file-storage maintenance — available {RunTime.Clock(Now.UtcDateTime.AddMinutes(52))}", "Run photo thumbnails", "Refresh"],
            items);
        Assert.Contains("Other job", cut.Markup);
        var cooling = f.Popover.FindAll(".mud-menu-item").Single(i => i.TextContent.Contains("available"));
        Assert.True(cooling.HasAttribute("disabled") || cooling.ClassList.Contains("mud-disabled"), cooling.OuterHtml);
    }

    [Fact]
    public async Task Every_run_item_exists_while_the_overview_is_loading()
    {
        await using var f = NewFixture();
        f.Api.Setup(a => a.GetOverviewAsync(It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource<ApiResult<BackgroundJobsOverview>>().Task);
        var cut = Render(f);

        var items = OpenMenu(f, cut).Select(i => i.TextContent.Trim()).ToList();

        Assert.Equal(["Run exchange-rate sync", "Run weather", "Run file-storage maintenance", "Run photo thumbnails", "Run photo original move", "Run file move", "Run instrument-price sync", "Refresh"], items);
        Assert.Equal("true", cut.Find("section[aria-label='Overview']").GetAttribute("aria-busy"));
    }

    /// <summary>Issue #444: the file-analysis queue is listed but has no Run item — it only runs consented analyses.</summary>
    [Fact]
    public async Task The_file_analysis_queue_has_a_tile_but_no_run_item()
    {
        await using var f = NewFixture();
        var cut = await RenderLoaded(f);

        Assert.Contains(cut.FindAll("h3.bgj-tile-h"), h => h.TextContent.Contains("File analysis", StringComparison.Ordinal));
        Assert.DoesNotContain(cut.Instance.RunItems, i => i.Job == JobType.FileAnalysis);
    }

    [Fact]
    public async Task A_reader_without_system_settings_update_gets_no_run_items()
    {
        await using var f = NewFixture(claims: [PermissionClaims.SystemSettingsRead]);
        var cut = Render(f);
        cut.WaitForAssertion(() => Assert.Equal(BackgroundJobPresentation.Jobs.Count, cut.FindAll("h3.bgj-tile-h").Count));

        Assert.Equal(["Refresh"], OpenMenu(f, cut).Select(i => i.TextContent.Trim()));
    }

    // ── Links and stored state (AC 8, 17, 26) ────────────────────────────────────────────────────

    [Fact]
    public async Task Repeated_and_invalid_job_links_apply_once_each_and_are_persisted()
    {
        await using var f = NewFixture();
        var cut = Render(f, "/background-jobs?job=Weather&job=Weather&job=7&job=photothumbnails&job=PhotoThumbnails");

        var first = Assert.Single(f.Requests);
        Assert.Equal([JobType.Weather, JobType.PhotoThumbnails], first!.JobTypes);
        f.State.Verify(s => s.QueueSave(BackgroundJobs.PageKey, It.Is<BackgroundJobsPageState>(st =>
            st.Jobs!.SequenceEqual(new[] { "Weather", "PhotoThumbnails" }))), Times.Once);
        Assert.Contains("Reset filters", cut.Markup);
    }

    [Fact]
    public async Task A_stored_invalid_value_is_dropped_without_a_request_error_and_the_state_rewritten()
    {
        await using var f = NewFixture(stored: new BackgroundJobsPageState { Jobs = ["Weather", "Weather", "2"], Statuses = ["Bogus"], SortKey = "StartedAt", SortDir = "desc", PageSize = 25 });
        var cut = Render(f);

        Assert.Equal([JobType.Weather], Assert.Single(f.Requests)!.JobTypes);
        Assert.Null(f.Requests[0]!.Statuses);
        f.State.Verify(s => s.QueueSave(BackgroundJobs.PageKey, It.Is<BackgroundJobsPageState>(st =>
            st.Jobs!.SequenceEqual(new[] { "Weather" }) && st.Statuses!.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task Currencies_links_to_the_page_filtered_to_the_sync()
    {
        await using var f = NewFixture();
        var cut = Render(f, "/background-jobs?job=ExchangeRateSync");

        Assert.Equal([JobType.ExchangeRateSync], f.Requests[0]!.JobTypes);
        Assert.NotNull(cut.Find("section[aria-label='Search']"));
        Assert.Equal(["ExchangeRateSync"], cut.FindComponents<OdsMultiSelect>()[0].Instance.Values);
    }

    // ── Filters (AC 7, 27) ───────────────────────────────────────────────────────────────────────

    private static Task Toggle(IRenderedComponent<BackgroundJobs> cut, int select, params string[] values) =>
        cut.InvokeAsync(() => cut.FindComponents<OdsMultiSelect>()[select].Instance.ValuesChanged.InvokeAsync(values));

    [Fact]
    public async Task A_burst_of_toggles_is_one_request_after_the_debounce()
    {
        await using var f = NewFixture();
        var cut = Render(f);

        await Toggle(cut, 0, "Weather");
        await Toggle(cut, 0, "Weather", "PhotoThumbnails");
        await Toggle(cut, 1, "Failed");
        Assert.Single(f.Requests);

        await cut.InvokeAsync(() => f.Time.Advance(BackgroundJobs.FilterDebounce));

        cut.WaitForAssertion(() => Assert.Equal(2, f.Requests.Count));
        Assert.Equal([JobType.Weather, JobType.PhotoThumbnails], f.Requests[1]!.JobTypes);
        Assert.Equal([JobRunStatus.Failed], f.Requests[1]!.Statuses);
    }

    [Fact]
    public async Task A_slower_earlier_response_never_replaces_a_newer_one()
    {
        await using var f = NewFixture();
        var cut = Render(f);
        var slow = new TaskCompletionSource<ApiResult<PagedResult<ExistingJobRun>>>();
        var newer = Run(JobType.PhotoThumbnails);
        var calls = 0;
        f.Api.Setup(a => a.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<JobRunsFilter?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? slow.Task : Task.FromResult(Page(newer)));

        await Toggle(cut, 0, "Weather");
        await cut.InvokeAsync(() => f.Time.Advance(BackgroundJobs.FilterDebounce));
        await Toggle(cut, 0, "PhotoThumbnails");
        await cut.InvokeAsync(() => f.Time.Advance(BackgroundJobs.FilterDebounce));
        cut.WaitForAssertion(() => Assert.Contains("Photo thumbnails run started", cut.Markup));

        await cut.InvokeAsync(() => slow.SetResult(Page(Run(JobType.Weather), Run(JobType.Weather))));

        Assert.Single(cut.FindAll("tbody tr:not(.odc-rec-detail-row)"));
        Assert.Contains("Photo thumbnails run started", cut.Markup);
    }

    // ── tz (AC 9, 28) ────────────────────────────────────────────────────────────────────────────

    private static Task PickTo(IRenderedComponent<BackgroundJobs> cut, DateTime day) =>
        cut.InvokeAsync(() => cut.FindComponent<OdsDateRangePicker>().Instance.ToChanged.InvokeAsync(day));

    private static ApiResult<PagedResult<ExistingJobRun>> BadRequest(string field) =>
        ApiResult<PagedResult<ExistingJobRun>>.Failure(HttpStatusCode.BadRequest, new ApiProblem
        {
            Status = 400,
            Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [field] = ["rejected"] },
        });

    [Fact]
    public async Task A_rejected_zone_retries_once_in_utc_and_later_requests_stay_in_utc()
    {
        await using var f = NewFixture();
        var cut = Render(f);
        Assert.Contains("Days in your time zone, Europe/Oslo", cut.Markup);
        f.ListRuns(filter => filter?.TimeZone == "Europe/Oslo" ? BadRequest("tz") : Page(Run(JobType.Weather)));

        await PickTo(cut, new DateTime(2026, 11, 5));

        cut.WaitForAssertion(() => Assert.Contains("Days in UTC — your time zone wasn’t recognised", cut.Markup));
        Assert.Equal(["Europe/Oslo", "UTC"], f.Requests.Skip(1).Select(r => r!.TimeZone));
        Assert.Equal(new DateOnly(2026, 11, 5), f.Requests[^1]!.StartedTo);

        await cut.InvokeAsync(() => cut.FindComponent<OdsDateRangePicker>().Instance.FromChanged.InvokeAsync(new DateTime(2026, 11, 1)));
        cut.WaitForAssertion(() => Assert.Equal(4, f.Requests.Count));
        Assert.Equal("UTC", f.Requests[^1]!.TimeZone);
    }

    [Fact]
    public async Task A_400_keyed_anything_else_never_retries_and_offers_reset()
    {
        await using var f = NewFixture();
        var cut = Render(f);
        f.ListRuns(_ => BadRequest("startedFrom"));

        await PickTo(cut, new DateTime(2026, 11, 5));

        cut.WaitForAssertion(() => Assert.Contains("These filters couldn’t be applied.", cut.Markup));
        Assert.Equal(2, f.Requests.Count);
        Assert.Contains("Days in your time zone", cut.Markup);
    }

    // ── Expanding a row (AC 10) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expanding_a_row_names_the_run_loads_its_detail_into_a_labelled_group()
    {
        var run = Run(JobType.Weather);
        await using var f = NewFixture(runs: [run]);
        var cut = Render(f);
        var name = BackgroundJobs.RowLabel(run);
        Assert.Equal($"Weather run started {RunTime.Precise(run.StartedAtUtc)}", name);

        var toggle = cut.WaitForElement($"button[aria-label='Show details for {name}']");
        toggle.Click();

        cut.WaitForAssertion(() => Assert.Contains("Details loaded", cut.Markup));
        var expanded = cut.Find($"button[aria-label='Hide details for {name}']");
        var group = cut.Find($"#{expanded.GetAttribute("aria-controls")}");
        Assert.Equal("group", group.GetAttribute("role"));
        Assert.Equal($"Details for {name}", group.GetAttribute("aria-label"));
        Assert.Contains("Points attempted", group.TextContent);
        f.Api.Verify(a => a.GetRunAsync(run.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_detail_shows_an_alert_with_retry()
    {
        var run = Run(JobType.Weather);
        await using var f = NewFixture(runs: [run]);
        var fail = true;
        f.Api.Setup(a => a.GetRunAsync(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => fail
            ? ApiResult<JobRunDetail>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 })
            : ApiResult<JobRunDetail>.Success(new JobRunDetail { Id = run.Id, JobType = JobType.Weather, Status = JobRunStatus.Succeeded }, HttpStatusCode.OK));
        var cut = Render(f);

        cut.WaitForElement("button.odc-rec-expand").Click();

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load these details.", cut.Markup));
        Assert.Contains("Couldn’t load details", cut.Find(".bgj-detail [role=status]").TextContent);
        Assert.Null(cut.Find(".bgj-detail").GetAttribute("aria-busy"));

        fail = false;
        cut.FindAll(".bgj-detail button").Single(b => b.TextContent.Contains("Retry")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Details loaded", cut.Find(".bgj-detail [role=status]").TextContent));
    }

    // ── Run now and follow (AC 13, 14) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_now_starts_a_follow_with_a_named_stop_button_and_stop_removes_it()
    {
        var runId = Guid.NewGuid();
        await using var f = NewFixture();
        f.Api.Setup(a => a.RunNowAsync(JobType.Weather, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<JobRunStarted>.Success(new JobRunStarted { Id = runId }, HttpStatusCode.Accepted));
        var cut = Render(f);
        cut.WaitForAssertion(() => Assert.Equal(BackgroundJobPresentation.Jobs.Count, cut.FindAll("h3.bgj-tile-h").Count));

        OpenMenu(f, cut).Single(i => i.TextContent.Trim() == "Run weather").Click();

        cut.WaitForAssertion(() => Assert.Contains("Stop following weather run", cut.Markup));
        var group = cut.Find("[role=group][aria-label='Followed runs']");
        Assert.Null(group.GetAttribute("aria-live"));
        Assert.Contains("Weather run started", cut.Find(".bgj-toast-host").TextContent);

        cut.FindAll(".bgj-follow button").Single().Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".bgj-follow")));
        Assert.Empty(cut.Instance.Follows);
    }

    [Fact]
    public async Task A_refused_run_is_an_error_toast_and_never_shows_the_servers_detail()
    {
        await using var f = NewFixture();
        f.Api.Setup(a => a.RunNowAsync(JobType.Weather, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<JobRunStarted>.Failure(HttpStatusCode.Conflict,
                new ApiProblem { Status = 409, Code = JobRunConflictCodes.AlreadyRunning, Detail = "SERVER-DETAIL" }));
        var cut = Render(f);
        cut.WaitForAssertion(() => Assert.Equal(BackgroundJobPresentation.Jobs.Count, cut.FindAll("h3.bgj-tile-h").Count));

        await cut.InvokeAsync(() => cut.Instance.RunNowAsync(JobType.Weather));

        cut.WaitForAssertion(() => Assert.Contains("A weather run is already in progress.", cut.Find(".bgj-toast-host").TextContent));
        Assert.DoesNotContain("SERVER-DETAIL", cut.Markup);
        Assert.Empty(cut.Instance.Follows);
    }

    // ── Shared helpers for the review-round tests (PR #418) ──────────────────────────────────────

    /// <summary>
    /// Steps a follow one read at a time through the page's follower seam: each wait blocks until the test releases
    /// it, so a test decides exactly when a tick happens instead of racing a timer. With <see cref="Time"/> set, each
    /// released wait also advances the fake clock by the interval (the 3-minute cap needs it).
    /// </summary>
    private sealed class FollowSteps
    {
        private readonly SemaphoreSlim gate = new(0);

        public FakeTimeProvider? Time { get; init; }

        public async Task Delay(TimeSpan span, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            Time?.Advance(span);
        }

        public void Step(int count = 1) => gate.Release(count);
    }

    /// <summary>
    /// Waits for the renderer's current work item to finish. A follow's end removes its line (a render, which is what
    /// WaitForAssertion re-checks on) and THEN announces, in the same work item, so the announcement is asserted only
    /// once that item has run — not on a render that happened before it.
    /// </summary>
    private static Task Settled(IRenderedComponent<BackgroundJobs> cut) => cut.InvokeAsync(() => { });

    private static List<string?> FocusCalls(Fixture f) =>
        [.. f.Ctx.JSInterop.Invocations.Where(i => i.Identifier == "odsFocusById").Select(i => i.Arguments[0] as string)];

    private static void Starts(Fixture f, JobType job, Guid runId) =>
        f.Api.Setup(a => a.RunNowAsync(job, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<JobRunStarted>.Success(new JobRunStarted { Id = runId }, HttpStatusCode.Accepted));

    private static ApiResult<JobRunDetail> Detail(Guid id, JobType job, JobRunStatus status, int processed = 4) =>
        ApiResult<JobRunDetail>.Success(new WeatherJobRunDetail
        {
            Id = id, JobType = job, Status = status, StartedAtUtc = Now.UtcDateTime.AddMinutes(-30),
            ItemsProcessed = processed, PointsAttempted = processed,
        }, HttpStatusCode.OK);

    private static async Task<IRenderedComponent<BackgroundJobs>> RenderLoaded(Fixture f, FollowSteps? steps = null)
    {
        var cut = Render(f);
        cut.WaitForAssertion(() => Assert.Equal(BackgroundJobPresentation.Jobs.Count, cut.FindAll("h3.bgj-tile-h").Count));
        if (steps is not null)
            await cut.InvokeAsync(() => cut.Instance.FollowDelay = steps.Delay);
        return cut;
    }

    // ── Sort (AC 6) ──────────────────────────────────────────────────────────────────────────────

    private static AngleSharp.Dom.IElement Header(IRenderedComponent<BackgroundJobs> cut, string text) =>
        cut.FindAll("thead th[aria-sort]").Single(th => th.QuerySelector("button > span")!.TextContent.Trim() == text);

    [Fact]
    public async Task The_header_sort_and_the_sort_select_share_one_state_and_re_query()
    {
        await using var f = NewFixture();
        var cut = await RenderLoaded(f);
        var select = cut.FindComponent<OdsSortSelect<ExistingJobRun>>();
        Assert.Equal("descending", Header(cut, "Started").GetAttribute("aria-sort"));
        Assert.Equal("none", Header(cut, "Duration").GetAttribute("aria-sort"));
        Assert.Equal((JobRunSortBy?)JobRunSortBy.StartedAt, f.Requests[0]!.SortBy);

        Header(cut, "Duration").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Equal(2, f.Requests.Count));
        var byHeader = select.Instance.Sort!;
        Assert.Equal(nameof(JobRunSortBy.Duration), byHeader.Key);
        var word = byHeader.Dir == OdsSortDirection.Asc ? "ascending" : "descending";
        Assert.Equal(word, Header(cut, "Duration").GetAttribute("aria-sort"));
        Assert.Equal("none", Header(cut, "Started").GetAttribute("aria-sort"));
        Assert.Equal((JobRunSortBy?)JobRunSortBy.Duration, f.Requests[^1]!.SortBy);
        Assert.Equal(byHeader.Dir == OdsSortDirection.Asc, f.Requests[^1]!.Ascending);

        await cut.InvokeAsync(() => select.Instance.SortChanged.InvokeAsync(new OdsTableSort(nameof(JobRunSortBy.Status), OdsSortDirection.Asc)));

        cut.WaitForAssertion(() => Assert.Equal(3, f.Requests.Count));
        Assert.Equal("ascending", Header(cut, "Status").GetAttribute("aria-sort"));
        Assert.Equal("none", Header(cut, "Duration").GetAttribute("aria-sort"));
        Assert.Equal((JobRunSortBy?)JobRunSortBy.Status, f.Requests[^1]!.SortBy);
        Assert.True(f.Requests[^1]!.Ascending);
        f.State.Verify(s => s.QueueSave(BackgroundJobs.PageKey, It.Is<BackgroundJobsPageState>(st =>
            st.SortKey == nameof(JobRunSortBy.Status) && st.SortDir == "asc")), Times.Once);
    }

    // ── Follow ticks (AC 11) ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A follow tick re-reads only the followed run and updates its row and detail in place. The page deliberately
    /// does not defer the update to focusout: a tick re-renders the row, but the toggle keeps its element, id and
    /// expanded state and nothing moves focus, so a keyboard user on it stays there.
    /// </summary>
    [Fact]
    public async Task A_follow_tick_refreshes_only_the_followed_run_and_keeps_focus_on_an_expanded_rows_toggle()
    {
        var followed = Run(JobType.Weather, JobRunStatus.Running);
        var other = Run(JobType.ExchangeRateSync);
        await using var f = NewFixture(runs: [followed, other]);
        var processed = 4;
        f.Api.Setup(a => a.GetRunAsync(followed.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Detail(followed.Id, JobType.Weather, JobRunStatus.Running, processed));
        Starts(f, JobType.Weather, followed.Id);
        var steps = new FollowSteps();
        var cut = await RenderLoaded(f, steps);
        var label = BackgroundJobs.RowLabel(followed);
        cut.WaitForElement($"button[aria-label='Show details for {label}']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Details loaded", cut.Find(".bgj-detail [role=status]").TextContent));
        var toggleId = cut.Find($"button[aria-label='Hide details for {label}']").Id;

        await cut.InvokeAsync(() => cut.Instance.RunNowAsync(JobType.Weather));
        cut.WaitForAssertion(() => Assert.Single(cut.Instance.Follows));
        var listRequests = f.Requests.Count;
        var focusCalls = FocusCalls(f).Count;
        f.Api.Invocations.Clear();

        processed = 9;
        steps.Step();

        cut.WaitForAssertion(() => Assert.Equal("9", cut.FindAll("tbody tr:not(.odc-rec-detail-row)")[0].Children[5].TextContent.Trim()));
        var toggle = cut.Find($"button[aria-label='Hide details for {label}']");
        Assert.Equal(toggleId, toggle.Id);
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        Assert.Contains("Details loaded", cut.Find(".bgj-detail [role=status]").TextContent);
        f.Api.Verify(a => a.GetRunAsync(followed.Id, It.IsAny<CancellationToken>()), Times.Once);
        f.Api.Verify(a => a.GetRunAsync(other.Id, It.IsAny<CancellationToken>()), Times.Never);
        f.Api.Verify(a => a.GetOverviewAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(listRequests, f.Requests.Count);
        Assert.Equal(focusCalls, FocusCalls(f).Count);
    }

    // ── Follow removal and its announcements (AC 14) ─────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_focused_follow_line_moves_focus_to_the_next_stop_button_then_the_heading()
    {
        var weather = Guid.NewGuid();
        var photos = Guid.NewGuid();
        await using var f = NewFixture();
        Starts(f, JobType.Weather, weather);
        Starts(f, JobType.PhotoThumbnails, photos);
        var cut = await RenderLoaded(f, new FollowSteps());
        await cut.InvokeAsync(() => cut.Instance.RunNowAsync(JobType.Weather));
        await cut.InvokeAsync(() => cut.Instance.RunNowAsync(JobType.PhotoThumbnails));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".bgj-follow").Count));
        var (first, second) = (cut.Instance.Follows[0].StopId, cut.Instance.Follows[1].StopId);
        var active = first;
        f.Ctx.JSInterop.Setup<string?>("odsActiveElementId").SetResult(active);

        cut.Find($"#{first}").Click();

        cut.WaitForAssertion(() => Assert.Equal(second, FocusCalls(f).LastOrDefault()));
        await Settled(cut);
        Assert.Single(cut.FindAll(".bgj-follow"));
        Assert.Contains("Stopped following weather run.", cut.Instance.Announcements.Requested);

        f.Ctx.JSInterop.Setup<string?>("odsActiveElementId").SetResult(second);
        cut.Find($"#{second}").Click();

        cut.WaitForAssertion(() => Assert.Equal(BackgroundJobs.HistoryHeadingId, FocusCalls(f).LastOrDefault()));
        Assert.Empty(cut.FindAll(".bgj-follow"));
    }

    [Fact]
    public async Task A_follow_stopped_by_errors_says_to_refresh()
    {
        var runId = Guid.NewGuid();
        await using var f = NewFixture();
        Starts(f, JobType.Weather, runId);
        f.Api.Setup(a => a.GetRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<JobRunDetail>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));
        var steps = new FollowSteps();
        var cut = await RenderLoaded(f, steps);
        await cut.InvokeAsync(() => cut.Instance.RunNowAsync(JobType.Weather));
        cut.WaitForAssertion(() => Assert.Single(cut.Instance.Follows));

        steps.Step(JobRunFollower.MaxConsecutiveErrors);

        cut.WaitForAssertion(() => Assert.Empty(cut.Instance.Follows));
        await Settled(cut);
        Assert.Contains("Stopped following weather run. Select Refresh to check again.", cut.Instance.Announcements.Requested);
        f.Api.Verify(a => a.GetRunAsync(runId, It.IsAny<CancellationToken>()), Times.Exactly(JobRunFollower.MaxConsecutiveErrors));
    }

    [Fact]
    public async Task A_follow_that_reaches_its_cap_says_the_run_is_still_running()
    {
        var runId = Guid.NewGuid();
        await using var f = NewFixture();
        Starts(f, JobType.Weather, runId);
        f.Api.Setup(a => a.GetRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Detail(runId, JobType.Weather, JobRunStatus.Running));
        var steps = new FollowSteps { Time = f.Time };
        var cut = await RenderLoaded(f, steps);
        await cut.InvokeAsync(() => cut.Instance.RunNowAsync(JobType.Weather));
        cut.WaitForAssertion(() => Assert.Single(cut.Instance.Follows));

        steps.Step(36);

        cut.WaitForAssertion(() => Assert.Empty(cut.Instance.Follows));
        await Settled(cut);
        Assert.Contains("Weather is still running. Select Refresh to check again.", cut.Instance.Announcements.Requested);
        f.Api.Verify(a => a.GetRunAsync(runId, It.IsAny<CancellationToken>()), Times.Exactly(36));
    }

    // ── Show {job} runs (AC 15) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Show_job_runs_filters_to_that_job_and_focuses_the_run_history_heading()
    {
        await using var f = NewFixture();
        var cut = await RenderLoaded(f);

        cut.FindAll(".bgj-tile-foot button").Single(b => b.TextContent.Contains("Show weather runs")).Click();

        cut.WaitForAssertion(() => Assert.Equal(BackgroundJobs.HistoryHeadingId, FocusCalls(f).LastOrDefault()));
        Assert.Equal([JobType.Weather], f.Requests[^1]!.JobTypes);
        Assert.Equal(["Weather"], cut.FindComponents<OdsMultiSelect>()[0].Instance.Values);
        Assert.Equal("-1", cut.Find($"#{BackgroundJobs.HistoryHeadingId}").GetAttribute("tabindex"));
    }

    // ── Refresh announcements (AC 16) ────────────────────────────────────────────────────────────

    private static async Task Refresh(Fixture f, IRenderedComponent<BackgroundJobs> cut, int overviewReads)
    {
        OpenMenu(f, cut).Single(i => i.TextContent.Trim() == "Refresh").Click();
        await Task.Yield();
        cut.WaitForAssertion(() => f.Api.Verify(a => a.GetOverviewAsync(It.IsAny<CancellationToken>()), Times.Exactly(overviewReads)));
    }

    [Fact]
    public async Task Refresh_announces_when_attention_and_worker_health_change_and_when_they_clear()
    {
        var healthy = Overview();
        var failing = Overview(
            Job(JobType.ExchangeRateSync, j => j.Health = JobHealth.Failing),
            Job(JobType.Weather), Job(JobType.FileStorageMaintenance), Job(JobType.PhotoThumbnails));
        failing.WorkerHealth = new BackgroundJobsWorkerHealth { FileStorage = new FileStorageWorkerHealth { FailingWorkers = 1, FailingChecks = ["V3"] } };
        var current = healthy;
        await using var f = NewFixture();
        f.Api.Setup(a => a.GetOverviewAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<BackgroundJobsOverview>.Success(current, HttpStatusCode.OK));
        var cut = await RenderLoaded(f);
        var workerLine = Assert.Single(BackgroundJobs.WorkerHealthLines(failing.WorkerHealth));

        current = failing;
        await Refresh(f, cut, 2);

        cut.WaitForAssertion(() => Assert.Contains("1 job needs attention", cut.Instance.Announcements.Requested));
        Assert.Contains(workerLine, cut.Instance.Announcements.Requested);

        current = healthy;
        await Refresh(f, cut, 3);

        cut.WaitForAssertion(() => Assert.Contains("No jobs need attention", cut.Instance.Announcements.Requested));
        Assert.Contains("Worker health is back to normal", cut.Instance.Announcements.Requested);
        Assert.Equal(2, cut.Instance.Announcements.Requested.Count(t => t == "Background jobs refreshed"));
    }

    // ── Reset (AC 7) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_clears_the_stored_state_announces_focuses_the_first_filter_and_sends_no_zone()
    {
        await using var f = NewFixture(stored: new BackgroundJobsPageState { Jobs = ["Weather"], Statuses = ["Failed"], SortKey = "Duration", SortDir = "asc", PageSize = 50 });
        var cut = await RenderLoaded(f);
        await PickTo(cut, new DateTime(2026, 11, 5));
        cut.WaitForAssertion(() => Assert.Equal("Europe/Oslo", f.Requests[^1]!.TimeZone));
        f.State.Invocations.Clear();

        cut.FindAll(".bgj-filter-bar button").Single(b => b.TextContent.Contains("Reset filters")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Filters reset. 2 runs", cut.Instance.Announcements.Requested));
        var defaults = JobRunFilters.Default.ToState();
        f.State.Verify(s => s.QueueSave(BackgroundJobs.PageKey, It.Is<BackgroundJobsPageState>(st => st.Fingerprint == defaults.Fingerprint)), Times.Once);
        Assert.Equal(BackgroundJobs.FirstFilterId, FocusCalls(f).LastOrDefault());
        var last = f.Requests[^1]!;
        Assert.Null(last.TimeZone);
        Assert.Null(last.StartedFrom);
        Assert.Null(last.StartedTo);
        Assert.True(last.JobTypes is null or { Count: 0 });
        Assert.Equal((JobRunSortBy?)JobRunSortBy.StartedAt, last.SortBy);
        Assert.DoesNotContain("Reset filters", cut.Find(".bgj-filter-bar").TextContent);
    }

    // ── Detail loading and retry (AC 10; accessibility review, finding 2) ─────────────────────────

    [Fact]
    public async Task A_pending_detail_is_busy_and_says_loading_details_until_it_arrives()
    {
        var run = Run(JobType.Weather);
        await using var f = NewFixture(runs: [run]);
        var pending = new TaskCompletionSource<ApiResult<JobRunDetail>>();
        f.Api.Setup(a => a.GetRunAsync(run.Id, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var cut = await RenderLoaded(f);

        cut.WaitForElement("button.odc-rec-expand").Click();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".bgj-detail").GetAttribute("aria-busy")));
        Assert.Equal("Loading details", cut.Find(".bgj-detail [role=status]").TextContent.Trim());

        await cut.InvokeAsync(() => pending.SetResult(Detail(run.Id, JobType.Weather, JobRunStatus.Succeeded)));

        cut.WaitForAssertion(() => Assert.Equal("Details loaded", cut.Find(".bgj-detail [role=status]").TextContent.Trim()));
        Assert.Null(cut.Find(".bgj-detail").GetAttribute("aria-busy"));
    }

    [Fact]
    public async Task Retrying_a_failed_detail_refetches_and_moves_focus_to_the_detail_body()
    {
        var run = Run(JobType.Weather);
        await using var f = NewFixture(runs: [run]);
        var fail = true;
        f.Api.Setup(a => a.GetRunAsync(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => fail
            ? ApiResult<JobRunDetail>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 })
            : Detail(run.Id, JobType.Weather, JobRunStatus.Succeeded));
        var cut = await RenderLoaded(f);
        cut.WaitForElement("button.odc-rec-expand").Click();
        cut.WaitForAssertion(() => Assert.Equal("Couldn’t load details", cut.Find(".bgj-detail [role=status]").TextContent.Trim()));
        var body = cut.Find(".bgj-detail");
        Assert.Equal("-1", body.GetAttribute("tabindex"));

        fail = false;
        cut.Find($"#bgj-detail-retry-{run.Id:N}").Click();

        cut.WaitForAssertion(() => Assert.Equal("Details loaded", cut.Find(".bgj-detail [role=status]").TextContent.Trim()));
        Assert.Equal(body.Id, FocusCalls(f).LastOrDefault());
        f.Api.Verify(a => a.GetRunAsync(run.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Retrying_the_overview_moves_focus_to_the_jobs_heading()
    {
        await using var f = NewFixture();
        var fail = true;
        f.Api.Setup(a => a.GetOverviewAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => fail
            ? ApiResult<BackgroundJobsOverview>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 })
            : ApiResult<BackgroundJobsOverview>.Success(Overview(), HttpStatusCode.OK));
        var cut = Render(f);
        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load the job overview.", cut.Markup));

        fail = false;
        cut.Find("#bgj-overview-retry").Click();

        cut.WaitForAssertion(() => Assert.Equal(BackgroundJobPresentation.Jobs.Count, cut.FindAll("h3.bgj-tile-h").Count));
        Assert.Equal(BackgroundJobs.JobsHeadingId, FocusCalls(f).LastOrDefault());
        Assert.Equal("-1", cut.Find($"#{BackgroundJobs.JobsHeadingId}").GetAttribute("tabindex"));
    }

    // ── Frontend and accessibility review fixes (PR #418) ────────────────────────────────────────

    [Fact]
    public async Task Each_date_input_has_its_own_name_and_is_described_by_the_caption()
    {
        await using var f = NewFixture();
        var cut = await RenderLoaded(f);

        var pickers = cut.FindComponent<OdsDateRangePicker>().FindComponents<OdsDatePicker>();

        Assert.Equal(["Started from", "Started to"], pickers.Select(p => p.Instance.AriaLabel));
        Assert.All(pickers, p => Assert.Equal(BackgroundJobs.DatesCaptionId, p.Instance.AriaDescribedBy));
        Assert.NotNull(cut.Find($"p#{BackgroundJobs.DatesCaptionId}"));
    }

    [Fact]
    public async Task A_run_item_in_cooldown_comes_back_on_its_own_when_the_cooldown_passes()
    {
        var available = Now.UtcDateTime.AddMinutes(1);
        await using var f = NewFixture(Overview(
            Job(JobType.ExchangeRateSync), Job(JobType.Weather, j => j.ManualRunAvailableAtUtc = available),
            Job(JobType.FileStorageMaintenance), Job(JobType.PhotoThumbnails), Job(JobType.PhotoOriginalBackfill),
            Job(JobType.FileAnalysis, j => j.CanRunNow = false), Job(JobType.FileBlobBackfill), Job(JobType.InstrumentPriceSync)));
        var cut = await RenderLoaded(f);
        Assert.Contains(cut.Instance.RunItems, i => i.Job == JobType.Weather && i.Disabled);

        await cut.InvokeAsync(() => f.Time.Advance(TimeSpan.FromMinutes(1)));

        cut.WaitForAssertion(() => Assert.Contains(cut.Instance.RunItems, i => i.Job == JobType.Weather && !i.Disabled && i.Label == "Run weather"));
        var items = OpenMenu(f, cut);
        Assert.Contains(items, i => i.TextContent.Trim() == "Run weather");
    }

    [Fact]
    public async Task A_page_past_the_end_of_a_shrunken_log_is_re_read_as_the_last_page()
    {
        await using var f = NewFixture();
        var pages = new List<int>();
        var rows = Enumerable.Range(0, 25).Select(_ => Run(JobType.Weather)).ToArray();
        f.Api.Setup(a => a.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<JobRunsFilter?>(), It.IsAny<CancellationToken>()))
            .Returns((int page, int _, JobRunsFilter? filter, CancellationToken _) =>
            {
                pages.Add(page);
                f.Requests.Add(filter);
                var paged = pages.Count == 1
                    ? new PagedResult<ExistingJobRun> { Items = rows, TotalCount = 30 }
                    : page == 2
                        ? new PagedResult<ExistingJobRun> { Items = [], TotalCount = 20 }
                        : new PagedResult<ExistingJobRun> { Items = rows[..20], TotalCount = 20 };
                return Task.FromResult(ApiResult<PagedResult<ExistingJobRun>>.Success(paged, HttpStatusCode.OK));
            });
        var cut = await RenderLoaded(f);

        await cut.InvokeAsync(() => cut.FindComponent<OdsPager>().Instance.PageChanged.InvokeAsync(2));

        cut.WaitForAssertion(() => Assert.Equal([1, 2, 1], pages));
        Assert.Equal(20, cut.FindAll("tbody tr:not(.odc-rec-detail-row)").Count);
        Assert.DoesNotContain("No runs yet", cut.Markup);
    }

    [Fact]
    public async Task A_job_link_followed_while_the_page_is_open_is_applied()
    {
        await using var f = NewFixture();
        var cut = await RenderLoaded(f);
        Assert.Null(f.Requests[^1]!.JobTypes);

        await cut.InvokeAsync(() => f.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/background-jobs?job=PhotoThumbnails"));

        cut.WaitForAssertion(() => Assert.Equal([JobType.PhotoThumbnails], f.Requests[^1]!.JobTypes));
        Assert.Equal(["PhotoThumbnails"], cut.FindComponents<OdsMultiSelect>()[0].Instance.Values);
    }

    [Fact]
    public async Task Two_runs_of_one_job_in_the_same_minute_have_distinct_row_names()
    {
        var first = Run(JobType.Weather) with { StartedAtUtc = Now.UtcDateTime.AddMinutes(-5) };
        var second = Run(JobType.Weather) with { StartedAtUtc = Now.UtcDateTime.AddMinutes(-5).AddSeconds(20), Trigger = JobRunTrigger.Manual };
        await using var f = NewFixture(runs: [first, second]);
        var cut = await RenderLoaded(f);

        var names = cut.FindAll("button.odc-rec-expand").Select(b => b.GetAttribute("aria-label")).ToList();

        Assert.Equal(2, names.Distinct().Count());
    }
}
