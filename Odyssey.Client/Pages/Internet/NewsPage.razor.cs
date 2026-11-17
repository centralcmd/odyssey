using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>Which empty state the article list shows (§3.7, rows 4–6).</summary>
public enum NewsEmptyKind { Filtered, AllRead, Pending }

public partial class NewsPage : IDisposable
{
    /// <summary>The user-preference key the page's layout and filters persist under (§3.1).</summary>
    public const string PageStateKey = "news-page";

    public const string RootId = "nw-page";

    /// <summary>The search field's one helper element, linked by <c>aria-describedby</c> (§7.4).</summary>
    public const string SearchHelpId = "news-search-help";

    public const string UnreadKey = "unread";

    public const string ReadKey = "read";

    public const string PublishedKey = "published";

    public const string RelevanceKey = "relevance";

    public const string ChangedBackText = "Couldn't update. Changed back.";

    public const string OfferedUndoText = "Marked as read";

    public const string UndoWithdrawnText = "Marked as read — use Mark unread on the article to undo";

    /// <summary>Counts refresh this long after the last read-state write (§10).</summary>
    public static readonly TimeSpan CountsDebounce = TimeSpan.FromSeconds(1);

    /// <summary>How long the "Marked as read" snackbar with Undo stays (§3.2.8: VisibleStateDuration 8000 ms).</summary>
    public static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(8);

    [Inject] private INewsApiClient News { get; set; } = default!;

    [Inject] private INewsCatalogueCache Catalogue { get; set; } = default!;

    [Inject] private NewsStoreGate Gate { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Inject] private IPageStateService PageState { get; set; } = default!;

    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private TimeProvider Time { get; set; } = default!;

    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary><c>?feedId=</c> preselects that one feed and clears the other filters except Status (§3.1).</summary>
    [SupplyParameterFromQuery(Name = "feedId")] public Guid? FeedIdQuery { get; set; }

    /// <summary><c>?tagId=</c> preselects that one tag, the same way.</summary>
    [SupplyParameterFromQuery(Name = "tagId")] public Guid? TagIdQuery { get; set; }

    private static readonly OdsTableSort DefaultSort = new(PublishedKey, OdsSortDirection.Desc);

    private static readonly OdsSortField<NewsArticleSummary> PublishedField =
        new() { Key = PublishedKey, Label = "Published", Type = OdsSortType.Date };

    private static readonly OdsSortField<NewsArticleSummary> RelevanceField =
        new() { Key = RelevanceKey, Label = "Relevance", Type = OdsSortType.Number, DefaultDir = OdsSortDirection.Desc };

    /// <summary>Unread and Read, each with an icon (§3.2.3 row 4).</summary>
    internal static readonly IReadOnlyList<OdsOption> StatusOptions =
    [
        new(UnreadKey, "Unread") { Icon = "mark_email_unread", IconColor = OdsBreakdown.Tone("info") },
        new(ReadKey, "Read") { Icon = "drafts", IconColor = OdsBreakdown.Tone("outline") },
    ];

    private OdsLiveAnnouncer? _announcer;

    // ── Claims ─────────────────────────────────────────────────────────────────────────────────────
    private bool _canUpdate;
    private bool _canCreateFeeds;
    private bool _canReadSettings;
    private bool _canReadFileStorage;

    // ── Reference data ─────────────────────────────────────────────────────────────────────────────
    private List<NewsFeedCatalogueEntry> _catalogue = [];
    private Dictionary<Guid, NewsFeedCatalogueEntry> _feedsById = [];
    private List<ExistingNewsTag> _tags = [];
    private Dictionary<Guid, ExistingNewsTag> _tagsById = [];
    private NewsUnreadCounts? _counts;
    private NewsStatus? _status;
    private int? _lastAttention;

    // ── The list ───────────────────────────────────────────────────────────────────────────────────
    private bool _loading = true;
    private LoadFailure? _failure;
    private int _failureCount;
    private List<NewsArticleSummary> _rows = [];
    private int? _total;
    private bool _totalCapped;
    private string? _cursor;
    private DateTime? _asOf;
    private NewsArticleListFilter? _listFilter;
    private string _appliedSearch = string.Empty;
    private bool _refetching;
    private bool _loadingMore;
    private bool _loadMoreFailed;
    private bool _appendBusyRetried;
    private int _generation;
    private bool _shownFirstPage;
    private object _resetKey = 0;
    private CancellationTokenSource _listCts = new();
    private readonly HashSet<Guid> _justRead = [];
    private readonly Dictionary<Guid, NewsArticleDetailState> _details = [];
    private Guid? _openId;

    private sealed record LoadFailure(bool Busy, TimeSpan? RetryAfter);

    // ── Layout, filters, sort ──────────────────────────────────────────────────────────────────────
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _feedFilter = [];
    private IReadOnlyCollection<string> _tagFilter = [];
    private IReadOnlyCollection<string> _statuses = [UnreadKey];
    private OdsTableSort _sort = DefaultSort;
    private int _batch = OdsPageSizes.Batch[1];
    private Guid? _handledFeedQuery;
    private Guid? _handledTagQuery;
    private bool _initialised;

    // ── Search messages (§7.4) ─────────────────────────────────────────────────────────────────────
    internal enum SearchErrorKind { Unusable, Limited, Busy, GaveUp }

    internal sealed record SearchError(SearchErrorKind Kind, string Text);

