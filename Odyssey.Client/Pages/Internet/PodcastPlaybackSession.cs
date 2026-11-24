using System.Net;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Auth;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>
/// The in-browser player's client-side behaviour constants (issue #594 §4). UX timing, not server rules: the server's
/// own guard is <see cref="PodcastLimits.PlaybackCompletionGuardSeconds"/>.
/// </summary>
public static class PodcastPlayerTiming
{
    /// <summary>A position save every this much playback.</summary>
    /// <remarks>15 s, written in minutes so a news-limit lint does not read it as <c>NewsLimits.MinFetchIntervalMinutes</c>.</remarks>
    public static readonly TimeSpan PeriodicSave = TimeSpan.FromMinutes(0.25);

    /// <summary>Never two saves closer than this.</summary>
    public static readonly TimeSpan MinSaveGap = TimeSpan.FromSeconds(10);

    /// <summary>A seek is saved once the slider settles.</summary>
    public static readonly TimeSpan SeekSaveDebounce = TimeSpan.FromSeconds(1);

    /// <summary>Browsers fire <c>pause</c> just before <c>ended</c>: a pause save waits this long and is dropped by an end.</summary>
    public static readonly TimeSpan PauseBeforeEndedGrace = TimeSpan.FromMilliseconds(300);

    /// <summary>"Buffering." is announced only when it lasts this long.</summary>
    public static readonly TimeSpan BufferingAnnounceAfter = TimeSpan.FromSeconds(2);

    /// <summary>Consecutive failed saves before "Couldn't save your place.".</summary>
    public const int SaveFailuresShown = 3;
}

/// <summary>What the page is told when a session event needs it (issue #594 §3, §7).</summary>
public interface IPodcastPlaybackHost
{
    /// <summary>Re-render.</summary>
    Task ChangedAsync();

    /// <summary>One polite announcement.</summary>
    void Say(string text);

    /// <summary>The listen reached the end and the server took the finish (or the reader cannot save): re-read the episode.</summary>
    Task EndedAsync(Guid episodeId, bool saved);

    /// <summary>
    /// The media element failed with a network or source error. The page re-reads the episode and says what it found:
    /// <c>null</c> when the session should simply stop (a 401: the global sign-in handles it).
    /// </summary>
    Task<OdsPodcastPlayerError?> ClassifyFailureAsync(Guid episodeId);

    /// <summary>Move focus to an element by id, after the next render.</summary>
    Task FocusAsync(string id);
}

