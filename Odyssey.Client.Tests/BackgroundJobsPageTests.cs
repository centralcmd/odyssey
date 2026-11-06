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

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("h3.bgj-tile-h").Count));
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
    public async Task All_four_run_items_exist_while_the_overview_is_loading()
    {
        await using var f = NewFixture();
        f.Api.Setup(a => a.GetOverviewAsync(It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource<ApiResult<BackgroundJobsOverview>>().Task);
        var cut = Render(f);

        var items = OpenMenu(f, cut).Select(i => i.TextContent.Trim()).ToList();

        Assert.Equal(["Run exchange-rate sync", "Run weather", "Run file-storage maintenance", "Run photo thumbnails", "Refresh"], items);
        Assert.Equal("true", cut.Find("section[aria-label='Overview']").GetAttribute("aria-busy"));
    }

    [Fact]
    public async Task A_reader_without_system_settings_update_gets_no_run_items()
    {
        await using var f = NewFixture(claims: [PermissionClaims.SystemSettingsRead]);
        var cut = Render(f);
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("h3.bgj-tile-h").Count));

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
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("h3.bgj-tile-h").Count));

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
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("h3.bgj-tile-h").Count));

        await cut.InvokeAsync(() => cut.Instance.RunNowAsync(JobType.Weather));

        cut.WaitForAssertion(() => Assert.Contains("A weather run is already in progress.", cut.Find(".bgj-toast-host").TextContent));
        Assert.DoesNotContain("SERVER-DETAIL", cut.Markup);
        Assert.Empty(cut.Instance.Follows);
    }
}
