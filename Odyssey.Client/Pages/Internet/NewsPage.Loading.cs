using System.Globalization;
using System.Net;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class NewsPage
{
    // ── Loading ────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        var catalogueTask = Catalogue.GetCatalogueAsync();
        var tagsTask = Catalogue.GetTagsAsync();
        var countsTask = News.GetUnreadCountsAsync();
        var statusTask = _canReadFileStorage ? News.GetStatusAsync() : null;

        var catalogue = await catalogueTask;
        if (!catalogue.IsSuccess)
        {
            // The catalogue is part of the list's first load: without it no stale id can be dropped (§3.1).
            _loading = false;
            _failure = new LoadFailure(NewsFeedback.IsBusy(catalogue.Problem), catalogue.RetryAfter);
            _failureCount++;
            StateHasChanged();
            return;
        }

        SetCatalogue(catalogue.Value ?? []);
        SetTags((await tagsTask).ItemsOrToast(Snackbar, "news tags"));
        SetCounts(await countsTask);
        if (statusTask is not null)
        {
            var status = await statusTask;
            _status = status.IsSuccess ? status.Value : null;
        }

        DropStaleIds();
        if (_catalogue.Count == 0)
        {
            _loading = false;
            _failure = null;
            StateHasChanged();
            return;
        }

        await ResetAsync();
    }

    private void SetCatalogue(List<NewsFeedCatalogueEntry> catalogue)
    {
        _catalogue = catalogue;
        _feedsById = catalogue.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());

        // A change in the attention count after the first load is said once (§3.2.2); the first load says nothing.
        var attention = catalogue.Count(f => OdsNewsFeedHealth.NeedsAttention(f.Health));
        if (_lastAttention is { } previous && previous != attention)
        {
            Say(attention == 0 ? "No feeds need attention" : $"{NewsFeedback.Count(attention, "feed needs", "feeds need")} attention");
        }

        _lastAttention = attention;
    }

    private void SetTags(List<ExistingNewsTag> tags)
    {
        _tags = tags;
        _tagsById = tags.GroupBy(t => t.NewsTagId).ToDictionary(g => g.Key, g => g.First());
    }

    private void SetCounts(ApiResult<NewsUnreadCounts> result)
    {
        if (result.IsSuccess)
        {
            _counts = result.Value;
        }
        else if (!NewsFeedback.IsBusy(result.Problem))
        {
            // A busy count read keeps the catalogue's figures and is not worth a snackbar; anything else is said.
            result.OrToast(Snackbar, "Couldn’t load the unread counts");
        }
    }

    /// <summary>Restored or linked ids that are not in the catalogue or the tag list are dropped before the load (§3.1).</summary>
    private void DropStaleIds()
    {
        if (_catalogue.Count > 0 || _feedFilter.Count > 0)
        {
            _feedFilter = [.. _feedFilter.Where(v => Guid.TryParse(v, out var id) && _feedsById.ContainsKey(id))];
        }

        _tagFilter = [.. _tagFilter.Where(v => Guid.TryParse(v, out var id) && _tagsById.ContainsKey(id))];
    }

    private async Task RetryLoadAsync(bool retryHadFocus)
    {
        _failure = null;
        if (_catalogue.Count == 0)
        {
            await LoadAsync();
        }
        else
        {
            await ResetAsync();
        }

        if (_failure is null && retryHadFocus)
        {
            await InvokeJsAsync("odsNewsFocusListStart", RootId);
        }
    }

    /// <summary>
    /// A fresh first page (§3.2.4): on any filter, sort or search change, Hide read, Refresh and after Mark all read.
    /// The request in flight is cancelled and its generation retired, so a late response is dropped; the old rows
    /// stay under the refetch bar until the new page arrives; an open Undo is withdrawn.
    /// </summary>
    internal async Task ResetAsync(string? announcePrefix = null, bool focusListStart = false, bool searchResend = false)
    {
        WithdrawUndo();
        _listCts.Cancel();
        _listCts.Dispose();
        var cts = _listCts = new CancellationTokenSource();
        var generation = ++_generation;
        _loadingMore = false;
        _loadMoreFailed = false;
        _appendBusyRetried = false;
        _refetching = !_loading && _rows.Count > 0;
        var filter = BuildFilter();
        StateHasChanged();

        ApiResult<NewsArticlePage> result;
        try
        {
            result = await News.ListArticlesAsync(filter, _batch, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _generation)
        {
            return; // superseded by a newer reset (AC 49)
        }

        _refetching = false;
        _loading = false;
        if (result.IsSuccess && result.Value is { } page)
        {
            await ApplyFirstPageAsync(page, filter, generation, announcePrefix, focusListStart);
            return;
        }

        if (filter.Search is not null && SearchErrorOf(result, searchResend) is { } searchError)
        {
            // A search failure is the search field's message, never the list's error state (§7.4); the rows stay.
            SetSearchError(searchError);
            if (searchError.Kind is SearchErrorKind.Limited or SearchErrorKind.Busy)
            {
                ScheduleSearchResend(filter.Search, result.RetryAfter);
            }

            StateHasChanged();
            return;
        }

        _failure = new LoadFailure(NewsFeedback.IsBusy(result.Problem), result.RetryAfter);
        _failureCount++;
        StateHasChanged();
        await InvokeJsAsync("odsNewsRecoverFocus", RootId);
    }

    private async Task ApplyFirstPageAsync(NewsArticlePage page, NewsArticleListFilter filter, int generation, string? announcePrefix, bool focusListStart)
    {
        var seen = new HashSet<Guid>();
        _rows = [.. page.Items.Where(a => seen.Add(a.Id))];
        _total = page.TotalCount;
        _totalCapped = page.TotalCountCapped == true;
        _cursor = page.NextCursor;
        _asOf = page.AsOfUtc;
        _listFilter = filter;
        _appliedSearch = filter.Search ?? string.Empty;
        _resetKey = generation;
        _justRead.Clear();
        _failure = null;
        SetSearchError(null);
        foreach (var gone in _details.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _details.Remove(gone);
        }

        if (_openId is { } open && !seen.Contains(open))
        {
            _openId = null;
        }

        var sentence = CountSentence();
        Say(announcePrefix is null ? sentence : $"{announcePrefix} {sentence}");
        StateHasChanged();
        if (!_shownFirstPage && !focusListStart)
        {
            // The page's first list: focus stays where route navigation put it, on the h1 (§6.6).
            _shownFirstPage = true;
            return;
        }

        _shownFirstPage = true;
        await InvokeJsAsync("odsNewsScrollListTop", RootId);
        await InvokeJsAsync(focusListStart ? "odsNewsFocusListStart" : "odsNewsRecoverFocus", RootId);
    }

    /// <summary>"1,234 articles", "More than 10,000 articles" or "No articles match these filters." (§6.7).</summary>
    internal string CountSentence()
    {
        if (_rows.Count == 0)
        {
            return EmptyKind switch
            {
                NewsEmptyKind.AllRead => "You’re all caught up.",
                NewsEmptyKind.Pending => "Your feeds haven’t been fetched yet.",
                _ => "No articles match these filters.",
            };
        }

        var total = _total ?? _rows.Count;
        return _totalCapped
            ? $"More than {total.ToString("N0", CultureInfo.CurrentCulture)} articles"
            : NewsFeedback.Count(total, "article", "articles");
    }

    /// <summary>
    /// The next page by <c>nextCursor</c> (§3.2.4, §4.4). Never concurrent; a response from an older generation is
    /// dropped; duplicates are removed by id; a <c>400</c> keyed <c>cursor</c> restarts from a fresh first page; a
    /// pill press moves focus to the first new card's header.
    /// </summary>
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

        ApiResult<NewsArticlePage> result;
        try
        {
            result = await News.ListArticlesAsync(filter, _batch, cursor, token);
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
            _appendBusyRetried = false;
            var seen = _rows.Select(r => r.Id).ToHashSet();
            var added = page.Items.Where(a => seen.Add(a.Id)).ToList();
            _rows = [.. _rows, .. added];
            _cursor = page.NextCursor;
            StateHasChanged();
            if (fromPill && added.Count > 0)
            {
                await InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(added[0].Id), RootId);
            }

            return;
        }

        if (IsCursorRejected(result))
        {
            await ResetAsync();
            return;
        }

        _loadMoreFailed = true;
        StateHasChanged();
        if (NewsFeedback.IsBusy(result.Problem) && !_appendBusyRetried)
        {
            // The pill is the region (§7.1): one automatic retry once Retry-After has passed.
            _appendBusyRetried = true;
            await Task.Delay(result.RetryAfter ?? NewsLoadProblem.DefaultBusyWait, Time);
            if (generation == _generation && _loadMoreFailed && !_loadingMore)
            {
                await LoadMoreAsync(fromPill: false);
            }
        }
    }

    private static bool IsCursorRejected(ApiResult<NewsArticlePage> result) =>
        result.Status == HttpStatusCode.BadRequest
        && (NewsFeedback.Is(result.Problem, NewsErrorCodes.CursorInvalid)
            || (result.Problem?.Errors?.Keys.Any(k => string.Equals(k, "cursor", StringComparison.OrdinalIgnoreCase)) ?? false));

    // ── Search messages ────────────────────────────────────────────────────────────────────────────

    private static SearchError? SearchErrorOf(ApiResult<NewsArticlePage> result, bool wasResend)
    {
        if (result.Status == HttpStatusCode.BadRequest
            && (NewsFeedback.Is(result.Problem, NewsErrorCodes.SearchUnusable)
                || (result.Problem?.Errors?.Keys.Any(k => string.Equals(k, "search", StringComparison.OrdinalIgnoreCase)) ?? false)))
        {
            return new SearchError(SearchErrorKind.Unusable, $"Use words of at least {NewsLimits.MinSearchTermLength} letters.");
        }

        var limited = result.Status == HttpStatusCode.TooManyRequests || NewsFeedback.Is(result.Problem, NewsErrorCodes.SearchLimited);
        var busy = NewsFeedback.IsBusy(result.Problem);
        if (!limited && !busy)
        {
            return null;
        }

        return wasResend
            ? new SearchError(SearchErrorKind.GaveUp, "Search didn’t run. Press Enter to try again.")
            : limited
                ? new SearchError(SearchErrorKind.Limited, "Too many searches. Searching again in a moment.")
                : new SearchError(SearchErrorKind.Busy, "Search is busy. Searching again in a moment.");
    }

    /// <summary>Sets the helper line; a new or changed message is announced once by its status region (§7.4).</summary>
    private void SetSearchError(SearchError? error)
    {
        var previous = _searchError?.Text;
        _searchError = error;
        if (error is not null && error.Text != previous)
        {
            _searchSaid = error.Text;
        }
        else if (error is null)
        {
            _searchSaid = string.Empty;
        }
    }

    private void ScheduleSearchResend(string query, TimeSpan? retryAfter)
    {
        CancelSearchResend();
        _searchResendTimer = Time.CreateTimer(_ => _ = InvokeAsync(async () =>
        {
            CancelSearchResend();
            // Only when the query is unchanged: a new query is its own search.
            if (string.Equals(_search.Trim(), query, StringComparison.Ordinal))
            {
                await ResetAsync(searchResend: true);
            }
        }), null, retryAfter ?? NewsLoadProblem.DefaultBusyWait, Timeout.InfiniteTimeSpan);
    }

    private void CancelSearchResend()
    {
        _searchResendTimer?.Dispose();
        _searchResendTimer = null;
    }
}
