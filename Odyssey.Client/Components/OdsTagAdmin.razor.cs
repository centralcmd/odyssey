using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

public partial class OdsTagAdmin<TRow>
{
    // ── Configuration (supplied by the route pages) ─────────────────────────────
    [Parameter, EditorRequired] public string Title { get; set; } = "Tags";
    [Parameter, EditorRequired] public string Noun { get; set; } = "tags";
    [Parameter, EditorRequired] public string PageStateKey { get; set; } = default!;
    [Parameter, EditorRequired] public string CreateClaim { get; set; } = default!;
    [Parameter, EditorRequired] public string UpdateClaim { get; set; } = default!;
    [Parameter, EditorRequired] public string DeleteClaim { get; set; } = default!;
    [Parameter] public string SearchPlaceholder { get; set; } = "Search name or description…";
    [Parameter] public string NamePlaceholder { get; set; } = "e.g. Groceries";
    [Parameter] public string ModalSubtitle { get; set; } = "Tags group records by category.";
    [Parameter] public string EmptyDesc { get; set; } = "Create your first tag.";

    // Accessors over the concrete tag DTO (its PK name differs between families).
    [Parameter, EditorRequired] public Func<TRow, Guid> Id { get; set; } = default!;
    [Parameter, EditorRequired] public Func<TRow, string> Name { get; set; } = default!;
    [Parameter, EditorRequired] public Func<TRow, string?> Description { get; set; } = default!;
    [Parameter, EditorRequired] public Func<TRow, DateTime?> Archived { get; set; } = default!;

    // ── State ────────────────────────────────────────────────────────────────────
    private List<TRow> _tags = [];
    private List<TRow> _allTags = [];
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";

    // Server pagination (OdsPager): 1-based page + rows-per-page; TotalCount from the PagedResult.
    private int _page = 1;
    private int _pageSize = 25;
    private int _totalCount;
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;

    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _statusFilter = [];

