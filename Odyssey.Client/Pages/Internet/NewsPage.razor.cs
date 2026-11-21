using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
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

    /// <summary>Read articles the publisher changed after they were read: a subset of Read.</summary>
    public const string UpdatedKey = "updated";

    /// <summary>The header's ⋯ "News actions" menu; the Mark all read dialog returns focus here.</summary>
    public const string ActionMenuId = "nw-actions";

    public const string LinkCopiedText = "Link copied";


    public const string PublishedKey = "published";

    public const string RelevanceKey = "relevance";

    public const string ChangedBackText = "Couldn’t update. Changed back.";

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

    [Inject] private IClipboardService Clipboard { get; set; } = default!;

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

    /// <summary>Unread, Read and Updated (read, then changed by the publisher), each with an icon (§3.2.3 row 4).</summary>
    internal static readonly IReadOnlyList<OdsOption> StatusOptions =
    [
        new(UnreadKey, "Unread") { Icon = "mark_email_unread", IconColor = OdsBreakdown.Tone("info") },
        new(ReadKey, "Read") { Icon = "drafts", IconColor = OdsBreakdown.Tone("outline") },
        new(UpdatedKey, "Updated") { Icon = "update", IconColor = OdsBreakdown.Tone("warning") },
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
    private Guid? _rereadId;

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

    /// <summary>Nothing in view can be unread, so Mark all read is not offered.</summary>
    private bool StatusExcludesUnread => _statuses.Count > 0 && !_statuses.Contains(UnreadKey);

    private bool HasListFilters =>
        !string.IsNullOrWhiteSpace(_appliedSearch) || (_listFilter?.FeedIds?.Count ?? 0) > 0 || (_listFilter?.TagIds?.Count ?? 0) > 0;

    private IReadOnlyList<OdsSortField<NewsArticleSummary>> SortFields =>
        string.IsNullOrWhiteSpace(_search) ? [PublishedField] : [PublishedField, RelevanceField];

    private IReadOnlyList<OdsOption> TagFilterOptions => NewsVisuals.TagFilterOptions(_tags);

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
        new() { Key = ReadKey, Label = "Read", Icon = "drafts", IconColor = OdsBreakdown.Tone("outline"), Count = ReadTotal },
        // A subset of Read, so the tile's total is Unread + Read (ArticleTotalText), not the rows' sum.
        new() { Key = UpdatedKey, Label = "Updated", Icon = "update", IconColor = OdsBreakdown.Tone("warning"), Count = LiveFeeds.Sum(f => f.UpdatedCount) },
    ];

    private int ReadTotal => LiveFeeds.Sum(f => Math.Max(0, f.ArticleCount - f.UnreadCount));

    private string ArticleTotalText => (LiveFeeds.Sum(f => f.UnreadCount) + ReadTotal).ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The header's ⋯ menu offers Mark all read… to a news.update holder with feeds, while something unread is in view.</summary>
    private bool CanMarkAll => _canUpdate && _catalogue.Count > 0 && !StatusExcludesUnread;

    private IReadOnlyList<OdsBreakdownRow> FeedRows =>
    [
        new() { Key = "ok", Label = "Healthy or pending", Icon = "check_circle", IconColor = OdsBreakdown.Tone("income"),
            Count = _catalogue.Count(f => f.State == NewsFeedState.Active && !OdsNewsFeedHealth.NeedsAttention(f.Health)) },
        new() { Key = "attention", Label = "Need attention", Icon = "error_outline", IconColor = OdsBreakdown.Tone("warning"),
            Count = _catalogue.Count(f => OdsNewsFeedHealth.NeedsAttention(f.Health)) },
        new() { Key = "paused", Label = "Paused", Icon = "pause_circle", IconColor = OdsBreakdown.Tone("pending"),
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
            Statuses = StatusFilter(_statuses),
            Search = search,
            SortBy = _sort.Key == RelevanceKey && search is not null ? NewsArticleSortBy.Relevance
                : _sort.Dir == OdsSortDirection.Asc ? NewsArticleSortBy.Oldest
                : NewsArticleSortBy.Newest,
        };
    }

    /// <summary>The Status members as the API's enum; null when none is ticked (every article).</summary>
    internal static IReadOnlyCollection<NewsArticleReadStatus>? StatusFilter(IReadOnlyCollection<string> statuses)
    {
        var members = statuses.Select(s => s switch
        {
            UnreadKey => NewsArticleReadStatus.Unread,
            ReadKey => NewsArticleReadStatus.Read,
            UpdatedKey => NewsArticleReadStatus.Updated,
            _ => NewsArticleReadStatus.Unknown,
        }).Where(s => s != NewsArticleReadStatus.Unknown).Distinct().Order().ToList();
        return members.Count == 0 ? null : members;
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