    private SearchError? _searchError;
    private string _searchSaid = string.Empty;
    private ITimer? _searchResendTimer;

    private bool StatusIsAll => _statuses.Count != 1;

    private bool StatusIsUnreadOnly => _statuses.Count == 1 && _statuses.Contains(UnreadKey);

    private bool StatusIsReadOnly => _statuses.Count == 1 && _statuses.Contains(ReadKey);

    private bool HasListFilters =>
        !string.IsNullOrWhiteSpace(_appliedSearch) || (_listFilter?.FeedIds?.Count ?? 0) > 0 || (_listFilter?.TagIds?.Count ?? 0) > 0;

    private IReadOnlyList<OdsSortField<NewsArticleSummary>> SortFields =>
        string.IsNullOrWhiteSpace(_search) ? [PublishedField] : [PublishedField, RelevanceField];

    private IReadOnlyList<OdsOption> TagFilterOptions => NewsVisuals.TagFilterOptions(_tags);

    /// <summary>Hide read: only under Status = Unread exactly, with a card in view read in this view (§3.2.8).</summary>
    private bool CanHideRead => StatusIsUnreadOnly && _rows.Any(r => r.IsRead && _justRead.Contains(r.Id));

    internal NewsEmptyKind EmptyKind =>
        _catalogue.Count > 0 && _catalogue.Sum(f => f.ArticleCount) == 0 && _catalogue.Any(f => f.Health == NewsFeedHealth.Pending)
            ? NewsEmptyKind.Pending
            : StatusIsUnreadOnly && !HasListFilters ? NewsEmptyKind.AllRead
            : NewsEmptyKind.Filtered;

    // ── The header ─────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<NewsFeedCatalogueEntry> LiveFeeds => _catalogue.Where(f => f.State != NewsFeedState.Archived);

    private int UnreadTotal => _counts?.Total ?? LiveFeeds.Sum(f => f.UnreadCount);

    private string SubLine => _loading || (_failure is not null && _catalogue.Count == 0)
        ? "News"
        : _catalogue.Count == 0 ? "No feeds yet"
        : $"{UnreadTotal.ToString("N0", CultureInfo.CurrentCulture)} unread · {NewsFeedback.Count(LiveFeeds.Count(), "feed", "feeds")}";

    private IReadOnlyList<OdsBreakdownRow> ArticleRows =>
    [
        new() { Key = UnreadKey, Label = "Unread", Icon = "mark_email_unread", IconColor = OdsBreakdown.Tone("info"), Count = LiveFeeds.Sum(f => f.UnreadCount) },
        new() { Key = ReadKey, Label = "Read", Icon = "drafts", IconColor = OdsBreakdown.Tone("outline"), Count = LiveFeeds.Sum(f => Math.Max(0, f.ArticleCount - f.UnreadCount)) },
    ];

    private IReadOnlyList<OdsBreakdownRow> FeedRows =>
    [
        new() { Key = "ok", Label = "Healthy or pending", Icon = "check_circle", IconColor = OdsBreakdown.Tone("income"),
            Count = _catalogue.Count(f => f.State == NewsFeedState.Active && !OdsNewsFeedHealth.NeedsAttention(f.Health)) },
        new() { Key = "attention", Label = "Need attention", Icon = "error_outline", IconColor = OdsBreakdown.Tone("warning"),
            Count = _catalogue.Count(f => OdsNewsFeedHealth.NeedsAttention(f.Health)) },
        new() { Key = "paused", Label = "Paused", Icon = "pause_circle", IconColor = OdsBreakdown.Tone("outline"),
            Count = _catalogue.Count(f => f.State == NewsFeedState.Paused) },
        new() { Key = "archived", Label = "Archived", Icon = "inventory_2", IconColor = OdsBreakdown.Tone("outline"),
            Count = _catalogue.Count(f => f.State == NewsFeedState.Archived) },
    ];

    /// <summary>
    /// The "Attention" problems (§3.2.2): one row per feed whose health is in <c>NeedsAttention</c> — Error for Gone
    /// and Quarantined, Warning for Failing and QuotaReached — with Review → that feed on Feeds; then, for a
    /// <c>file-storage.read</c> holder, a Warning row per full quota, whose Settings → needs <c>system-settings.read</c>.
    /// </summary>
    internal IReadOnlyCollection<PageHeaderProblem>? Problems
    {
        get
        {
            var rows = new List<PageHeaderProblem>();
            foreach (var feed in _catalogue.Where(f => OdsNewsFeedHealth.NeedsAttention(f.Health))
                         .OrderBy(f => f.DisplayTitle, StringComparer.CurrentCultureIgnoreCase))
            {
                var id = feed.Id;
                rows.Add(new PageHeaderProblem
                {
                    Severity = feed.Health is NewsFeedHealth.Gone or NewsFeedHealth.Quarantined ? PageHeaderSeverity.Error : PageHeaderSeverity.Warning,
                    Lead = feed.DisplayTitle,
                    Message = $"{OdsNewsFeedHealth.Meta(feed.Health).Label}.",
                    ViewLabel = "Review",
                    ActionAriaLabel = $"Review {feed.DisplayTitle}",
                    OnView = EventCallback.Factory.Create(this, () => Nav.NavigateTo($"news-feeds?state=All&reveal={id}")),
                });
            }

            if (_canReadFileStorage && _status is { } status)
            {
                if (status.ImageQuotaReached)
                {
                    rows.Add(QuotaRow("Image storage quota reached.", "image"));
                }

                if (status.TextAndSourceQuotaReached)
                {
                    rows.Add(QuotaRow("Text storage quota reached.", "text"));
                }
            }

            return rows.Count == 0 ? null : rows;
        }
    }