    // Sort (§6.7): Name is the one meaningful key → the control renders as a direction toggle only.
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<TRow>> _sortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text },
    ];

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [new("active", "Active"), new("archived", "Archived")];

    // Overview/breakdown reflect the whole dataset (issue #277 follow-up): derived from the unfiltered
    // _allTags, not the server-filtered display list.
    private IReadOnlyList<OdsBreakdownRow> StatusRows => OdsBreakdown.StatusRows(
        _allTags, t => Archived(t) is not null ? "archived" : "active",
        new OdsBreakdownDef<string>("active", "Active", "income", "task_alt"),
        new OdsBreakdownDef<string>("archived", "Archived", "outline", "inventory_2"));

    private int _activeCount => _allTags.Count(t => Archived(t) is null);
    private int _archivedCount => _allTags.Count - _activeCount;
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _statusFilter.Count > 0;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        StateHasChanged();
        await LoadPermissionsAsync();
        await RefreshAsync();
    }

    // Full refresh: unfiltered overview set + server-filtered display list, back to page 1.
    private async Task RefreshAsync()
    {
        await RefreshOverviewAsync();
        await ReloadAsync();
    }

    // The overview set only — for the edits that should leave the reader on the current page.
    private async Task RefreshOverviewAsync() =>
        _allTags = (await Tags.ListAllAsync()).ItemsOrToast(Snackbar, Noun);

    // ── Page-state persistence (search section + filters) ─────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<TagsPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(TagsPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _pageSize = state.PageSize == 0 ? 25 : state.PageSize;
    }

    private TagsPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        PageSize = _pageSize,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await ReloadAsync(); }

    private sealed class TagsPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = 25;
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(CreateClaim);
        _canUpdate = user.HasPermission(UpdateClaim);
        _canDelete = user.HasPermission(DeleteClaim);
    }

    // Server-side fetch (issue #277): name search + status filter + name sort applied by the API.
    private async Task GetTags()
    {
        // First load blanks the table for a spinner; every later fetch keeps the rows and shows the bar.
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var load = (await Tags.ListAsync(
            _page, _pageSize,
            search: _search,
            status: _statusFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc"))
            .PagedOrToast(Snackbar, Noun);
        if (load.IsSuccess)
        {
            _tags = [.. load.Items];
            _totalCount = load.TotalCount;
            _loadError = false;
            _announce = _totalCount == 0 ? "No tags match your filters."
                : $"Showing {OdsPagerMath.FirstShown(_page, _pageSize, _totalCount)}–{OdsPagerMath.LastShown(_page, _pageSize, _totalCount)} of {_totalCount} tag{(_totalCount == 1 ? "" : "s")}.";
        }
        else
        {
            _loadError = true;
            _announce = "Couldn't load tags.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // Reset to page 1, then fetch — for any search / filter / sort / size change. Page navigation
    // calls GetTags directly so it keeps the requested page.
    private Task ReloadAsync()
    {
        _page = 1;
        return GetTags();
    }

    private Task OnPageChanged(int page)
    {
        _page = page;
        return GetTags();
    }

    private Task OnPageSizeChanged(int size)
    {
        _pageSize = size;
        _page = 1;
        PersistPageState();
        return GetTags();
    }

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _statusFilter = [];
        PersistPageState();
        await ReloadAsync();
    }

    private IReadOnlyList<OdsMenuItem> BuildActions(TRow t, OdsRecordActionContext ctx)
    {
        var items = new List<OdsMenuItem>
        {
            new()
            {
                Icon = ctx.Expanded ? "close" : "expand_more",
                Label = ctx.Expanded ? "Collapse" : "View details",
                OnClick = EventCallback.Factory.Create(this, ctx.Toggle),
            },
        };

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, () => EditClicked(t)) });
            items.Add(new OdsMenuItem
            {
                Icon = Archived(t) is not null ? "unarchive" : "archive",
                Label = Archived(t) is not null ? "Restore" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(t)),
            });
        }

        items.Add(new OdsMenuItem { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => CopyId(Id(t))) });

        if (_canDelete)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, ctx.Remove) });
        }

        return items;
    }

    // ── Create / edit ───────────────────────────────────────────────────────────
    // The dialog's draft lives here rather than in the dialog so create and edit can share one
    // instance. _editId is the mode switch: Guid.Empty = create.
    private bool _formOpen;
    private Guid _formKey;
    private Guid _editId;
    private bool _editArchived;
    private string? _draftName;
    private string? _draftDescription;
    private bool _draftNameError;

    private bool _isEditing => _editId != Guid.Empty;

    private void AddClicked()
    {
        if (!_canCreate)
            return;

        _editId = Guid.Empty;
        _editArchived = false;
        _draftName = null;
        _draftDescription = null;
        _draftNameError = false;
        _formKey = Guid.NewGuid();
        _formOpen = true;
    }

    private void EditClicked(TRow tag)
    {
        if (!_canUpdate)
            return;

        _editId = Id(tag);
        _editArchived = Archived(tag) is not null;
        _draftName = Name(tag);
        _draftDescription = Description(tag);
        _draftNameError = false;
        _formKey = Guid.NewGuid();
        _formOpen = true;
    }

    private async Task<bool> SaveAsync()
    {
        if (_isEditing ? !_canUpdate : !_canCreate)
            return false;

        _draftNameError = string.IsNullOrWhiteSpace(_draftName);
        if (_draftNameError)
            return false;

        // Archive / restore is a separate row action, so an edit preserves the current state.
        var body = new TagWrite(
            _draftName!.Trim(),
            string.IsNullOrWhiteSpace(_draftDescription) ? null : _draftDescription!.Trim(),
            Archived: _isEditing && _editArchived);

        return Invalidated(_isEditing
            ? (await Tags.UpdateAsync(_editId, body)).Toast(Snackbar, "Update failed", "Tag updated.")
            : (await Tags.CreateAsync(body)).Toast(Snackbar, "Unable to create tag", "Tag created."));
    }

    // Transaction tags are cached for the whole session so the pickers don't re-fetch them per dialog
    // open (issue #372); a write here has to drop that cache. The other three tag resources this
    // component serves aren't cached, hence the type test rather than an unconditional invalidate.
    private bool Invalidated(bool saved)
    {
        if (saved && typeof(TRow) == typeof(ExistingTransactionTag))
            ReferenceData.InvalidateTransactionTags();
        return saved;
    }

    // A create can land anywhere in the sort order, so it returns to page 1; an edit leaves the
    // reader where they were — only the overview set and the visible window reload.
    private Task OnFormSaved() => _isEditing ? RefreshCurrentPageAsync() : RefreshAsync();

    // ── Archive / delete ────────────────────────────────────────────────────────
    // Archive/restore replays the tag through the full PUT contract, flipping only the archival flag.
    private async Task ToggleArchive(TRow tag)
    {
        if (!_canUpdate)
            return;

        var body = new TagWrite(Name(tag), Description(tag), Archived: Archived(tag) is null);
        if (Invalidated((await Tags.UpdateAsync(Id(tag), body)).Toast(Snackbar, "Update failed", "Tag updated.")))
            await RefreshCurrentPageAsync();

        StateHasChanged();
    }

    private async Task HandleDelete(object key)
    {
        if (!_canDelete)
            return;

        var tag = _tags.FirstOrDefault(t => Id(t).Equals(key));
        if (tag is null)
            return;

        if (Invalidated((await Tags.DeleteAsync(Id(tag))).Toast(Snackbar, "Delete failed", "Tag deleted.")))
        {
            // Full refresh, not a local Remove: the delete changes the total, so the pager and the
            // current page have to be re-fetched or the page renders short against a stale count.
            await RefreshAsync();
        }

        StateHasChanged();
    }

    private async Task RefreshCurrentPageAsync()
    {
        await RefreshOverviewAsync();
        await GetTags();
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), "Tag ID copied to clipboard.");
}
