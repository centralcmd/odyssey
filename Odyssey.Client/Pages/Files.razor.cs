using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages;

public partial class Files
{
    private bool _isLoading = true;
    private bool _loadError;

    private List<FileListItem> _files = [];
    private List<FileListItem> _allFiles = [];

    // Client-side pagination (OdsPager) over the client-filtered FileRows — see the render block.
    private int _page = 1;
    private int _pageSize = OdsPageSizes.Default[0];
    private string _announce = "";

    // Live client-side filters over the loaded set (the specimen filters live too).
    private const string PageStateKey = "files-page";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _kindFilter = [];

    // ── Files export (ZIP) — gated on files.export-all, same as the Settings page's card ──────
    private bool _filesExportAvailable;
    private bool _isExportingFiles;
    private string? _exportScope; // null | "all" | "filtered"

    // Sort (§6.11): the curated keys already back the OdsFilesTable columns; one OdsTableSort syncs
    // the toolbar control with the header sort. Default: Uploaded, newest first.
    private static readonly OdsTableSort DefaultSort = new("uploaded", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<OdsFilesRow>> _sortFields =
    [
        new() { Key = "uploaded", Label = "Uploaded", Type = OdsSortType.Date },
        new() { Key = "name", Label = "File name", Type = OdsSortType.Text },
        new() { Key = "size", Label = "Size", Type = OdsSortType.Number },
        new() { Key = "kind", Label = "Type", Type = OdsSortType.Status },
    ];

    // Overview reflects the whole file set (issue #277 follow-up), not the server-searched/kind-filtered view.
    private IReadOnlyList<OdsBreakdownRow> TypeRows => OdsBreakdown.TypeRows(
        _allFiles, f => KindLabel(f.ContentType), ["PDF", "Image", "File"],
        k => { var m = KindMeta(k); return (m.Icon, m.Color, k); });

    private bool _canUpdate;
    private bool _canDelete;

    private PreviewState? _preview;
    private Guid _previewKey;
    private bool _previewOpen;

    private sealed record PreviewState(
        string BlobUrl, string ContentType, string FileName, long SizeBytes,
        DateTime UploadedAtUtc, AccountFileType FileType);

    private readonly HashSet<Guid> _busyFiles = [];

    private static readonly string[] PreviewableContentTypes =
        { "image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp", "application/pdf" };

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canUpdate = user.HasPermission(PermissionClaims.FilesUpdate);
        _canDelete = user.HasPermission(PermissionClaims.FilesDelete);
        _filesExportAvailable = user.HasPermission(PermissionClaims.FilesExportAll);

        await RestorePageStateAsync();
        StateHasChanged();
        await RefreshAsync();
        _isLoading = false;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
            StateHasChanged();
    }

    // ── Data loading ──
    // Full refresh: unfiltered overview set + server-searched/sorted display list.
    private async Task RefreshAsync()
    {
        _allFiles = (await FilesApi.ListAllAsync()).ItemsOrToast(Snackbar, "files");
        await LoadFilesAsync();
    }

    // Server-side (issue #277): filename search + sort applied by the API; the multi-select Type
    // filter stays client-side over the fetched set (see FilteredFiles).
    private async Task LoadFilesAsync()
    {
        _page = 1; // a new search / sort resets to the first page
        var result = await FilesApi.ListAsync(
            page: 1, pageSize: PagedQuery.SizeAll,
            search: _search,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        var load = result.PagedOrToast(Snackbar, "files");
        if (load.IsSuccess)
        {
            _files = [.. load.Items];
            _loadError = false;
        }
        else
        {
            _files = [];
            _loadError = true;
        }

        Announce();
    }

    // Paging and the Type filter are client-side, so nothing on the wire signals the change —
    // WCAG 2.2 §4.1.3 needs the new result window stated out loud (see OdsLiveAnnouncer).
    private void Announce()
    {
        var count = FilteredFiles.Count;
        var totalPages = Math.Max(1, OdsPagerMath.TotalPages(count, _pageSize));
        _announce = count == 0
            ? "No files match your filters."
            : _pageSize == OdsPageSizes.All
                ? $"Showing all {count} file{(count == 1 ? "" : "s")}."
                : $"Page {Math.Min(_page, totalPages)} of {totalPages}, {count} file{(count == 1 ? "" : "s")}.";
    }

    // ── Page-state persistence (search section + filters) ─────────────────────
    // Type options are derived from the loaded files → restored as-is.
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<FilesPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(FilesPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _kindFilter = state.KindFilter ?? [];
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _pageSize = OdsPageSizes.Restore(state.PageSize);
    }

    private FilesPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        KindFilter = [.. _kindFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        PageSize = _pageSize,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }
    private void OnKindFilterChanged(IReadOnlyCollection<string> values) { _kindFilter = values ?? []; _page = 1; PersistPageState(); Announce(); }

    // The search is server-side and the Type filter client-side, so an empty table can mean either.
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _kindFilter.Count > 0;

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _kindFilter = [];
        _page = 1;
        PersistPageState();
        await LoadFilesAsync();
    }

