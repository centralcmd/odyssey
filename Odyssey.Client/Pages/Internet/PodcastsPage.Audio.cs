using System.Net;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class PodcastsPage
{
    private readonly Dictionary<Guid, PodcastCardAlert> _alerts = [];
    private readonly Dictionary<Guid, PodcastAudioAction> _pending = [];

    /// <summary>The last audio state each loaded episode was announced or shown in, so a transition is said once.</summary>
    private readonly Dictionary<Guid, PodcastAudioState> _lastState = [];

    /// <summary>Episodes whose cooldown was running when last rendered, so its end is announced once.</summary>
    private readonly HashSet<Guid> _cooling = [];

    private readonly PodcastAudioPoller _poller = new();
    private ITimer? _pollTimer;
    private ITimer? _cooldownTimer;
    private bool _polling;

    private PodcastsPageRemoval? _removal;

    private PodcastEpisodeSummary? _removeEpisode => _removal?.Episode;

    private bool _removeOpen;

    private string? _removeReturnFocusId;

    private sealed record PodcastsPageRemoval(PodcastEpisodeSummary Episode);

    // ── Actions ────────────────────────────────────────────────────────────────────────────────────

    private async Task OnActionAsync(PodcastEpisodeSummary episode, PodcastAudioAction action)
    {
        switch (action)
        {
            case PodcastAudioAction.Save:
                // The browser downloads the attachment natively; the file never enters the WASM heap (§3.2.5).
                await Transfer.DownloadAsync(Podcasts.AudioUrl(episode.Id));
                return;
            case PodcastAudioAction.Remove:
                _alerts.Remove(episode.Id);
                _removal = new PodcastsPageRemoval(episode);
                _removeReturnFocusId = PodcastAudioActions.ActionId(PodcastAudioAction.Remove, episode.Id);
                _removeOpen = true;
                return;
        }

        if (_pending.ContainsKey(episode.Id))
        {
            return;
        }

        _alerts.Remove(episode.Id);
        _pending[episode.Id] = action;
        StateHasChanged();
        try
        {
            if (action is PodcastAudioAction.Download or PodcastAudioAction.Retry)
            {
                var result = await Podcasts.RequestAudioAsync(episode.Id);
                if (result.IsSuccess && result.Value is { } audio)
                {
                    SetAudio(episode.Id, audio);
                    _lastState[episode.Id] = audio.State;
                    Say(OdsPodcastAudioState.StatusLine(audio));
                    _pending.Remove(episode.Id);
                    StateHasChanged();
                    await FocusAfterAsync(episode.Id, _canDelete && OdsPodcastAudioState.InFlight(audio.State) ? PodcastAudioAction.Cancel : null);
                    SchedulePoll();
                    return;
                }

                await FailedAsync(episode, result.Status, result.Problem, result.RetryAfter);
                return;
            }

            // Cancel and Clear: the server releases the request and starts the re-request cooldown.
            var removed = await Podcasts.RemoveAudioAsync(episode.Id);
            if (removed.IsSuccess)
            {
                await RefreshEpisodeAsync(episode.Id);
                Say(action == PodcastAudioAction.Cancel ? "Download cancelled." : "Cleared.");
                _pending.Remove(episode.Id);
                StateHasChanged();
                await FocusAfterAsync(episode.Id, _canRequest ? PodcastAudioAction.Download : null);
                return;
            }

            await FailedAsync(episode, removed.Status, removed.Problem, removed.RetryAfter);
        }
        finally
        {
            if (_pending.Remove(episode.Id))
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>A refused action's card alert (§7). Download failures are states, never alerts.</summary>
    private async Task FailedAsync(PodcastEpisodeSummary episode, HttpStatusCode status, ApiProblem? problem, TimeSpan? retryAfter)
    {
        var message = await ProblemTextAsync(episode, status, problem, retryAfter);
        if (message is not null)
        {
            _alerts[episode.Id] = message;
        }

        StateHasChanged();
    }

    private async Task<PodcastCardAlert?> ProblemTextAsync(PodcastEpisodeSummary episode, HttpStatusCode status, ApiProblem? problem, TimeSpan? retryAfter)
    {
        if (status == HttpStatusCode.NotFound)
        {
            RemoveEpisode(episode.Id);
            return null;
        }

        if (NewsFeedback.Is(problem, NewsErrorCodes.PodcastAudioBusy) || status == HttpStatusCode.Conflict)
        {
            await RefreshEpisodeAsync(episode.Id);
            return new("The audio is downloading right now. Try again when it’s finished.");
        }

        if (NewsFeedback.Is(problem, NewsErrorCodes.PodcastAudioCooldown) || status == HttpStatusCode.TooManyRequests)
        {
            await RefreshEpisodeAsync(episode.Id);
            var at = Now + (retryAfter ?? TimeSpan.Zero);
            return new($"You can try again at {PodcastFormat.LocalTime(at.UtcDateTime, LocalZone)}.");
        }

        if (NewsFeedback.Is(problem, NewsErrorCodes.PodcastAudioQuotaReached) || (int)status == 507)
        {
            return new("The audio storage limit is reached. Remove audio from the server, or ask an administrator to raise the limit in Settings.");
        }

        if (NewsFeedback.Is(problem, NewsErrorCodes.FeedInactive))
        {
            return new("This podcast is archived. Unarchive it on the Feeds page to download audio.",
                "Go to Feeds", $"news-feeds?state=All&reveal={episode.FeedId}");
        }

        if (NewsFeedback.Is(problem, NewsErrorCodes.StorageUnavailable))
        {
            return new(PodcastEpisodeCard.StorageText);
        }

        if (status == HttpStatusCode.Forbidden)
        {
            return new("You no longer have permission to do this.");
        }

        return new("Couldn’t do that. Try again.");
    }

    /// <summary>Re-reads one episode with the detail endpoint and puts it in its row (its cooldown and audio block).</summary>
    private async Task RefreshEpisodeAsync(Guid id)
    {
        var result = await Podcasts.GetEpisodeAsync(id);
        if (result.IsSuccess && result.Value is { } fresh)
        {
            Replace(fresh);
            if (_details.ContainsKey(id))
            {
                _details[id] = new PodcastEpisodeDetailState(NewsBodyPhase.Ready, fresh);
            }

            _lastState[id] = fresh.Audio.State;
            ScheduleCooldown();
            SchedulePoll();
        }
        else if (result.Status == HttpStatusCode.NotFound)
        {
            RemoveEpisode(id);
        }
    }

    private void SetAudio(Guid id, PodcastEpisodeAudioInfo audio)
    {
        var index = _rows.FindIndex(r => r.Id == id);
        if (index >= 0)
        {
            _rows[index] = _rows[index] with { Audio = audio };
        }
    }

    /// <summary>Focus lands on <paramref name="action"/>'s button once it exists, else on the card's header.</summary>
    private async Task FocusAfterAsync(Guid id, PodcastAudioAction? action)
    {
        await Task.Yield();
        if (action is { } a)
        {
            await InvokeJsAsync("odsFocusById", PodcastAudioActions.ActionId(a, id));
        }
        else
        {
            await InvokeJsAsync("odsNewsFocusCard", PodcastEpisodeCard.CardId(id), RootId);
        }
    }

    // ── Remove from server ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The dialog stays open while the DELETE runs; a failure is shown in it. On success the card is refreshed, the
    /// return target becomes Download to server (aria-disabled during the cooldown), the page renders it, and the
    /// dialog closes onto an element that exists.
    /// </summary>
    private async Task<string?> ConfirmRemoveAsync()
    {
        if (_removal is not { } removal)
        {
            return null;
        }

        var episode = removal.Episode;
        var result = await Podcasts.RemoveAudioAsync(episode.Id);
        if (!result.IsSuccess)
        {
            var alert = await ProblemTextAsync(episode, result.Status, result.Problem, result.RetryAfter);
            return alert?.Text ?? "This episode is no longer available.";
        }

        await RefreshEpisodeAsync(episode.Id);
        NewsFeedback.Success(Snackbar, "Audio removed from the server.");
        _removeReturnFocusId = _canRequest ? PodcastAudioActions.ActionId(PodcastAudioAction.Download, episode.Id) : null;
        StateHasChanged();
        await Task.Yield();
        return null;
    }

    private async Task OnRemoveOpenChanged(bool open)
    {
        _removeOpen = open;
        if (!open && _removal is { } removal && _removeReturnFocusId is null)
        {
            // Without podcast-audio.create there is no Download button: the card's own header takes focus.
            await InvokeJsAsync("odsNewsFocusCard", PodcastEpisodeCard.CardId(removal.Episode.Id), RootId);
        }
    }

    // ── Announcements ──────────────────────────────────────────────────────────────────────────────

    private void RememberStates(IEnumerable<PodcastEpisodeSummary> rows)
    {
        foreach (var row in rows)
        {
            _lastState[row.Id] = row.Audio.State;
        }
    }

    /// <summary>
    /// A poll's transitions, said once each: three or more in one tick become one summary counted by outcome (§3.2.5).
    /// </summary>
    private void AnnounceTransitions(IReadOnlyList<PodcastEpisodeSummary> changed)
    {
        var fresh = changed.Where(e => !_lastState.TryGetValue(e.Id, out var last) || last != e.Audio.State).ToList();
        foreach (var e in fresh)
        {
            _lastState[e.Id] = e.Audio.State;
        }

        var stored = fresh.Where(e => e.Audio.State == PodcastAudioState.Stored).ToList();
        var failed = fresh.Where(e => e.Audio.State == PodcastAudioState.Failed).ToList();
        if (stored.Count + failed.Count >= 3)
        {
            var parts = new List<string>();
            if (stored.Count > 0)
            {
                parts.Add(stored.Count == 1 ? "1 episode is on the server and ready to save." : $"{stored.Count} episodes are on the server and ready to save.");
            }

            if (failed.Count > 0)
            {
                parts.Add(failed.Count == 1 ? "1 couldn’t be downloaded." : $"{failed.Count} couldn’t be downloaded.");
            }

            Say(string.Join(' ', parts));
            return;
        }

        foreach (var e in stored)
        {
            Say($"{PodcastEpisodeCard.TitleOf(e)} is on the server and ready to save.");
        }

        foreach (var e in failed)
        {
            Say($"{PodcastEpisodeCard.TitleOf(e)} couldn’t be downloaded.");
        }
    }

    // ── Polling ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Arms the one poll timer for the next tick the rules allow, or none.</summary>
    private void SchedulePoll()
    {
        if (_disposed || _polling)
        {
            return;
        }

        _pollTimer?.Dispose();
        _pollTimer = null;
        var delay = _poller.NextDelay(_rows.Select(r => r.Audio.State), Now);
        if (delay is { } wait)
        {
            _pollTimer = Time.CreateTimer(_ => InvokeAsync(PollAsync), null, wait, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task CheckAgainAsync()
    {
        _poller.CheckAgain();
        await PollAsync();
    }

    /// <summary>
    /// One tick: a single list request for the in-flight episodes' podcasts (archived included); an in-flight episode
    /// absent from it changed state and is re-read with the detail endpoint, at most ten a tick, in list order.
    /// </summary>
    internal async Task PollAsync()
    {
        if (_disposed || _polling)
        {
            return;
        }

        if (await PageHiddenAsync())
        {
            // Paused while the tab is hidden; the next tick looks again.
            SchedulePoll();
            return;
        }

        var inFlight = _rows.Where(r => OdsPodcastAudioState.InFlight(r.Audio.State)).ToList();
        if (inFlight.Count == 0)
        {
            SchedulePoll();
            return;
        }

        _polling = true;
        try
        {
            var feeds = _poller.FeedsForTick([.. inFlight.Select(r => r.FeedId)]);
            var filter = new PodcastEpisodeListFilter
            {
                FeedIds = feeds,
                AudioStates = PodcastAudioPoller.PolledStates,
                IncludeArchivedFeeds = true,
            };
            var result = await Podcasts.ListEpisodesAsync(filter, PodcastAudioPoller.RowsPerTick);
            if (_disposed)
            {
                return;
            }

            if (!result.IsSuccess || result.Value is not { } page)
            {
                if (result.Status == HttpStatusCode.UnprocessableEntity)
                {
                    // A podcast was deleted or switched to articles elsewhere: reload the feeds and the list.
                    _poller.Failed((int)HttpStatusCode.UnprocessableEntity, null);
                    await LoadAsync();
                    return;
                }

                if (_poller.Failed((int)result.Status, result.RetryAfter))
                {
                    Say("Couldn’t refresh the download status.");
                }

                StateHasChanged();
                return;
            }

            var wasTrouble = _poller.Trouble;
            _poller.Succeeded();
            var byId = page.Items.ToDictionary(e => e.Id);
            var changed = new List<PodcastEpisodeSummary>();
            var reread = new List<Guid>();
            foreach (var row in inFlight)
            {
                if (byId.TryGetValue(row.Id, out var fresh))
                {
                    if (fresh.Audio.State != row.Audio.State || fresh.Audio.DeferredReason != row.Audio.DeferredReason)
                    {
                        _alerts.Remove(row.Id);
                        Replace(fresh);
                        changed.Add(fresh);
                    }
                }
                else if (page.NextCursor is null && feeds.Contains(row.FeedId) && reread.Count < PodcastAudioPoller.DetailReadsPerTick)
                {
                    // Absent: it left the in-flight set. A truncated result proves nothing, so it is skipped then.
                    reread.Add(row.Id);
                }
            }

            foreach (var id in reread)
            {
                var detail = await Podcasts.GetEpisodeAsync(id);
                if (detail.IsSuccess && detail.Value is { } fresh)
                {
                    _alerts.Remove(id);
                    Replace(fresh);
                    if (_details.ContainsKey(id))
                    {
                        _details[id] = new PodcastEpisodeDetailState(NewsBodyPhase.Ready, fresh);
                    }

                    changed.Add(fresh);
                }
                else if (detail.Status == HttpStatusCode.NotFound)
                {
                    RemoveEpisode(id);
                }
            }

            AnnounceTransitions(changed);
            if (wasTrouble && changed.Count == 0)
            {
                Say("Download status refreshed.");
            }

            ScheduleCooldown();
            StateHasChanged();
        }
        finally
        {
            _polling = false;
            SchedulePoll();
        }
    }

    private async Task<bool> PageHiddenAsync()
    {
        try
        {
            return await JS.InvokeAsync<bool>("odsPageHidden");
        }
        catch (JSException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ── The cooldown ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One timer for the page: it re-renders when the next running cooldown ends, so its button turns active, and says
    /// "Download to server is available again for ‹title›." once (§3.2.5). The client never computes a cooldown itself.
    /// </summary>
    private void ScheduleCooldown()
    {
        if (_disposed)
        {
            return;
        }

        _cooldownTimer?.Dispose();
        _cooldownTimer = null;
        var now = Now;
        foreach (var row in _rows)
        {
            if (Until(row) is { } until && until > now)
            {
                _cooling.Add(row.Id);
            }
        }

        var next = _rows.Select(Until).Where(u => u is { } at && at > now).Select(u => u!.Value).DefaultIfEmpty().Min();
        if (next != default)
        {
            _cooldownTimer = Time.CreateTimer(_ => InvokeAsync(CooldownPassed), null, next - now + TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    private void CooldownPassed()
    {
        var now = Now;
        foreach (var row in _rows.Where(r => _cooling.Contains(r.Id) && !(Until(r) > now)).ToList())
        {
            _cooling.Remove(row.Id);
            if (_canRequest && row.Audio.State is PodcastAudioState.None or PodcastAudioState.Failed)
            {
                var verb = row.Audio.State == PodcastAudioState.Failed ? "Try again" : "Download to server";
                Say($"{verb} is available again for {PodcastEpisodeCard.TitleOf(row)}.");
            }
        }

        ScheduleCooldown();
        StateHasChanged();
    }

    private static DateTimeOffset? Until(PodcastEpisodeSummary row) =>
        row.Audio.RequestAvailableAtUtc is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null;
}
