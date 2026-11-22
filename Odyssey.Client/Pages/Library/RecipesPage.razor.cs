using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

public partial class RecipesPage
{
    [Inject] private IRecipesApiClient Recipes { get; set; } = default!;
    [Inject] private ITagsApiClient<ExistingRecipeTag> RecipeTags { get; set; } = default!;
    [Inject] private IPhotosApiClient Photos { get; set; } = default!;
    [Inject] private IFilesApiClient Files { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IClipboardService Clipboard { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private IPageStateService PageState { get; set; } = default!;
    [Inject] private IRenderInteractivity Interactivity { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private OdsLiveAnnouncer? _announcer;

    // ── Claims (design system components-recipes.md · Claims): a missing claim removes its surface. ──
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canPhotos;
    private bool _canFiles;
    private bool _canTags;
    private bool _canCreateTags;
    private PhotoImageAccess _photoAccess = new(false);

    // ── Tags ──
    private List<ExistingRecipeTag> _tags = [];
    private Dictionary<Guid, ExistingRecipeTag> _tagsById = [];

    // ── Persisted page state ──
    private const string PageStateKey = "recipes-page";
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _tagFilter = [];
    private string _timeFilter = RecipeFilters.AnyTime;
    private IReadOnlyCollection<string> _photoFilter = [];
    private IReadOnlyCollection<string> _fileFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];
    private int _batch = OdsPageSizes.Batch[0];

    // RecipeSortBy: Title (default, A→Z), UpdatedAt, CreatedAt, TotalMinutes (nulls last both ways). The SERVER sorts
    // (RecipeFilters.SortByFor), so the fields carry no client SortValue: RecipeSummary has no CreatedAt to project, and a
    // stand-in projection (the old UpdatedAt) would sort "Added" by the wrong date if anything ever sorted in memory.
    private static readonly OdsTableSort DefaultSort = new("title", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;

    internal static readonly OdsSortField<RecipeSummary>[] SortFields =
    [
        new() { Key = "title", Label = "Title", Type = OdsSortType.Text },
        new() { Key = "updated", Label = "Updated", Type = OdsSortType.Date },
        new() { Key = "created", Label = "Added", Type = OdsSortType.Date },
        new() { Key = "total", Label = "Total time", Type = OdsSortType.Number },
    ];

    internal sealed class RecipesPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TagFilter { get; set; } = [];
        public string TimeFilter { get; set; } = RecipeFilters.AnyTime;
        public List<string> PhotoFilter { get; set; } = [];
        public List<string> FileFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    // ── Lifecycle ──
    protected override async Task OnInitializedAsync()
    {
        if (!Interactivity.IsInteractive)
        {
            return;
        }

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.RecipesCreate);
        _canUpdate = user.HasPermission(PermissionClaims.RecipesUpdate);
        _canDelete = user.HasPermission(PermissionClaims.RecipesDelete);
        _canPhotos = user.HasPermission(PermissionClaims.PhotosRead);
        _canFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canTags = user.HasPermission(PermissionClaims.RecipeTagsRead);
        _canCreateTags = _canTags && user.HasPermission(PermissionClaims.RecipeTagsCreate);
        _photoAccess = PhotoImageAccess.From(user);

        await PageState.RestoreOrSeedAsync<RecipesPageState>(PageStateKey, ApplyPageState, BuildPageState);
        await Task.WhenAll(LoadTagsAsync(), ResetAsync(), LoadCountsAsync());
    }

    private void ApplyPageState(RecipesPageState state)
    {
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _tagFilter = [.. state.TagFilter ?? []];
        _timeFilter = RecipeFilters.TimeOptions.Any(o => o.Value == state.TimeFilter) ? state.TimeFilter : RecipeFilters.AnyTime;
        _photoFilter = RecipeFilters.PhotoOptions.KnownValues(state.PhotoFilter);
        _fileFilter = RecipeFilters.FileOptions.KnownValues(state.FileFilter);
        _statusFilter = ArchivalStatusOptions.Options.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private RecipesPageState BuildPageState() => new()
    {
        SearchOpen = _searchOpen,
        Search = _searchString,
        TagFilter = [.. _tagFilter],
        TimeFilter = _timeFilter,
        PhotoFilter = [.. _photoFilter],
        FileFilter = [.. _fileFilter],
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    // ── Filter bar ──
    private bool HasFilters => !string.IsNullOrWhiteSpace(_searchString) || _tagFilter.Count > 0 || _timeFilter != RecipeFilters.AnyTime
        || _photoFilter.Count > 0 || _fileFilter.Count > 0 || _statusFilter.Count > 0;

    private IReadOnlyList<OdsOption> TagFilterOptions =>
        [.. _tags.Where(t => t.Archived is null).Select(t => JournalOverviews.TagOption(t.RecipeTagId, t.Name, t.Icon, t.Color, JournalOverviews.TagRowColor))];

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private Task OnSearchCommitted() => ResetAsync();
    private Task OnTagFilterChanged(IReadOnlyCollection<string> v) => Refilter(() => _tagFilter = v ?? []);
    private Task OnTimeFilterChanged(string v) => Refilter(() => _timeFilter = v ?? RecipeFilters.AnyTime);
    private Task OnPhotoFilterChanged(IReadOnlyCollection<string> v) => Refilter(() => _photoFilter = RecipeFilters.OneAtATime(_photoFilter, v));
    private Task OnFileFilterChanged(IReadOnlyCollection<string> v) => Refilter(() => _fileFilter = RecipeFilters.OneAtATime(_fileFilter, v));
    private Task OnStatusFilterChanged(IReadOnlyCollection<string> v) => Refilter(() => _statusFilter = RecipeFilters.OneAtATime(_statusFilter, v));
    private Task OnSortChanged(OdsTableSort sort) => Refilter(() => _sort = sort);
    private Task OnBatchChanged(int size) => Refilter(() => _batch = size);

    private Task Refilter(Action apply)
    {
        apply();
        PersistPageState();
        return ResetAsync();
    }

    private Task ClearFilters() => Refilter(() =>
    {
        _searchString = string.Empty;
        _tagFilter = [];
        _timeFilter = RecipeFilters.AnyTime;
        _photoFilter = [];
        _fileFilter = [];
        _statusFilter = [];
    });

    private RecipeListFilter CurrentFilter() => RecipeFilters.Build(
        _searchString, _tagFilter, _timeFilter, _photoFilter, _fileFilter, _statusFilter, _sort, _canTags, _canPhotos, _canFiles);

    // ── The card head ──
    /// <summary>The id of a row's menu wrapper — the focus target after save, archive and delete.</summary>
    internal static string MenuId(Guid id) => $"rc-menu-{id}";

    /// <summary>The card's root id — after create, focus goes to its header.</summary>
    internal static string CardId(Guid id) => $"rc-{id}";

    /// <summary>"4 recipes · 2 with photos", the photo half only with photos.read; empty until the counts are known.</summary>
    private string SubLineText => _activeCount is { } n
        ? $"{RecipeFormat.Count(n, "recipe")}{(_canPhotos && _withPhotosCount is { } p ? $" · {p} with photos" : "")}"
        : "The household’s shared cookbook";

    // The cover thumbnail with photos.read, else the glyph — never an accent: a recipe has no type (RecordCard rule).
    private OdsRecordImage? CoverOf(RecipeSummary r) =>
        PhotoImageSources.For(r.CoverPhotoId, r.CoverThumbnailsReady, ThumbnailSize.Small, _photoAccess, Photos).Thumb is { } src
            ? new OdsRecordImage(src)
            : null;

    private IReadOnlyList<OdsTagChips.Tag> TagsOf(RecipeSummary r) =>
        OdsTagChips.Tag.For(r.TagIds, _tagsById, t => t.Name, t => t.Icon, t => t.Color);

    private IReadOnlyList<RenderFragment?> MetaFor(RecipeSummary r)
    {
        var detail = _cook.GetValueOrDefault(r.RecipeId)?.Detail;
        var displayed = detail?.DisplayedServings ?? r.Servings;
        return
        [
            OdsRecordMeta.Text(RecipeFormat.YieldMeta(displayed, r.Servings, r.ServingsLabel)),
            OdsRecordMeta.Text(RecipeFormat.Times(r.PrepMinutes, r.CookMinutes)),
            OdsRecordMeta.Text(RecipeFormat.Total(r.TotalMinutes)),
        ];
    }

    internal static IReadOnlyList<OdsRecordCount> CountsFor(RecipeSummary r, bool canFiles)
    {
        var counts = new List<OdsRecordCount>
        {
            new("format_list_bulleted", r.IngredientCount.ToString(System.Globalization.CultureInfo.InvariantCulture), r.IngredientCount == 1 ? "ingredient" : "ingredients"),
            new("format_list_numbered", r.StepCount.ToString(System.Globalization.CultureInfo.InvariantCulture), r.StepCount == 1 ? "step" : "steps"),
        };
        if (canFiles && r.AttachmentCount is > 0 and var files)
        {
            counts.Add(new("attach_file", files.ToString(System.Globalization.CultureInfo.InvariantCulture), files == 1 ? "file" : "files"));
        }

        return counts;
    }

    private IReadOnlyList<OdsRecordCount> CountsFor(RecipeSummary r) => CountsFor(r, _canFiles);

    // The first load finishes before the first render has set the announcer's @ref, so its message waits for it.
    private string? _pendingAnnouncement;

    private Task Announce(string message)
    {
        if (_announcer is null)
        {
            _pendingAnnouncement = message;
            return Task.CompletedTask;
        }

        return _announcer.Announce(message);
    }

    private Task CopyAsync(string text, string toast) => Clipboard.CopyAsync(text, toast);

    public async ValueTask DisposeAsync()
    {
        _listLoader.Dispose();
        _moreCts?.Cancel();
        await ReleaseWakeLockAsync(announce: false);
        _wakeRef?.Dispose();
        if (_wakeModule is not null)
        {
            try
            {
                await _wakeModule.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException or JSException or InvalidOperationException)
            {
                // The circuit or runtime is already gone.
            }
        }
    }
}