    private void OnPageChanged(int page) { _page = page; Announce(); StateHasChanged(); }
    private void OnPageSizeChanged(int size) { _pageSize = size; _page = 1; PersistPageState(); Announce(); StateHasChanged(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadFilesAsync(); StateHasChanged(); }

    private sealed class FilesPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> KindFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = OdsPageSizes.Default[0];
    }

    // ── Header sub-line ──
    private string HeaderSubLine
    {
        get
        {
            var count = _allFiles.Count;
            var totalBytes = _allFiles.Sum(f => f.SizeBytes);
            return $"{count} {(count == 1 ? "file" : "files")} · {FormatFileSize(totalBytes)}";
        }
    }

    // ── Filtering / sorting (client-side over the loaded set) ──
    private IReadOnlyList<FileListItem> FilteredFiles
    {
        get
        {
            IEnumerable<FileListItem> files = _files;

            // Search is applied server-side (issue #277); the Type multi-select remains client-side.
            if (_kindFilter.Count > 0)
                files = files.Where(f => _kindFilter.Contains(KindLabel(f.ContentType)));

            return files.ToList();
        }
    }

    // The filtered set mapped to the table's denormalized row shape. OdsFilesTable
    // owns the sort (default: Uploaded, newest first), so the page only filters.
    private IEnumerable<OdsFilesRow> FileRows => FilteredFiles.Select(f => new OdsFilesRow
    {
        Id = f.Id.ToString(),
        Name = f.FileName,
        Kind = KindLabel(f.ContentType),
        SizeBytes = f.SizeBytes,
        UploadedAtUtc = f.UploadedAtUtc,
        Description = f.Description,
    });

    // The table hands back a row; map its id to the owning file to build the menu.
    private FileListItem FileById(string id) => _files.First(f => f.Id.ToString() == id);

    private IReadOnlyList<OdsOption> KindOptions =>
        _files.Select(f => KindLabel(f.ContentType))
              .Distinct()
              .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
              .Select(OdsOption.From)
              .ToList();

    // View details / Edit / Delete are owned by the shared table (expand · inline
    // edit panel · OnDelete); the page supplies only the file-specific items.
    private List<OdsMenuItem> RowMenu(FileListItem file)
    {
        var busy = _busyFiles.Contains(file.Id);
        var items = new List<OdsMenuItem>();

        if (IsPreviewable(file.ContentType))
            items.Add(new OdsMenuItem
            {
                Icon = "visibility",
                Label = "Preview",
                Disabled = busy,
                OnClick = EventCallback.Factory.Create(this, () => ViewFileAsync(file)),
            });

        items.Add(new OdsMenuItem
        {
            Icon = "download",
            Label = "Download",
            Disabled = busy,
            OnClick = EventCallback.Factory.Create(this, () => DownloadFileAsync(file)),
        });

        return items;
    }

    private EventCallback<OdsRecordSaveEventArgs> SaveAction =>
        _canUpdate ? EventCallback.Factory.Create<OdsRecordSaveEventArgs>(this, HandleSaveAsync) : default;

    private EventCallback<OdsFilesRow> DeleteAction =>
        _canDelete ? EventCallback.Factory.Create<OdsFilesRow>(this, row => ConfirmDeleteAsync(FileById(row.Id))) : default;

    private async Task HandleSaveAsync(OdsRecordSaveEventArgs args)
    {
        if (args.Patch is not FilesMetaEditPanel.Patch patch || args.Key is not string key)
            return;

        var file = _files.FirstOrDefault(f => f.Id.ToString() == key);
        if (file is null)
            return;

        var nameChanged = !string.Equals(patch.Name, file.FileName, StringComparison.Ordinal);
        var descriptionChanged = !string.Equals(patch.Description, file.Description, StringComparison.Ordinal);
        if (!nameChanged && !descriptionChanged)
            return;

        var updated = await FilesApi.UpdateMetadataAsync(file.Id, patch.Description, patch.Name);
        if (updated is null)
        {
            Snackbar.Add("Unable to update file.", Severity.Error);
            return;
        }

        Snackbar.Add("File updated.", Severity.Success);
        var index = _files.FindIndex(f => f.Id == updated.Id);
        if (index >= 0)
            _files[index] = new FileListItem(
                updated.Id, updated.FileName, updated.ContentType,
                updated.SizeBytes, updated.UploadedAtUtc, updated.Description);
    }

    // ── Row actions ──
    private async Task ViewFileAsync(FileListItem file)
    {
        if (!_busyFiles.Add(file.Id))
            return;

        StateHasChanged();
        try
        {
            var content = await FilesApi.GetContentAsync(file.Id);
            if (content is null)
            {
                Snackbar.Add("Preview failed.", Severity.Error);
                return;
            }

            var contentType = content.ContentType ?? file.ContentType;
            var blobUrl = await JsRuntime.InvokeAsync<string>("createBlobUrl", content.Bytes, contentType);

            _preview = new PreviewState(blobUrl, contentType, file.FileName, file.SizeBytes,
                file.UploadedAtUtc, AccountFileType.Other);
            _previewKey = Guid.NewGuid();
            _previewOpen = true;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Preview failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _busyFiles.Remove(file.Id);
        }
    }

