using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.ExchangeRateSync;

public partial class ExchangeRateSyncStatusPanel
{
    /// <summary>
    /// How often the panel re-reads status while a run is active. A UI choice, not a server value — the
    /// one numeric constant the surface's source-lint allows (issue #351 §5).
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Consecutive failed reads after which polling stops as stale.</summary>
    internal const int MaxConsecutivePollFailures = 2;

    /// <summary>Raised once when a run the panel watched ends — the page re-reads its lists.</summary>
    [Parameter] public EventCallback<ExchangeRateSyncStatus> OnRunFinished { get; set; }

    /// <summary>Raised with every fresh status — the page reads the banners' inputs from it.</summary>
    [Parameter] public EventCallback<ExchangeRateSyncStatus> OnStatus { get; set; }

    /// <summary>Polite announcement (run started / finished, stale).</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    /// <summary>Assertive announcement (Run now failures, a failed run, the 403 reload message).</summary>
    [Parameter] public EventCallback<string> OnAnnounceAssertive { get; set; }

    internal enum StaleReason { Cap, Errors }

    private ExchangeRateSyncStatus? _status;
    private bool _loadFailed;
    private bool _refreshing;
    private StaleReason? _stale;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _readInFlight;
    private int _consecutiveFailures;
    private int _readFailures;
    private bool _permissionLost;

    /// <summary>The run the panel saw start — its end is what gets announced and refreshed once.</summary>
    private Guid? _watchedRunId;

    internal ExchangeRateSyncStatus? Status => _status;

    internal bool IsPolling => _pollTask is { IsCompleted: false };

    /// <summary>The first status read failed, so there is no status to show.</summary>
    internal bool LoadFailed => _loadFailed;

    internal StaleReason? Stale => _stale;

    protected override async Task OnInitializedAsync()
    {
        await ReadStatusAsync(announce: false);
        if (_status?.CurrentRun is { } run)
        {
            _watchedRunId = run.Id;
            StartPolling();
        }
    }

