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

public partial class NewsFeedsPage
{
    /// <summary>The user-preference key the page's layout and filters persist under (§3.1).</summary>
    public const string PageStateKey = "news-feeds-page";

    public const string RootId = "nf-page";

    /// <summary>The ⋯ menu root's id, which the import dialog returns focus to (issue #509 §3.1, v5 §1).</summary>
    public const string ActionMenuId = "news-feeds-actions";

    /// <summary>The OPML download's media type (§3.2).</summary>
    public const string OpmlContentType = "text/x-opml";

    /// <summary>The hard limit on consecutive list requests for the full set (§3.3, Loading).</summary>
    public const int MaxListRequests = 5;



    [Inject] private INewsApiClient News { get; set; } = default!;

    [Inject] private INewsCatalogueCache Catalogue { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Inject] private IPageStateService PageState { get; set; } = default!;

    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private IClaimsRefresher ClaimsRefresher { get; set; } = default!;

    [Inject] private NavigationManager Nav { get; set; } = default!;

    [Inject] private IPodcastsApiClient Podcasts { get; set; } = default!;

    /// <summary><c>?kind=Podcast</c> (the Podcasts page's Manage podcasts) ticks that Type for this visit (#559 §3.3).</summary>
    [SupplyParameterFromQuery(Name = "kind")] public string? KindQuery { get; set; }

    /// <summary><c>?state=All</c> opens the page with the Status filter cleared (§3.1).</summary>
    [SupplyParameterFromQuery(Name = "state")] public string? StateQuery { get; set; }