    private async Task DownloadFileAsync(FileListItem file)
    {
        if (!_busyFiles.Add(file.Id))
            return;

        StateHasChanged();
        try
        {
            var content = await FilesApi.GetContentAsync(file.Id);
            if (content is null)
            {
                Snackbar.Add("Download failed.", Severity.Error);
                return;
            }

            var contentType = content.ContentType ?? "application/octet-stream";
            await JsRuntime.InvokeVoidAsync("downloadFileFromBytes", content.Bytes, file.FileName, contentType);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Download failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _busyFiles.Remove(file.Id);
        }
    }

    private async Task ConfirmDeleteAsync(FileListItem file)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete file",
            $"Permanently delete '{file.FileName}'? This can't be undone.",
            yesText: "Delete",
            cancelText: "Cancel");

        if (confirmed == true)
            await DeleteFileAsync(file);
    }

    private async Task DeleteFileAsync(FileListItem file)
    {
        if (!_busyFiles.Add(file.Id))
            return;

        StateHasChanged();
        try
        {
            if ((await FilesApi.DeleteAsync(file.Id)).Toast(Snackbar, "Delete failed", "File deleted."))
            {
                _files.RemoveAll(f => f.Id == file.Id);
                _allFiles = (await FilesApi.ListAllAsync()).ItemsOrToast(Snackbar, "files");
            }
        }
        finally
        {
            _busyFiles.Remove(file.Id);
        }
    }

    // ── Files export (ZIP) ────────────────────────────────────────────────────
    // "all" always uses the whole loaded set (_allFiles, already fetched for the Overview tile);
    // "filtered" uses the same client-filtered set the table is showing (FilteredFiles) — no extra
    // round-trip just to populate the confirm dialog's count/size.
    private IReadOnlyList<FileListItem> ExportSourceFiles => _exportScope == "filtered" ? FilteredFiles : _allFiles;
    private int ExportCount => ExportSourceFiles.Count;
    private string ExportSizeLabel => FormatFileSize(ExportSourceFiles.Sum(f => f.SizeBytes));

    // Chip style for the confirm modal — mirrors the design-system export toast (mono label on a
    // faint hover-tinted pill), same as the Settings page's own files-export confirm modal.
    private const string ExportChipStyle =
        "display: inline-flex; align-items: center; gap: 5px; padding: 4px 10px; border-radius: 999px; "
        + "font: 400 0.75rem/1.3 var(--font-mono); color: var(--mud-palette-text-secondary); "
        + "background: var(--mud-palette-action-default-hover); border: 1px solid var(--mud-palette-divider);";

    private void OpenExportConfirm(string scope)
    {
        if (!_isExportingFiles)
            _exportScope = scope;
    }

    private async Task ConfirmExport()
    {
        var scope = _exportScope;
        _exportScope = null;
        if (_isExportingFiles || scope is null)
            return;

        _isExportingFiles = true;
        StateHasChanged();
        try
        {
            // "filtered" re-runs the page's own current search/type filter server-side, unpaginated
            // (Odyssey Design System · Files.jsx) — the same filter FilteredFiles already applies
            // client-side, so the confirm dialog's count/size and the actual export agree.
            var result = scope == "filtered"
                ? await FileExport.DownloadFilteredAsync(_search, _kindFilter)
                : await FileExport.DownloadAsync();

            switch (result.Outcome)
            {
                case FileExportOutcome.Success when result.File is not null:
                    await JsRuntime.InvokeVoidAsync("downloadFileFromBytes", result.File.Bytes, result.File.FileName, "application/zip");
                    Snackbar.Add($"Exported {result.File.FileName}", Severity.Success);
                    break;
                case FileExportOutcome.Forbidden:
                    Snackbar.Add("You do not have permission to export files.", Severity.Error);
                    break;
                case FileExportOutcome.Conflict:
                    Snackbar.Add("An export is already running. Try again after it completes.", Severity.Warning);
                    break;
                default:
                    Snackbar.Add("The export could not be created. Try again or contact support.", Severity.Error);
                    break;
            }
        }
        finally
        {
            _isExportingFiles = false;
            StateHasChanged();
        }
    }

    // ── Helpers ──
    private static bool IsPreviewable(string contentType) =>
        PreviewableContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    // The flat file has no document type, so its kind is derived from the MIME type.
    private static string KindLabel(string contentType)
    {
        if (string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return "PDF";
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return "Image";
        return "File";
    }

    // Kind visuals by derived label — semantic, theme-safe tints. The avatar tile and
    // the Type chip share the same hue (the file kind's color, not a fixed chip tone).
    private static OdsFileKindMeta KindMeta(string kindLabel) => kindLabel switch
    {
        "PDF" => new("picture_as_pdf", "var(--mud-palette-error)", "color-mix(in srgb, var(--mud-palette-error) 14%, transparent)"),
        "Image" => new("image", "var(--mud-palette-info)", "color-mix(in srgb, var(--mud-palette-info) 16%, transparent)"),
        _ => new("insert_drive_file", "var(--mud-palette-text-secondary)", "var(--mud-palette-action-disabled-background)"),
    };
}
