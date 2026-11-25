using System.Net;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class PodcastsPage
{
    // ── The list ───────────────────────────────────────────────────────────────────────────────────
    private bool _loading = true;
    private LoadFailure? _failure;
    private int _failureCount;
    private List<PodcastEpisodeSummary> _rows = [];
    private int? _total;
    private bool _totalCapped;
    private string? _cursor;
    private PodcastEpisodeListFilter? _listFilter;
    private bool _refetching;
    private bool _loadingMore;
    private bool _loadMoreFailed;
    private int _generation;
    private bool _shownFirstPage;
    private object _resetKey = 0;
    private CancellationTokenSource _listCts = new();
    private readonly Dictionary<Guid, PodcastEpisodeDetailState> _details = [];
    private Guid? _openId;
    private bool _disposed;

    private sealed record LoadFailure(bool Busy, TimeSpan? RetryAfter);

    /// <summary>The podcast feeds, the tags and (for a file-storage.read holder) the news status, then a first page.</summary>
    private async Task LoadAsync()
    {
        var tagsTask = Catalogue.GetTagsAsync();
        var statusTask = _canReadFileStorage ? News.GetStatusAsync() : null;
        var (feeds, problem, retryAfter) = await LoadAllFeedsAsync();
        if (feeds is null)
        {
            _loading = false;
            _failure = new LoadFailure(NewsFeedback.IsBusy(problem), retryAfter);
            _failureCount++;
            StateHasChanged();
            return;
        }

        SetFeeds(feeds);
        var tags = await tagsTask;
        _tags = tags.IsSuccess ? tags.Value ?? [] : [];
        if (statusTask is not null)
        {
            var status = await statusTask;
            _status = status.IsSuccess ? status.Value : null;
        }

        DropStaleIds();
        if (_feeds.Count == 0)
        {
            _loading = false;
            _failure = null;
            _rows = [];
            StateHasChanged();
            return;
        }

        await ResetAsync();
    }

    private void SetFeeds(List<ExistingNewsFeed> feeds)
    {
        _feeds = feeds;
        _feedsById = feeds.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
    }

    private async Task<(List<ExistingNewsFeed>? Feeds, ApiProblem? Problem, TimeSpan? RetryAfter)> LoadAllFeedsAsync()
    {
        var all = new List<ExistingNewsFeed>();
        var seen = new HashSet<Guid>();
        for (var page = 1; page <= MaxFeedRequests; page++)
        {
            var result = await Podcasts.ListFeedsAsync(page, NewsLimits.MaxPageSize);
            if (!result.IsSuccess || result.Value is not { } chunk)
            {
                return (null, result.Problem, result.RetryAfter);
            }

            all.AddRange(chunk.Items.Where(f => f.Kind == NewsFeedKind.Podcast && seen.Add(f.Id)));
            if (chunk.Items.Count < NewsLimits.MaxPageSize)
            {
                break;
            }
        }

        return (all, null, null);
    }

    private async Task RetryLoadAsync(bool retryHadFocus)
    {
        _failure = null;
        await LoadAsync();
        if (_failure is null && retryHadFocus)
        {
            await InvokeJsAsync("odsNewsFocusListStart", RootId);
        }
    }

    /// <summary>
    /// A fresh first page on any filter, sort or search change. The request in flight is cancelled and its generation
    /// retired, so a late response is dropped; the old rows stay under the refetch bar until the new page arrives.
    /// </summary>
    internal async Task ResetAsync(bool focusListStart = false)
    {
        _listCts.Cancel();
        _listCts.Dispose();
        var cts = _listCts = new CancellationTokenSource();
        var generation = ++_generation;
        _loadingMore = false;
        _loadMoreFailed = false;
        _refetching = !_loading && _rows.Count > 0;
        var filter = BuildFilter();
        StateHasChanged();

        ApiResult<PodcastEpisodePage> result;
        try
        {
            result = await Podcasts.ListEpisodesAsync(filter, _batch, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _generation)
        {
            return;
        }

        _refetching = false;
        _loading = false;
        if (result.IsSuccess && result.Value is { } page)
        {
            await ApplyFirstPageAsync(page, filter, generation, focusListStart);
            return;
        }

        if (filter.Search is not null && result.Status == HttpStatusCode.BadRequest
            && (NewsFeedback.Is(result.Problem, NewsErrorCodes.SearchUnusable)
                || (result.Problem?.Errors?.Keys.Any(k => string.Equals(k, "search", StringComparison.OrdinalIgnoreCase)) ?? false)))
        {
            // A search the server cannot use is the field's message, never the list's error state (news-fe §7.4).
            _searchError = $"Use words of at least {NewsLimits.MinSearchTermLength} letters.";
            Say(_searchError);
            StateHasChanged();
            return;
        }

        if (result.Status == HttpStatusCode.UnprocessableEntity
            && (result.Problem?.Errors?.Keys.Any(k => string.Equals(k, "feedIds", StringComparison.OrdinalIgnoreCase)) ?? false))
        {
            // A podcast in the filter was deleted or switched to articles elsewhere: drop it and reload (§7).
            Say("A podcast in the filter is no longer available.");
            await LoadAsync();
            return;
        }

        _failure = new LoadFailure(NewsFeedback.IsBusy(result.Problem), result.RetryAfter);
        _failureCount++;
        StateHasChanged();
        await InvokeJsAsync("odsNewsRecoverFocus", RootId);
    }

    private async Task ApplyFirstPageAsync(PodcastEpisodePage page, PodcastEpisodeListFilter filter, int generation, bool focusListStart)
    {
        var seen = new HashSet<Guid>();
        _rows = [.. page.Items.Where(e => seen.Add(e.Id))];
        await StopIfGoneAsync();
        _total = page.TotalCount;
        _totalCapped = page.TotalCountCapped == true;
        _cursor = page.NextCursor;
        _listFilter = filter;
        _resetKey = generation;
        _failure = null;
        _searchError = null;
        foreach (var gone in _details.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _details.Remove(gone);
            _alerts.Remove(gone);
        }

        if (_openId is { } open && !seen.Contains(open))
        {
            _openId = null;
        }

        Say(CountSentence());
        RememberStates(_rows);
        SchedulePoll();
        ScheduleCooldown();
        StateHasChanged();
        if (!_shownFirstPage && !focusListStart)
        {
            // The page's first list: focus stays where route navigation put it, on the h1.
            _shownFirstPage = true;
            return;
        }

        _shownFirstPage = true;
        await InvokeJsAsync("odsNewsScrollListTop", RootId);
        await InvokeJsAsync(focusListStart ? "odsNewsFocusListStart" : "odsNewsRecoverFocus", RootId);
    }

    /// <summary>"212 episodes", "More than 10,000 episodes" or "No episodes match these filters.".</summary>
    internal string CountSentence()
    {
        if (_rows.Count == 0)
        {
            return NoEpisodesYet ? "No episodes yet." : "No episodes match these filters.";
        }

        var total = _total ?? _rows.Count;
        return _totalCapped
            ? $"More than {total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} episodes"
            : NewsFeedback.Count(total, "episode", "episodes");
    }

    /// <summary>The next page by <c>nextCursor</c>: never concurrent, de-duplicated by id; a rejected cursor restarts.</summary>
    private async Task LoadMoreAsync(bool fromPill)
    {
        if (_cursor is not { } cursor || _loadingMore || _listFilter is not { } filter)
        {
            return;
        }

        var generation = _generation;
        var token = _listCts.Token;
        _loadingMore = true;
        _loadMoreFailed = false;
        StateHasChanged();

        ApiResult<PodcastEpisodePage> result;
        try
        {
            result = await Podcasts.ListEpisodesAsync(filter, _batch, cursor, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _generation)
        {
            return;
        }

        _loadingMore = false;
        if (result.IsSuccess && result.Value is { } page)
        {
            var seen = _rows.Select(r => r.Id).ToHashSet();
            var added = page.Items.Where(e => seen.Add(e.Id)).ToList();
            _rows = [.. _rows, .. added];
            _cursor = page.NextCursor;
            RememberStates(added);
            SchedulePoll();
            ScheduleCooldown();
            StateHasChanged();
            if (fromPill && added.Count > 0)
            {
                await InvokeJsAsync("odsNewsFocusCard", PodcastEpisodeCard.CardId(added[0].Id), RootId);
            }

            return;
        }

        if (result.Status == HttpStatusCode.BadRequest
            && (NewsFeedback.Is(result.Problem, NewsErrorCodes.CursorInvalid)
                || (result.Problem?.Errors?.Keys.Any(k => string.Equals(k, "cursor", StringComparison.OrdinalIgnoreCase)) ?? false)))
        {
            await ResetAsync();
            return;
        }

        _loadMoreFailed = true;
        StateHasChanged();
    }

    // ── The detail ─────────────────────────────────────────────────────────────────────────────────

    private async Task OnToggleAsync(PodcastEpisodeSummary episode, bool open)
    {
        _openId = open ? episode.Id : (_openId == episode.Id ? null : _openId);
        if (!open)
        {
            return;
        }

        // Loaded once per expansion: a card reopened after a state change reads it again (§3.2.4).
        _details.Remove(episode.Id);
        await LoadDetailAsync(episode);
    }

    /// <summary>The detail, with "Loading episode…" announced only when it takes longer than a second (§3.2.4).</summary>
    private async Task LoadDetailAsync(PodcastEpisodeSummary episode)
    {
        _details[episode.Id] = new PodcastEpisodeDetailState(NewsBodyPhase.Loading);
        StateHasChanged();
        using var slow = Time.CreateTimer(_ => InvokeAsync(() =>
        {
            if (_details.TryGetValue(episode.Id, out var state) && state.Phase == NewsBodyPhase.Loading)
            {
                _details[episode.Id] = state with { Slow = true };
                StateHasChanged();
            }
        }), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        var result = await Podcasts.GetEpisodeAsync(episode.Id);
        if (_disposed)
        {
            return;
        }

        if (result.IsSuccess && result.Value is { } detail)
        {
            _details[episode.Id] = new PodcastEpisodeDetailState(NewsBodyPhase.Ready, detail);
            Replace(detail);
        }
        else if (result.Status == HttpStatusCode.NotFound)
        {
            RemoveEpisode(episode.Id);
        }
        else
        {
            _details[episode.Id] = new PodcastEpisodeDetailState(
                NewsFeedback.Is(result.Problem, NewsErrorCodes.StorageUnavailable) ? NewsBodyPhase.StorageUnavailable : NewsBodyPhase.Error);
        }

        StateHasChanged();
    }

    /// <summary>Puts a fresher copy of an episode in its row (a detail or a poll result), keeping its place.</summary>
    private void Replace(PodcastEpisodeSummary fresh)
    {
        var index = _rows.FindIndex(r => r.Id == fresh.Id);
        if (index >= 0)
        {
            _rows[index] = fresh;
        }
    }

    /// <summary>An episode the server no longer has: its card goes, and focus moves to the list start (§7).</summary>
    private void RemoveEpisode(Guid id)
    {
        _rows.RemoveAll(r => r.Id == id);
        _ = StopIfGoneAsync();
        _details.Remove(id);
        _alerts.Remove(id);
        if (_openId == id)
        {
            _openId = null;
        }

        Say("This episode is no longer available.");
        _ = InvokeJsAsync("odsNewsRecoverFocus", RootId);
    }
}