    /// <summary><c>?reveal=&lt;id&gt;</c> opens that feed, expanded and scrolled to (Review from News' Attention).</summary>
    [SupplyParameterFromQuery(Name = "reveal")] public Guid? RevealQuery { get; set; }

    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);

    internal static readonly IReadOnlyList<OdsSortField<ExistingNewsFeed>> SortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = f => f.DisplayTitle.ToLowerInvariant() },
        new() { Key = "unread", Label = "Unread", Type = OdsSortType.Number, SortValue = f => f.UnreadCount },
        new() { Key = "checked", Label = "Last checked", Type = OdsSortType.Date, SortValue = f => f.LastCheckedAtUtc },
    ];

    private OdsLiveAnnouncer? _announcer;

    // ── Data ───────────────────────────────────────────────────────────────────────────────────────
    private List<ExistingNewsFeed> _feeds = [];
    private List<ExistingNewsTag> _tags = [];
    private Dictionary<Guid, ExistingNewsTag> _tagsById = [];
    private bool _loading = true;
    private LoadFailure? _failure;
    private int _failureCount;
    private readonly HashSet<Guid> _busyIds = [];

    private sealed record LoadFailure(bool Busy, TimeSpan? RetryAfter);

    // ── Claims ─────────────────────────────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadSettings;
    private bool _canReadFileStorage;

    /// <summary>The storage status, for the Attention panel's quota rows (a <c>file-storage.read</c> holder only).</summary>
    private NewsStatus? _status;

    // ── Layout, filters, sort ──────────────────────────────────────────────────────────────────────
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _statuses = [NewsVisuals.Key(NewsFeedStatus.Active)];
    private IReadOnlyCollection<string> _tagFilter = [];
    private IReadOnlyCollection<string> _kinds = [];
    private OdsTableSort _sort = DefaultSort;
    private int _batch = OdsPageSizes.Batch[1];
    private Guid? _openId;
    private Guid? _revealId;

    private bool HasFilters => !string.IsNullOrWhiteSpace(_search) || _tagFilter.Count > 0 || _kinds.Count > 0;

    /// <summary>The Type filter (#559 §3.3): Articles · Podcasts, after Status; none ticked = every feed.</summary>
    internal static readonly OdsBreakdownDef<string>[] KindDefs =
    [
        new(nameof(NewsFeedKind.Article), "Articles", "outline", "article"),
        new(nameof(NewsFeedKind.Podcast), "Podcasts", "outline", "podcasts"),
    ];

    internal static readonly IReadOnlyList<OdsOption> KindOptions = OdsBreakdown.Options(KindDefs);

    private static string KindKey(ExistingNewsFeed feed) =>
        feed.Kind == NewsFeedKind.Podcast ? nameof(NewsFeedKind.Podcast) : nameof(NewsFeedKind.Article);

    private IReadOnlyList<OdsBreakdownRow> KindRows =>
    [
        .. KindDefs.Select(d => new OdsBreakdownRow
        {
            Key = d.Key, Label = d.Label, Icon = d.Icon, IconColor = OdsBreakdown.Tone(d.Tone),
            Count = _feeds.Count(f => KindKey(f) == d.Key),
        }),
    ];

    /// <summary>
    /// By feed: what each active feed holds — articles for an article feed, episodes for a podcast — with the
    /// kind's glyph and the unit in text ("5 articles", "12 episodes"). The units differ, so the tile has no total.
    /// </summary>
    private IReadOnlyList<OdsBreakdownRow> ByFeedRows => NewsVisuals.CountByFeedRows(
        _feeds.Where(f => NewsVisuals.StatusOf(f) == NewsFeedStatus.Active).Select(f => f.Kind == NewsFeedKind.Podcast
            ? new FeedCount(f.Id, f.DisplayTitle, f.Podcast?.EpisodeCount ?? 0, "podcasts", ("episode", "episodes"))
            : new FeedCount(f.Id, f.DisplayTitle, f.ArticleCount, "rss_feed", ("article", "articles"))));

    private string? ArtworkFor(ExistingNewsFeed feed) =>
        feed.Podcast?.ArtworkId is { } art ? Podcasts.ArtworkUrl(art, PodcastArtworkSize.Small) : null;

    private int AttentionCount => _feeds.Count(f => OdsNewsFeedHealth.NeedsAttention(f.Health));

    private string SubLine => _loading || _failure is not null
        ? "Feeds"
        : $"{NewsFeedback.Count(_feeds.Count, "feed", "feeds")} · {AttentionCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} {(AttentionCount == 1 ? "needs" : "need")} attention";

    private IReadOnlyList<OdsOption> TagFilterOptions => NewsVisuals.TagFilterOptions(_tags);

    /// <summary>The full set, filtered and sorted client-side (§3.3): OR within a filter, AND across them.</summary>
    internal IReadOnlyList<ExistingNewsFeed> Filtered => Filter(_feeds);

    private IReadOnlyList<ExistingNewsFeed> Filter(IEnumerable<ExistingNewsFeed> feeds)
    {
        var term = _search.Trim();
        var tagIds = _tagFilter.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).ToHashSet();
        var shown = feeds.Where(f =>
            NewsVisuals.MatchesStatus(f, _statuses)
            && (tagIds.Count == 0 || f.TagIds.Any(tagIds.Contains))
            && (_kinds.Count == 0 || _kinds.Contains(KindKey(f)))
            && (term.Length == 0
                || f.DisplayTitle.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || (NewsFeedCard.SiteHostOf(f.SiteUrl)?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)));
        return OdsSortHelpers.SortRows(shown, SortFields, _sort, f => f.Id.ToString());
    }

    private bool Shows(ExistingNewsFeed feed) => Filter([feed]).Count == 1;

    // ── Overview ───────────────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<OdsBreakdownRow> TagRows =>
    [
        .. _tags.Where(t => t.Archived is null).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => new OdsBreakdownRow
        {
            Key = t.NewsTagId,
            Label = t.Name,
            Icon = OdsTagChip.GlyphFor(t.Icon),
            IconColor = UserColor.Glyph(t.Color) ?? NewsVisuals.TagRowColor,
            Count = _feeds.Count(f => f.TagIds.Contains(t.NewsTagId)),
        }),
        new OdsBreakdownRow { Key = "none", Label = "No tag", Icon = "label_off", IconColor = OdsBreakdown.Tone("outline"), Count = _feeds.Count(f => f.TagIds.Count == 0) },
    ];

    /// <summary>
    /// By status: the Status filter's own members, so a row's count is what ticking it alone shows. The rows overlap
    /// (a feed is Active AND Healthy), so the tile's total is the feed count, not their sum.
    /// </summary>
    private IReadOnlyList<OdsBreakdownRow> StatusRows =>
        OdsBreakdown.CountedStatusRows(key => _feeds.Count(f => NewsVisuals.MatchesStatus(f, [key])), NewsVisuals.StatusDefs);

    // ── Attention ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The "Attention" problems, as on News (design system NewsFeeds.jsx): one row per feed that needs attention —
    /// Error for Gone and Quarantined, Warning for Failing and QuotaReached — whose Review stays on this page; then,
    /// for a <c>file-storage.read</c> holder, a Warning row per full quota, its Settings action only with
    /// <c>system-settings.read</c>.
    /// </summary>
    internal IReadOnlyCollection<PageHeaderProblem>? Problems
    {
        get
        {
            if (_loading || _failure is not null)
            {
                return null;
            }

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
                    OnView = EventCallback.Factory.Create(this, () => ReviewAsync(id)),
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
    /// Review stays on this page: it clears whichever filter hides the feed, expands it, reveals it in the list and
    /// focuses its header.
    /// </summary>
    internal async Task ReviewAsync(Guid id)
    {
        if (_feeds.FirstOrDefault(f => f.Id == id) is not { } feed)
        {
            return;
        }

        if (_statuses.Count > 0 && !NewsVisuals.MatchesStatus(feed, _statuses))
        {
            _statuses = [];
        }

        if (_tagFilter.Count > 0 && !feed.TagIds.Any(t => _tagFilter.Contains(t.ToString())))
        {
            _tagFilter = [];
        }

        if (_kinds.Count > 0 && !_kinds.Contains(KindKey(feed)))
        {
            _kinds = [];
        }

        if (!Shows(feed))
        {
            _search = string.Empty;
        }

        Persist();
        _openId = id;
        _revealId = id;
        StateHasChanged();
        await FocusCardAsync(id);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        ReadClaims(await Auth.GetUserAsync());
        // A claim change (a 403's refresh, §7.3) redraws the menu and the actions without a reload (F6, S1).
        Auth.AuthenticationStateChanged += OnAuthenticationStateChanged;

        await PageState.RestoreOrSeedAsync<NewsFeedsPageState>(PageStateKey, ApplyPageState, BuildPageState);

        // A link overrides the saved state for this visit only (§3.1): Status cleared to All, the feed revealed.
        if (string.Equals(StateQuery, nameof(NewsFeedState.All), StringComparison.OrdinalIgnoreCase) || RevealQuery is not null)
        {
            _statuses = [];
        }

        if (KindQuery is { } kind)
        {
            // Parsed, never trusted (§3.1): exactly Article or Podcast, else ignored.
            _kinds = string.Equals(kind, nameof(NewsFeedKind.Podcast), StringComparison.OrdinalIgnoreCase) ? [nameof(NewsFeedKind.Podcast)]
                : string.Equals(kind, nameof(NewsFeedKind.Article), StringComparison.OrdinalIgnoreCase) ? [nameof(NewsFeedKind.Article)]
                : _kinds;
        }

        await LoadAsync();

        if (RevealQuery is { } reveal && _feeds.Any(f => f.Id == reveal))
        {
            _openId = reveal;
            _revealId = reveal;
            _search = string.Empty;
            _tagFilter = [];
            _kinds = [];
        }
    }

    private void ReadClaims(System.Security.Claims.ClaimsPrincipal user)
    {
        _canCreate = user.HasPermission(PermissionClaims.NewsFeedsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.NewsFeedsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.NewsFeedsDelete);
        _canReadSettings = user.HasPermission(PermissionClaims.SystemSettingsRead);
        _canReadFileStorage = user.HasPermission(PermissionClaims.FileStorageRead);
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state) => _ = InvokeAsync(async () =>
    {
        try
        {
            ReadClaims((await state).User);
        }
        catch (Exception)
        {
            return;
        }

        StateHasChanged();
    });

    // ── Page state ─────────────────────────────────────────────────────────────────────────────────

    internal sealed class NewsFeedsPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string>? Statuses { get; set; }
        public List<string> TagIds { get; set; } = [];
        public List<string> Kinds { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[1];
    }

    private void ApplyPageState(NewsFeedsPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _statuses = state.Statuses is null ? _statuses : NewsVisuals.StatusOptions.KnownValues(state.Statuses);
        _tagFilter = state.TagIds ?? [];
        _kinds = KindOptions.KnownValues(state.Kinds ?? []);
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private NewsFeedsPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        Statuses = [.. _statuses],
        TagIds = [.. _tagFilter],
        Kinds = [.. _kinds],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void Persist() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; Persist(); }

    private void OnSearchToggled(bool open) { _searchOpen = open; Persist(); }

    // A filter, sort or search change leaves focus on its control; only a reset that removed the focused element
    // moves it (§6.6), which the recovery call below does once the list has re-rendered.
    private Task OnSearchChanged(string value) { _search = value ?? string.Empty; return FiltersChangedAsync(); }

    private Task OnStatusesChanged(IReadOnlyCollection<string> values) { _statuses = values ?? []; return FiltersChangedAsync(); }

    private Task OnTagFilterChanged(IReadOnlyCollection<string> values) { _tagFilter = values ?? []; return FiltersChangedAsync(); }

    private Task OnKindsChanged(IReadOnlyCollection<string> values) { _kinds = values ?? []; return FiltersChangedAsync(); }

    private Task OnSortChanged(OdsTableSort sort) { _sort = sort; return FiltersChangedAsync(); }

    private void OnBatchChanged(int size) { _batch = size; Persist(); }

    private Task ClearFilters()
    {
        _search = string.Empty;
        _tagFilter = [];
        _kinds = [];
        return FiltersChangedAsync(focusListStart: true);
    }

    private Task ShowAllFeeds()
    {
        _statuses = [];
        return FiltersChangedAsync(focusListStart: true);
    }

    private async Task FiltersChangedAsync(bool focusListStart = false)
    {
        _revealId = null;
        Persist();
        StateHasChanged();
        await InvokeJsAsync(focusListStart ? "odsNewsFocusListStart" : "odsNewsRecoverFocus", RootId);
    }

    // ── Loading ────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        var tagsTask = Catalogue.GetTagsAsync();
        var statusTask = _canReadFileStorage ? News.GetStatusAsync() : null;
        var loaded = await LoadAllFeedsAsync();
        var tags = await tagsTask;
        SetTags(tags.ItemsOrToast(Snackbar, "news tags"));
        if (statusTask is not null)
        {
            var status = await statusTask;
            _status = status.IsSuccess ? status.Value : null;
        }

        _loading = false;
        if (loaded.Feeds is { } feeds)
        {
            _feeds = feeds;
            _failure = null;
        }
        else
        {
            // The partial set is discarded: the page shows its load error with Retry (§3.3).
            _feeds = [];
            _failure = new LoadFailure(NewsFeedback.IsBusy(loaded.Problem), loaded.RetryAfter);
            _failureCount++;
        }

        StateHasChanged();
    }

    private void SetTags(List<ExistingNewsTag> tags)
    {
        _tags = tags;
        _tagsById = tags.ToDictionary(t => t.NewsTagId);
        // A stored tag filter naming a tag that no longer exists is dropped before it filters anything (§3.1).
        _tagFilter = [.. _tagFilter.Where(v => Guid.TryParse(v, out var id) && _tagsById.ContainsKey(id))];
    }

    private async Task<(List<ExistingNewsFeed>? Feeds, ApiProblem? Problem, TimeSpan? RetryAfter)> LoadAllFeedsAsync()
    {
        var all = new List<ExistingNewsFeed>();
        var seen = new HashSet<Guid>();
        for (var page = 1; page <= MaxListRequests; page++)
        {
            var result = await News.ListFeedsAsync(page, NewsLimits.MaxPageSize, state: NewsFeedState.All);
            if (!result.IsSuccess || result.Value is not { } chunk)
            {
                return (null, result.Problem, result.RetryAfter);
            }

            all.AddRange(chunk.Items.Where(f => seen.Add(f.Id)));
            if (chunk.Items.Count < NewsLimits.MaxPageSize)
            {
                break;
            }
        }

        return (all, null, null);
    }

    private async Task RetryLoadAsync(bool retryHadFocus)
    {
        await LoadAsync();
        if (_failure is null && retryHadFocus)
        {
            await InvokeJsAsync("odsNewsFocusListStart", RootId);
        }
    }

    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────

    private void OnToggle(ExistingNewsFeed feed, bool open)
    {
        _openId = open ? feed.Id : (_openId == feed.Id ? null : _openId);
        if (_revealId == feed.Id && !open)
        {
            _revealId = null;
        }
    }

    internal void Replace(ExistingNewsFeed updated)
    {
        var index = _feeds.FindIndex(f => f.Id == updated.Id);
        if (index >= 0)
        {
            _feeds[index] = updated;
        }
        else
        {
            _feeds.Add(updated);
        }

        StateHasChanged();
    }

    /// <summary>The card after <paramref name="id"/> in the shown order, else the one before it.</summary>
    private Guid? Neighbour(Guid id)
    {
        var shown = Filtered;
        var index = shown.ToList().FindIndex(f => f.Id == id);
        if (index < 0)
        {
            return null;
        }

        return index + 1 < shown.Count ? shown[index + 1].Id : index > 0 ? shown[index - 1].Id : null;
    }

    private Task FocusCardAsync(Guid id) => InvokeJsAsync("odsNewsFocusCard", NewsFeedCard.CardId(id), RootId);

    /// <summary>A card that left the list: focus to the next card's header, else the visible target (§6.6).</summary>
    private Task FocusAfterRemovalAsync(Guid? next) =>
        next is { } id ? FocusCardAsync(id) : InvokeJsAsync("odsNewsFocusListStart", RootId);

    // ── The row menu ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// View articles first, then only what the caller can do now (§3.3, AC 14, 32): an action the claim or the feed's
    /// state rules out is ABSENT, never dimmed. Delete… is offered on any feed — there is no archive-first step;
    /// Release only on a quarantined feed; Resume replaces Pause while paused and Unarchive replaces Archive while
    /// archived. The feed's tags are edited in its Edit dialog.
    /// </summary>
    internal IReadOnlyList<OdsMenuItem> MenuFor(ExistingNewsFeed feed)
    {
        var items = new List<OdsMenuItem>();
        var archived = feed.ArchivedAtUtc is not null;
        var name = feed.DisplayTitle;
        var id = feed.Id;
        items.Add(feed.Kind == NewsFeedKind.Podcast
            ? Item("podcasts", "View episodes", name, () => Nav.NavigateTo($"podcasts?feedId={id}"))
            : Item("feed", "View articles", name, () => Nav.NavigateTo($"news?feedId={id}")));
        items.Add(new OdsMenuItem { Divider = true });
        if (_canUpdate)
        {
            if (!archived && feed.Management is not null)
            {
                items.Add(Item("edit", "Edit", name, () => OpenEdit(feed)));
            }

            if (!archived && feed.IsActive && feed.Health != NewsFeedHealth.Quarantined)
            {
                items.Add(Item("refresh", "Refresh now", name, () => RefreshAsync(feed)));
            }

            if (!archived && feed.Management is not null)
            {
                items.Add(feed.IsActive
                    ? Item("pause_circle", "Pause", name, () => SetActiveAsync(feed, false))
                    : Item("play_circle", "Resume", name, () => SetActiveAsync(feed, true)));
            }

            if (feed.Health == NewsFeedHealth.Quarantined)
            {
                items.Add(Item("gpp_good", "Release from quarantine", name, () => ReleaseAsync(feed)));
            }

            items.Add(new OdsMenuItem { Divider = true });
            items.Add(archived
                ? Item("unarchive", "Unarchive", name, () => SetArchivedAsync(feed, false))
                : Item("archive", "Archive", name, () => SetArchivedAsync(feed, true)));
        }

        if (_canDelete)
        {
            var delete = Item("delete", "Delete…", name, () => OpenDelete(feed));
            delete.Danger = true;
            items.Add(delete);
        }

        return items;
    }

    private OdsMenuItem Item(string icon, string label, string name, Func<Task> action) => new()
    {
        Icon = icon,
        Label = label,
        AriaLabel = $"{label} {name}",
        OnClick = EventCallback.Factory.Create(this, action),
    };

    private OdsMenuItem Item(string icon, string label, string name, Action action) => new()
    {
        Icon = icon,
        Label = label,
        AriaLabel = $"{label} {name}",
        OnClick = EventCallback.Factory.Create(this, action),
    };

    /// <summary>The <c>PUT</c> body for a feed as it stands, so a one-field change replays the rest unchanged.</summary>
    internal static UpdateNewsFeed UpdateOf(ExistingNewsFeed feed) => new()
    {
        Version = feed.Version,
        Url = feed.Management?.Url,
        CustomTitle = feed.CustomTitle,
        FetchIntervalMinutes = feed.FetchIntervalMinutes,
        DownloadImages = feed.DownloadImages,
        IsActive = feed.IsActive,
    };

    private Task SetActiveAsync(ExistingNewsFeed feed, bool active)
    {
        var update = UpdateOf(feed);
        update.IsActive = active;
        return StateSwapAsync(feed, () => News.UpdateFeedAsync(feed.Id, update), active ? "resumed" : "paused",
            active ? "Couldn’t resume the feed" : "Couldn’t pause the feed");
    }

    private Task SetArchivedAsync(ExistingNewsFeed feed, bool archive) =>
        StateSwapAsync(feed, () => archive ? News.ArchiveFeedAsync(feed.Id) : News.UnarchiveFeedAsync(feed.Id),
            archive ? "archived" : "unarchived", archive ? "Couldn’t archive the feed" : "Couldn’t unarchive the feed");

    private Task ReleaseAsync(ExistingNewsFeed feed) =>
        StateSwapAsync(feed, () => News.ReleaseQuarantineAsync(feed.Id), "released from quarantine",
            "Couldn’t release the feed");

    /// <summary>
    /// One state change on a feed (§3.3, "State swaps are announced"): the card shows the server’s copy, and the page
    /// announcer says "Example News paused" — or, when the change also takes the card out of the current filter,
    /// "Example News paused and moved to Paused", with focus on the next card’s header.
    /// </summary>
    private async Task StateSwapAsync(ExistingNewsFeed feed, Func<Task<ApiResult<ExistingNewsFeed>>> write, string verb, string failureLead)
    {
        if (!_busyIds.Add(feed.Id))
        {
            return;
        }

        StateHasChanged();
        var next = Neighbour(feed.Id);
        var result = await write();
        _busyIds.Remove(feed.Id);

        if (result.IsSuccess && result.Value is { } updated)
        {
            Replace(updated);
            Catalogue.Invalidate();
            if (Shows(updated))
            {
                Say($"{updated.DisplayTitle} {verb}");
            }
            else
            {
                Say($"{updated.DisplayTitle} {verb} and moved to {NewsVisuals.Label(NewsVisuals.StatusOf(updated))}");
                if (_openId == updated.Id)
                {
                    _openId = null;
                }

                await FocusAfterRemovalAsync(next);
            }

            return;
        }

        await ActionFailedAsync(feed, result.Status, result.Problem, failureLead, next);
    }

    private async Task ActionFailedAsync(ExistingNewsFeed feed, HttpStatusCode status, ApiProblem? problem, string failureLead, Guid? next)
    {
        if (status == HttpStatusCode.NotFound)
        {
            // Gone elsewhere: the card is removed and focus moves on (§7.2).
            _feeds.RemoveAll(f => f.Id == feed.Id);
            Catalogue.Invalidate();
            Say(NewsFeedback.Gone(Snackbar));
            StateHasChanged();
            await FocusAfterRemovalAsync(next);
            return;
        }

        if (NewsFeedback.Is(problem, NewsErrorCodes.FeedChanged))
        {
            // Changed elsewhere between the read and this write: the card shows the server's copy and the reader retries.
            if ((await News.GetFeedAsync(feed.Id)).Value is { } fresh)
            {
                Replace(fresh);
            }

            const string changed = "This feed was changed elsewhere. Try again.";
            Snackbar.Add(changed, Severity.Warning);
            Say(NewsFeedback.Spoken(changed));
            StateHasChanged();
            return;
        }

        // A 422 not-archived / inactive / not-quarantined is only a race: the server's message (§7.2).
        Say(NewsFeedback.Failure(Snackbar, problem, failureLead));
        StateHasChanged();
    }

    private async Task RefreshAsync(ExistingNewsFeed feed)
    {
        if (!_busyIds.Add(feed.Id))
        {
            return;
        }

        StateHasChanged();
        var next = Neighbour(feed.Id);
        var result = await News.RefreshFeedAsync(feed.Id);
        _busyIds.Remove(feed.Id);
        if (result.IsSuccess)
        {
            Say(NewsFeedback.Success(Snackbar, "Refresh requested."));
            StateHasChanged();
            return;
        }

        await ActionFailedAsync(feed, result.Status, result.Problem, "Couldn’t refresh the feed", next);
    }

    // ── New / Edit ─────────────────────────────────────────────────────────────────────────────────

    private Guid _dialogKey;
    private bool _dialogOpen;
    private ExistingNewsFeed? _dialogFeed;
    private bool _dialogConflict;
    private string? _dialogUrl;

    private void NewFeedClicked()
    {
        if (!_canCreate)
        {
            return;
        }

        OpenDialog(null, conflict: false, url: null);
    }

    private void OpenEdit(ExistingNewsFeed feed)
    {
        if (!_canUpdate || feed.Management is null)
        {
            return;
        }

        OpenDialog(feed, conflict: false, url: null);
    }

    private void OpenDialog(ExistingNewsFeed? feed, bool conflict, string? url)
    {
        _dialogFeed = feed;
        _dialogConflict = conflict;
        _dialogUrl = url;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private void OnDialogOpenChanged(bool open) => _dialogOpen = open;

    private async Task OnFeedSavedAsync(ExistingNewsFeed saved)
    {
        var created = _dialogFeed is null;
        _dialogOpen = false;
        Replace(saved);
        Catalogue.Invalidate();

        if (created)
        {
            // The new card is shown expanded, marked Pending first fetch, with focus on its header; the page polls it.
            if (!Shows(saved))
            {
                _search = string.Empty;
                _tagFilter = [];
                _statuses = [NewsVisuals.Key(NewsFeedStatus.Active)];
                Persist();
            }

            _openId = saved.Id;
            _revealId = saved.Id;
            Say(NewsFeedback.Success(Snackbar, "Feed added. Odyssey will fetch it shortly."));
        }
        else
        {
            Say(NewsFeedback.Success(Snackbar, "Feed saved"));
        }

        StateHasChanged();
        await FocusCardAsync(saved.Id);
    }

    private async Task GoToFeedAsync(Guid id)
    {
        _dialogOpen = false;
        if (!_feeds.Any(f => f.Id == id) && (await News.GetFeedAsync(id)).Value is { } fetched)
        {
            Replace(fetched);
        }

        _search = string.Empty;
        _tagFilter = [];
        _statuses = [];
        _openId = id;
        _revealId = id;
        StateHasChanged();
        await FocusCardAsync(id);
    }

    private void OnFeedGoneInDialog(Guid id)
    {
        _feeds.RemoveAll(f => f.Id == id);
        Catalogue.Invalidate();
    }

    // ── Delete ─────────────────────────────────────────────────────────────────────────────────────

    private ExistingNewsFeed? _deleting;
    private Guid _deleteKey;
    private bool _deleteOpen;
    private bool _deleteAck;
    private bool _deleteBusy;
    private string? _deleteError;

    private void OpenDelete(ExistingNewsFeed feed)
    {
        if (!_canDelete)
        {
            return;
        }

        _deleting = feed;
        _deleteAck = false;
        _deleteError = null;
        _deleteKey = Guid.NewGuid();
        _deleteOpen = true;
    }

    private void OnDeleteOpenChanged(bool open)
    {
        _deleteOpen = open;
        if (!open)
        {
            _deleting = null;
        }
    }

    private void CloseDelete() => OnDeleteOpenChanged(false);

    private async Task DeleteAsync()
    {
        if (_deleting is not { } feed || !_deleteAck || _deleteBusy)
        {
            return;
        }

        _deleteBusy = true;
        _deleteError = null;
        StateHasChanged();
        var next = Neighbour(feed.Id);
        var result = await News.DeleteFeedAsync(feed.Id);
        _deleteBusy = false;

        if (result.IsSuccess || result.Status == HttpStatusCode.NotFound)
        {
            _feeds.RemoveAll(f => f.Id == feed.Id);
            Catalogue.Invalidate();
            _deleteOpen = false;
            _deleting = null;
            Say(result.IsSuccess
                ? NewsFeedback.Success(Snackbar, "Feed deleted. Its articles are being removed in the background.")
                : NewsFeedback.Gone(Snackbar));
            StateHasChanged();
            await FocusAfterRemovalAsync(next);
            return;
        }

        _deleteError = NewsFeedback.IsBusy(result.Problem) ? NewsFeedback.BusyText : result.Error;
        StateHasChanged();
    }

    // ── OPML (issue #509) ──────────────────────────────────────────────────────────────────────────

    private bool _exporting;
    private Guid _importKey;
    private bool _importOpen;

    /// <summary>"Export all": every feed, paused and archived included, whatever the Status filter shows (F15).</summary>
    internal bool ShowsExportAll => _canUpdate && !_loading && _failure is null && _feeds.Count > 0;

    /// <summary>
    /// "Export filtered (N)": only from a list loaded in full (v5 §4), and only when the filters show some feeds but
    /// not all of them — otherwise it would duplicate Export all or export nothing (§3.1).
    /// </summary>
    internal bool ShowsExportFiltered => ShowsExportAll && Filtered.Count >= 1 && Filtered.Count < _feeds.Count;

    private string FilteredCountText => Filtered.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// One export at a time (§3.2, v4 §5, v5 §3). The menu has closed and returned focus to the ⋯ button; the page
    /// announcer says the export is being prepared, and the snackbar alone reports how it ended (§6.5).
    /// </summary>
    private async Task ExportAsync(bool filtered)
    {
        if (_exporting)
        {
            Say("An export is already running.");
            return;
        }

        _exporting = true;
        Say("Preparing the OPML export…");
        try
        {
            // The whole filtered set held in memory, including feeds the batch loader has not rendered yet.
            List<Guid>? ids = filtered ? [.. Filtered.Select(f => f.Id)] : null;
            var result = await News.ExportOpmlAsync(ids);
            if (!result.IsSuccess || result.Value is not { } export)
            {
                await ExportFailedAsync(result.Status);
                return;
            }

            // Zero rows first: the server's empty export is a valid document with an empty <body/>.
            if (export.Rows == 0)
            {
                Snackbar.Add(filtered ? "No feeds to export: the filtered feeds no longer exist." : "No feeds to export.", Severity.Info);
                return;
            }

            if (!EndsWithOpmlClose(export.File.Bytes))
            {
                Say(NewsFeedback.Failure(Snackbar, "The export didn’t complete. Try again."));
                return;
            }

            try
            {
                await JS.SaveFileAsync(export.File.Bytes, export.File.FileName, OpmlContentType);
            }
            catch (JSException)
            {
                Say(NewsFeedback.Failure(Snackbar, "Couldn’t export the feeds."));
                return;
            }

            Say(NewsFeedback.Success(Snackbar, export.Rows is { } rows
                ? $"Exported {NewsFeedback.Count(rows, "feed", "feeds")} to {export.File.FileName}."
                : $"Exported {export.File.FileName}."));
        }
        finally
        {
            _exporting = false;
        }
    }

    /// <summary>§7.1, by status: a <c>429</c> and a <c>503</c> carry no usable code for this purpose.</summary>
    private async Task ExportFailedAsync(HttpStatusCode status)
    {
        var text = status switch
        {
            HttpStatusCode.TooManyRequests => "An export is already running. Try again in a moment.",
            HttpStatusCode.ServiceUnavailable => NewsFeedback.BusyText,
            HttpStatusCode.Forbidden => "You don’t have permission to export feeds.",
            _ => "Couldn’t export the feeds.",
        };
        Say(NewsFeedback.Failure(Snackbar, text));
        if (status == HttpStatusCode.Forbidden)
        {
            // The provider's AuthenticationStateChanged redraws the menu without the lost items (§7.3).
            await ClaimsRefresher.OnResponseAsync(status);
        }
    }

    /// <summary>
    /// Whether the bytes, ignoring trailing whitespace, end in <c>&lt;/opml&gt;</c> (§5, S6). No row count is compared:
    /// a feed with several tags is written once per tag.
    /// </summary>
    internal static bool EndsWithOpmlClose(byte[]? bytes)
    {
        ReadOnlySpan<byte> close = "</opml>"u8;
        if (bytes is null)
        {
            return false;
        }

        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        return end >= close.Length && bytes.AsSpan(end - close.Length, close.Length).SequenceEqual(close);
    }

    private void OpenImport()
    {
        if (!_canCreate)
        {
            return;
        }

        _importKey = Guid.NewGuid();
        _importOpen = true;
    }

    /// <summary>
    /// After ANY sent request (§3.3 step 6, F5) — a result, a 409, a 503, a drop or a stop — tags or feeds may have been
    /// committed, so the catalogue is invalidated and the list reloaded. Nothing from the report reaches the page.
    /// </summary>
    private async Task OnImportClosedAsync(bool sent)
    {
        _importOpen = false;
        if (!sent)
        {
            return;
        }

        Catalogue.Invalidate();
        await LoadAsync();
        if (_failure is null)
        {
            Say("Feed list updated.");
        }
    }

    // ── Pending feed ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Check again: one read of the pending feed; the card then shows what the server says now.</summary>
    private async Task CheckAgainAsync(ExistingNewsFeed feed)
    {
        var result = await News.GetFeedAsync(feed.Id);
        if (result.Status == HttpStatusCode.NotFound)
        {
            _feeds.RemoveAll(f => f.Id == feed.Id);
            Say(NewsFeedback.Gone(Snackbar));
            StateHasChanged();
            return;
        }

        if (result.Value is not { } fresh)
        {
            Say(NewsFeedback.Failure(Snackbar, result.Problem, "Couldn’t check the feed"));
            return;
        }

        Replace(fresh);
        Say(fresh.Health == NewsFeedHealth.Pending
            ? $"{fresh.DisplayTitle}: still waiting for the first fetch."
            : $"{fresh.DisplayTitle}: {OdsNewsFeedHealth.Meta(fresh.Health).Label}.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private void Say(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _ = _announcer?.Announce(text);
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
        Auth.AuthenticationStateChanged -= OnAuthenticationStateChanged;
    }
}