    private PageHeaderProblem QuotaRow(string message, string what)
    {
        var row = new PageHeaderProblem { Severity = PageHeaderSeverity.Warning, Message = message };
        if (_canReadSettings)
        {
            row.ViewLabel = "Settings";
            row.ActionAriaLabel = $"Open Settings for the {what} quota";
            row.OnView = EventCallback.Factory.Create(this, () => Nav.NavigateTo("settings"));
        }

        return row;
    }

    /// <summary>
    /// Every feed, with its state as TEXT in the option name — "(needs attention)", "(paused)" — and archived feeds
    /// last with the archive glyph and "(archived)" (§3.2.3 row 2). The glyph is decorative.
    /// </summary>
    internal IReadOnlyList<OdsOption> FeedOptions =>
    [
        .. _catalogue.OrderBy(f => f.State == NewsFeedState.Archived)
            .ThenBy(f => f.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .Select(f =>
            {
                var archived = f.State == NewsFeedState.Archived;
                var meta = OdsNewsFeedHealth.Meta(f.Health);
                var note = archived ? " (archived)"
                    : OdsNewsFeedHealth.NeedsAttention(f.Health) ? " (needs attention)"
                    : f.State == NewsFeedState.Paused ? " (paused)"
                    : string.Empty;
                return new OdsOption(f.Id.ToString(), f.DisplayTitle + note)
                {
                    Icon = archived ? "archive" : meta.Icon,
                    IconColor = OdsBreakdown.Tone(archived ? "outline" : meta.Tone ?? "outline"),
                };
            }),
    ];

    private string? FeedLang(string feedId) =>
        Guid.TryParse(feedId, out var id) && _feedsById.TryGetValue(id, out var feed) ? NewsArticleCard.LangOf(feed.Language) : null;

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        var user = await Auth.GetUserAsync();
        _canUpdate = user.HasPermission(PermissionClaims.NewsUpdate);
        _canCreateFeeds = user.HasPermission(PermissionClaims.NewsFeedsCreate);
        _canReadSettings = user.HasPermission(PermissionClaims.SystemSettingsRead);
        _canReadFileStorage = user.HasPermission(PermissionClaims.FileStorageRead);

        await PageState.RestoreOrSeedAsync<NewsPageState>(PageStateKey, ApplyPageState, BuildPageState);
        ApplyDeepLink();
        _initialised = true;
        await LoadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        // A later "Show articles from ‹feed›" or ?feedId= link on the same page: the link wins for this visit.
        if (_initialised && (FeedIdQuery != _handledFeedQuery || TagIdQuery != _handledTagQuery))
        {
            ApplyDeepLink();
            DropStaleIds();
            await ResetAsync();
        }
    }

    /// <summary>
    /// A link preselects exactly that one feed or tag, clears the other filters except Status and overrides the saved
    /// page state for this visit (§3.1). It is not persisted.
    /// </summary>
    private void ApplyDeepLink()
    {
        _handledFeedQuery = FeedIdQuery;
        _handledTagQuery = TagIdQuery;
        if (FeedIdQuery is null && TagIdQuery is null)
        {
            return;
        }

        _search = string.Empty;
        _feedFilter = FeedIdQuery is { } feed ? [feed.ToString()] : [];
        _tagFilter = TagIdQuery is { } tag ? [tag.ToString()] : [];
        if (_sort.Key == RelevanceKey)
        {
            _sort = DefaultSort;
        }
    }

    // ── Page state ─────────────────────────────────────────────────────────────────────────────────

