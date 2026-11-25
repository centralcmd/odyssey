using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>
/// The Podcasts page (issue #559 §3.2). This file holds the state, the header and the filters; the list, the detail,
/// the audio actions and the polling are in the partial files beside it.
/// </summary>
public partial class PodcastsPage : IDisposable
{
    /// <summary>The user-preference key the page's layout and filters persist under (§3.1).</summary>
    public const string PageStateKey = "podcasts-page";

    public const string RootId = "pod-page";

    public const string SearchHelpId = "podcast-search-help";

    /// <summary>The header's ⋯ "Podcast actions" menu.</summary>
    public const string ActionMenuId = "pod-actions";

    /// <summary>The Status filter's two device members (design system <c>POD_SAVED_OPTIONS</c>).</summary>
    public const string SavedKey = "saved";

    public const string UnsavedKey = "unsaved";

    public const string NewestKey = "published";

    private const int MaxFeedRequests = 10;

    [Inject] private IPodcastsApiClient Podcasts { get; set; } = default!;

    [Inject] private INewsApiClient News { get; set; } = default!;

    [Inject] private INewsCatalogueCache Catalogue { get; set; } = default!;

    [Inject] private INativeTransfer Transfer { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Inject] private IPageStateService PageState { get; set; } = default!;

    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private TimeProvider Time { get; set; } = default!;

    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary><c>?feedId=</c> preselects that podcast for this visit (§3.1); a non-podcast id is dropped.</summary>
    [SupplyParameterFromQuery(Name = "feedId")] public Guid? FeedIdQuery { get; set; }

    private static readonly OdsTableSort DefaultSort = new(NewestKey, OdsSortDirection.Desc);

    private static readonly IReadOnlyList<OdsSortField<PodcastEpisodeSummary>> SortFields =
        [new() { Key = NewestKey, Label = "Published", Type = OdsSortType.Date }];

    /// <summary>
    /// The Status filter is ONE mixed list, as on Feeds (design system <c>podMatchesStatus</c>): the audio states, then
    /// Saved to a device · Not saved to a device. Members within a group OR; the two groups AND.
    /// </summary>
    internal static readonly IReadOnlyList<OdsOption> StatusOptions =
    [
        .. OdsPodcastAudioState.Order.Select(s =>
        {
            var meta = OdsPodcastAudioState.Meta(s);
            return new OdsOption(s.ToString(), meta.Label) { Icon = meta.Icon, IconColor = OdsBreakdown.Tone(meta.Tone) };
        }),
        new(SavedKey, "Saved to a device") { Icon = "phone_iphone", IconColor = OdsBreakdown.Tone("outline") },
        new(UnsavedKey, "Not saved to a device") { Icon = "mobile_off", IconColor = OdsBreakdown.Tone("outline") },
    ];

    private OdsLiveAnnouncer? _announcer;

    // ── Claims ─────────────────────────────────────────────────────────────────────────────────────
    private bool _canRequest;
    private bool _canDelete;
    private bool _canCreateFeeds;
    private bool _canUpdateFeeds;
    private bool _canReadSettings;
    private bool _canReadFileStorage;

    // ── Reference data ─────────────────────────────────────────────────────────────────────────────
    private List<ExistingNewsFeed> _feeds = [];
    private Dictionary<Guid, ExistingNewsFeed> _feedsById = [];
    private List<ExistingNewsTag> _tags = [];
    private NewsStatus? _status;

    // ── Layout and filters ─────────────────────────────────────────────────────────────────────────
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private string? _searchError;
    private IReadOnlyCollection<string> _feedFilter = [];
    private IReadOnlyCollection<string> _statuses = [];
    private OdsTableSort _sort = DefaultSort;
    private int _batch = OdsPageSizes.Batch[1];
    private Guid? _handledFeedQuery;
    private bool _initialised;

    // ── Dialogs ────────────────────────────────────────────────────────────────────────────────────
    private Guid _feedDialogKey;
    private bool _feedDialogOpen;

    private DateTimeOffset Now => Time.GetUtcNow();

    private TimeZoneInfo LocalZone => Time.LocalTimeZone;

    // ── The header ─────────────────────────────────────────────────────────────────────────────────

