using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

public partial class TasksPage
{
    // ── Data (the whole shared list; filtering/partitioning happen client-side so the board can show
    //    every column at once, matching the design system) ────────────────────────────────────────
    private List<JournalTaskSummary> _allTasks = [];
    private readonly Dictionary<Guid, ExistingJournalTask> _details = new();
    private readonly Dictionary<Guid, FileMetadataResponse?> _fileMeta = new();

    private List<ExistingJournalTaskTag> _tags = [];
    private Dictionary<Guid, ExistingJournalTaskTag> _tagById = new();
    private IReadOnlyList<OdsOption> _tagOptions = [];

    // ── UI / permissions ──────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _loadError;
    private string _announce = "";
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadFiles;

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "tasks-page";
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _tagFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];
    private string _view = "board";
    private OdsTableSort _sort = DefaultSort;

    private static readonly IReadOnlyList<JournalTaskStatus> BoardKeys =
        [JournalTaskStatus.Backlog, JournalTaskStatus.Doing, JournalTaskStatus.Done];

    private static readonly IReadOnlyList<OdsOption> _statusOptions = JournalTaskUi.StatusOptions;

    private static readonly IReadOnlyList<OdsOption> _viewOptions =
        [new("board", "Board"), new("list", "List")];

    // ── Sort (list view; client-side) ───────────────────────────────────────────
    private static readonly OdsTableSort DefaultSort = new("status", OdsSortDirection.Asc);
    private static readonly IReadOnlyList<OdsSortField<JournalTaskSummary>> _sortFields =
    [
        new() { Key = "status", Label = "Status", Type = OdsSortType.Status, SortValue = t => StatusOrder(t.Status) },
        new() { Key = "position", Label = "Order", Type = OdsSortType.Number, SortValue = t => t.Position },
        new() { Key = "title", Label = "Title", Type = OdsSortType.Text, SortValue = t => t.Title.ToLowerInvariant() },
        new() { Key = "deadline", Label = "Deadline", Type = OdsSortType.Date, SortValue = t => t.Deadline is { } d ? d.ToDateTime(TimeOnly.MinValue) : (IComparable?)null },
    ];

    // Doing first, then Backlog, Done, Archived — the DS list ordering.
    private static int StatusOrder(JournalTaskStatus s) => s switch
    {
        JournalTaskStatus.Doing => 0,
        JournalTaskStatus.Backlog => 1,
        JournalTaskStatus.Done => 2,
        _ => 3,
    };

    // ── Computed ─────────────────────────────────────────────────────────────────
    private int Count(JournalTaskStatus s) => _allTasks.Count(t => t.Status == s);
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _tagFilter.Count > 0 || _statusFilter.Count > 0;
    private bool _showArchived => _statusFilter.Count == 0 || _statusFilter.Contains(nameof(JournalTaskStatus.Archived));

    private bool MatchQ(JournalTaskSummary t) =>
        string.IsNullOrWhiteSpace(_search)
        || t.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
        || (t.Snippet?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false);
    private bool MatchTag(JournalTaskSummary t) =>
        _tagFilter.Count == 0 || t.TagIds.Any(id => _tagFilter.Contains(id.ToString()));

    // No status filter → all three columns. Otherwise only the selected board columns — which may be
    // empty if the filter names only off-board statuses (e.g. Archived only), in which case the board is
    // hidden and just the Archived section shows.
    private IReadOnlyList<JournalTaskStatus> BoardColumns =>
        _statusFilter.Count == 0
            ? [.. BoardKeys]
            : [.. BoardKeys.Where(k => _statusFilter.Contains(k.ToString()))];

    private IReadOnlyList<JournalTaskSummary> BoardTasks =>
        [.. _allTasks.Where(t => t.Status != JournalTaskStatus.Archived && BoardColumns.Contains(t.Status) && MatchQ(t) && MatchTag(t))];

    private IReadOnlyList<JournalTaskSummary> ArchivedTasks =>
        [.. _allTasks.Where(t => t.Status == JournalTaskStatus.Archived && MatchQ(t) && MatchTag(t))];

    private IReadOnlyList<JournalTaskSummary> ListTasks
    {
        get
        {
            var want = _statusFilter.Count > 0
                ? _statusFilter.Select(s => Enum.TryParse<JournalTaskStatus>(s, out var e) ? e : (JournalTaskStatus?)null).Where(e => e is not null).Cast<JournalTaskStatus>().ToHashSet()
                : [.. Enum.GetValues<JournalTaskStatus>()];
            var basis = _allTasks.Where(t => want.Contains(t.Status) && MatchQ(t) && MatchTag(t)).ToList();
            return OdsSortHelpers.SortRows(basis, _sortFields, _sort, t => t.JournalTaskId.ToString());
        }
    }

    private IEnumerable<string> TagNames(JournalTaskSummary t) =>
        t.TagIds.Select(id => _tagById.GetValueOrDefault(id)?.Name).Where(n => n is not null).Cast<string>();

    // ── Lifecycle ────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        await LoadTags();
        await LoadTasks();
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.TasksCreate);
        _canUpdate = user.HasPermission(PermissionClaims.TasksUpdate);
        _canDelete = user.HasPermission(PermissionClaims.TasksDelete);
        _canReadFiles = user.HasPermission(PermissionClaims.FilesRead);
    }

    private async Task LoadTags()
    {
        _tags = (await TaskTags.ListAllAsync()).ItemsOrToast(Snackbar, "task tags");
        _tagById = _tags.ToDictionary(t => t.JournalTaskTagId);
        _tagOptions =
        [
            .. _tags.Where(t => t.Archived is null)
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new OdsOption(t.JournalTaskTagId.ToString(), t.Name)),
        ];
    }

    // The whole shared list, all statuses (incl. Archived — the server hides Archived unless the status
    // filter names it, so we request every status and partition client-side for the board + list).
    private static readonly string[] AllStatuses =
        [nameof(JournalTaskStatus.Backlog), nameof(JournalTaskStatus.Doing), nameof(JournalTaskStatus.Done), nameof(JournalTaskStatus.Archived)];

    private IJSObjectReference? _boardJs;
    private Guid? _pendingFocusCardId;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pendingFocusCardId is not { } cardId)
        {
            return;
        }

        _pendingFocusCardId = null;
        try
        {
            _boardJs ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/journal-board.js");
            await _boardJs.InvokeVoidAsync("focusMove", cardId.ToString());
        }
        catch (Exception)
        {
            // Best-effort focus return; the move was already announced via the live region.
        }
    }

    private async Task LoadTasks()
    {
        // Track failure explicitly: ItemsOrToast falls back to [], which is indistinguishable from a
        // genuinely empty set and would render the onboarding empty state after a 500.
        var result = await Tasks.ListAsync(statuses: AllStatuses);
        _allTasks = result.ItemsOrToast(Snackbar, "tasks");
        _loadError = !result.IsSuccess;

        _announce = _loadError ? "Couldn't load tasks."
            : $"{_allTasks.Count} task{(_allTasks.Count == 1 ? "" : "s")} loaded.";
        _isLoading = false;
        StateHasChanged();
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<TasksPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(TasksPageState state)
    {
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _tagFilter = state.TagFilter ?? [];
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _view = state.View == "list" ? "list" : "board";
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
    }

    private TasksPageState BuildPageState() => new()
    {
        SearchOpen = _searchOpen,
        Search = _search,
        TagFilter = [.. _tagFilter],
        StatusFilter = [.. _statusFilter],
        View = _view,
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); StateHasChanged(); }
    private void OnTagFilterChanged(IReadOnlyCollection<string> values) { _tagFilter = values ?? []; PersistPageState(); StateHasChanged(); }
    private void OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); StateHasChanged(); }
    private void OnViewChanged(string value) { _view = value == "list" ? "list" : "board"; PersistPageState(); StateHasChanged(); }
    private void OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); StateHasChanged(); }

    private void ClearFilters()
    {
        _search = string.Empty;
        _tagFilter = [];
        _statusFilter = [];
        PersistPageState();
        StateHasChanged();
    }

    private sealed class TasksPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TagFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string View { get; set; } = "board";
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
    }

    // ── Detail / file hydration ──────────────────────────────────────────────────
    private async Task<ExistingJournalTask?> EnsureDetail(Guid id)
    {
        if (_details.TryGetValue(id, out var cached))
            return cached;
        var task = await Tasks.GetAsync(id);
        if (task is null)
            return null;
        _details[id] = task;
        await HydrateFilesAsync(task);
        return task;
    }

    private async Task HydrateFilesAsync(ExistingJournalTask task)
    {
        if (!_canReadFiles)
            return;
        foreach (var a in task.Attachments)
        {
            if (_fileMeta.ContainsKey(a.FileId))
                continue;
            _fileMeta[a.FileId] = await Files.GetMetadataAsync(a.FileId);
        }
    }

    // ── Mutations (all through PUT, re-projecting the loaded task) ─────────────────
    private async Task OnMove(OdsTaskBoard<JournalTaskSummary>.Move move)
    {
        var detail = await EnsureDetail(move.Id);
        if (detail is null) return;
        var update = JournalTaskWrite.FromDetail(detail, move.ToStatus, move.ToIndex);
        if ((await Tasks.UpdateAsync(move.Id, update)).Toast(Snackbar, "Unable to move task", null))
        {
            _announce = $"{detail.Title} moved to {move.ToStatus}.";
            await ReloadTask(move.Id);
            // The reload re-renders the board, dropping keyboard focus; return it to the moved card.
            _pendingFocusCardId = move.Id;
        }
    }

    private async Task SetStatus(JournalTaskSummary t, JournalTaskStatus target)
    {
        if (!_canUpdate) return;
        var detail = await EnsureDetail(t.JournalTaskId);
        if (detail is null) return;
        var update = JournalTaskWrite.FromDetail(detail, target);
        if ((await Tasks.UpdateAsync(t.JournalTaskId, update)).Toast(Snackbar, "Unable to update task", $"Task set to {target}."))
        {
            _announce = $"{t.Title} set to {target}.";
            await ReloadTask(t.JournalTaskId);
        }
    }

    private async Task ArchiveTask(JournalTaskSummary t)
    {
        if (!_canUpdate) return;
        var detail = await EnsureDetail(t.JournalTaskId);
        if (detail is null) return;
        var archiving = t.Status != JournalTaskStatus.Archived;
        var target = archiving ? JournalTaskStatus.Archived : JournalTaskStatus.Backlog;
        var update = JournalTaskWrite.FromDetail(detail, target);
        if ((await Tasks.UpdateAsync(t.JournalTaskId, update)).Toast(Snackbar,
                archiving ? "Unable to archive task" : "Unable to unarchive task",
                archiving ? "Task archived." : "Task unarchived."))
        {
            _announce = archiving ? "Task archived." : "Task unarchived.";
            await ReloadTask(t.JournalTaskId);
        }
    }

    private async Task ConfirmDelete(Guid id)
    {
        if (!_canDelete) return;
        var task = _allTasks.FirstOrDefault(t => t.JournalTaskId == id);
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete task",
            $"Permanently delete '{task?.Title}'? This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");
        if (confirmed == true && (await Tasks.DeleteAsync(id)).Toast(Snackbar, "Delete failed", "Task deleted."))
        {
            _announce = "Task deleted.";
            _details.Remove(id);
            await LoadTasks();
        }
    }

    private async Task ReloadTask(Guid id)
    {
        _details.Remove(id);
        await LoadTasks();
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), "Task ID copied.");

    // ── VTODO/.ics export + import (issue #337) ────────────────────────────────────
    // Import creates or updates, so it needs BOTH claims (mirrors the server's stacked policies).
    private bool _canImport => _canCreate && _canUpdate;
    private bool _exporting;
    private bool _importOpen;

    // The set the "Export filtered" action would produce, computed with the SAME semantics the server
    // applies: search + tag filter, and either the explicit status filter or (when none) archived hidden.
    private bool MatchStatusFilter(JournalTaskSummary t) =>
        _statusFilter.Count == 0 ? t.Status != JournalTaskStatus.Archived : _statusFilter.Contains(t.Status.ToString());
    private int FilteredCount => _allTasks.Count(t => MatchQ(t) && MatchTag(t) && MatchStatusFilter(t));

    private enum TaskExportScope { All, Filtered }

    // All ignores the page filters and exports every task (all four statuses, incl. archived); Filtered
    // forwards the current search/tag/status set so the download matches the visible view. The browser
    // download is handled by the shared downloadFileFromBytes interop (same as the calendar export).
    private async Task ExportTasksAsync(TaskExportScope scope)
    {
        if (_exporting) return;
        _exporting = true;
        _announce = "Preparing task export…";
        try
        {
            var result = scope == TaskExportScope.Filtered
                ? await Tasks.ExportIcsAsync(_search, [.. _tagFilter], [.. _statusFilter])
                : await Tasks.ExportIcsAsync(statuses: AllStatuses);
            if (result.OrToast(Snackbar, "Unable to export tasks") is not { } file) return;

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/calendar");
            _announce = $"Exported {file.FileName}.";
            Snackbar.Add($"Exported {file.FileName}.", Severity.Success);
        }
        finally
        {
            _exporting = false;
        }
    }

    // Per-task export (card + row menu): a single-VTODO .ics. Pairs the id with all statuses so an
    // archived task still exports (the export otherwise hides archived when no status filter is given).
    private async Task ExportTaskAsync(JournalTaskSummary t)
    {
        if (_exporting) return;
        _exporting = true;
        try
        {
            var result = await Tasks.ExportIcsAsync(statuses: AllStatuses, ids: [t.JournalTaskId.ToString()]);
            if (result.OrToast(Snackbar, "Unable to export the task") is not { } file) return;

            await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/calendar");
            _announce = $"Exported {t.Title}.";
            Snackbar.Add($"Exported {file.FileName}.", Severity.Success);
        }
        finally
        {
            _exporting = false;
        }
    }

    private void OpenImport()
    {
        if (!_canImport) return;
        _importOpen = true;
    }

    private async Task OnImported()
    {
        _details.Clear();
        await LoadTasks();
    }

    // ── Create / edit dialog ──────────────────────────────────────────────────────
    private bool _dialogOpen;
    private Guid _dialogKey;
    private ExistingJournalTask? _editTask;
    private IReadOnlyList<OdsUploadFile> _editUploads = [];

    private void AddClicked()
    {
        if (!_canCreate) return;
        _editTask = null;
        _editUploads = [];
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task OpenEdit(JournalTaskSummary t)
    {
        if (!_canUpdate) return;
        var detail = await EnsureDetail(t.JournalTaskId);
        if (detail is null) return;
        _editTask = detail;
        _editUploads =
        [
            .. detail.Attachments.Select(a =>
            {
                var meta = _fileMeta.GetValueOrDefault(a.FileId);
                return new OdsUploadFile
                {
                    Uid = a.FileId.ToString(),
                    Name = meta?.FileName ?? a.FileId.ToString(),
                    Kind = "File",
                    SizeBytes = meta?.SizeBytes,
                };
            }),
        ];
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task OnTaskSaved()
    {
        _details.Clear();
        await LoadTasks();
    }

    public async ValueTask DisposeAsync()
    {
        if (_boardJs is not null)
        {
            try { await _boardJs.DisposeAsync(); } catch (Exception) { /* JS already gone on teardown */ }
        }
    }
}