    /// <summary>
    /// Re-read status once, on the user's request; recovers a stale panel. The outcome is announced,
    /// because the button keeps focus and nothing else tells a screen-reader user the read happened.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        StateHasChanged();
        _stale = null;
        _consecutiveFailures = 0;
        var before = _readFailures;
        await ReadStatusAsync(announce: true);
        _refreshing = false;
        if (_readFailures != before)
        {
            // A read that already went stale, or lost permission, announced it; say it once.
            if (_stale is null && !_permissionLost)
                await OnAnnounce.InvokeAsync("Couldn’t refresh status");
            _stale = StaleReason.Errors;
        }
        else if (_status is { } status)
        {
            await OnAnnounce.InvokeAsync($"Status refreshed: {OdsTypeRegistries.ExchangeRateSyncStateOf(status.State).Label}");
        }
        ResumePollingIfRunning();
        StateHasChanged();
    }

    /// <summary>
    /// Re-read status without announcing — after the page changed what it reports (a sync started or
    /// stopped, a start date saved, a purge), so the chip, the banners and the synced count follow.
    /// </summary>
    public async Task ReloadQuietlyAsync()
    {
        await ReadStatusAsync(announce: false);
        ResumePollingIfRunning();
        StateHasChanged();
    }

    private void ResumePollingIfRunning()
    {
        if (_status?.CurrentRun is { } run && !IsPolling)
        {
            _watchedRunId ??= run.Id;
            StartPolling();
        }
    }

    /// <summary>Why Run now is unavailable right now, as visible text; null when it can run.</summary>
    public string? RunNowReason => RunNowReasonFor(_status, Time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Why a manual run cannot start, from a status alone — the page omits its menu item on the same
    /// reading, so the item and the panel's note cannot disagree.
    /// </summary>
    public static string? RunNowReasonFor(ExchangeRateSyncStatus? status, DateTime utcNow) => status switch
    {
        { CurrentRun: not null } => "A run is in progress",
        { ManualRunAvailableAtUtc: { } at } when at > utcNow =>
            $"You can start another run at {ExchangeRateSyncTime.Clock(at)}",
        _ => null,
    };

    /// <summary>Start a run, and poll it.</summary>
    public async Task RunNowAsync()
    {
        if (RunNowReason is not null)
            return;

        var result = await Sync.RunNowAsync();
        if (result.IsSuccess)
        {
            _watchedRunId = result.Value?.RunId;
            await OnAnnounce.InvokeAsync("Run started");
            await ReadStatusAsync(announce: false);
            StartPolling();
            return;
        }

        switch (result.Status)
        {
            case HttpStatusCode.Conflict:
                Snackbar.Add("A run is already in progress", Severity.Info);
                await ReadStatusAsync(announce: false);
                _watchedRunId = _status?.CurrentRun?.Id;
                StartPolling();
                break;
            case HttpStatusCode.TooManyRequests:
                await ReadStatusAsync(announce: false);
                var at = _status?.ManualRunAvailableAtUtc;
                Snackbar.Add(at is { } time ? $"You can start another run at {ExchangeRateSyncTime.Clock(time)}" : "Wait a moment before starting another run", Severity.Warning);
                break;
            case HttpStatusCode.Forbidden:
                Snackbar.Add(PermissionMessage, Severity.Error);
                await OnAnnounceAssertive.InvokeAsync(PermissionMessage);
                break;
            default:
                Snackbar.Add($"Couldn’t start a run: {result.Error}", Severity.Error);
                await OnAnnounceAssertive.InvokeAsync("Couldn’t start a run.");
                break;
        }

        StateHasChanged();
    }

    internal const string PermissionMessage =
        "You no longer have permission to do this. Sign out and in again if your role changed.";

    // ── Polling (issue #351 §5): single-flight, capped by the run timeout, stopped by failures ────────

    private void StartPolling()
    {
        if (IsPolling)
            return;
        _stale = null;
        _consecutiveFailures = 0;
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        _pollTask = PollAsync(_pollCts.Token);
    }

    private void StopPolling() => _pollCts?.Cancel();

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval, Time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // A tick that lands while the previous read is still out is skipped, never queued.
                if (_readInFlight)
                    continue;
                var keepGoing = true;
                await InvokeAsync(async () => keepGoing = await PollOnceAsync());
                if (!keepGoing)
                    return;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One poll. Returns false when polling should stop.</summary>
    private async Task<bool> PollOnceAsync()
    {
        var keepGoing = await ReadStatusAsync(announce: true);
        StateHasChanged();
        return keepGoing && _status?.CurrentRun is not null;
    }

    /// <summary>
    /// Reads status and applies every stop rule. Returns false when polling should stop: a failure that
    /// ends it, the run timeout reached, or the run over.
    /// </summary>
    private async Task<bool> ReadStatusAsync(bool announce)
    {
        _readInFlight = true;
        ApiResult<ExchangeRateSyncStatus> result;
        try
        { result = await Sync.GetStatusAsync(); }
        finally
        { _readInFlight = false; }

        if (!result.IsSuccess || result.Value is null)
        {
            _readFailures++;
            return await OnReadFailedAsync(result.Status, announce);
        }

        _consecutiveFailures = 0;
        _loadFailed = false;
        var previous = _status;
        _status = result.Value;
        await OnStatus.InvokeAsync(_status);

        if (_status.CurrentRun is { } run)
        {
            _watchedRunId ??= run.Id;
            // Counted from the run's own start, never from the first poll.
            var deadline = run.StartedAtUtc.AddMinutes(_status.RunTimeoutMinutes);
            if (Time.GetUtcNow().UtcDateTime >= deadline)
            {
                await GoStaleAsync(StaleReason.Cap, announce);
                return false;
            }

            return true;
        }

        // Out of Running: refresh the page's lists and say how it went — once per watched run.
        if (_watchedRunId is not null && (previous?.CurrentRun is not null || announce))
        {
            _watchedRunId = null;
            await OnRunFinished.InvokeAsync(_status);
            if (announce && _status.LastRun is { } last)
            {
                if (last.Status == ExchangeRateSyncRunStatus.Failed)
                    await OnAnnounceAssertive.InvokeAsync($"Run failed: {ErrorText(last)}");
                else
                    await OnAnnounce.InvokeAsync($"Run finished: {last.RowsInserted:N0} rate{(last.RowsInserted == 1 ? "" : "s")} added");
            }
        }

        return false;
    }

    private async Task<bool> OnReadFailedAsync(HttpStatusCode status, bool announce)
    {
        if (_status is null)
            _loadFailed = true;

        switch (status)
        {
            case HttpStatusCode.Forbidden:
                _permissionLost = true;
                await OnAnnounceAssertive.InvokeAsync("Your permissions changed — reload the page");
                Snackbar.Add("Your permissions changed — reload the page", Severity.Error);
                return false;
            case HttpStatusCode.TooManyRequests:
                await GoStaleAsync(StaleReason.Errors, announce);
                return false;
        }

        if (++_consecutiveFailures >= MaxConsecutivePollFailures)
        {
            await GoStaleAsync(StaleReason.Errors, announce);
            return false;
        }

        return true;
    }

    private async Task GoStaleAsync(StaleReason reason, bool announce)
    {
        _stale = reason;
        if (announce)
            await OnAnnounce.InvokeAsync(reason == StaleReason.Errors ? "Couldn’t refresh status" : "Status not current");
    }

    // ── Text, built only from status fields ───────────────────────────────────────────────────────

    /// <summary>
    /// The forward-looking row: the state as coloured text (<see cref="Word"/>), then when. The state's own
    /// label leads, shortened where the full one would not read as a sentence.
    /// </summary>
    private (string Word, string Tail) NextRun(ExchangeRateSyncStatus s) => s.State switch
    {
        ExchangeRateSyncState.Idle => ("Not syncing", string.Empty),
        ExchangeRateSyncState.CatchingUp => ("Fetching history", $", next batch at {MomentOrDash(s.NextRunAtUtc)}"),
        ExchangeRateSyncState.Running => ("Running", $" since {MomentOrDash(s.CurrentRun?.StartedAtUtc)}"),
        ExchangeRateSyncState.BackingOff => ("Retrying", $" at {MomentOrDash(s.NextRunAtUtc)}"),
        _ => (OdsTypeRegistries.ExchangeRateSyncStateOf(s.State).Label, $" at {MomentOrDash(s.NextRunAtUtc)}"),
    };

    private string MomentOrDash(DateTime? utc) => utc is { } at ? Moment(at) : "—";

    /// <summary>A vocabulary tone as TEXT colour — the chip's text colour, never a chip.</summary>
    private static string ToneClass(OdsChipTone tone) => tone switch
    {
        OdsChipTone.Income => "xrs-tone-income",
        OdsChipTone.Info => "xrs-tone-info",
        OdsChipTone.Warning or OdsChipTone.Pending => "xrs-tone-warning",
        OdsChipTone.Error or OdsChipTone.Expense => "xrs-tone-error",
        _ => string.Empty,
    };

    private string Moment(DateTime utc) => $"{ExchangeRateSyncTime.Absolute(utc)} ({Relative(utc)})";

    private string Relative(DateTime utc) => ExchangeRateSyncTime.Relative(utc, Time.GetUtcNow());

    private static string StaleText(StaleReason reason) => reason == StaleReason.Errors
        ? "Couldn’t refresh status"
        : "Status not current — the run is taking longer than expected";

    private static string ErrorText(ExistingExchangeRateSyncRun run) =>
        run.ErrorKind is { } kind ? OdsTypeRegistries.ExchangeRateSyncErrorOf(kind) : OdsTypeRegistries.SyncErrorFallback;

    public async ValueTask DisposeAsync()
    {
        StopPolling();
        if (_pollTask is not null)
        {
            try
            { await _pollTask; }
            catch (OperationCanceledException) { }
        }
        _pollCts?.Dispose();
    }
}
