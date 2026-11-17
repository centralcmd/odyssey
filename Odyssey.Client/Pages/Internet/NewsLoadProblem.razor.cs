using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Odyssey.Client.Pages.Internet;

public partial class NewsLoadProblem
{
    [Inject] private TimeProvider Time { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>The fallback wait when a <c>news-busy</c> answer carries no <c>Retry-After</c> (the server sends 2 s).</summary>
    public static readonly TimeSpan DefaultBusyWait = TimeSpan.FromSeconds(2);

    /// <summary>True for <c>503 news-busy</c>; false for any other failure.</summary>
    [Parameter] public bool Busy { get; set; }

    /// <summary>What the region holds, in the plural: "feeds", "tags".</summary>
    [Parameter, EditorRequired] public string Noun { get; set; } = "items";

    /// <summary>The failed response's <c>Retry-After</c>.</summary>
    [Parameter] public TimeSpan? RetryAfter { get; set; }

    /// <summary>Bumped by the page on every failed load, so this component can tell a new failure from a re-render.</summary>
    [Parameter] public int Failure { get; set; }

    /// <summary>Runs the load again; the argument is whether Retry had focus when it started.</summary>
    [Parameter, EditorRequired] public Func<bool, Task> OnRetry { get; set; } = default!;

    /// <summary>The id stem; the Retry button is <c>{IdPrefix}-retry</c>.</summary>
    [Parameter] public string IdPrefix { get; set; } = "nw-region";

    public string RetryId => $"{IdPrefix}-retry";

    private enum Phase { Waiting, Retrying, Failed }

    private Phase _phase = Phase.Waiting;
    private int _seenFailure = -1;
    private bool _autoUsed;
    private DateTimeOffset _availableAt;
    private ITimer? _retryTimer;
    private ITimer? _tickTimer;
    private string _status = string.Empty;

    /// <summary>Whole seconds left before Retry-After allows another request, or null when it already does.</summary>
    private int? Remaining
    {
        get
        {
            var left = _availableAt - Time.GetUtcNow();
            return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalSeconds) : null;
        }
    }

    protected override void OnParametersSet()
    {
        if (Failure == _seenFailure)
        {
            return;
        }

        _seenFailure = Failure;
        _status = string.Empty;
        _availableAt = Time.GetUtcNow() + (RetryAfter ?? (Busy ? DefaultBusyWait : TimeSpan.Zero));
        if (Busy && !_autoUsed)
        {
            _phase = Phase.Waiting;
            Schedule(_availableAt, automatic: true);
        }
        else
        {
            _phase = Busy ? Phase.Failed : Phase.Waiting;
        }

        StartTicking();
    }

    private async Task RetryPressedAsync()
    {
        if (!_disposed && _phase == Phase.Retrying)
        {
            return;
        }

        if (Remaining is { } wait)
        {
            // Pressed before the server allows another request: the press schedules the retry for that moment
            // rather than doing nothing, and says so once.
            _status = $"Retrying in {wait.ToString(CultureInfo.CurrentCulture)} seconds";
            Schedule(_availableAt, automatic: false);
            return;
        }

        await RunAsync();
    }

    private void Schedule(DateTimeOffset at, bool automatic)
    {
        _retryTimer?.Dispose();
        var due = at - Time.GetUtcNow();
        _retryTimer = Time.CreateTimer(_ => _ = InvokeAsync(async () =>
        {
            if (automatic)
            {
                _autoUsed = true;
            }

            await RunAsync();
        }), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    // "Available in N s" moves at most every 5 s, unannounced (§7.3).
    private void StartTicking()
    {
        _tickTimer?.Dispose();
        _tickTimer = Remaining is null
            ? null
            : Time.CreateTimer(_ => _ = InvokeAsync(() =>
            {
                if (Remaining is null)
                {
                    _tickTimer?.Dispose();
                    _tickTimer = null;
                }

                StateHasChanged();
            }), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task RunAsync()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        var hadFocus = await RetryHasFocusAsync();
        _phase = Phase.Retrying;
        StateHasChanged();
        await OnRetry(hadFocus);

        // Still rendered after the retry: it failed again, and the page has bumped Failure (OnParametersSet set the
        // new phase). A page that did not bump it gets Retry back rather than a button stuck on "Retrying…".
        if (!_disposed && _phase == Phase.Retrying)
        {
            _phase = Busy && _autoUsed ? Phase.Failed : Phase.Waiting;
            StateHasChanged();
        }
    }

    private async Task<bool> RetryHasFocusAsync()
    {
        try
        {
            return await JS.InvokeAsync<string?>("odsActiveElementId") == RetryId;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            return false;
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _retryTimer?.Dispose();
        _tickTimer?.Dispose();
    }
}