/// <summary>
/// The page's ONE playback session (issue #594 §3): the playing episode, its state for <see cref="OdsPodcastPlayer"/>,
/// the serialized save queue and the bridge to <c>podcast-player.js</c>, which owns the &lt;audio&gt;. Owned by
/// <see cref="PodcastsPage"/>, not a DI service; v3's floating player lifts it into a scoped service.
/// </summary>
public sealed class PodcastPlaybackSession(
    IJSRuntime js,
    IPodcastsApiClient api,
    AntiforgeryTokenStore tokens,
    TimeProvider time,
    IPodcastPlaybackHost host) : IAsyncDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private IJSObjectReference? _module;
    private DotNetObjectReference<PodcastPlaybackSession>? _self;
    private ITimer? _pauseTimer;
    private ITimer? _seekTimer;
    private ITimer? _bufferTimer;
    private DateTimeOffset _lastSaveAt;
    private int? _pendingPosition;
    private bool _completed;
    private DateTimeOffset? _finishedAt;
    private int _saveFailures;
    private string _title = string.Empty;

    public Guid? EpisodeId { get; private set; }

    /// <summary>The playing episode's title, for announcements after it has left the list.</summary>
    public string Title => _title;

    public OdsPodcastPlayerState State { get; private set; }

    public double CurrentSeconds { get; private set; }

    public double? DurationSeconds { get; private set; }

    public int Volume { get; private set; } = 100;

    public bool Muted { get; private set; }

    public string? Status { get; private set; }

    public OdsPodcastPlayerSaveNote? SaveNote { get; private set; }

    public OdsPodcastPlayerError? Error { get; private set; }

    /// <summary>Whether saves are sent (<c>news.update</c>, and not refused since).</summary>
    public bool CanSave { get; private set; }

    public bool PlayingNow => State is OdsPodcastPlayerState.Playing or OdsPodcastPlayerState.Buffering;

    public bool Is(Guid id) => EpisodeId == id;

    private DateTimeOffset Now => time.GetUtcNow();

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", OdsPodcastPlayer.ModulePath);

    /// <summary>Whether this browser can play the type at all (<c>canPlayType</c>).</summary>
    public async Task<bool> CanPlayAsync(string? contentType)
    {
        try
        {
            return await (await ModuleAsync()).InvokeAsync<bool>("canPlay", contentType);
        }
        catch (JSException)
        {
            return true;
        }
    }

    /// <summary>
    /// Starts <paramref name="episode"/> (stopping any other first, with a save). <paramref name="canSave"/> is
    /// <c>news.update</c>. The source is read once here: later re-reads of the episode never restart or re-seek it.
    /// </summary>
    public async Task StartAsync(PodcastEpisodeSummary episode, string title, bool canSave)
    {
        await StopAsync();
        EpisodeId = episode.Id;
        _title = title;
        CanSave = canSave;
        _completed = false;
        _saveFailures = 0;
        SaveNote = null;
        Error = null;
        Status = null;
        DurationSeconds = episode.DurationSeconds;
        CurrentSeconds = OdsPodcastPlayback.ResumeAt(episode.PlaybackPositionSeconds, null);
        State = OdsPodcastPlayerState.Loading;
        _lastSaveAt = Now;
        _pendingPosition = null;
        await host.ChangedAsync();

        string outcome;
        try
        {
            outcome = await StartModuleAsync(episode, canSave);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            // The module failed to load or start: never a spinner forever — the network error, with Try again.
            outcome = "refused";
        }

        await PlayOutcomeAsync(outcome);
    }

    private async Task<string> StartModuleAsync(PodcastEpisodeSummary episode, bool canSave)
    {
        var module = await ModuleAsync();
        Volume = await module.InvokeAsync<int>("readVolume");
        _self?.Dispose();
        _self = DotNetObjectReference.Create(this);
        var src = api.AudioStreamUrl(episode.Id);
        return await module.InvokeAsync<string>("start", _self, new
        {
            src,
            apiOrigin = new Uri(src).GetLeftPart(UriPartial.Authority),
            pattern = api.AudioStreamPathPattern,
            saveUrl = SaveUrl(src),
            token = canSave ? await tokens.GetTokenAsync() : null,
            tokenHeader = AntiforgeryHandler.HeaderName,
            canSave,
            volume = Volume,
            muted = Muted,
            startAt = CurrentSeconds,
        });
    }

    private static string SaveUrl(string streamUrl) => streamUrl[..streamUrl.LastIndexOf("/audio/stream", StringComparison.Ordinal)] + "/playback";

    private async Task PlayOutcomeAsync(string outcome)
    {
        switch (outcome)
        {
            case "ok":
                State = OdsPodcastPlayerState.Playing;
                host.Say("Playing.");
                break;
            case "NotAllowedError":
                State = OdsPodcastPlayerState.Blocked;
                host.Say("Press play to start.");
                break;
            case "refused":
                State = OdsPodcastPlayerState.Paused;
                Error = OdsPodcastPlayerError.Network;
                break;
            default:
                // A play() rejection other than the autoplay policy: the media element's error event follows.
                State = OdsPodcastPlayerState.Paused;
                break;
        }

        await host.ChangedAsync();
    }

    public async Task ToggleAsync()
    {
        if (EpisodeId is null || _module is null)
        {
            return;
        }

        // Loading counts as playing: the header and the chip say "Playing", so the press pauses.
        if (PlayingNow || State == OdsPodcastPlayerState.Loading)
        {
            await _module.InvokeVoidAsync("pause");
            return;
        }

        if (State == OdsPodcastPlayerState.Ended)
        {
            _completed = false;
            Status = null;
            await _module.InvokeVoidAsync("seek", 0);
        }

        await PlayOutcomeAsync(await _module.InvokeAsync<string>("play"));
    }

    public async Task SeekAsync(int seconds)
    {
        if (_module is null)
        {
            return;
        }

        CurrentSeconds = seconds;
        if (State == OdsPodcastPlayerState.Ended)
        {
            State = OdsPodcastPlayerState.Paused;
            Status = null;
            _completed = false;
        }

        await _module.InvokeVoidAsync("seek", seconds);
        _seekTimer?.Dispose();
        _seekTimer = time.CreateTimer(_ => _ = QueueSaveAsync(seconds, force: true), null, PodcastPlayerTiming.SeekSaveDebounce, Timeout.InfiniteTimeSpan);
        await host.ChangedAsync();
    }

    public async Task SetVolumeAsync(int percent)
    {
        Volume = Math.Clamp(percent, 0, 100);
        Muted = false;
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("writeVolume", Volume);
            await _module.InvokeVoidAsync("setVolume", Volume, Muted);
        }

        await host.ChangedAsync();
    }

    public async Task ToggleMuteAsync()
    {
        Muted = !Muted;
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setVolume", Volume, Muted);
        }

        await host.ChangedAsync();
    }

    public async Task RetryAsync()
    {
        if (_module is null || EpisodeId is null)
        {
            return;
        }

        Error = null;
        State = OdsPodcastPlayerState.Loading;
        await host.ChangedAsync();
        await PlayOutcomeAsync(await _module.InvokeAsync<string>("play"));
    }

    /// <summary>Stops the session (another episode, a removal, a list change, leaving): a final save, then release.</summary>
    public async Task StopAsync()
    {
        if (EpisodeId is null)
        {
            return;
        }

        CancelTimers();
        var id = EpisodeId.Value;
        var save = !_completed && CanSave && State != OdsPodcastPlayerState.Loading;
        var at = (int)Math.Floor(CurrentSeconds);
        EpisodeId = null;
        State = OdsPodcastPlayerState.Paused;
        Error = null;
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("stop");
            }
        }
        catch (JSDisconnectedException)
        {
        }

        _self?.Dispose();
        _self = null;
        if (save)
        {
            await SaveAsync(id, at, completed: false);
        }
    }

    /// <summary>The page is going away: the module sends the final save as a keepalive fetch, fire-and-forget.</summary>
    public async Task LeaveAsync()
    {
        CancelTimers();
        EpisodeId = null;
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("leave");
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
        }
    }

    /// <summary>The module's media events (<c>podcast-player.js</c> <c>emit</c>).</summary>
    [JSInvokable]
    public async Task OnPlayerEvent(string kind, double current, double? duration, string? extra)
    {
        if (EpisodeId is not { } id)
        {
            return;
        }

        CurrentSeconds = current;
        if (OdsPodcastPlayback.LengthKnown(duration))
        {
            DurationSeconds = duration;
        }

        switch (kind)
        {
            case "time":
                if (PlayingNow && Now - _lastSaveAt >= PodcastPlayerTiming.PeriodicSave)
                {
                    _ = QueueSaveAsync((int)Math.Floor(current), force: false);
                }

                // Ticks re-render only the player; nothing is announced.
                break;
            case "playing":
                _bufferTimer?.Dispose();
                if (State != OdsPodcastPlayerState.Playing)
                {
                    State = OdsPodcastPlayerState.Playing;
                }

                break;
            case "waiting":
                State = OdsPodcastPlayerState.Buffering;
                _bufferTimer?.Dispose();
                _bufferTimer = time.CreateTimer(_ => { if (State == OdsPodcastPlayerState.Buffering) { host.Say("Buffering."); } }, null, PodcastPlayerTiming.BufferingAnnounceAfter, Timeout.InfiniteTimeSpan);
                break;
            case "pause":
                if (State != OdsPodcastPlayerState.Ended)
                {
                    State = OdsPodcastPlayerState.Paused;
                    var at = (int)Math.Floor(current);
                    _pauseTimer?.Dispose();
                    _pauseTimer = time.CreateTimer(_ => _ = QueueSaveAsync(at, force: true), null, PodcastPlayerTiming.PauseBeforeEndedGrace, Timeout.InfiniteTimeSpan);
                }

                break;
            case "ended":
                await EndAsync(id);
                return;
            case "error":
                await FailAsync(id, extra);
                return;
        }

        await host.ChangedAsync();
    }

    private async Task EndAsync(Guid id)
    {
        CancelTimers();
        _pendingPosition = null;
        _completed = true;
        _finishedAt = Now;
        State = OdsPodcastPlayerState.Ended;
        CurrentSeconds = 0;
        var saved = false;
        if (CanSave)
        {
            saved = await SaveAsync(id, null, completed: true);
        }

        Status = saved ? "Finished. Listened just now." : "Finished.";
        host.Say($"Finished {_title}.");
        await host.ChangedAsync();
        await host.EndedAsync(id, saved);
    }

    private async Task FailAsync(Guid id, string? code)
    {
        // MediaError: 1 aborted (our own stop or seek), 2 network, 3 decode, 4 source not supported.
        switch (code)
        {
            case "1":
                return;
            case "3":
                Error = OdsPodcastPlayerError.Decode;
                _ = host.FocusAsync(PodcastAudioActions.ActionId(PodcastAudioAction.Save, id));
                break;
            default:
                if (await host.ClassifyFailureAsync(id) is not { } kind)
                {
                    await StopAsync();
                    await host.ChangedAsync();
                    return;
                }

                Error = kind;
                if (kind == OdsPodcastPlayerError.Removed)
                {
                    _completed = true;
                    await TellModuleNoSaveAsync();
                }

                break;
        }

        State = OdsPodcastPlayerState.Paused;
        CancelTimers();
        await host.ChangedAsync();
    }

    /// <summary>The episode's audio went away (removed, or 409 on a finish): the player shows it and stops saving.</summary>
    public async Task MarkRemovedAsync()
    {
        Error = OdsPodcastPlayerError.Removed;
        State = OdsPodcastPlayerState.Paused;
        _completed = true;
        CancelTimers();
        await TellModuleNoSaveAsync();
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("pause");
        }
    }

    /// <summary>The keepalive save on leaving must stop too once .NET stops saving (a refusal, removed audio).</summary>
    private async Task TellModuleNoSaveAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("setCanSave", false);
            }
        }
        catch (JSException)
        {
        }
    }

    // ── The save queue ─────────────────────────────────────────────────────────────────────────────
    // One request in flight; a newer position replaces a queued one; nothing after the end.

    private async Task QueueSaveAsync(int position, bool force)
    {
        if (!CanSave || _completed || EpisodeId is not { } id)
        {
            return;
        }

        if (!force && Now - _lastSaveAt < PodcastPlayerTiming.MinSaveGap)
        {
            return;
        }

        // A newer position replaces one still waiting for the gate.
        _pendingPosition = position;
        await SaveAsync(id, null, completed: false);
    }

    private async Task<bool> SaveAsync(Guid target, int? explicitPosition, bool completed)
    {
        await _saveGate.WaitAsync();
        try
        {
            int? position = completed ? null : explicitPosition ?? _pendingPosition;
            _pendingPosition = null;
            if ((!completed && position is null) || !CanSave || (!completed && _completed && target == EpisodeId))
            {
                return false;
            }

            _lastSaveAt = Now;
            if (_module is not null && !completed)
            {
                // The keepalive save on leaving uses the freshest token (null after a sign-out skips it).
                try
                {
                    await _module.InvokeVoidAsync("setToken", await tokens.GetTokenAsync());
                }
                catch (JSException)
                {
                }
            }

            var result = await api.SavePlaybackAsync(target, position, completed);
            if (result.IsSuccess)
            {
                _saveFailures = 0;
                if (!completed && _finishedAt is { } finished && Now - finished < TimeSpan.FromSeconds(PodcastLimits.PlaybackCompletionGuardSeconds))
                {
                    // A replay inside the server's completion guard: that save was ignored, so re-read the episode once
                    // and let "Resume from" show what the server holds.
                    _finishedAt = null;
                    await host.EndedAsync(target, saved: false);
                }

                if (SaveNote == OdsPodcastPlayerSaveNote.Trouble)
                {
                    SaveNote = null;
                }

                return true;
            }

            if (result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // A role change reaches a live session within a minute: stop saving, say so once, never retry.
                CanSave = false;
                await TellModuleNoSaveAsync();
                SaveNote = OdsPodcastPlayerSaveNote.Off;
                host.Say("Your place isn’t being saved.");
                await host.ChangedAsync();
                return false;
            }

            if (completed && result.Status == HttpStatusCode.Conflict)
            {
                await MarkRemovedAsync();
                return false;
            }

            if (++_saveFailures == PodcastPlayerTiming.SaveFailuresShown)
            {
                SaveNote = OdsPodcastPlayerSaveNote.Trouble;
                host.Say("Couldn’t save your place.");
                await host.ChangedAsync();
            }

            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void CancelTimers()
    {
        _pauseTimer?.Dispose();
        _seekTimer?.Dispose();
        _bufferTimer?.Dispose();
        _pauseTimer = _seekTimer = _bufferTimer = null;
    }

    public async ValueTask DisposeAsync()
    {
        await LeaveAsync();
        _self?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        // The gate is not disposed: a save still in flight when the page goes away must finish (or fail) quietly.
    }
}