    internal sealed class NewsPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> FeedIds { get; set; } = [];
        public List<string> TagIds { get; set; } = [];
        public List<string>? Statuses { get; set; }
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[1];
    }

    private void ApplyPageState(NewsPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _feedFilter = state.FeedIds ?? [];
        _tagFilter = state.TagIds ?? [];
        _statuses = state.Statuses is null ? _statuses : StatusOptions.KnownValues(state.Statuses);
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private NewsPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        FeedIds = [.. _feedFilter],
        TagIds = [.. _tagFilter],
        Statuses = [.. _statuses],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void Persist() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; Persist(); }

    private void OnSearchToggled(bool open) { _searchOpen = open; Persist(); }

    // ── Filters ────────────────────────────────────────────────────────────────────────────────────

    private async Task OnSearchChanged(string value)
    {
        _search = value ?? string.Empty;
        CancelSearchResend();
        if (string.IsNullOrWhiteSpace(_search))
        {
            SetSearchError(null);
            if (_sort.Key == RelevanceKey)
            {
                // Relevance needs a search: clearing it falls back to Published, newest first, and says so (§6.7).
                _sort = DefaultSort;
                Say("Sorted by newest, because the search was cleared");
            }
        }

        await FiltersChangedAsync();
    }

    private void OnSearchKeyUp(KeyboardEventArgs args)
    {
        // After the automatic re-send failed too, Enter in the field sends the search again (§7.4).
        if (args.Key == "Enter" && _searchError?.Kind == SearchErrorKind.GaveUp)
        {
            _ = InvokeAsync(() => ResetAsync());
        }
    }

    private Task OnFeedFilterChanged(IReadOnlyCollection<string> values)
    {
        _feedFilter = [.. (values ?? []).Take(NewsLimits.MaxFilterIds)];
        return FiltersChangedAsync();
    }

    private Task OnTagFilterChanged(IReadOnlyCollection<string> values)
    {
        _tagFilter = [.. (values ?? []).Take(NewsLimits.MaxFilterIds)];
        return FiltersChangedAsync();
    }

    private Task OnStatusesChanged(IReadOnlyCollection<string> values)
    {
        _statuses = values ?? [];
        return FiltersChangedAsync();
    }

    private Task OnSortChanged(OdsTableSort sort)
    {
        _sort = sort;
        return FiltersChangedAsync();
    }

    /// <summary>"Load N at a time" applies from the next append: the cursor does not bind the page size (§3.2.3).</summary>
    private void OnBatchChanged(int size)
    {
        _batch = size;
        Persist();
    }

    private Task FiltersChangedAsync()
    {
        Persist();
        return ResetAsync();
    }

    private Task ClearFiltersAsync()
    {
        _search = string.Empty;
        _feedFilter = [];
        _tagFilter = [];
        _statuses = [UnreadKey];
        SetSearchError(null);
        if (_sort.Key == RelevanceKey)
        {
            _sort = DefaultSort;
        }

        Persist();
        return ResetAsync(focusListStart: true);
    }

    private Task ShowAllArticlesAsync()
    {
        _statuses = [];
        Persist();
        return ResetAsync(focusListStart: true);
    }

    /// <summary>The request filter from the controls: ids OR within a filter, AND across them (§3.2.3).</summary>
    internal NewsArticleListFilter BuildFilter()
    {
        var feeds = NewsFilterIds.Normalize(_feedFilter.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty));
        var tags = NewsFilterIds.Normalize(_tagFilter.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty));
        var search = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim();
        return new NewsArticleListFilter
        {
            FeedIds = feeds.Count == 0 ? null : [.. feeds.Take(NewsLimits.MaxFilterIds)],
            TagIds = tags.Count == 0 ? null : [.. tags.Take(NewsLimits.MaxFilterIds)],
            IsRead = StatusIsUnreadOnly ? false : StatusIsReadOnly ? true : null,
            Search = search,
            SortBy = _sort.Key == RelevanceKey && search is not null ? NewsArticleSortBy.Relevance
                : _sort.Dir == OdsSortDirection.Asc ? NewsArticleSortBy.Oldest
                : NewsArticleSortBy.Newest,
        };
    }

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
        _tagsById = tags.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
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
            result.OrToast(Snackbar, "Couldn't load the unread counts");
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

    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Expand: one card open at a time; the detail loads on the first expand ("Loading article…" said once) and is
    /// kept; an unread card is marked read straight away for a <c>news.update</c> holder (§3.2.8). Focus stays on the
    /// card header.
    /// </summary>
    private async Task OnToggleAsync(NewsArticleSummary article, bool open)
    {
        _openId = open ? article.Id : (_openId == article.Id ? null : _openId);
        if (!open)
        {
            return;
        }

        var tasks = new List<Task>();
        var state = _details.GetValueOrDefault(article.Id);
        if (state is null || (state.Phase is NewsBodyPhase.Error or NewsBodyPhase.StorageUnavailable && !state.Retrying))
        {
            Say("Loading article…");
            tasks.Add(LoadDetailAsync(article));
        }
        else if (state.Detail is { } detail)
        {
            AnnounceBodyState(detail);
        }

        if (!article.IsRead && _canUpdate)
        {
            tasks.Add(WriteReadAsync(article, true, ReadSource.Expand));
        }

        await Task.WhenAll(tasks);
    }

    private async Task LoadDetailAsync(NewsArticleSummary article)
    {
        _details[article.Id] = new NewsArticleDetailState(NewsBodyPhase.Loading);
        StateHasChanged();
        var result = await News.GetArticleAsync(article.Id);
        if (!_rows.Any(r => r.Id == article.Id))
        {
            return;
        }

        var outcome = Gate.Observe(result);
        if (outcome == NewsStoreOutcome.Ok)
        {
            _details[article.Id] = new NewsArticleDetailState(NewsBodyPhase.Ready, result.Value);
            if (_openId == article.Id)
            {
                AnnounceBodyState(result.Value!);
            }
        }
        else if (result.Status == HttpStatusCode.NotFound)
        {
            await RemoveCardAsync(article);
            return;
        }
        else
        {
            _details[article.Id] = new NewsArticleDetailState(
                outcome == NewsStoreOutcome.StorageUnavailable ? NewsBodyPhase.StorageUnavailable : NewsBodyPhase.Error);
        }

        StateHasChanged();
    }

    /// <summary>Body.Unavailable and Body.Omitted are said once by the page announcer as the card expands (§3.2.7).</summary>
    private void AnnounceBodyState(NewsArticleDetail detail)
    {
        if (detail.BodyUnavailable)
        {
            Say(_canReadSettings
                ? $"{NewsArticleCard.UnavailableText} Ask an administrator to check the Background jobs page."
                : NewsArticleCard.UnavailableText);
        }
        else if (detail.BodyOmittedReason is not null)
        {
            Say(NewsArticleCard.OmittedText);
        }
    }

    /// <summary>Retry on Body.Error: pressed before Retry-After allows it, it waits the rest out and says so (§7.3).</summary>
    private async Task RetryDetailAsync(NewsArticleSummary article)
    {
        if (_details.GetValueOrDefault(article.Id) is not { Retrying: false } state)
        {
            return;
        }

        if (Gate.RemainingSeconds is { } wait)
        {
            _details[article.Id] = state with { Retrying = true };
            Say($"Retrying in {wait.ToString(CultureInfo.CurrentCulture)} seconds");
            StateHasChanged();
            await Gate.WaitAsync();
        }

        await LoadDetailAsync(article);
        if (_details.GetValueOrDefault(article.Id)?.Phase == NewsBodyPhase.Ready)
        {
            // Retry is gone with the error: focus back on the card header rather than <body>.
            await InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(article.Id), RootId);
        }
    }

    private async Task RemoveCardAsync(NewsArticleSummary article)
    {
        var next = Neighbour(article.Id);
        _rows = [.. _rows.Where(r => r.Id != article.Id)];
        _details.Remove(article.Id);
        if (_openId == article.Id)
        {
            _openId = null;
        }

        Say(NewsFeedback.Gone(Snackbar));
        StateHasChanged();
        await (next is { } id
            ? InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(id), RootId)
            : InvokeJsAsync("odsNewsFocusListStart", RootId));
    }

    private Guid? Neighbour(Guid id)
    {
        var index = _rows.FindIndex(r => r.Id == id);
        return index < 0 ? null : index + 1 < _rows.Count ? _rows[index + 1].Id : index > 0 ? _rows[index - 1].Id : null;
    }

    // ── The ⋯ menu (§3.2.10) ───────────────────────────────────────────────────────────────────────

    /// <summary>Mark read / Mark unread · Tags… (both <c>news.update</c>) · Show articles from ‹feed›.</summary>
    internal IReadOnlyList<OdsMenuItem> MenuFor(NewsArticleSummary article)
    {
        var title = NewsArticleCard.TitleOf(article);
        var items = new List<OdsMenuItem>();
        if (_canUpdate)
        {
            items.Add(article.IsRead
                ? Item("mark_email_unread", "Mark unread", title, () => WriteReadAsync(article, false, ReadSource.Menu))
                : Item("mark_email_read", "Mark read", title, () => WriteReadAsync(article, true, ReadSource.Menu)));
            items.Add(Item("local_offer", "Tags…", title, () => OpenTags(article)));
        }

        var feedLabel = $"Show articles from {article.FeedTitle}";
        items.Add(new OdsMenuItem
        {
            Icon = "rss_feed",
            Label = feedLabel,
            AriaLabel = feedLabel,
            OnClick = EventCallback.Factory.Create(this, () => ShowFeedAsync(article.FeedId)),
        });
        return items;
    }

    private OdsMenuItem Item(string icon, string label, string title, Func<Task> action) => new()
    {
        Icon = icon,
        Label = label,
        AriaLabel = $"{label} {title}",
        OnClick = EventCallback.Factory.Create(this, action),
    };

    private OdsMenuItem Item(string icon, string label, string title, Action action) => new()
    {
        Icon = icon,
        Label = label,
        AriaLabel = $"{label} {title}",
        OnClick = EventCallback.Factory.Create(this, action),
    };

    /// <summary>Show articles from ‹feed›: the deep link, applied here and written to the URL (§3.1).</summary>
    private Task ShowFeedAsync(Guid feedId)
    {
        _handledFeedQuery = feedId;
        _handledTagQuery = null;
        _search = string.Empty;
        _feedFilter = [feedId.ToString()];
        _tagFilter = [];
        if (_sort.Key == RelevanceKey)
        {
            _sort = DefaultSort;
        }

        Nav.NavigateTo($"news?feedId={feedId}");
        return ResetAsync();
    }

    // ── Read state (§3.2.8, §10) ───────────────────────────────────────────────────────────────────

    internal enum ReadSource { Expand, Menu, Undo }

    private readonly Dictionary<Guid, bool> _wanted = [];
    private readonly HashSet<Guid> _writing = [];
    private readonly Dictionary<Guid, (bool IsRead, DateTime? ReadAt)> _confirmed = [];
    private ITimer? _countsTimer;
    private Snackbar? _undoSnackbar;

    /// <summary>
    /// An optimistic read-state write: the chip and the counts change at once; writes are serialised per article and
    /// the last one wins; a failure rolls the article and the counts back and says "Couldn't update. Changed back."
    /// without moving focus. Counts are re-read a second after the last write.
    /// </summary>
    private async Task WriteReadAsync(NewsArticleSummary article, bool isRead, ReadSource source)
    {
        var id = article.Id;
        if (!_confirmed.ContainsKey(id))
        {
            _confirmed[id] = (article.IsRead, article.ReadAtUtc);
        }

        ApplyLocally(article, isRead, isRead ? Time.GetUtcNow().UtcDateTime : null);
        if (isRead)
        {
            _justRead.Add(id);
        }

        _wanted[id] = isRead;
        switch (source)
        {
            case ReadSource.Expand:
                OfferUndo(article);
                Say(NewsFeedback.Spoken(OfferedUndoText));
                break;
            default:
                Say(isRead ? "Marked as read" : "Marked as unread");
                break;
        }

        StateHasChanged();
        if (!_writing.Add(id))
        {
            return; // the write in flight picks the new wish up when it returns
        }

        try
        {
            while (true)
            {
                var want = _wanted[id];
                var result = await News.SetReadAsync(id, want);
                if (!result.IsSuccess)
                {
                    if (result.Status == HttpStatusCode.NotFound)
                    {
                        await RemoveCardAsync(article);
                        return;
                    }

                    var (confirmedRead, confirmedAt) = _confirmed[id];
                    ApplyLocally(article, confirmedRead, confirmedAt);
                    var text = NewsFeedback.IsBusy(result.Problem) ? NewsFeedback.BusyText : ChangedBackText;
                    Snackbar.Add(text, Severity.Error, config => config.RequireInteraction = true);
                    Say(NewsFeedback.Spoken(text));
                    StateHasChanged();
                    return;
                }

                _confirmed[id] = (want, article.ReadAtUtc);
                if (_wanted[id] == want)
                {
                    return;
                }
            }
        }
        finally
        {
            _writing.Remove(id);
            _confirmed.Remove(id);
            ScheduleCountsRefresh();
        }
    }

    private void ApplyLocally(NewsArticleSummary article, bool isRead, DateTime? readAt)
    {
        if (article.IsRead != isRead)
        {
            AdjustCounts(article, isRead ? -1 : 1);
        }

        article.IsRead = isRead;
        article.ReadAtUtc = isRead ? readAt : null;
    }

    /// <summary>Moves the unread figures by one for an optimistic change; never below 0.</summary>
    private void AdjustCounts(NewsArticleSummary article, int delta)
    {
        if (_counts is { } counts)
        {
            counts.Total = Math.Max(0, counts.Total + delta);
            foreach (var feed in counts.ByFeed.Where(f => f.FeedId == article.FeedId))
            {
                feed.UnreadCount = Math.Max(0, feed.UnreadCount + delta);
            }

            var tags = article.TagIds.Concat(article.FeedTagIds).ToHashSet();
            foreach (var tag in counts.ByTag.Where(t => tags.Contains(t.TagId)))
            {
                tag.UnreadCount = Math.Max(0, tag.UnreadCount + delta);
            }
        }

        if (_feedsById.TryGetValue(article.FeedId, out var entry))
        {
            entry.UnreadCount = Math.Max(0, entry.UnreadCount + delta);
        }
    }

    private void ScheduleCountsRefresh()
    {
        _countsTimer?.Dispose();
        _countsTimer = Time.CreateTimer(_ => _ = InvokeAsync(async () =>
        {
            SetCounts(await News.GetUnreadCountsAsync());
            StateHasChanged();
        }), null, CountsDebounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>One "Marked as read" snackbar with Undo for 8 s; the newest replaces any earlier one (§3.2.8).</summary>
    private void OfferUndo(NewsArticleSummary article)
    {
        if (_undoSnackbar is { } previous)
        {
            Snackbar.Remove(previous);
        }

        var id = article.Id;
        _undoSnackbar = Snackbar.Add(OfferedUndoText, Severity.Normal, config =>
        {
            config.VisibleStateDuration = (int)UndoDuration.TotalMilliseconds;
            config.Action = "Undo";
            config.ActionColor = Color.Primary;
            config.OnClick = _ => InvokeAsync(() => UndoAsync(id));
        });
    }

    private async Task UndoAsync(Guid id)
    {
        _undoSnackbar = null;
        if (_rows.FirstOrDefault(r => r.Id == id) is not { } article)
        {
            return;
        }

        await WriteReadAsync(article, false, ReadSource.Undo);
        await InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(id), RootId);
    }

    /// <summary>
    /// Undo is withdrawn once its card is off screen (§3.2.8, AC 30): after Hide read or any fresh first page the
    /// snackbar loses its action and reads the Mark-unread sentence.
    /// </summary>
    private void WithdrawUndo()
    {
        if (_undoSnackbar is not { } undo)
        {
            return;
        }

        _undoSnackbar = null;
        if (Snackbar.ShownSnackbars.Contains(undo))
        {
            Snackbar.Remove(undo);
            Snackbar.Add(UndoWithdrawnText, Severity.Normal, config => config.VisibleStateDuration = (int)UndoDuration.TotalMilliseconds);
        }
    }

    /// <summary>Hide read: a fresh first page with ONE combined announcement, then the visible target (§3.2.8).</summary>
    private Task HideReadAsync()
    {
        var hidden = _rows.Count(r => r.IsRead && _justRead.Contains(r.Id));
        return ResetAsync($"{NewsFeedback.Count(hidden, "read article", "read articles")} hidden.", focusListStart: true);
    }

    // ── Earlier versions ───────────────────────────────────────────────────────────────────────────

    private NewsArticleSummary? _versionsArticle;
    private Guid _versionsKey;
    private bool _versionsOpen;

    private void OpenVersions(NewsArticleSummary article)
    {
        _versionsArticle = article;
        _versionsKey = Guid.NewGuid();
        _versionsOpen = true;
    }

    private void OnVersionsOpenChanged(bool open)
    {
        _versionsOpen = open;
        if (!open)
        {
            _versionsArticle = null;
        }
    }

    // ── Tags (§3.2.11) ─────────────────────────────────────────────────────────────────────────────

    private NewsArticleSummary? _tagsArticle;
    private Guid _tagsKey;
    private bool _tagsOpen;
    private IReadOnlyCollection<string> _tagsDraft = [];
    private string? _tagsError;

    private IEnumerable<ExistingNewsTag> InheritedTagsOf(NewsArticleSummary article) =>
        article.FeedTagIds.Select(id => _tagsById.GetValueOrDefault(id)).OfType<ExistingNewsTag>();

    private void OpenTags(NewsArticleSummary article)
    {
        if (!_canUpdate)
        {
            return;
        }

        _tagsArticle = article;
        _tagsDraft = [.. article.TagIds.Select(id => id.ToString())];
        _tagsError = null;
        _tagsKey = Guid.NewGuid();
        _tagsOpen = true;
    }

    private void OnTagsOpenChanged(bool open)
    {
        _tagsOpen = open;
        if (!open)
        {
            _tagsArticle = null;
        }
    }

    /// <summary>One <c>PUT …/tags</c> per Save — removing a single own tag included; never a per-tag DELETE.</summary>
    private async Task<bool> SaveArticleTagsAsync()
    {
        if (_tagsArticle is not { } article)
        {
            return false;
        }

        if (_tagsDraft.Count > NewsLimits.MaxTagIdsPerRequest)
        {
            _tagsError = $"Choose at most {NewsLimits.MaxTagIdsPerRequest} tags.";
            return false;
        }

        var ids = _tagsDraft.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var result = await News.SetArticleTagsAsync(article.Id, ids);
        if (result.IsSuccess)
        {
            article.TagIds = ids;
            Say($"Tags saved for {NewsArticleCard.TitleOf(article)}");
            _tagsArticle = null;
            StateHasChanged();
            await InvokeJsAsync("odsNewsFocusCard", NewsArticleCard.CardId(article.Id), RootId);
            return true;
        }

        if (NewsFeedback.Is(result.Problem, NewsErrorCodes.TagNotUsable) || NewsFeedback.Is(result.Problem, NewsErrorCodes.TagArchived))
        {
            _tagsError = "One of these tags is archived.";
            return false;
        }

        if (result.Status == HttpStatusCode.NotFound)
        {
            _tagsOpen = false;
            _tagsArticle = null;
            await RemoveCardAsync(article);
            return true;
        }

        Say(NewsFeedback.Failure(Snackbar, result.Problem, "Couldn't save the tags"));
        return false;
    }

    // ── Mark all read (§3.2.9) ─────────────────────────────────────────────────────────────────────

    private Guid _markAllKey;
    private bool _markAllOpen;
    private bool _marking;
    private bool _markAllOutOfDate;
    private string _markAllProgress = string.Empty;
    private int? _markAllCount;
    private NewsMarkAllRun? _run;

    private void OpenMarkAll()
    {
        if (!_canUpdate || _listFilter is null)
        {
            return;
        }

        _markAllCount = MarkAllCount();
        _markAllOutOfDate = false;
        _markAllProgress = string.Empty;
        _markAllKey = Guid.NewGuid();
        _markAllOpen = true;
    }

    /// <summary>
    /// The exact count, shown only when one is known (§3.2.9, AC 31): Status is Unread, no search, the total is not
    /// capped, and the selection is no feed and no tag (the overall total), exactly one feed alone, or exactly one tag
    /// alone. A feed or tag the counts omit — an archived feed — shows no number rather than 0.
    /// </summary>
    internal int? MarkAllCount()
    {
        if (_listFilter is not { } filter || filter.IsRead != false || filter.Search is not null || _totalCapped || _counts is not { } counts)
        {
            return null;
        }

        var feeds = filter.FeedIds?.Count ?? 0;
        var tags = filter.TagIds?.Count ?? 0;
        return (feeds, tags) switch
        {
            (0, 0) => counts.Total,
            (1, 0) => counts.ByFeed.FirstOrDefault(f => f.FeedId == filter.FeedIds!.First())?.UnreadCount,
            (0, 1) => counts.ByTag.FirstOrDefault(t => t.TagId == filter.TagIds!.First())?.UnreadCount,
            _ => null,
        };
    }

    /// <summary>"Mark 42 unread articles in ‹Example News› as read?" — the scope named in words, names emphasised.</summary>
    private RenderFragment MarkAllSentence => builder =>
    {
        var filter = _listFilter ?? new NewsArticleListFilter();
        var feeds = (filter.FeedIds ?? []).Select(id => _feedsById.GetValueOrDefault(id)).OfType<NewsFeedCatalogueEntry>().ToList();
        var tags = (filter.TagIds ?? []).Select(id => _tagsById.GetValueOrDefault(id)?.Name).OfType<string>().ToList();
        var seq = 0;

        void Text(string text) => builder.AddContent(seq++, text);

        void Em(string text, string? lang)
        {
            builder.OpenElement(seq++, "em");
            builder.AddAttribute(seq++, "lang", lang);
            builder.OpenElement(seq++, "bdi");
            builder.AddContent(seq++, text);
            builder.CloseElement();
            builder.CloseElement();
        }

        Text(_markAllCount is { } count
            ? $"Mark {count.ToString("N0", CultureInfo.CurrentCulture)} unread {(count == 1 ? "article" : "articles")} "
            : "Mark all unread articles ");

        if (feeds.Count == 0 && tags.Count == 0)
        {
            Text("in all feeds");
        }
        else if (feeds.Count == 1)
        {
            Text("in ");
            Em(feeds[0].DisplayTitle, NewsArticleCard.LangOf(feeds[0].Language));
        }
        else if (feeds.Count > 1 && tags.Count > 0)
        {
            Text($"from {feeds.Count.ToString("N0", CultureInfo.CurrentCulture)} feeds");
        }
        else if (feeds.Count > 1)
        {
            Text("in ");
            Em(feeds[0].DisplayTitle, NewsArticleCard.LangOf(feeds[0].Language));
            Text($" and {NewsFeedback.Count(feeds.Count - 1, "other feed", "other feeds")}");
        }

        if (tags.Count > 0)
        {
            Text(feeds.Count > 1 ? ", tagged " : feeds.Count == 1 ? " tagged " : "tagged ");
            for (var i = 0; i < tags.Count; i++)
            {
                if (i > 0)
                {
                    Text(i == tags.Count - 1 ? " or " : ", ");
                }

                Em(tags[i], null);
            }
        }

        if (filter.Search is { } search)
        {
            Text(" matching ‘");
            builder.OpenElement(seq++, "bdi");
            builder.AddContent(seq++, search);
            builder.CloseElement();
            Text("’");
        }

        Text(" as read?");
    };

    private async Task MarkAllAsync()
    {
        if (_marking || _markAllOutOfDate || _listFilter is not { } filter || _asOf is not { } asOf)
        {
            return;
        }

        _marking = true;
        var run = _run = new NewsMarkAllRun(News, Time);
        StateHasChanged();
        var result = await run.RunAsync(filter, asOf, total =>
        {
            _markAllProgress = $"Marking… {total.ToString("N0", CultureInfo.CurrentCulture)} so far";
            StateHasChanged();
            return Task.CompletedTask;
        });
        _marking = false;
        _run = null;
        _markAllProgress = $"Marking… {result.Total.ToString("N0", CultureInfo.CurrentCulture)} so far";

        if (result.End == NewsMarkAllEnd.OutOfDate)
        {
            _markAllOutOfDate = true;
            StateHasChanged();
            return;
        }

        _markAllOpen = false;
        var marked = $"{NewsFeedback.Count(result.Total, "article", "articles")} marked as read.";
        switch (result.End)
        {
            case NewsMarkAllEnd.Failed:
                Say(NewsFeedback.Failure(Snackbar, result.Problem, "Couldn't mark the articles as read"));
                StateHasChanged();
                await InvokeJsAsync("odsNewsFocusListStart", RootId);
                return;
            case NewsMarkAllEnd.Finished:
                Say(NewsFeedback.Success(Snackbar, marked));
                break;
            case NewsMarkAllEnd.Stopped:
                var stopped = $"{marked} Stopped before finishing.";
                Snackbar.Add(stopped, Severity.Info, config => config.VisibleStateDuration = (int)UndoDuration.TotalMilliseconds);
                Say(NewsFeedback.Spoken(stopped));
                break;
            default:
                const string partial = "Some articles were marked as read; try again to finish.";
                Snackbar.Add(partial, Severity.Error, config => config.RequireInteraction = true);
                Say(NewsFeedback.Spoken(partial));
                break;
        }

        await RefreshAfterMarkingAsync();
        await ResetAsync(focusListStart: true);
    }

    private async Task RefreshAfterMarkingAsync()
    {
        Catalogue.Invalidate();
        var catalogueTask = Catalogue.GetCatalogueAsync();
        SetCounts(await News.GetUnreadCountsAsync());
        var catalogue = await catalogueTask;
        if (catalogue.IsSuccess && catalogue.Value is { } entries)
        {
            SetCatalogue(entries);
        }
    }

    /// <summary>Cancel and Escape stay available while marking: they stop further calls after the one in flight.</summary>
    private void CancelMarkAll()
    {
        _run?.Stop();
        _markAllOpen = false;
    }

    private void OnMarkAllOpenChanged(bool open)
    {
        if (!open)
        {
            CancelMarkAll();
        }
    }

    // Escape and the scrim already raise OpenChanged; OnClose is set only so the modal offers them.
    private static void MarkAllClosed()
    {
    }

    private async Task RefreshAfterOutOfDateAsync()
    {
        _markAllOpen = false;
        await RefreshAfterMarkingAsync();
        await ResetAsync(focusListStart: true);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private readonly List<string> _unsaid = [];

    /// <summary>Says <paramref name="text"/> once through the page's one announcer — held until it has rendered.</summary>
    private void Say(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_announcer is null)
        {
            _unsaid.Add(text);
            return;
        }

        _ = _announcer.Announce(text);
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (_announcer is not null && _unsaid.Count > 0)
        {
            var held = _unsaid.ToList();
            _unsaid.Clear();
            foreach (var text in held)
            {
                _ = _announcer.Announce(text);
            }
        }
    }

    private async Task InvokeJsAsync(string identifier, params object?[] args)
    {
        try
        {
            await JS.InvokeVoidAsync(identifier, args);
        }
        catch (JSException)
        {
            // Not rendered (a concurrent re-render): only the focus courtesy is lost.
        }
    }

    public void Dispose()
    {
        _listCts.Cancel();
        _listCts.Dispose();
        _searchResendTimer?.Dispose();
        _countsTimer?.Dispose();
        _run?.Stop();
    }
}
