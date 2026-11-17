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

public partial class NewsTagsPage
{
    /// <summary>The user-preference key the page's filters persist under (§3.1).</summary>
    public const string PageStateKey = "news-tags-page";

    public const string RootId = "nt-page";

    public const string HeadingId = "nt-h2";

    [Inject] private INewsApiClient News { get; set; } = default!;

    [Inject] private INewsCatalogueCache Catalogue { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Inject] private IPageStateService PageState { get; set; } = default!;

    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    internal static readonly OdsBreakdownDef<string>[] StatusDefs =
    [
        new("active", "Active", "income", "task_alt"),
        new("archived", "Archived", "outline", "inventory_2"),
    ];

    internal static readonly IReadOnlyList<OdsOption> StatusOptions = OdsBreakdown.Options(StatusDefs);

    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);

    internal static readonly IReadOnlyList<OdsSortField<ExistingNewsTag>> SortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = t => t.Name.ToLowerInvariant() },
        new() { Key = "feeds", Label = "Feeds", Type = OdsSortType.Number, SortValue = t => t.FeedCount },
        new() { Key = "articles", Label = "Articles", Type = OdsSortType.Number, SortValue = t => t.ArticleCount },
        new() { Key = "status", Label = "Status", Type = OdsSortType.Status, SortValue = t => t.Archived ? 1 : 0 },
    ];

    private OdsLiveAnnouncer? _announcer;

    // ── Data ───────────────────────────────────────────────────────────────────────────────────────
    private List<ExistingNewsTag> _tags = [];
    private bool _loading = true;
    private bool _refetching;
    private LoadFailure? _failure;
    private int _failureCount;
    private readonly HashSet<Guid> _busyIds = [];

    private sealed record LoadFailure(bool Busy, TimeSpan? RetryAfter);

    // ── Claims ─────────────────────────────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canDetachFeeds;
    private bool _canDetachArticles;

    // ── Filters, sort and the window ───────────────────────────────────────────────────────────────
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _statusFilter = [];
    private OdsTableSort _sort = DefaultSort;
    private int _batch = OdsPageSizes.Batch[1];
    private int _window = OdsPageSizes.Batch[1];

    private int _activeCount => _tags.Count(t => !t.Archived);

    private bool HasFilters => !string.IsNullOrWhiteSpace(_search) || _statusFilter.Count > 0;

    /// <summary>The whole filtered set, sorted BEFORE windowing, so the window is always the head of one order (AC 64).</summary>
    internal IReadOnlyList<ExistingNewsTag> Sorted
    {
        get
        {
            var term = _search.Trim();
            var filtered = _tags.Where(t =>
                (_statusFilter.Count == 0 || _statusFilter.Contains(t.Archived ? "archived" : "active"))
                && (term.Length == 0
                    || t.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (t.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)));
            return OdsSortHelpers.SortRows(filtered, SortFields, _sort, t => t.Id.ToString());
        }
    }

    private IReadOnlyList<ExistingNewsTag> Window => [.. Sorted.Take(Math.Max(_window, _batch))];

    /// <summary>The Color field's help line — the one every tag page shows (issue #394).</summary>
    private static string ColorHelp => OdsTagAdmin<ExistingNewsTag>.ColorHelp;

    private static string MenuTriggerId(object key) => $"nt-menu-{key}";

    private static string NameButtonId(Guid id) => $"nt-name-{id}";

    protected override async Task OnInitializedAsync()
    {
        var user = await Auth.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.NewsTagsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.NewsTagsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.NewsTagsDelete);
        _canDetachFeeds = user.HasPermission(PermissionClaims.NewsFeedsUpdate);
        _canDetachArticles = user.HasPermission(PermissionClaims.NewsUpdate);

        await PageState.RestoreOrSeedAsync<NewsTagsPageState>(PageStateKey, ApplyPageState, BuildPageState);
        await LoadAsync();
    }

    // ── Page state ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The persisted layout. A plain mutable class so System.Text.Json round-trips it and tolerates drift.</summary>
    internal sealed class NewsTagsPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[1];
    }

    private void ApplyPageState(NewsTagsPageState state)
    {
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _statusFilter = StatusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
        _window = _batch;
    }

    private NewsTagsPageState BuildPageState() => new()
    {
        SearchOpen = _searchOpen,
        Search = _search,
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void Persist() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnSearchToggled(bool open) { _searchOpen = open; Persist(); }

    // A filter change resets the window, so the reader starts at the head of the new set. A sort keeps the
    // window's size: growing the window keeps the order, and re-sorting re-heads it (AC 64).
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; _window = _batch; Persist(); }

    private void OnStatusChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; _window = _batch; Persist(); }

    private void OnSortChanged(OdsTableSort sort) { _sort = sort; Persist(); }

    private void OnBatchChanged(int size) { _batch = size; _window = size; Persist(); }

    private void ClearFilters()
    {
        _search = string.Empty;
        _statusFilter = [];
        _window = _batch;
        Persist();
    }

    // ── Loading ────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (!_loading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var result = await News.ListTagsAsync();
        _loading = false;
        _refetching = false;
        if (result.IsSuccess)
        {
            _tags = result.Value ?? [];
            _failure = null;
        }
        else if (_tags.Count == 0)
        {
            // A first load (or a reload of an empty set): the region's own failure state (§7.1, Error.Load).
            _failure = new LoadFailure(NewsFeedback.IsBusy(result.Problem), result.RetryAfter);
            _failureCount++;
        }
        else
        {
            // A refresh after a write: the rows stay, and the reader is told.
            Say(NewsFeedback.Failure(Snackbar, result.Problem, "Unable to refresh news tags"));
        }

        StateHasChanged();
    }

    private async Task RetryLoadAsync(bool retryHadFocus)
    {
        await LoadAsync();
        if (_failure is null && retryHadFocus)
        {
            // The retry's button left with the failure state: focus goes to the region's heading (§6.6).
            await FocusAsync(HeadingId);
        }
    }

    // ── Columns and actions ────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<OdsRecordColumn<ExistingNewsTag>> Columns =>
    [
        new()
        {
            Key = "name", HeaderText = "Name", Sortable = true, SortValue = t => t.Name.ToLowerInvariant(),
            Cell = (t, _) => NameCell(t),
        },
        new()
        {
            Key = "feeds", HeaderText = "Feeds", Sortable = true, SortType = OdsSortType.Number, Align = OdsAlign.End,
            SortValue = t => t.FeedCount, Cell = (t, _) => Text(t.FeedCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)),
        },
        new()
        {
            Key = "articles", HeaderText = "Articles", Sortable = true, SortType = OdsSortType.Number, Align = OdsAlign.End,
            SortValue = t => t.ArticleCount, Cell = (t, _) => Text(t.ArticleCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)),
        },
        new()
        {
            Key = "status", HeaderText = "Status", Sortable = true, SortType = OdsSortType.Status,
            SortValue = t => t.Archived ? 1 : 0, Cell = (t, _) => StatusCell(t),
        },
    ];

    private static RenderFragment Text(string value) => builder => builder.AddContent(0, value);

    private IReadOnlyList<OdsMenuItem> BuildActions(ExistingNewsTag tag, OdsRecordActionContext ctx)
    {
        // An action the caller lacks the claim for is absent, never dimmed (§3.3, AC 32).
        var items = new List<OdsMenuItem>();
        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", AriaLabel = $"Edit {tag.Name}", OnClick = EventCallback.Factory.Create(this, () => EditClicked(tag)) });
            items.Add(new OdsMenuItem
            {
                Icon = tag.Archived ? "unarchive" : "archive",
                Label = tag.Archived ? "Unarchive" : "Archive",
                AriaLabel = $"{(tag.Archived ? "Unarchive" : "Archive")} {tag.Name}",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchiveAsync(tag)),
            });
        }

        if (_canDelete)
        {
            if (items.Count > 0)
            {
                items.Add(new OdsMenuItem { Divider = true });
            }

            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete…", AriaLabel = $"Delete… {tag.Name}", Danger = true, OnClick = EventCallback.Factory.Create(this, () => OpenDelete(tag)) });
        }

        return items;
    }

    private async Task ToggleArchiveAsync(ExistingNewsTag tag)
    {
        if (!_canUpdate || !_busyIds.Add(tag.Id))
        {
            return;
        }

        StateHasChanged();
        var unarchive = tag.Archived;
        var result = unarchive ? await News.UnarchiveTagAsync(tag.Id) : await News.ArchiveTagAsync(tag.Id);
        _busyIds.Remove(tag.Id);
        if (result.IsSuccess && result.Value is { } updated)
        {
            Replace(updated);
            Catalogue.InvalidateTags();
            Say($"{updated.Name} {(unarchive ? "unarchived" : "archived")}");
        }
        else if (result.Status == HttpStatusCode.NotFound)
        {
            await RemoveGoneAsync(tag);
        }
        else
        {
            Say(NewsFeedback.Failure(Snackbar, result.Problem, unarchive ? "Couldn't unarchive the tag" : "Couldn't archive the tag"));
        }

        StateHasChanged();
    }

    private void Replace(ExistingNewsTag updated)
    {
        var index = _tags.FindIndex(t => t.Id == updated.Id);
        if (index >= 0)
        {
            _tags[index] = updated;
        }
        else
        {
            _tags.Add(updated);
        }
    }

    private async Task RemoveGoneAsync(ExistingNewsTag tag)
    {
        var next = NextAfter(tag.Id);
        _tags.RemoveAll(t => t.Id == tag.Id);
        Catalogue.InvalidateTags();
        Say(NewsFeedback.Gone(Snackbar));
        await FocusAfterRemovalAsync(next);
    }

    /// <summary>The row after <paramref name="id"/> in the shown order, else the one before it.</summary>
    private Guid? NextAfter(Guid id)
    {
        var shown = Window;
        var index = shown.ToList().FindIndex(t => t.Id == id);
        if (index < 0)
        {
            return null;
        }

        return index + 1 < shown.Count ? shown[index + 1].Id : index > 0 ? shown[index - 1].Id : null;
    }

    // Delete tag: the next row, else the h2 Tags (§6.6). The row's name button when the caller can edit, else its
    // menu trigger — either is a control in that row.
    private async Task FocusAfterRemovalAsync(Guid? next)
    {
        StateHasChanged();
        if (next is { } id)
        {
            await FocusAsync(_canUpdate ? NameButtonId(id) : MenuTriggerId(id));
        }
        else
        {
            await FocusAsync(HeadingId);
        }
    }

    // ── Create and edit ────────────────────────────────────────────────────────────────────────────

    private bool _formOpen;
    private Guid _formKey;
    private ExistingNewsTag? _editing;
    private string? _draftName;
    private string? _draftDescription;
    private string? _draftNameError;
    private string? _draftDescriptionError;
    private string? _draftIcon;
    private string? _draftIconError;
    private string? _draftColor;
    private bool _draftColorValid = true;
    private bool _draftColorShowErrors;
    private string? _draftColorError;
    private string? _formAlert;
    private bool _saving;
    private Guid? _createdId;
    private OdsTagColorPicker? _colorPicker;

    private void AddClicked()
    {
        if (!_canCreate)
        {
            return;
        }

        OpenForm(null);
    }

    private void EditClicked(ExistingNewsTag tag)
    {
        if (!_canUpdate)
        {
            return;
        }

        OpenForm(tag);
    }

    private void OpenForm(ExistingNewsTag? tag)
    {
        _editing = tag;
        _draftName = tag?.Name;
        _draftDescription = tag?.Description;
        _draftNameError = null;
        _draftDescriptionError = null;
        _draftIcon = tag is null ? null : TagIcons.Normalize(tag.Icon);
        _draftIconError = null;
        _draftColor = UserColor.Canonical(tag?.Color);
        _draftColorValid = true;
        _draftColorShowErrors = false;
        _draftColorError = null;
        _formAlert = null;
        _createdId = null;
        _formKey = Guid.NewGuid();
        _formOpen = true;
    }

    private void OnDraftNameChanged(string? value) { _draftName = value; _draftNameError = null; }

    private void OnDraftDescriptionChanged(string? value) { _draftDescription = value; _draftDescriptionError = null; }

    private void OnDraftIconChanged(string? value) { _draftIcon = value; _draftIconError = null; }

    private void OnDraftColorChanged(string? value) { _draftColor = value; _draftColorError = null; }

    private void OnDraftColorValidChanged(bool valid)
    {
        _draftColorValid = valid;
        if (valid)
        {
            _draftColorShowErrors = false;
        }
    }

    /// <summary>The client mirror of the DTO annotations (§8.1); the server stays the enforcer and wins.</summary>
    internal static (string? Name, string? Description) Validate(string? name, string? description)
    {
        string? nameError = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            nameError = "Give the tag a name.";
        }
        else if (name.Trim().Length > NewsLimits.MaxTagNameLength)
        {
            nameError = $"Keep it under {NewsLimits.MaxTagNameLength} characters.";
        }

        var descriptionError = description is { } d && d.Trim().Length > NewsLimits.MaxTagDescriptionLength
            ? $"Keep it under {NewsLimits.MaxTagDescriptionLength} characters."
            : null;
        return (nameError, descriptionError);
    }

    private async Task<bool> SaveAsync()
    {
        var editing = _editing;
        if (editing is null ? !_canCreate : !_canUpdate)
        {
            return false;
        }

        (_draftNameError, _draftDescriptionError) = Validate(_draftName, _draftDescription);
        _formAlert = null;
        if (_draftNameError is not null || _draftDescriptionError is not null)
        {
            StateHasChanged();
            await FocusAsync(_draftNameError is not null ? "nt-form-name" : "nt-form-description");
            return false;
        }

        if (!_draftColorValid)
        {
            _draftColorShowErrors = true;
            StateHasChanged();
            if (_colorPicker is not null)
            {
                await _colorPicker.FocusErrorAsync();
            }

            return false;
        }

        var name = _draftName!.Trim();
        var description = string.IsNullOrWhiteSpace(_draftDescription) ? null : _draftDescription.Trim();
        var icon = TagIcons.Normalize(_draftIcon);
        var color = UserColor.Canonical(_draftColor);

        _saving = true;
        StateHasChanged();
        ApiResult<ExistingNewsTag> result;
        try
        {
            result = editing is null
                ? await News.CreateTagAsync(new NewNewsTag { Name = name, Description = description, Icon = icon, Color = color })
                : await News.UpdateTagAsync(editing.Id, new UpdateNewsTag { Name = name, Description = description, Icon = icon, Color = color });
        }
        finally
        {
            _saving = false;
        }

        if (result.IsSuccess && result.Value is { } saved)
        {
            Replace(saved);
            Catalogue.InvalidateTags();
            _createdId = editing is null ? saved.Id : null;
            Say(NewsFeedback.Success(Snackbar, editing is null ? "Tag created" : "Tag saved"));
            StateHasChanged();
            return true;
        }

        // The server's verdict wins over the client's (§8.1), on the field it names.
        if (NewsFeedback.Is(result.Problem, NewsErrorCodes.TagDuplicate))
        {
            _draftNameError = "A news tag with this name exists.";
        }
        else if (NewsFeedback.Is(result.Problem, NewsErrorCodes.TagLimit))
        {
            // The tag cap is a compile-time constant, not a setting: no "raise it in Settings" sentence (§7.2).
            _formAlert = "The limit has been reached.";
        }
        else if (result.Status == HttpStatusCode.NotFound && editing is not null)
        {
            _formAlert = "This tag was deleted.";
        }
        else if (result.Problem?.ErrorFor("name") is { } nameError)
        {
            _draftNameError = nameError;
        }
        else if (result.Problem?.ErrorFor("description") is { } descriptionError)
        {
            _draftDescriptionError = descriptionError;
        }
        else if (result.Problem?.ErrorFor("icon") is { } iconError)
        {
            _draftIconError = iconError;
        }
        else if (result.Problem?.ErrorFor("color") is { } colorError)
        {
            _draftColorError = colorError;
        }
        else
        {
            Say(NewsFeedback.Failure(Snackbar, result.Problem, editing is null ? "Couldn't create the tag" : "Couldn't save the tag"));
            return false;
        }

        StateHasChanged();
        if (_draftNameError is not null)
        {
            await FocusAsync("nt-form-name");
        }
        else if (_draftDescriptionError is not null)
        {
            await FocusAsync("nt-form-description");
        }

        return false;
    }

    // After a create, focus moves to the new row's name button, its edit trigger (§3.4). The window grows to hold it.
    private async Task OnFormSaved()
    {
        if (_createdId is not { } id)
        {
            return;
        }

        var index = Sorted.ToList().FindIndex(t => t.Id == id);
        if (index >= _window)
        {
            _window = index + 1;
        }

        StateHasChanged();
        await FocusAsync(_canUpdate ? NameButtonId(id) : MenuTriggerId(id));
    }

    // ── Delete ─────────────────────────────────────────────────────────────────────────────────────

    private ExistingNewsTag? _deleting;
    private bool _deleteOpen;
    private Guid _deleteKey;
    private bool _deleteAck;
    private bool _deleteBusy;
    private string? _deleteError;

    private void OpenDelete(ExistingNewsTag tag)
    {
        if (!_canDelete)
        {
            return;
        }

        _deleting = tag;
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

    /// <summary>"Used by 3 feeds and 41 articles." — counts only; the dialog never names a feed or an article.</summary>
    internal static string UsedBySentence(ExistingNewsTag tag)
    {
        var parts = new List<string>(2);
        if (tag.FeedCount > 0)
        {
            parts.Add(NewsFeedback.Count(tag.FeedCount, "feed", "feeds"));
        }

        if (tag.ArticleCount > 0)
        {
            parts.Add(NewsFeedback.Count(tag.ArticleCount, "article", "articles"));
        }

        return $"Used by {string.Join(" and ", parts)}.";
    }

    /// <summary>Remove from all and delete is offered only with the claim for every link class present (AC 16).</summary>
    private bool CanDetach(ExistingNewsTag tag) =>
        (tag.FeedCount == 0 || _canDetachFeeds) && (tag.ArticleCount == 0 || _canDetachArticles);

    private IEnumerable<string> MissingDetachPermissions(ExistingNewsTag tag)
    {
        if (tag.FeedCount > 0 && !_canDetachFeeds)
        {
            yield return "You need permission to edit feeds to remove this tag from them.";
        }

        if (tag.ArticleCount > 0 && !_canDetachArticles)
        {
            yield return "You need permission to edit articles to remove this tag from them.";
        }
    }

    private async Task DeleteAsync(bool detach)
    {
        if (_deleting is not { } tag || _deleteBusy || (detach && !_deleteAck))
        {
            return;
        }

        _deleteBusy = true;
        _deleteError = null;
        StateHasChanged();
        var result = await News.DeleteTagAsync(tag.Id, detach);
        _deleteBusy = false;

        if (result.IsSuccess || result.Status == HttpStatusCode.NotFound)
        {
            var next = NextAfter(tag.Id);
            _tags.RemoveAll(t => t.Id == tag.Id);
            Catalogue.Invalidate();
            _deleteOpen = false;
            _deleting = null;
            Say(result.IsSuccess ? NewsFeedback.Success(Snackbar, "Tag deleted") : NewsFeedback.Gone(Snackbar));
            await FocusAfterRemovalAsync(next);
            return;
        }

        if (NewsFeedback.Is(result.Problem, NewsErrorCodes.TagInUse))
        {
            // The counts the page held were stale: the dialog turns into the in-use explanation with the server's.
            var feeds = result.Problem!.TryGetExtensionValue<int>("feedCount", out var f) ? f : tag.FeedCount;
            var articles = result.Problem.TryGetExtensionValue<int>("articleCount", out var a) ? a : tag.ArticleCount;
            var updated = tag with { FeedCount = feeds, ArticleCount = articles };
            Replace(updated);
            _deleting = updated;
            _deleteAck = false;
        }
        else if (result.Status == HttpStatusCode.Forbidden && detach)
        {
            _deleteError = string.Join(" ", MissingDetachPermissions(tag)) is { Length: > 0 } missing
                ? missing
                : "You need permission to edit feeds and articles to remove this tag from them.";
        }
        else
        {
            _deleteError = NewsFeedback.IsBusy(result.Problem) ? NewsFeedback.BusyText : $"Couldn't delete the tag: {result.Error}";
        }

        StateHasChanged();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private void Say(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _ = _announcer?.Announce(text);
        }
    }

    private async Task FocusAsync(string id)
    {
        try
        {
            await JS.InvokeVoidAsync("odsFocusById", id);
        }
        catch (JSException)
        {
            // The element is not rendered (a concurrent re-render): only the focus courtesy is lost.
        }
    }
}
