using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.JobRuns;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using static Odyssey.Client.Pages.JobRuns.BackgroundJobPresentation;

namespace Odyssey.Client.Pages;

public partial class BackgroundJobs
{
    /// <summary>The user-preference key the filters persist under (#410 §3.7).</summary>
    public const string PageKey = "background-jobs-page";

    public const string HistoryHeadingId = "bgj-history-h";

    public const string FirstFilterId = "bgj-filter-job";

    public const string DatesCaptionId = "bgj-dates-caption";

    public const string JobsHeadingId = "bgj-jobs-h";

    /// <summary>A burst of multi-select toggles is one request (X11).</summary>
    public static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(300);

    [Inject] private IBackgroundJobsApiClient Api { get; set; } = default!;

    [Inject] private IPageStateService PageState { get; set; } = default!;

    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    [Inject] private TimeProvider Time { get; set; } = default!;

    [Inject] private IJSRuntime Js { get; set; } = default!;

    /// <summary><c>?job=</c> — repeatable; each value passes the allow-list (F13, FS3, S-N4).</summary>
    [SupplyParameterFromQuery(Name = "job")]
    public string[]? JobQuery { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListLoader _overviewLoader = new();
    private readonly ListLoader _runsLoader = new();

    private OdsLiveAnnouncer? _polite;
    private OdsLiveAnnouncer? _assertive;
    private BackgroundJobToastHost? _toasts;
    private JobRunAnnouncements _announce = default!;

    private bool _canRun;

    // ── Overview ───────────────────────────────────────────────────────────────────────────────────
    private BackgroundJobsOverview? _overview;
    private bool _overviewLoading = true;
    private bool _overviewError;
    private int? _problemCount;
    private string _workerHealthKey = string.Empty;
    private ITimer? _cooldownTimer;

    // ── Filters and the run log ────────────────────────────────────────────────────────────────────
    private JobRunFilters _filters = JobRunFilters.Default;
    private ITimer? _filterDebounce;
    private bool _tzUtc;
    private List<ExistingJobRun> _runs = [];
    private int _total;
    private int _page = 1;
    private bool _runsLoading = true;
    private bool _runsRefetching;
    private bool _runsError;
    private bool _runsBadFilters;

    // ── Run now, follows, details ──────────────────────────────────────────────────────────────────
    private readonly HashSet<JobType> _inFlight = [];
    private readonly List<FollowedRun> _follows = [];
    private readonly Dictionary<Guid, JobRunDetailState> _details = [];
    private string? _focusAfterRender;
    private string _appliedJobQuery = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        _announce = new JobRunAnnouncements(
            text => InvokeAsync(() => _polite?.Announce(text) ?? Task.CompletedTask),
            text => InvokeAsync(() => _assertive?.Announce(text) ?? Task.CompletedTask),
            Time);

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canRun = user.HasPermission(PermissionClaims.SystemSettingsUpdate);

        await RestoreFiltersAsync();
        await Task.WhenAll(LoadOverviewAsync(), LoadRunsAsync(RunsAnnouncement.None));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusAfterRender is not { } id)
            return;
        _focusAfterRender = null;
        try
        {
            await Js.InvokeVoidAsync("odsFocusById", id);
        }
        catch (JSException)
        {
        }
    }

    // ── Restore ────────────────────────────────────────────────────────────────────────────────────

    // Stored values and every ?job= value pass the allow-list by exact name and are de-duplicated; anything dropped
    // rewrites the stored state (FS3). A ?job= link is persisted like any filter choice (S-FS3).
    private async Task RestoreFiltersAsync()
    {
        var stored = await PageState.LoadAsync<BackgroundJobsPageState>(PageKey);
        var filters = JobRunFilters.FromState(stored);
        _appliedJobQuery = JobQueryKey;
        if (JobQuery is { Length: > 0 } query)
        {
            var linked = JobRunAllowList.Clean(query, BackgroundJobPresentation.Jobs);
            if (linked.Count > 0)
                filters = filters with { Jobs = linked };
        }

        _filters = filters;
        if (stored is null || stored.Fingerprint != filters.ToState().Fingerprint)
            PageState.QueueSave(PageKey, filters.ToState());
    }

    private string JobQueryKey => JobQuery is { Length: > 0 } query ? string.Join('\n', query) : string.Empty;

    /// <summary>
    /// A navigation to <c>/background-jobs?job=…</c> while the page is already showing reuses this component, so the
    /// link is applied here as well as on first load: the Job filter becomes the linked jobs, persisted like any
    /// choice (frontend review on PR #418). A link whose every value fails the allow-list changes nothing.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var key = JobQueryKey;
        if (key == _appliedJobQuery)
            return;

        _appliedJobQuery = key;
        if (JobQuery is not { Length: > 0 } query)
            return;

        var linked = JobRunAllowList.Clean(query, BackgroundJobPresentation.Jobs);
        if (linked.Count > 0 && !linked.SequenceEqual(_filters.Jobs))
            await ApplyAsync(_filters with { Jobs = linked });
    }

    // ── Overview ───────────────────────────────────────────────────────────────────────────────────

    private DateTimeOffset GeneratedAt => _overview is { } overview
        ? new DateTimeOffset(DateTime.SpecifyKind(overview.GeneratedAtUtc, DateTimeKind.Utc))
        : Time.GetUtcNow();

    private async Task LoadOverviewAsync(bool announceChanges = false)
    {
        var response = await _overviewLoader.RunAsync(ct => Api.GetOverviewAsync(ct));
        if (response.IsSuperseded)
            return;

        var result = response.Value;
        _overviewLoading = false;
        if (result.IsSuccess && result.Value is { } overview)
        {
            _overview = overview;
            _overviewError = false;
            var problems = overview.Jobs.Count(j => AttentionHealths.Contains(j.Health));
            var workerHealth = string.Join('\n', WorkerHealthLines(overview.WorkerHealth));
            if (announceChanges)
            {
                if (_problemCount is { } before && before != problems)
                    _announce.Say(problems == 0 ? "No jobs need attention" : $"{Plural(problems, "job needs", "jobs need")} attention", AnnouncementKind.Refresh);
                if (workerHealth != _workerHealthKey)
                    _announce.Say(workerHealth.Length == 0 ? "Worker health is back to normal" : workerHealth.Replace('\n', ' '), AnnouncementKind.Refresh);
            }

            _problemCount = problems;
            _workerHealthKey = workerHealth;
            ArmCooldownTimer();
        }
        else
        {
            _overviewError = true;
        }

        StateHasChanged();
    }

    /// <summary>
    /// A Run item in cooldown says "available HH:mm" and is disabled until then. One timer, aimed at the earliest of
    /// the overview's cooldown ends, re-renders the menu when it passes, so the item comes back on its own rather than
    /// waiting for Refresh (frontend review on PR #418). It is re-armed for the next one and disposed with the page.
    /// </summary>
    private void ArmCooldownTimer()
    {
        _cooldownTimer?.Dispose();
        _cooldownTimer = null;
        var now = Time.GetUtcNow().UtcDateTime;
        var next = _overview?.Jobs
            .Select(j => j.ManualRunAvailableAtUtc)
            .OfType<DateTime>()
            .Select(at => DateTime.SpecifyKind(at, DateTimeKind.Utc))
            .Where(at => at > now)
            .DefaultIfEmpty()
            .Min();
        if (next is not { } at || at == default)
            return;

        _cooldownTimer = Time.CreateTimer(_ => _ = InvokeAsync(() =>
        {
            if (_lifetime.IsCancellationRequested)
                return;
            ArmCooldownTimer();
            StateHasChanged();
        }), null, at - now, Timeout.InfiniteTimeSpan);
    }

    // Retry's button leaves the DOM with its alert, so focus moves to the Jobs heading the reload fills in rather than
    // falling back to the body (accessibility review on PR #418, WCAG 2.4.3).
    private Task RetryOverviewAsync()
    {
        _overviewError = false;
        _overviewLoading = true;
        _focusAfterRender = JobsHeadingId;
        return LoadOverviewAsync();
    }

    private BackgroundJobSummary? SummaryOf(JobType job) => _overview?.Jobs.FirstOrDefault(j => j.JobType == job);

    /// <summary>A run whose job the caller cannot read the detail of: its nulled counts read "Not shown" (F-N5).</summary>
    private bool NotShown(ExistingJobRun run) =>
        JobVisuals.ContainsKey(run.JobType) && SummaryOf(run.JobType) is { CanReadDetail: false };

    /// <summary>One line per worker-health block that reports a problem; a null block (no claim) is not shown (V4).</summary>
    internal static List<string> WorkerHealthLines(BackgroundJobsWorkerHealth? health)
    {
        var lines = new List<string>();
        if (health?.FileStorage is { FailingWorkers: > 0 } storage)
        {
            var checks = storage.FailingChecks.Count > 0 ? $" ({string.Join(", ", storage.FailingChecks)})" : string.Empty;
            lines.Add($"{Plural(storage.FailingWorkers, "worker", "workers")} can’t verify file storage{checks}. "
                + $"File-storage maintenance is held on {(storage.FailingWorkers == 1 ? "it" : "them")}.");
        }

        if (health?.Thumbnails is { } thumbnails && ThumbnailWorkerLine(thumbnails.State, thumbnails.ResumesAtUtc) is { } line)
            lines.Add(line);

        return lines;
    }

    private static string? SuccessRate(JobRunTotalsWindow window) => window.Runs == 0
        ? null
        : $"{Math.Round(window.Succeeded * 100.0 / window.Runs).ToString("0", CultureInfo.CurrentCulture)}%";

    /// <summary>One row per job whose health needs attention, worded with absolute times so it never reads stale (F17).</summary>
    private IReadOnlyCollection<PageHeaderProblem>? Problems => _overview?.Jobs
        .Where(j => AttentionHealths.Contains(j.Health))
        .Select(j => new PageHeaderProblem
        {
            Severity = j.Health is JobHealth.Failing or JobHealth.NotTicking ? PageHeaderSeverity.Error : PageHeaderSeverity.Warning,
            Message = ProblemText(j),
            ViewLabel = $"Show {JobOf(j.JobType).Lower} runs",
            OnView = EventCallback.Factory.Create(this, () => ShowRunsAsync(j.JobType)),
        })
        .ToList();

    internal static string ProblemText(BackgroundJobSummary job)
    {
        var name = JobOf(job.JobType).Name;
        return job.Health switch
        {
            JobHealth.Failing => $"{name}’s last run failed{(job.LastRun is { } last ? $" at {RunTime.Clock(last.FinishedAtUtc ?? last.StartedAtUtc)}" : string.Empty)}.",
            JobHealth.NotTicking => $"{name} is not ticking. Last heartbeat {(job.LastHeartbeatAtUtc is { } beat ? RunTime.Clock(beat) : "never")}.",
            JobHealth.Held => HeldOf(job.HeldReason) is { } reason
                ? $"{name} is held: {reason}{(job.HeldUntilUtc is { } until ? $" until {RunTime.Clock(until)}" : string.Empty)}."
                : $"{name} is held.",
            JobHealth.Overdue => $"{name} is overdue. {(job.LastRun is { } run ? $"Last run {RunTime.Full(run.StartedAtUtc)}" : "It has never run")}.",
            _ => name,
        };
    }

    // ── Filters ────────────────────────────────────────────────────────────────────────────────────

    private string DateCaption => _tzUtc
        ? "Format yyyy-mm-dd. Days in UTC — your time zone wasn’t recognised"
        : $"Format yyyy-mm-dd. Days in your time zone, {Time.LocalTimeZone.Id}";

    private string TimeZoneToSend => _tzUtc ? "UTC" : Time.LocalTimeZone.Id;

    private Task OnJobsChanged(IReadOnlyCollection<string> values) =>
        Debounce(_filters with { Jobs = JobRunAllowList.Clean(values, BackgroundJobPresentation.Jobs) });

    private Task OnStatusesChanged(IReadOnlyCollection<string> values) =>
        Debounce(_filters with { Statuses = JobRunAllowList.Clean(values, BackgroundJobPresentation.Statuses) });

    private Task OnTriggersChanged(IReadOnlyCollection<string> values) =>
        Debounce(_filters with { Triggers = JobRunAllowList.Clean(values, BackgroundJobPresentation.Triggers) });

    // MudDatePicker raises ValueChanged with the date the page itself just set, so an unchanged day is not a change
    // (docs/frontend-mudblazor-gotchas.md).
    private Task OnStartedFromChanged(DateTime? value) => ToDateOnly(value) == _filters.StartedFrom
        ? Task.CompletedTask
        : ApplyAsync(_filters with { StartedFrom = ToDateOnly(value) });

    private Task OnStartedToChanged(DateTime? value) => ToDateOnly(value) == _filters.StartedTo
        ? Task.CompletedTask
        : ApplyAsync(_filters with { StartedTo = ToDateOnly(value) });

    private Task OnSortChanged(OdsTableSort sort) =>
        sort == _filters.Sort ? Task.CompletedTask : ApplyAsync(_filters with { Sort = sort });

    private Task OnPageSizeChanged(int size)
    {
        var next = OdsPageSizes.Batch.Contains(size) ? size : JobRunFilters.DefaultPageSize;
        return next == _filters.PageSize ? Task.CompletedTask : ApplyAsync(_filters with { PageSize = next });
    }

    // A multi-select toggle shows at once and is queried 300 ms after the last one, so a burst is one request (X11).
    private Task Debounce(JobRunFilters next)
    {
        _filters = next;
        _filterDebounce?.Dispose();
        _filterDebounce = Time.CreateTimer(_ => InvokeAsync(() => ApplyAsync(_filters)), null, FilterDebounce, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    // Every application returns to page 1, persists (never the dates) and announces the count (a user change).
    private Task ApplyAsync(JobRunFilters next)
    {
        _filterDebounce?.Dispose();
        _filterDebounce = null;
        _filters = next;
        _page = 1;
        PageState.QueueSave(PageKey, next.ToState());
        return LoadRunsAsync(RunsAnnouncement.Count);
    }

    private async Task ResetFiltersAsync()
    {
        _filterDebounce?.Dispose();
        _filterDebounce = null;
        _filters = JobRunFilters.Default;
        _page = 1;
        PageState.QueueSave(PageKey, _filters.ToState());
        _focusAfterRender = FirstFilterId;
        await LoadRunsAsync(RunsAnnouncement.Reset);
    }

    /// <summary>"Show {job} runs": the Job filter set to that job, page 1, focus on the Run history heading (X5, F18).</summary>
    private Task ShowRunsAsync(JobType job)
    {
        _focusAfterRender = HistoryHeadingId;
        return ApplyAsync(_filters with { Jobs = [job.ToString()] });
    }

    private Task OnPageChanged(int page)
    {
        _page = page;
        return LoadRunsAsync(RunsAnnouncement.Count);
    }

    private static DateOnly? ToDateOnly(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) : null;

    private static DateTime? ToDateTime(DateOnly? value) => value?.ToDateTime(TimeOnly.MinValue);

    // ── The run log ────────────────────────────────────────────────────────────────────────────────

    private enum RunsAnnouncement { None, Count, Reset }

    // Only a user's filter, sort or page change announces "{n} runs"; a programmatic re-read does not (X11, F-N4).
    // ListLoader numbers each request and cancels the previous one, so a slower earlier response is dropped (V1).
    private async Task LoadRunsAsync(RunsAnnouncement announcement)
    {
        if (_runsError || _runsBadFilters)
        {
            _runsError = false;
            _runsBadFilters = false;
            _runsLoading = true;
        }
        else if (!_runsLoading)
        {
            _runsRefetching = true;
        }

        StateHasChanged();

        var response = await _runsLoader.RunAsync(ct => Api.ListRunsAsync(_page, _filters.PageSize, _filters.ToRequest(TimeZoneToSend), ct));
        if (response.IsSuperseded)
            return;

        var result = response.Value;

        // A 400 keyed tz retries once in UTC, and the page remembers it for its lifetime (S-N1, V2). A 400 keyed
        // anything else never retries.
        if (!_tzUtc && _filters.HasDates && result.Status == HttpStatusCode.BadRequest && result.Problem?.ErrorFor("tz") is not null)
        {
            _tzUtc = true;
            response = await _runsLoader.RunAsync(ct => Api.ListRunsAsync(_page, _filters.PageSize, _filters.ToRequest(TimeZoneToSend), ct));
            if (response.IsSuperseded)
                return;
            result = response.Value;
        }

        _runsLoading = false;
        _runsRefetching = false;
        if (result.IsSuccess && result.Value is { } paged)
        {
            // Retention pruning (or another tab) can shrink the log under the page being shown: a page past the last
            // one is re-read as the last, rather than left as "No runs yet" with the pager hidden (frontend review on
            // PR #418).
            var lastPage = Math.Max(1, (paged.TotalCount + _filters.PageSize - 1) / _filters.PageSize);
            if (paged.Items.Count == 0 && paged.TotalCount > 0 && _page > lastPage)
            {
                _page = lastPage;
                await LoadRunsAsync(announcement);
                return;
            }

            _runs = [.. paged.Items];
            _total = paged.TotalCount;
            var count = Plural(paged.TotalCount, "run", "runs");
            if (announcement == RunsAnnouncement.Count)
                _announce.SayCount(count);
            else if (announcement == RunsAnnouncement.Reset)
                _announce.SayCount($"Filters reset. {count}");
        }
        else
        {
            _runs = [];
            _total = 0;
            _runsBadFilters = result.Status == HttpStatusCode.BadRequest;
            _runsError = !_runsBadFilters;
        }

        StateHasChanged();
    }

    private Task RetryRunsAsync() => LoadRunsAsync(RunsAnnouncement.None);

    // Seconds, not minutes: a manual run beside a scheduled one can start in the same minute, and two rows sharing one
    // "Show details for …" name could not be told apart (frontend review on PR #418).
    internal static string RowLabel(ExistingJobRun run) => $"{JobOf(run.JobType).Name} run started {RunTime.Precise(run.StartedAtUtc)}";

    private IReadOnlyList<OdsRecordColumn<ExistingJobRun>> Columns =>
    [
        new()
        {
            Key = nameof(JobRunSortBy.StartedAt), HeaderText = "Started", Sortable = true, SortType = OdsSortType.Date,
            SortValue = r => r.StartedAtUtc, Cell = StartedCell,
        },
        new()
        {
            Key = nameof(JobRunSortBy.JobType), HeaderText = "Job", Sortable = true, SortType = OdsSortType.Text,
            SortValue = r => (int)r.JobType, Cell = (r, _) => b => b.AddContent(0, JobOf(r.JobType).Name),
        },
        new() { Key = "trigger", HeaderText = "Trigger", Cell = (r, _) => b => b.AddContent(0, TriggerOf(r.Trigger)) },
        new()
        {
            Key = nameof(JobRunSortBy.Status), HeaderText = "Status", Sortable = true, SortType = OdsSortType.Status,
            SortValue = r => (int)r.Status, Cell = StatusCell,
        },
        new()
        {
            Key = nameof(JobRunSortBy.Duration), HeaderText = "Duration", Sortable = true, SortType = OdsSortType.Number,
            Align = OdsAlign.End, SortValue = r => r.DurationMs,
            Cell = (r, _) => b => b.AddContent(0, r.DurationMs is { } ms ? Duration(ms) : r.Status == JobRunStatus.Running ? "Running" : "Ongoing"),
        },
        new() { Key = "processed", HeaderText = "Processed", Align = OdsAlign.End, Cell = (r, _) => CountCell(r, r.ItemsProcessed) },
        new() { Key = "failed", HeaderText = "Failed", Align = OdsAlign.End, Cell = (r, _) => CountCell(r, r.ItemsFailed) },
        new() { Key = "problem", HeaderText = "Problem", Cell = (r, _) => b => b.AddContent(0, ProblemOf(r)) },
    ];

    private string ProblemOf(ExistingJobRun run) => run.FailureCategory is { } category
        ? FailureOf(category)
        : run.Status == JobRunStatus.Held
            ? HeldOf(run.HeldReason) is { } held ? JobRunDetailBodyBase.Capitalize(held) : NotShown(run) ? "Not shown" : "Held"
            : "None";

    // ── Details ────────────────────────────────────────────────────────────────────────────────────

    private JobRunDetailState? DetailStateOf(Guid id) => _details.GetValueOrDefault(id);

    // On first expand (and again when a Refresh dropped a live entry while it was open). A retry replaces a failure.
    private async Task EnsureDetailAsync(Guid id)
    {
        if (_details.TryGetValue(id, out var current) && current.Status != JobRunDetailStatus.Failed)
            return;

        _details[id] = JobRunDetailState.Loading;
        StateHasChanged();
        var result = await Api.GetRunAsync(id, _lifetime.Token);
        if (_lifetime.IsCancellationRequested)
            return;

        _details[id] = result.IsSuccess && result.Value is { } detail
            ? new JobRunDetailState(JobRunDetailStatus.Loaded, detail)
            : new JobRunDetailState(result.Status == HttpStatusCode.NotFound ? JobRunDetailStatus.Gone : JobRunDetailStatus.Failed);
        StateHasChanged();
    }

    // ── Run now ────────────────────────────────────────────────────────────────────────────────────

    internal sealed record RunItem(JobType Job, string Label, string? MenuIcon, bool Disabled);

    /// <summary>
    /// The header menu's Run items (§3.5, F7, FS5): built from the presentation map, so they exist while the overview
    /// loads or has failed; once it has loaded, a job the caller cannot run is not rendered, one in cooldown or in
    /// flight is disabled and says why. The coarse gate is system-settings.update; the per-job decision is the server's
    /// <c>canRunNow</c> — the client holds no claim list.
    /// </summary>
    internal IReadOnlyList<RunItem> RunItems
    {
        get
        {
            if (!_canRun)
                return [];

            var now = Time.GetUtcNow().UtcDateTime;
            var items = new List<RunItem>();
            foreach (var job in BackgroundJobPresentation.Jobs)
            {
                var visual = JobOf(job);
                // A job with no Run label has no manual run at all (the file-analysis queue, issue #444).
                if (visual.RunLabel is null)
                    continue;

                var summary = SummaryOf(job);
                if (_overview is not null && summary is not { CanRunNow: true })
                    continue;

                if (_inFlight.Contains(job))
                    items.Add(new RunItem(job, $"Starting {visual.Lower}…", visual.MenuIcon, true));
                else if (summary?.ManualRunAvailableAtUtc is { } at && DateTime.SpecifyKind(at, DateTimeKind.Utc) > now)
                    items.Add(new RunItem(job, $"{visual.RunLabel} — available {RunTime.Clock(at)}", visual.MenuIcon, true));
                else
                    items.Add(new RunItem(job, visual.RunLabel!, visual.MenuIcon, false));
            }

            return items;
        }
    }

    /// <summary>Runs a menu item's work without holding the menu open (MudMenu waits for its handler) — the Currencies pattern (F20).</summary>
    private void Detach(Func<Task> work) => _ = ObserveAsync(work);

    private async Task ObserveAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            await DispatchExceptionAsync(ex);
        }
    }

    internal async Task RunNowAsync(JobType job)
    {
        if (!JobVisuals.ContainsKey(job) || !_inFlight.Add(job))
            return;

        var visual = JobOf(job);
        _announce.Say($"Starting {visual.Lower} run…");
        StateHasChanged();

        ApiResult<JobRunStarted> result;
        try
        {
            result = await Api.RunNowAsync(job, _lifetime.Token);
        }
        finally
        {
            _inFlight.Remove(job);
        }

        if (_lifetime.IsCancellationRequested)
            return;

        var outcome = JobRunOutcome.Describe(job, result);
        if (outcome.Success)
        {
            _toasts?.Push(OdsToastSeverity.Success, outcome.Text);
            _announce.Say(outcome.Text);
            if (outcome.FollowRunId is { } runId)
                StartFollowing(job, runId);
            await ReloadAsync();
        }
        else
        {
            _toasts?.Push(OdsToastSeverity.Error, outcome.Text);
            _announce.Alert(outcome.Text);
            StateHasChanged();
        }
    }

    // The overview and the runs, re-read without announcing a count.
    private Task ReloadAsync() => Task.WhenAll(LoadOverviewAsync(), LoadRunsAsync(RunsAnnouncement.None));

    private async Task RefreshAsync()
    {
        foreach (var id in _details.Where(pair => pair.Value.IsLive).Select(pair => pair.Key).ToList())
            _details.Remove(id);

        _announce.Say("Background jobs refreshed", AnnouncementKind.Refresh);
        await Task.WhenAll(LoadOverviewAsync(announceChanges: true), LoadRunsAsync(RunsAnnouncement.None));
    }

    // ── Follow ─────────────────────────────────────────────────────────────────────────────────────

    internal sealed class FollowedRun(JobType job, Guid runId, CancellationToken lifetime)
    {
        public JobType Job { get; } = job;

        public Guid RunId { get; } = runId;

        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

        public string StopId => $"bgj-stop-{RunId:N}";
    }

    internal IReadOnlyList<FollowedRun> Follows => _follows;

    /// <summary>Test seam: the page's announcement policy, whose <c>Requested</c> log a page test reads.</summary>
    internal JobRunAnnouncements Announcements => _announce;

    /// <summary>The follower's wait between reads; <c>null</c> in the app. A page test steps follows through it.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? FollowDelay { get; set; }

    private void StartFollowing(JobType job, Guid runId)
    {
        if (_follows.Any(f => f.RunId == runId))
            return;

        var follow = new FollowedRun(job, runId, _lifetime.Token);
        _follows.Add(follow);
        _ = FollowAsync(follow);
    }

    private async Task FollowAsync(FollowedRun follow)
    {
        var result = await JobRunFollower.FollowAsync(Api, follow.RunId, Time, detail => InvokeAsync(() => ApplyTick(detail)), follow.Cancellation.Token, FollowDelay);
        if (result.End == FollowEnd.Stopped || _lifetime.IsCancellationRequested)
            return;

        await InvokeAsync(async () =>
        {
            var lower = JobOf(follow.Job).Lower;
            await RemoveFollowAsync(follow);
            switch (result.End)
            {
                case FollowEnd.Finished:
                    _announce.Say($"{JobOf(follow.Job).Name} run {StatusSentenceOf(result.Last!.Status)}");
                    await ReloadAsync();
                    break;
                case FollowEnd.Cap:
                    _announce.Say($"{JobOf(follow.Job).Name} is still running. Select Refresh to check again.");
                    break;
                default:
                    _announce.Say($"Stopped following {lower} run. Select Refresh to check again.");
                    break;
            }
        });
    }

    // A tick touches only the followed run: its detail entry and its table row, updated in place (F-N2).
    private void ApplyTick(JobRunDetail detail)
    {
        _details[detail.Id] = new JobRunDetailState(JobRunDetailStatus.Loaded, detail);
        var index = _runs.FindIndex(r => r.Id == detail.Id);
        if (index >= 0)
        {
            _runs[index] = _runs[index] with
            {
                Status = detail.Status,
                FailureCategory = detail.FailureCategory,
                HeldReason = detail.HeldReason,
                FinishedAtUtc = detail.FinishedAtUtc,
                DurationMs = detail.DurationMs,
                ItemsProcessed = detail.ItemsProcessed,
                ItemsFailed = detail.ItemsFailed,
            };
        }

        StateHasChanged();
    }

    private async Task StopFollowingAsync(FollowedRun follow)
    {
        follow.Cancellation.Cancel();
        await RemoveFollowAsync(follow);
        _announce.Say($"Stopped following {JobOf(follow.Job).Lower} run.");
    }

    // When the removed line held focus, focus moves to the next remaining Stop button, else to the heading (A4).
    private async Task RemoveFollowAsync(FollowedRun follow)
    {
        var index = _follows.IndexOf(follow);
        if (index < 0)
            return;

        string? active = null;
        try
        {
            active = await Js.InvokeAsync<string?>("odsActiveElementId");
        }
        catch (JSException)
        {
        }

        _follows.RemoveAt(index);
        follow.Cancellation.Dispose();
        if (active == follow.StopId)
            _focusAfterRender = _follows.Count == 0 ? HistoryHeadingId : _follows[Math.Min(index, _follows.Count - 1)].StopId;

        StateHasChanged();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _filterDebounce?.Dispose();
        _cooldownTimer?.Dispose();
        foreach (var follow in _follows)
            follow.Cancellation.Dispose();
        _overviewLoader.Dispose();
        _runsLoader.Dispose();
        _announce?.Dispose();
        _lifetime.Dispose();
    }
}
