using System.Net;
using Microsoft.AspNetCore.Components;
using Odyssey.ApiClient.Auth;
using Odyssey.Client.Components;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>
/// In-browser playback on the Podcasts page (issue #594 over backend #593). The page owns ONE
/// <see cref="PodcastPlaybackSession"/>; each card's player is a view of it. This file starts and stops it, answers its
/// questions (re-read an episode, classify a failure) and moves focus.
/// </summary>
public partial class PodcastsPage : IPodcastPlaybackHost
{
    [Inject] private AntiforgeryTokenStore Antiforgery { get; set; } = default!;

    private PodcastPlaybackSession? _session;

    /// <summary>Episodes whose audio type this browser cannot play: Play is removed, an info line says why.</summary>
    private readonly HashSet<Guid> _unsupported = [];

    /// <summary><c>news.update</c>: the shared position and the listened stamp are saved.</summary>
    private bool _canSavePlayback;

    private PodcastPlaybackSession Session => _session ??= new PodcastPlaybackSession(JS, Podcasts, Antiforgery, Time, this);

    private string TitleOf(Guid id) => _rows.FirstOrDefault(r => r.Id == id) is { } row ? PodcastEpisodeCard.TitleOf(row) : "the episode";

    /// <summary>A Play press already starting: a second press (a double click) is ignored rather than starting twice.</summary>
    private bool _starting;

    private async Task PlayAsync(PodcastEpisodeSummary episode)
    {
        if (_starting)
        {
            return;
        }

        _starting = true;
        try
        {
            await StartPlayAsync(episode);
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task StartPlayAsync(PodcastEpisodeSummary episode)
    {
        if (_session?.EpisodeId is { } playing && playing != episode.Id)
        {
            var wasPlaying = _session.PlayingNow;
            await _session.StopAsync();
            if (wasPlaying)
            {
                Say($"Paused {TitleOf(playing)}.");
            }
        }

        if (!await Session.CanPlayAsync(episode.Audio.ContentType))
        {
            _unsupported.Add(episode.Id);
            Say(OdsPodcastPlayback.ErrorText(OdsPodcastPlayerError.Unsupported, PodcastFormat.Type(episode.Audio.ContentType)));
            StateHasChanged();
            await FocusAsync(PodcastAudioActions.ActionId(PodcastAudioAction.Save, episode.Id));
            return;
        }

        Say("Loading audio.");
        // The player replaces the Play button, so focus follows to its toggle ("Pause, ‹title›") once it renders.
        _ = FocusAsync(PodcastAudioActions.PlayerToggleId(episode.Id));
        await Session.StartAsync(episode, PodcastEpisodeCard.TitleOf(episode), _canSavePlayback);
    }

    /// <summary>Stops playback when the playing episode leaves the list (a filter change, a reload, a removal).</summary>
    private async Task StopIfGoneAsync()
    {
        if (_session?.EpisodeId is { } id && _rows.All(r => r.Id != id))
        {
            var title = _session.Title;
            await _session.StopAsync();
            Say($"Stopped {title}.");
        }
    }

    /// <summary>Remove from server on the playing episode stops it first, with a save (#594 §3).</summary>
    private async Task StopIfPlayingAsync(Guid id)
    {
        if (_session?.Is(id) == true)
        {
            await _session.StopAsync();
        }
    }

    /// <summary>The episode whose card the page opened to show a playback error, so it is opened once, not on every render.</summary>
    private Guid? _openedForError;

    Task IPodcastPlaybackHost.ChangedAsync() => _disposed ? Task.CompletedTask : InvokeAsync(async () =>
    {
        // A playback error is explained only by the open card's player (its alert, and the focus target the page
        // moves to). A collapsed card would say nothing, so the page opens it (a11y review, 4.1.3 / 2.4.3).
        if (_session is { EpisodeId: { } id, Error: not null } && _openId != id && _openedForError != id
            && _rows.FirstOrDefault(r => r.Id == id) is { } row)
        {
            _openedForError = id;
            await OnToggleAsync(row, true);
        }
        else if (_session?.Error is null)
        {
            _openedForError = null;
        }

        StateHasChanged();
    });

    void IPodcastPlaybackHost.Say(string text) => Say(text);

    async Task IPodcastPlaybackHost.EndedAsync(Guid episodeId, bool saved) => await InvokeAsync(async () =>
    {
        await RefreshEpisodeAsync(episodeId);
        StateHasChanged();
    });

    async Task<OdsPodcastPlayerError?> IPodcastPlaybackHost.ClassifyFailureAsync(Guid episodeId)
    {
        // The <audio> element cannot see the HTTP status, so the episode is re-read through the API client.
        var result = await Podcasts.GetEpisodeAsync(episodeId);
        OdsPodcastPlayerError? error;
        string focus;
        if (result.Status == HttpStatusCode.Unauthorized)
        {
            return null;
        }
        else if (result.Status == HttpStatusCode.Forbidden)
        {
            error = OdsPodcastPlayerError.Forbidden;
            focus = PodcastAudioActions.PlayerAlertId(episodeId);
        }
        else if (result.Status == HttpStatusCode.NotFound || (result.IsSuccess && result.Value?.Audio.State != PodcastAudioState.Stored))
        {
            error = OdsPodcastPlayerError.Removed;
            focus = PodcastAudioActions.StatusId(episodeId);
            await InvokeAsync(async () =>
            {
                await RefreshEpisodeAsync(episodeId);
                StateHasChanged();
            });
        }
        else
        {
            error = OdsPodcastPlayerError.Network;
            focus = PodcastAudioActions.PlayerRetryId(episodeId);
        }

        _ = FocusAsync(focus);
        return error;
    }

    /// <summary>Focus by id once the element exists (two frames: the player renders after this call returns).</summary>
    public async Task FocusAsync(string id)
    {
        await Task.Yield();
        await InvokeJsAsync("odsFocusFirstIn", id, true, 2);
    }

    private void DisposePlayback()
    {
        // Leaving the page: the module sends the final save as a keepalive fetch; nothing waits for it.
        if (_session is { } session)
        {
            _ = session.DisposeAsync().AsTask();
        }
    }
}