    private int StoredCount => _feeds.Sum(f => f.Podcast?.StoredAudioCount ?? 0);

    private long StoredBytes => _feeds.Sum(f => f.Podcast?.StoredAudioBytes ?? 0);

    private int EpisodeTotal => _feeds.Sum(f => f.Podcast?.EpisodeCount ?? 0);

    private int SavedTotal => _feeds.Sum(f => f.Podcast?.SavedEpisodeCount ?? 0);

    private string EpisodeTotalText => EpisodeTotal.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"6 podcasts · 3 episodes on the server (231 MB)" — over every podcast, archived included (§3.2.1).</summary>
    private string SubLine => _loading && _feeds.Count == 0 ? "Podcasts"
        : _feeds.Count == 0 ? "No podcasts yet"
        : $"{NewsFeedback.Count(_feeds.Count, "podcast", "podcasts")} · {NewsFeedback.Count(StoredCount, "episode", "episodes")} on the server ({PodcastFormat.Size(StoredBytes)})";

    private static NewsFeedStatus StateOf(ExistingNewsFeed feed) => NewsVisuals.StatusOf(feed);

    private IReadOnlyList<OdsBreakdownRow> PodcastRows =>
    [
        Row("ok", "Healthy or pending", _feeds.Count(f => StateOf(f) == NewsFeedStatus.Active && !OdsNewsFeedHealth.NeedsAttention(f.Health)), "check_circle", "income"),
        Row("attention", "Need attention", _feeds.Count(f => OdsNewsFeedHealth.NeedsAttention(f.Health)), "error_outline", "warning"),
        Row("paused", "Paused", _feeds.Count(f => StateOf(f) == NewsFeedStatus.Paused), "pause_circle", "pending"),
        Row("archived", "Archived", _feeds.Count(f => StateOf(f) == NewsFeedStatus.Archived), "inventory_2", "outline"),
    ];

