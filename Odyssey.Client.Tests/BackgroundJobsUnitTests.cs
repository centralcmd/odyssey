using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Client.Pages.JobRuns;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Background jobs page's pure parts (issue #410 §11.23): the allow-list, the request mapping, the announcement
/// policy, the follow's stop conditions, the Run outcomes, the detail registry, the toast host and the null fallbacks.
/// </summary>
public class BackgroundJobsUnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    // ── Allow-list (AC 8, 26) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_allow_list_keeps_exact_names_only_and_drops_duplicates()
    {
        var kept = JobRunAllowList.Clean(
            ["Weather", "2", "weather", "Unknown", "Weather", " PhotoThumbnails", "PhotoThumbnails", null, "255"],
            BackgroundJobPresentation.Jobs);

        Assert.Equal(["Weather", "PhotoThumbnails"], kept);
    }

    [Fact]
    public void A_stored_state_is_cleaned_and_its_unknown_sort_and_size_reset()
    {
        var filters = JobRunFilters.FromState(new BackgroundJobsPageState
        {
            Jobs = ["Weather", "Weather", "1"],
            Statuses = ["Failed", "failed", "Running"],
            Triggers = ["Purge", "Nope"],
            SortKey = "Secret",
            SortDir = "sideways",
            PageSize = 7,
        });

        Assert.Equal(["Weather"], filters.Jobs);
        Assert.Equal(["Failed", "Running"], filters.Statuses);
        Assert.Equal(["Purge"], filters.Triggers);
        Assert.Equal(JobRunFilters.DefaultSort, filters.Sort);
        Assert.Equal(JobRunFilters.DefaultPageSize, filters.PageSize);
        Assert.Null(filters.StartedFrom);
    }

    [Fact]
    public void Dates_are_never_persisted()
    {
        var state = (JobRunFilters.Default with { StartedFrom = new DateOnly(2026, 11, 1), Jobs = ["Weather"] }).ToState();

        Assert.DoesNotContain("2026", JsonSerializer.Serialize(state), StringComparison.Ordinal);
        Assert.Equal(["Weather"], state.Jobs);
    }

    // ── tz date mapping (AC 9) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_zone_travels_only_with_a_date_and_the_days_are_local_calendar_days()
    {
        var undated = JobRunFilters.Default.ToRequest("Europe/Oslo");
        Assert.Null(undated.TimeZone);

        // A run at 23:30 Oslo on 5 Nov is in "to 5 Nov": the day goes as a calendar day and the server applies the zone.
        var dated = (JobRunFilters.Default with { StartedTo = new DateOnly(2026, 11, 5) }).ToRequest("Europe/Oslo");
        Assert.Equal("Europe/Oslo", dated.TimeZone);
        Assert.Equal(new DateOnly(2026, 11, 5), dated.StartedTo);
        Assert.Null(dated.StartedFrom);
        Assert.Equal(JobRunSortBy.StartedAt, dated.SortBy);
        Assert.False(dated.Ascending);
    }

    [Fact]
    public void A_filter_maps_to_enum_values_once_each()
    {
        var request = (JobRunFilters.Default with
        {
            Jobs = ["Weather", "Weather"],
            Statuses = ["Held"],
            Sort = new OdsTableSort(nameof(JobRunSortBy.Duration), OdsSortDirection.Asc),
        }).ToRequest("UTC");

        Assert.Equal([JobType.Weather], request.JobTypes);
        Assert.Equal([JobRunStatus.Held], request.Statuses);
        Assert.Null(request.Triggers);
        Assert.Equal(JobRunSortBy.Duration, request.SortBy);
        Assert.True(request.Ascending);
    }

    // ── Announcements (AC 25) ────────────────────────────────────────────────────────────────────

    private sealed class Spoken
    {
        public List<string> Polite { get; } = [];

        public List<string> Assertive { get; } = [];
    }

    private static (JobRunAnnouncements Announce, Spoken Spoken, FakeTimeProvider Time) NewAnnouncer()
    {
        var spoken = new Spoken();
        var time = new FakeTimeProvider(Now);
        var announce = new JobRunAnnouncements(
            text => { spoken.Polite.Add(text); return Task.CompletedTask; },
            text => { spoken.Assertive.Add(text); return Task.CompletedTask; },
            time);
        return (announce, spoken, time);
    }

    [Fact]
    public void Polite_messages_are_spoken_in_order_at_least_the_spacing_apart()
    {
        var (announce, spoken, time) = NewAnnouncer();

        announce.Say("Starting weather run…");
        announce.Say("Weather run started");
        Assert.Equal(["Starting weather run…"], spoken.Polite);

        time.Advance(JobRunAnnouncements.Spacing - TimeSpan.FromMilliseconds(1));
        Assert.Single(spoken.Polite);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["Starting weather run…", "Weather run started"], spoken.Polite);
    }

    [Fact]
    public void Three_toggles_within_a_second_produce_one_count_spoken_after_the_last()
    {
        var (announce, spoken, time) = NewAnnouncer();

        announce.SayCount("12 runs");
        time.Advance(TimeSpan.FromMilliseconds(400));
        announce.SayCount("7 runs");
        time.Advance(TimeSpan.FromMilliseconds(400));
        announce.SayCount("3 runs");
        time.Advance(JobRunAnnouncements.Spacing - TimeSpan.FromMilliseconds(1));
        Assert.Empty(spoken.Polite);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["3 runs"], spoken.Polite);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(["3 runs"], spoken.Polite);
    }

    [Fact]
    public void An_assertive_message_is_spoken_at_once_and_drops_only_counts_and_refreshes()
    {
        var (announce, spoken, time) = NewAnnouncer();
        announce.Say("Starting weather run…");
        announce.Say("Background jobs refreshed", AnnouncementKind.Refresh);
        announce.Say("Photo thumbnails run started");
        announce.SayCount("4 runs");

        announce.Alert("A weather run is already in progress.");

        Assert.Equal(["A weather run is already in progress."], spoken.Assertive);
        Assert.Equal(["Photo thumbnails run started"], announce.Pending);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(["Starting weather run…", "Photo thumbnails run started"], spoken.Polite);
    }

    // ── Follow stop conditions (AC 14) ───────────────────────────────────────────────────────────

    private static JobRunDetail Detail(JobRunStatus status) => new()
    {
        Id = Guid.NewGuid(), JobType = JobType.Weather, Status = status, StartedAtUtc = Now.UtcDateTime,
    };

    private static ApiResult<JobRunDetail> Read(HttpStatusCode status) =>
        ApiResult<JobRunDetail>.Failure(status, new ApiProblem { Status = (int)status });

    /// <summary>
    /// Drives a follow through the follower's delay seam: each wait advances the fake clock by the interval and
    /// completes at once, so the whole follow runs synchronously to its end — no timer continuation to poll for, and
    /// nothing that depends on how fast the runner is. A guard bounds the waits so a regression cannot loop forever.
    /// </summary>
    private static async Task<(FollowResult Result, int Requests, int Waits)> Follow(
        Func<int, ApiResult<JobRunDetail>> respond, CancellationToken ct = default, Action<int, CancellationTokenSource>? onRequest = null)
    {
        var time = new FakeTimeProvider(Now);
        var requests = 0;
        var waits = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var api = new Mock<IBackgroundJobsApiClient>();
        api.Setup(a => a.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            requests++;
            onRequest?.Invoke(requests, stop);
            return respond(requests);
        });

        Task Step(TimeSpan span, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.True(++waits <= 100, "The follow never ended.");
            time.Advance(span);
            return Task.CompletedTask;
        }

        var follow = JobRunFollower.FollowAsync(api.Object, Guid.NewGuid(), time, _ => Task.CompletedTask, stop.Token, Step);
        Assert.True(follow.IsCompleted, "The follow should run synchronously through the seam.");
        return (await follow, requests, waits);
    }

    [Fact]
    public async Task A_follow_ends_when_the_run_leaves_running()
    {
        var (result, requests, _) = await Follow(n => ApiResult<JobRunDetail>.Success(Detail(n < 3 ? JobRunStatus.Running : JobRunStatus.Succeeded), HttpStatusCode.OK));

        Assert.Equal(FollowEnd.Finished, result.End);
        Assert.Equal(JobRunStatus.Succeeded, result.Last?.Status);
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, FollowEnd.Gone)]
    [InlineData(HttpStatusCode.Unauthorized, FollowEnd.Forbidden)]
    [InlineData(HttpStatusCode.Forbidden, FollowEnd.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, FollowEnd.RateLimited)]
    public async Task A_follow_stops_at_once_on_a_terminal_refusal(HttpStatusCode status, FollowEnd expected)
    {
        var (result, requests, _) = await Follow(_ => Read(status));

        Assert.Equal(expected, result.End);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task A_follow_stops_after_three_consecutive_errors_and_a_success_resets_the_count()
    {
        var (result, requests, _) = await Follow(n => n == 3
            ? ApiResult<JobRunDetail>.Success(Detail(JobRunStatus.Running), HttpStatusCode.OK)
            : n == 1 || n == 2 ? Read(HttpStatusCode.InternalServerError) : ApiResult<JobRunDetail>.Failure(new HttpRequestException("offline")));

        Assert.Equal(FollowEnd.Errors, result.End);
        Assert.Equal(6, requests);
    }

    [Fact]
    public async Task A_follow_is_capped_at_three_minutes_or_36_requests()
    {
        var (result, requests, waits) = await Follow(_ => ApiResult<JobRunDetail>.Success(Detail(JobRunStatus.Running), HttpStatusCode.OK));

        Assert.Equal(FollowEnd.Cap, result.End);
        Assert.Equal(36, requests);
        Assert.Equal(36, waits);
    }

    [Fact]
    public async Task Stop_following_ends_the_follow_without_another_request()
    {
        var (result, requests, _) = await Follow(
            _ => ApiResult<JobRunDetail>.Success(Detail(JobRunStatus.Running), HttpStatusCode.OK),
            onRequest: (n, stop) => { if (n == 2) stop.Cancel(); });

        Assert.Equal(FollowEnd.Stopped, result.End);
        Assert.Equal(2, requests);
    }

    // ── Run outcomes (AC 13, 19) ─────────────────────────────────────────────────────────────────

    private static ApiResult<JobRunStarted> Refused(HttpStatusCode status, string? code = null, Dictionary<string, object?>? extensions = null)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>(extensions ?? [])
        {
            ["status"] = (int)status,
            ["detail"] = "SERVER-DETAIL-NEVER-SHOWN",
            ["code"] = code,
        });
        var problem = JsonSerializer.Deserialize<ApiProblem>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return ApiResult<JobRunStarted>.Failure(status, problem);
    }

    public static TheoryData<string, string, bool> Outcomes(DateTime at) => new()
    {
        { "202", "Weather run started", true },
        { "200", "Weather: nothing to do", true },
        { "409-running", "A weather run is already in progress.", false },
        { "409-held", $"Weather is held: paused after repeated crashes until {RunTime.Clock(at)}.", false },
        { "409-held-unknown", "Weather is held right now and can’t be started.", false },
        { "409-held-bare", "Weather is held right now and can’t be started.", false },
        { "409-other", "Weather couldn’t be started.", false },
        { "429", $"Try again at {RunTime.Clock(at)}.", false },
        { "403", "You don’t have permission to run weather. If your role just changed, try again in a minute.", false },
        { "500", "Weather couldn’t be started. Try again.", false },
        { "network", "Weather couldn’t be started. Try again.", false },
    };

    public static TheoryData<string, string, bool> OutcomeCases => Outcomes(At);

    private static readonly DateTime At = new(2026, 11, 5, 10, 52, 0, DateTimeKind.Utc);

    [Theory]
    [MemberData(nameof(OutcomeCases))]
    public void Each_run_response_has_its_own_copy_and_never_the_servers(string response, string expected, bool success)
    {
        var result = response switch
        {
            "202" => ApiResult<JobRunStarted>.Success(new JobRunStarted { Id = Guid.NewGuid() }, HttpStatusCode.Accepted),
            "200" => ApiResult<JobRunStarted>.Success(new JobRunStarted { Id = Guid.NewGuid() }, HttpStatusCode.OK),
            "409-running" => Refused(HttpStatusCode.Conflict, JobRunConflictCodes.AlreadyRunning),
            "409-held" => Refused(HttpStatusCode.Conflict, JobRunConflictCodes.Held, new() { ["heldReason"] = "BreakerTripped", ["resumesAtUtc"] = At }),
            "409-held-unknown" => Refused(HttpStatusCode.Conflict, JobRunConflictCodes.Held, new() { ["heldReason"] = "SomethingNewer", ["resumesAtUtc"] = At }),
            "409-held-bare" => Refused(HttpStatusCode.Conflict, JobRunConflictCodes.Held),
            "409-other" => Refused(HttpStatusCode.Conflict, "job-run.something-else"),
            "429" => Refused(HttpStatusCode.TooManyRequests, null, new() { ["retryAfterUtc"] = At }),
            "403" => Refused(HttpStatusCode.Forbidden),
            "500" => Refused(HttpStatusCode.InternalServerError),
            _ => ApiResult<JobRunStarted>.Failure(new HttpRequestException("SERVER-DETAIL-NEVER-SHOWN")),
        };

        var outcome = JobRunOutcome.Describe(JobType.Weather, result);

        Assert.Equal(expected, outcome.Text);
        Assert.Equal(success, outcome.Success);
        Assert.Equal(response == "202", outcome.FollowRunId is not null);
        Assert.DoesNotContain("SERVER-DETAIL", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_held_reason_without_its_time_has_no_until_clause()
    {
        var outcome = JobRunOutcome.Describe(JobType.FileStorageMaintenance,
            Refused(HttpStatusCode.Conflict, JobRunConflictCodes.Held, new() { ["heldReason"] = "StorageCheckFailed" }));

        Assert.Equal("File-storage maintenance is held: storage check failed.", outcome.Text);
    }

    // ── Presentation and null fallbacks (AC 19, 30) ──────────────────────────────────────────────

    [Fact]
    public void Every_member_has_words_and_an_unknown_one_reads_Unknown()
    {
        Assert.All(Enum.GetValues<JobType>().Where(t => t != JobType.Unknown), t => Assert.True(BackgroundJobPresentation.JobVisuals.ContainsKey(t)));
        Assert.All(Enum.GetValues<JobHealth>().Where(h => h != JobHealth.Unknown), h => Assert.True(BackgroundJobPresentation.Healths.ContainsKey(h)));
        Assert.All(Enum.GetValues<JobRunStatus>().Where(s => s != JobRunStatus.Unknown), s => Assert.True(BackgroundJobPresentation.StatusChips.ContainsKey(s)));
        Assert.All(Enum.GetValues<JobRunTrigger>().Where(s => s != JobRunTrigger.Unknown), s => Assert.True(BackgroundJobPresentation.TriggerWords.ContainsKey(s)));
        Assert.All(Enum.GetValues<JobFailureCategory>().Where(s => s != JobFailureCategory.Unknown), s => Assert.True(BackgroundJobPresentation.FailureWords.ContainsKey(s)));
        Assert.All(Enum.GetValues<JobHeldReason>().Where(s => s != JobHeldReason.Unknown), s => Assert.True(BackgroundJobPresentation.HeldWords.ContainsKey(s)));
        Assert.All(Enum.GetValues<WeatherFetchFailureKind>(), s => Assert.True(BackgroundJobPresentation.WeatherFailureWords.ContainsKey(s)));

        Assert.Equal("Unknown", BackgroundJobPresentation.HealthOf(JobHealth.Unknown).Label);
        Assert.Equal(OdsChipTone.Outline, BackgroundJobPresentation.StatusOf(JobRunStatus.Unknown).Tone);
        Assert.Equal("Unknown", BackgroundJobPresentation.TriggerOf(JobRunTrigger.Unknown));
        Assert.Equal("Unknown", BackgroundJobPresentation.FailureOf(JobFailureCategory.Unknown));
        Assert.Null(BackgroundJobPresentation.HeldOf(JobHeldReason.Unknown));
        Assert.Equal("Other job", BackgroundJobPresentation.JobOf(JobType.Unknown).Name);
        Assert.Null(BackgroundJobPresentation.JobOf(JobType.Unknown).RunLabel);
    }

    private static BackgroundJobSummary Held(JobType job, JobHeldReason? reason, bool canRead) => new()
    {
        JobType = job, Health = JobHealth.Held, HeldReason = reason, CanReadDetail = canRead,
    };

    [Fact]
    public void A_held_tile_and_attention_row_fall_back_when_the_reason_is_withheld_or_unknown()
    {
        Assert.Equal("Held — details need photo access", BackgroundJobTile.HeldLine(Held(JobType.PhotoThumbnails, null, canRead: false)));
        Assert.Equal("Held", BackgroundJobTile.HeldLine(Held(JobType.PhotoThumbnails, null, canRead: true)));
        Assert.Equal("Held", BackgroundJobTile.HeldLine(Held(JobType.PhotoThumbnails, JobHeldReason.Unknown, canRead: true)));
        Assert.Equal("Held: storage check failed (VolumeLost)", BackgroundJobTile.HeldLine(
            Held(JobType.FileStorageMaintenance, JobHeldReason.StorageCheckFailed, canRead: true) with { HeldCheck = "VolumeLost" }));

        Assert.Equal("Photo thumbnails is held.", BackgroundJobs.ProblemText(Held(JobType.PhotoThumbnails, null, canRead: false)));
        Assert.Equal("Weather is not ticking. Last heartbeat never.",
            BackgroundJobs.ProblemText(new BackgroundJobSummary { JobType = JobType.Weather, Health = JobHealth.NotTicking }));
    }

    [Fact]
    public void A_null_worker_health_block_is_not_shown()
    {
        Assert.Empty(BackgroundJobs.WorkerHealthLines(new BackgroundJobsWorkerHealth()));
        Assert.Empty(BackgroundJobs.WorkerHealthLines(new BackgroundJobsWorkerHealth
        {
            FileStorage = new FileStorageWorkerHealth { FailingWorkers = 0 },
            Thumbnails = new ThumbnailWorkerHealth { State = ThumbnailWorkerState.Running },
        }));
        Assert.Equal(
            ["1 worker can’t verify file storage (VolumeLost). File-storage maintenance is held on it."],
            BackgroundJobs.WorkerHealthLines(new BackgroundJobsWorkerHealth
            {
                FileStorage = new FileStorageWorkerHealth { FailingWorkers = 1, FailingChecks = ["VolumeLost"] },
            }));
    }

    // ── Detail registry (AC 10) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_known_kind_has_its_body_and_anything_else_the_base()
    {
        Assert.Equal(
            JobRunDetailJsonConverter.Kinds.Values.ToHashSet(),
            JobRunDetailRegistry.Bodies.Keys.ToHashSet());
        Assert.Equal(typeof(JobRunDetailWeather), JobRunDetailRegistry.BodyFor(new WeatherJobRunDetail()));
        Assert.Equal(typeof(JobRunDetailBase), JobRunDetailRegistry.BodyFor(new JobRunDetail()));

        var unknown = JsonSerializer.Deserialize<JobRunDetail>(
            """{"$kind":"SomethingNewer","id":"7b0c6f5e-0000-0000-0000-000000000001","jobType":"Unknown","status":"Succeeded","itemsProcessed":9}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(typeof(JobRunDetailBase), JobRunDetailRegistry.BodyFor(unknown));
    }

    [Fact]
    public async Task An_unknown_kind_renders_the_base_detail_and_a_withheld_count_says_why()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var run = new ExistingJobRun { Id = Guid.NewGuid(), JobType = JobType.Weather, Status = JobRunStatus.Succeeded, StartedAtUtc = Now.UtcDateTime };
        var cut = ctx.Render<JobRunDetailPanel>(p => p
            .Add(x => x.Run, run)
            .Add(x => x.CanReadDetail, false)
            .Add(x => x.OnLoad, _ => Task.CompletedTask)
            .Add(x => x.State, new JobRunDetailState(JobRunDetailStatus.Loaded,
                new JobRunDetail { Id = run.Id, JobType = JobType.Weather, Status = JobRunStatus.Succeeded, DurationMs = 1800, StartedAtUtc = Now.UtcDateTime })));

        Assert.Contains("Details loaded", cut.Find("[role=status]").TextContent);
        Assert.Contains("1.8 s", cut.Markup);
        Assert.Contains("Not shown — needs weather point access", cut.Markup);
        Assert.Contains("Trigger", cut.Markup);
    }

    // ── Photo original move (issue #428) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_move_runs_detail_shows_its_four_counters_and_says_why_when_they_are_withheld()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var run = new ExistingJobRun { Id = Guid.NewGuid(), JobType = JobType.PhotoOriginalBackfill, Status = JobRunStatus.PartiallySucceeded, StartedAtUtc = Now.UtcDateTime };
        IRenderedComponent<JobRunDetailPanel> Detail(bool canRead, PhotoOriginalBackfillJobRunDetail detail) => ctx.Render<JobRunDetailPanel>(p => p
            .Add(x => x.Run, run)
            .Add(x => x.CanReadDetail, canRead)
            .Add(x => x.OnLoad, _ => Task.CompletedTask)
            .Add(x => x.State, new JobRunDetailState(JobRunDetailStatus.Loaded, detail)));

        var shown = Detail(true, new PhotoOriginalBackfillJobRunDetail
        {
            Id = run.Id, JobType = JobType.PhotoOriginalBackfill, Status = JobRunStatus.PartiallySucceeded, StartedAtUtc = Now.UtcDateTime,
            PhotosMoved = 1180, PhotosCopiedShared = 2, PhotosParked = 3, Interruptions = 0,
        });
        var tiles = shown.FindAll(".odc-meta-tile").Select(t => t.TextContent).ToList();
        Assert.Contains(tiles, t => t.Contains("Moved") && t.Contains(1180.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        Assert.Contains(tiles, t => t.Contains("Copied and kept") && t.Contains('2'));
        Assert.Contains(tiles, t => t.Contains("Parked") && t.Contains('3'));
        Assert.Contains(tiles, t => t.Contains("Interruptions") && t.Contains('0'));

        var hidden = Detail(false, new PhotoOriginalBackfillJobRunDetail { Id = run.Id, JobType = JobType.PhotoOriginalBackfill, StartedAtUtc = Now.UtcDateTime });
        Assert.Contains("Not shown — needs file storage access", hidden.Markup);
    }

    private static IRenderedComponent<PhotoOriginalMoveCard> MoveCard(BunitContext ctx, PhotoBackfillStatus status) =>
        ctx.Render<PhotoOriginalMoveCard>(p => p.Add(x => x.Status, status).Add(x => x.GeneratedAtUtc, Now));

    [Fact]
    public async Task The_move_card_counts_what_is_left_apart_from_what_is_parked_and_lists_the_parked_by_reason()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();

        var cut = MoveCard(ctx, new PhotoBackfillStatus
        {
            Remaining = 2843, Failed = 3, CopiedShared = 5,
            FailedByCode = new() { ["hash"] = 1, ["read"] = 2, ["too-large"] = 0, ["error"] = 0 },
            LastRunAtUtc = Now.UtcDateTime.AddMinutes(-1),
        });

        Assert.Equal($"{2840.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} photo originals still to move from the database to disk.",
            cut.Find(".bgj-move-lead").TextContent);
        var stats = cut.FindAll(".odc-stat").Select(s => s.TextContent).ToList();
        Assert.Equal(3, stats.Count);
        Assert.Contains(stats, t => t.Contains("Parked") && t.Contains('3'));
        Assert.Contains(stats, t => t.Contains("Copied and kept") && t.Contains('5'));
        Assert.Equal("Parked photos by reason", cut.Find("ul.bgj-move-codes").GetAttribute("aria-label"));
        Assert.Equal(["Checksum mismatch: 1", "Couldn’t be read: 2"], cut.FindAll("ul.bgj-move-codes li").Select(li => li.TextContent));
        Assert.Empty(cut.FindAll(".bgj-held"));
        Assert.Equal($"Last pass {RunTime.Absolute(Now.UtcDateTime.AddMinutes(-1))}", cut.Find(".bgj-caption").TextContent);
        Assert.Empty(cut.FindAll("[aria-live], [role=status], button"));
    }

    [Fact]
    public async Task The_move_card_says_when_only_parked_photos_are_left_names_an_unknown_code_and_shows_the_back_off()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();

        var cut = MoveCard(ctx, new PhotoBackfillStatus
        {
            Remaining = 4, Failed = 4, FailedByCode = new() { ["read"] = 1, ["something-newer"] = 3 },
            BackingOffUntilUtc = Now.UtcDateTime.AddMinutes(14),
        });

        Assert.StartsWith("Only parked photos are left.", cut.Find(".bgj-move-lead").TextContent);
        Assert.Equal(["Couldn’t be read: 1", "Other: 3"], cut.FindAll("ul.bgj-move-codes li").Select(li => li.TextContent));
        Assert.Equal($"Paused after a storage or database error until {RunTime.Clock(Now.UtcDateTime.AddMinutes(14))}.", cut.Find(".bgj-held").TextContent);
        Assert.Equal("No pass yet", cut.Find(".bgj-caption").TextContent);
    }

    [Fact]
    public async Task The_move_card_says_every_original_is_on_disk_and_a_past_back_off_is_not_shown()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();

        var cut = MoveCard(ctx, new PhotoBackfillStatus { Remaining = 0, BackingOffUntilUtc = Now.UtcDateTime.AddMinutes(-5) });

        Assert.Equal("Every photo original is on disk.", cut.Find(".bgj-move-lead").TextContent);
        Assert.Empty(cut.FindAll("ul.bgj-move-codes"));
        Assert.Empty(cut.FindAll(".bgj-held"));
    }

    // ── Toast host (AC 29) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_toast_host_keeps_four_drops_the_oldest_success_first_and_times_out_successes_only()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var time = new FakeTimeProvider(Now);
        ctx.Services.AddSingleton<TimeProvider>(time);
        var cut = ctx.Render<BackgroundJobToastHost>();
        var host = cut.Instance;

        await cut.InvokeAsync(() =>
        {
            host.Push(OdsToastSeverity.Success, "s1");
            host.Push(OdsToastSeverity.Error, "e1");
            host.Push(OdsToastSeverity.Success, "s2");
            host.Push(OdsToastSeverity.Error, "e2");
            host.Push(OdsToastSeverity.Error, "e3");
        });

        Assert.Equal(["e3", "e2", "s2", "e1"], host.Toasts.Select(t => t.Text));
        Assert.Single(cut.FindAll(".bgj-toast-host"));
        Assert.All(cut.FindAll(".odc-toast"), toast => Assert.Equal("off", toast.GetAttribute("aria-live")));

        await cut.InvokeAsync(() => time.Advance(BackgroundJobToastHost.SuccessLifetime));
        cut.WaitForAssertion(() => Assert.Equal(["e3", "e2", "e1"], host.Toasts.Select(t => t.Text)));

        await cut.InvokeAsync(() => time.Advance(TimeSpan.FromMinutes(10)));
        Assert.Equal(3, host.Toasts.Count);

        cut.FindAll("button[aria-label='Dismiss']")[0].Click();
        Assert.Equal(["e2", "e1"], host.Toasts.Select(t => t.Text));
    }
}