    /// <summary>By tags, as on Feeds: podcasts per active tag, then "No tag"; the rows overlap, so the total is the podcast count.</summary>
    private IReadOnlyList<OdsBreakdownRow> TagRows =>
    [
        .. _tags.Where(t => t.Archived is null).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new OdsBreakdownRow
            {
                Key = t.NewsTagId,
                Label = t.Name,
                Icon = OdsTagChip.GlyphFor(t.Icon),
                IconColor = UserColor.Glyph(t.Color) ?? NewsVisuals.TagRowColor,
                Count = _feeds.Count(f => f.TagIds.Contains(t.NewsTagId)),
            }),
        Row("none", "No tag", _feeds.Count(f => f.TagIds.Count == 0), "label_off", "outline"),
    ];

    /// <summary>By podcast: the episodes of each active podcast (<c>Podcast.EpisodeCount</c>); the rows partition them, so the tile sums them.</summary>
    private IReadOnlyList<OdsBreakdownRow> ByPodcastRows => NewsVisuals.CountByFeedRows(
        _feeds.Where(f => StateOf(f) == NewsFeedStatus.Active).Select(f => new FeedCount(f.Id, f.DisplayTitle, f.Podcast?.EpisodeCount ?? 0, "podcasts")));

    /// <summary>Saved to a device or not, over every episode of every podcast (§3.2.1 as amended by the design).</summary>
    private IReadOnlyList<OdsBreakdownRow> EpisodeRows =>
    [
        Row(SavedKey, "Saved to a device", SavedTotal, "phone_iphone", "outline"),
        Row(UnsavedKey, "Not saved to a device", Math.Max(0, EpisodeTotal - SavedTotal), "mobile_off", "outline"),
    ];

    private IReadOnlyList<OdsBreakdownRow> ServerRows =>
    [
        Row("episodes", "Episodes", StoredCount, "cloud_done", "income"),
        new() { Key = "size", Label = "Size", Icon = "sd_storage", IconColor = OdsBreakdown.Tone("outline"), Count = PodcastFormat.Size(StoredBytes) },
    ];

    private static OdsBreakdownRow Row(string key, string label, int count, string icon, string tone) =>
        new() { Key = key, Label = label, Count = count, Icon = icon, IconColor = OdsBreakdown.Tone(tone) };

    /// <summary>
    /// The "Attention" problems: one row per podcast that needs attention (Review → that podcast on Feeds), then, for a
    /// <c>file-storage.read</c> holder, "Audio storage limit reached." with Settings → for <c>system-settings.read</c>.
    /// </summary>
    internal IReadOnlyCollection<PageHeaderProblem>? Problems
    {
        get
        {
            var rows = new List<PageHeaderProblem>();
            foreach (var feed in _feeds.Where(f => OdsNewsFeedHealth.NeedsAttention(f.Health))
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

            if (_canReadFileStorage && _status is { AudioQuotaReached: true })
            {
                var row = new PageHeaderProblem { Severity = PageHeaderSeverity.Warning, Message = "Audio storage limit reached." };
                if (_canReadSettings)
                {
                    row.ViewLabel = "Settings";
                    row.ActionAriaLabel = "Open Settings for the audio storage limit";
                    row.OnView = EventCallback.Factory.Create(this, () => Nav.NavigateTo("settings"));
                }

                rows.Add(row);
            }

            return rows.Count == 0 ? null : rows;
        }
    }

    /// <summary>Every podcast, its state as TEXT in the option name, archived ones last (the News Feed filter's rule).</summary>
    internal IReadOnlyList<OdsOption> FeedOptions =>
    [
        .. _feeds.OrderBy(f => StateOf(f) == NewsFeedStatus.Archived)
            .ThenBy(f => f.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .Select(f =>
            {
                var archived = StateOf(f) == NewsFeedStatus.Archived;
                var meta = OdsNewsFeedHealth.Meta(f.Health);
                var note = archived ? " (archived)"
                    : OdsNewsFeedHealth.NeedsAttention(f.Health) ? " (needs attention)"
                    : StateOf(f) == NewsFeedStatus.Paused ? " (paused)"
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

    /// <summary>No episode has arrived yet, and no filter is narrowing the list.</summary>
    private bool NoEpisodesYet => EpisodeTotal == 0 && _feedFilter.Count == 0 && _statuses.Count == 0 && string.IsNullOrWhiteSpace(_search);

    /// <summary>Navigation only: the audio actions live in the card's Audio panel (design decision, #559 doc).</summary>
    private IReadOnlyList<OdsMenuItem> MenuFor(PodcastEpisodeSummary episode)
    {
        var feedId = episode.FeedId;
        var items = new List<OdsMenuItem>
        {
            new()
            {
                Icon = "podcasts",
                Label = $"Show episodes from {episode.FeedTitle}",
                OnClick = EventCallback.Factory.Create(this, () => ShowPodcastAsync(feedId)),
            },
        };
        if (_canUpdateFeeds)
        {
            items.Add(new()
            {
                Icon = "rss_feed",
                Label = "Go to the podcast’s feed",
                OnClick = EventCallback.Factory.Create(this, () => Nav.NavigateTo($"news-feeds?state=All&reveal={feedId}")),
            });
        }

        return items;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        var user = await Auth.GetUserAsync();
        _canRequest = user.HasPermission(PermissionClaims.PodcastAudioCreate);
        _canDelete = user.HasPermission(PermissionClaims.PodcastAudioDelete);
        _canSavePlayback = user.HasPermission(PermissionClaims.NewsUpdate);
        _canCreateFeeds = user.HasPermission(PermissionClaims.NewsFeedsCreate);
        _canUpdateFeeds = user.HasPermission(PermissionClaims.NewsFeedsUpdate);
        _canReadSettings = user.HasPermission(PermissionClaims.SystemSettingsRead);
        _canReadFileStorage = user.HasPermission(PermissionClaims.FileStorageRead);

        await PageState.RestoreOrSeedAsync<PodcastsPageState>(PageStateKey, ApplyPageState, BuildPageState);
        ApplyDeepLink();
        _initialised = true;
        await LoadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_initialised && FeedIdQuery != _handledFeedQuery)
        {
            ApplyDeepLink();
            DropStaleIds();
            await ResetAsync();
        }
    }

    /// <summary><c>?feedId=</c> preselects that podcast and overrides the saved state for this visit (§3.1).</summary>
    private void ApplyDeepLink()
    {
        _handledFeedQuery = FeedIdQuery;
        if (FeedIdQuery is { } feed)
        {
            _search = string.Empty;
            _feedFilter = [feed.ToString()];
        }
    }

    // ── Page state ─────────────────────────────────────────────────────────────────────────────────

    internal sealed class PodcastsPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> FeedIds { get; set; } = [];
        public List<string> Statuses { get; set; } = [];
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[1];
    }

    private void ApplyPageState(PodcastsPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _feedFilter = state.FeedIds ?? [];
        _statuses = StatusOptions.KnownValues(state.Statuses ?? []);
        _sort = new OdsTableSort(NewestKey, state.SortDirection ?? OdsSortDirection.Desc);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private PodcastsPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        FeedIds = [.. _feedFilter],
        Statuses = [.. _statuses],
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void Persist() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; Persist(); }

    private void OnSearchToggled(bool open) { _searchOpen = open; Persist(); }

    // ── Filters ────────────────────────────────────────────────────────────────────────────────────

    private Task OnSearchChanged(string value)
    {
        _search = value ?? string.Empty;
        return FiltersChangedAsync();
    }

    private Task OnFeedFilterChanged(IReadOnlyCollection<string> values)
    {
        _feedFilter = [.. (values ?? []).Take(NewsLimits.MaxFilterIds)];
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

    /// <summary>"Load N at a time" applies from the next append: the cursor does not bind the page size.</summary>
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
        _statuses = [];
        _searchError = null;
        Persist();
        return ResetAsync(focusListStart: true);
    }

    private Task ShowPodcastAsync(Guid feedId)
    {
        _feedFilter = [feedId.ToString()];
        Persist();
        return ResetAsync(focusListStart: true);
    }

    /// <summary>The request filter from the controls (§3.2.2): podcasts OR, states OR, device members OR; the groups AND.</summary>
    internal PodcastEpisodeListFilter BuildFilter()
    {
        var feeds = NewsFilterIds.Normalize(_feedFilter.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty));
        var states = _statuses.Select(s => Enum.TryParse<PodcastAudioState>(s, out var state) ? state : PodcastAudioState.Unknown)
            .Where(s => s != PodcastAudioState.Unknown).Distinct().Order().ToList();
        var search = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim();
        // An archived podcast's episodes show only when it is named (includeArchivedFeeds stays false).
        return new PodcastEpisodeListFilter
        {
            FeedIds = feeds.Count == 0 ? null : [.. feeds.Take(NewsLimits.MaxFilterIds)],
            AudioStates = states.Count == 0 ? null : states,
            SavedToDevice = SavedFilter(_statuses),
            Search = search,
            SortBy = _sort.Dir == OdsSortDirection.Asc ? PodcastEpisodeSortBy.Oldest : PodcastEpisodeSortBy.Newest,
        };
    }

    /// <summary>Saved alone → true, Not saved alone → false, both or neither → null (both).</summary>
    internal static bool? SavedFilter(IReadOnlyCollection<string> statuses)
    {
        var saved = statuses.Contains(SavedKey);
        var unsaved = statuses.Contains(UnsavedKey);
        return saved == unsaved ? null : saved;
    }

    /// <summary>Restored or linked ids that are not podcasts are dropped before the load (§3.1).</summary>
    private void DropStaleIds() =>
        _feedFilter = [.. _feedFilter.Where(v => Guid.TryParse(v, out var id) && _feedsById.ContainsKey(id))];

    // ── New podcast ────────────────────────────────────────────────────────────────────────────────

    private void NewPodcast()
    {
        _feedDialogKey = Guid.NewGuid();
        _feedDialogOpen = true;
    }

    private async Task OnPodcastCreatedAsync(ExistingNewsFeed feed)
    {
        _feedDialogOpen = false;
        NewsFeedback.Success(Snackbar, "Podcast added. Episodes appear after the next check.");
        await LoadAsync();
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
        _disposed = true;
        _listCts.Cancel();
        _listCts.Dispose();
        _pollTimer?.Dispose();
        _cooldownTimer?.Dispose();
        DisposePlayback();
    }
}
