using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

public partial class RecipeImportsPage
{
    internal const string ShowAll = "all";
    internal const string ShowReady = "ready";
    internal const string ShowProgress = "progress";
    internal const string ShowFailed = "failed";

    /// <summary>One request's worth of cards: the list is server-paged and appends the next page as it scrolls.</summary>
    internal const int BatchSize = 25;

    /// <summary>How often an open Waiting or Reading import is re-read.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    internal static readonly IReadOnlyList<OdsOption> ShowOptions =
    [
        new(ShowAll, "All"),
        new(ShowReady, "Ready to review"),
        new(ShowProgress, "In progress"),
        new(ShowFailed, "Failed"),
    ];

    internal static readonly OdsSortField<RecipeImportSummary>[] SortFields =
    [
        new() { Key = "queued", Label = "Started", Type = OdsSortType.Date },
        new() { Key = "status", Label = "Status", Type = OdsSortType.Status },
        new() { Key = "title", Label = "Draft recipe", Type = OdsSortType.Text },
    ];

    private static readonly OdsTableSort DefaultSort = new("queued", OdsSortDirection.Desc);

    private sealed record Confirmation(
        string Kind, RecipeImportSummary Import, string Title, string Icon, OdsModalTone Tone, string Verb, IReadOnlyList<string> Lines);

    [Inject] private IRecipeImportsApiClient Imports { get; set; } = default!;

    [Inject] private ITagsApiClient<ExistingRecipeTag> RecipeTags { get; set; } = default!;

    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    /// <summary><c>?open={id}</c> opens that import's card (a link from the Recipes page's Draft recipes ready signal).</summary>
    [SupplyParameterFromQuery(Name = "open")] public Guid? OpenQuery { get; set; }

    /// <summary><c>&amp;review=true</c> also opens its draft recipe for review.</summary>
    [SupplyParameterFromQuery(Name = "review")] public bool ReviewQuery { get; set; }

    private OdsLiveAnnouncer? _announcer;
    private RecipeImportOptions? _options;
    private List<RecipeImportSummary> _rows = [];
    private readonly Dictionary<Guid, RecipeImportDetail> _details = [];
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private int? _listTotal;
    private int _page = 1;
    private bool _loadingMore;
    private bool _loadMoreFailed;
    private object _resetKey = 0;
    private CancellationTokenSource? _moreCts;
    private Guid? _openId;
    private string _show = ShowAll;
    private OdsTableSort _sort = DefaultSort;
    private int _total;
    private int _readyCount;
    private int _progressCount;

    private bool _canCreate;
    private bool _canFiles;
    private bool _canTags;
    private bool _canCreateTags;
    private List<ExistingRecipeTag> _tags = [];

    private Guid _composerKey;
    private bool _composerOpen;
    private RecipeImportSeed? _composerSeed;

    private RecipeImportReview? _review;
    private Guid _reviewKey;
    private bool _reviewOpen;

    private Confirmation? _confirm;
    private Guid _confirmKey;
    private bool _confirmOpen;
    private string? _confirmError;

    private CancellationTokenSource? _poll;

    private bool _initialized;
    private (Guid?, bool)? _appliedLink;

    /// <summary>Bumped per list load, so a slower earlier response never overwrites a later filter or sort.</summary>
    private int _loadGeneration;

    private readonly Dictionary<Guid, string> _acceptedTitles = [];

    private DateTime Now => Clock.GetUtcNow().UtcDateTime;

    private bool Enabled => _options is { Enabled: true };

    private string SubLineText => _isLoading || _loadError
        ? "Recipes drafted by AI from your text and files"
        : $"{_total} {(_total == 1 ? "import" : "imports")} · {_readyCount} ready to review"
          + (_progressCount > 0 ? $" · {_progressCount} in progress" : string.Empty);

    internal static string CardId(Guid id) => $"ri-card-{id:N}";

    internal static string MenuId(Guid id) => $"ri-menu-{id:N}";

    protected override async Task OnInitializedAsync()
    {
        var user = await AuthenticationState.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.RecipesCreate);
        _canFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canTags = user.HasPermission(PermissionClaims.RecipeTagsRead);
        _canCreateTags = _canTags && user.HasPermission(PermissionClaims.RecipeTagsCreate);

        var options = Imports.GetOptionsAsync();
        var tags = _canTags ? RecipeTags.ListAllAsync() : null;
        await Task.WhenAll(ResetAsync(), LoadCountsAsync());
        var optionsResult = await options;
        _options = optionsResult.IsSuccess ? optionsResult.Value : null;
        if (tags is not null)
        {
            var tagsResult = await tags;
            _tags = tagsResult.IsSuccess ? tagsResult.Value ?? [] : [];
        }

        _initialized = true;
        await ApplyDeepLinkAsync();
    }

    /// <summary>A link to this page while it is already open (the Recipes signal) changes only the query.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (_initialized)
        {
            await ApplyDeepLinkAsync();
        }
    }

    private async Task ApplyDeepLinkAsync()
    {
        var link = (OpenQuery, ReviewQuery);
        if (link == _appliedLink)
        {
            return;
        }

        _appliedLink = link;
        if (OpenQuery is { } id && await RowForLinkAsync(id) is { } row)
        {
            await ToggleAsync(row, true);
            if (ReviewQuery && _canCreate)
            {
                await ReviewAsync(row);
            }
        }
    }

    // ── Loading ──────────────────────────────────────────────────────────────────────

    private RecipeImportListFilter Filter => new()
    {
        Status = _show switch
        {
            ShowProgress => [RecipeImportStatus.Queued, RecipeImportStatus.Running],
            ShowFailed => [RecipeImportStatus.Failed],
            _ => null,
        },
        HasPendingDraft = _show == ShowReady ? true : null,
        IncludePurged = true,
        SortBy = _sort.Key switch
        {
            "status" => RecipeImportSortBy.Status,
            "title" => RecipeImportSortBy.DraftTitle,
            _ => RecipeImportSortBy.QueuedAt,
        },
        SortDir = _sort.Dir == OdsSortDirection.Asc ? SortDirection.Asc : SortDirection.Desc,
    };

    private bool HasMore => _listTotal is { } total && _rows.Count < total;

    /// <summary>A fresh first page, on load and on any filter, sort or new import (OdsInfiniteList remote mode).</summary>
    private async Task ResetAsync()
    {
        _moreCts?.Cancel();
        var generation = ++_loadGeneration;
        _refetching = !_isLoading;
        _loadingMore = false;
        _loadMoreFailed = false;
        StateHasChanged();
        var result = await Imports.ListAsync(Filter, 1, BatchSize);
        if (generation != _loadGeneration)
        {
            return;
        }

        _loadError = !result.IsSuccess;
        _rows = [.. result.ValueOr(new()).Items];
        _listTotal = result.IsSuccess ? result.Value?.TotalCount : null;
        _page = 1;
        _resetKey = generation;
        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    /// <summary>The next page, appended. Never concurrent: the list asks only when no append is in flight.</summary>
    private async Task LoadMoreAsync(bool pressed)
    {
        if (_loadingMore || !HasMore)
        {
            return;
        }

        var generation = _loadGeneration;
        _moreCts?.Cancel();
        var cts = _moreCts = new CancellationTokenSource();
        _loadingMore = true;
        _loadMoreFailed = false;
        StateHasChanged();
        var result = await Imports.ListAsync(Filter, _page + 1, BatchSize, cts.Token);
        if (generation != _loadGeneration || cts.IsCancellationRequested)
        {
            return;
        }

        _loadingMore = false;
        if (result.IsSuccess && result.Value is { } page)
        {
            // An import created or re-sorted between pages may come round again; it is shown once.
            var known = _rows.Select(r => r.Id).ToHashSet();
            var added = page.Items.Where(r => known.Add(r.Id)).ToList();
            _rows.AddRange(added);
            _listTotal = page.TotalCount;
            _page++;
            await AnnounceAsync($"{added.Count} more {(added.Count == 1 ? "import" : "imports")} loaded.");
        }
        else
        {
            _loadMoreFailed = true;
        }

        StateHasChanged();
    }

    /// <summary>The sub-line's tallies, independent of the Show filter and of how many pages are loaded.</summary>
    private async Task LoadCountsAsync()
    {
        var ready = Imports.ListAsync(new RecipeImportListFilter { HasPendingDraft = true }, 1, 1);
        var progress = Imports.ListAsync(new RecipeImportListFilter { Status = [RecipeImportStatus.Queued, RecipeImportStatus.Running] }, 1, 1);
        var all = Imports.ListAsync(new RecipeImportListFilter { IncludePurged = true }, 1, 1);
        _readyCount = (await ready).ValueOr(new()).TotalCount;
        _progressCount = (await progress).ValueOr(new()).TotalCount;
        _total = (await all).ValueOr(new()).TotalCount;
        StateHasChanged();
    }

    /// <summary>
    /// After an action on one import: re-read that card in place (or drop it) and the tallies, so the pages already
    /// scrolled in stay where they are. A card that no longer matches the Show filter goes at the next filter or sort.
    /// </summary>
    private async Task RefreshImportAsync(Guid id, bool deleted)
    {
        if (deleted)
        {
            _details.Remove(id);
            if (_rows.RemoveAll(r => r.Id == id) > 0 && _listTotal is { } total)
            {
                _listTotal = Math.Max(0, total - 1);
            }
        }
        else
        {
            await LoadDetailAsync(id);
        }

        await LoadCountsAsync();
    }

    /// <summary>A linked import may be on a page not loaded yet: read it and show it first.</summary>
    private async Task<RecipeImportSummary?> RowForLinkAsync(Guid id)
    {
        if (_rows.FirstOrDefault(r => r.Id == id) is { } loaded)
        {
            return loaded;
        }

        var result = await Imports.GetAsync(id);
        if (!result.IsSuccess || result.Value is not { } detail)
        {
            return null;
        }

        _details[id] = detail;
        _rows.Insert(0, detail.Summary);
        return detail.Summary;
    }

    private async Task OnShowChanged(string? show)
    {
        _show = show ?? ShowAll;
        await ResetAsync();
    }

    private async Task OnSortChanged(OdsTableSort sort)
    {
        _sort = sort;
        await ResetAsync();
    }

    private async Task ToggleAsync(RecipeImportSummary row, bool open)
    {
        _openId = open ? row.Id : null;
        StopPolling();
        if (open)
        {
            await LoadDetailAsync(row.Id);
            StartPollingIfOpen(row.Id);
        }
    }

    private async Task LoadDetailAsync(Guid id)
    {
        var result = await Imports.GetAsync(id);
        if (result.IsSuccess && result.Value is { } detail)
        {
            _details[id] = detail;
            var index = _rows.FindIndex(r => r.Id == id);
            if (index >= 0)
            {
                _rows[index] = detail.Summary;
            }
        }
        else if (result.Status == System.Net.HttpStatusCode.NotFound)
        {
            _rows.RemoveAll(r => r.Id == id);
            _details.Remove(id);
        }

        StateHasChanged();
    }

    // ── Polling an open Waiting / Reading import ───────────────────────────────────────

    private void StartPollingIfOpen(Guid id)
    {
        if (!_details.TryGetValue(id, out var detail) || !RecipeImportText.IsOpen(detail.Summary.Status))
        {
            return;
        }

        _poll = new CancellationTokenSource();
        _ = PollAsync(id, _poll.Token);
    }

    private async Task PollAsync(Guid id, CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval, Clock);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var before = _details.GetValueOrDefault(id)?.Summary.Status;
                await InvokeAsync(() => LoadDetailAsync(id));
                if (_details.GetValueOrDefault(id) is not { } now || !RecipeImportText.IsOpen(now.Summary.Status))
                {
                    if (before is not null && _details.GetValueOrDefault(id) is { } done)
                    {
                        await InvokeAsync(() => AnnounceAsync(DoneAnnouncement(done.Summary)));
                        await InvokeAsync(LoadCountsAsync);
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed, or the page is going away.
        }
    }

    private static string DoneAnnouncement(RecipeImportSummary summary) => RecipeImportText.Display(summary).Key switch
    {
        "ready" => "The draft recipe is ready to review.",
        "failed" => "The import failed.",
        _ => $"The import is now {RecipeImportText.Display(summary).Label.ToLowerInvariant()}.",
    };

    private void StopPolling()
    {
        _poll?.Cancel();
        _poll?.Dispose();
        _poll = null;
    }

    public ValueTask DisposeAsync()
    {
        StopPolling();
        _moreCts?.Cancel();
        _moreCts?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ── The composer ─────────────────────────────────────────────────────────────────

    private void StartImport()
    {
        if (Enabled)
        {
            OpenComposer(null);
        }
    }

    private void OpenComposer(RecipeImportSeed? seed)
    {
        _composerSeed = seed;
        _composerKey = Guid.NewGuid();
        _composerOpen = true;
    }

    private async Task RestartAsync(RecipeImportSummary row)
    {
        if (!_details.TryGetValue(row.Id, out var detail))
        {
            await LoadDetailAsync(row.Id);
            detail = _details.GetValueOrDefault(row.Id);
        }

        // The same text and the same files preselected; the box unticked; a new request id.
        OpenComposer(new RecipeImportSeed(
            detail?.Sources.FirstOrDefault(s => s.Kind == RecipeImportSourceKind.Text)?.Text,
            [.. (detail?.Sources ?? []).Where(s => s.File is not null).Select(s => s.File!.FileId)]));
    }

    private async Task OnImportSentAsync(RecipeImportDetail created)
    {
        _show = ShowAll;
        await Task.WhenAll(ResetAsync(), LoadCountsAsync());
        _details[created.Summary.Id] = created;
        _openId = created.Summary.Id;
        StopPolling();
        StartPollingIfOpen(created.Summary.Id);
        await AnnounceAsync("Import queued. You can leave this page — the draft recipe will wait here.");
    }

    // ── Review ───────────────────────────────────────────────────────────────────────

    private async Task ReviewAsync(RecipeImportSummary row)
    {
        await LoadDetailAsync(row.Id);
        // A purged import's draft has no content left to review (accept would answer 409 content-purged).
        if (_details.GetValueOrDefault(row.Id) is not { } detail
            || detail.Summary.ContentPurged
            || detail.Drafts.FirstOrDefault(d => d.Status == RecipeImportDraftStatus.Pending && d.Content is not null) is not { } draft)
        {
            return;
        }

        StopPolling();
        var files = detail.Sources.Count(s => s.Kind == RecipeImportSourceKind.File && s.File is not null);
        _review = new RecipeImportReview(row.Id, draft, files, _canFiles);
        _reviewKey = Guid.NewGuid();
        _reviewOpen = true;
    }

    private void OnReviewOpenChanged(bool open)
    {
        _reviewOpen = open;
        if (!open)
        {
            _review = null;
        }
    }

    private async Task OnDraftSavedAsync(RecipeImportDraftItem saved)
    {
        if (_review is { } review)
        {
            await LoadDetailAsync(review.ImportId);
        }

        await AnnounceAsync("Your edits are saved on the import. Nothing was added to Recipes — finish it any time from here.");
    }

    private async Task OnDraftAcceptedAsync(RecipeImportAccepted accepted)
    {
        var id = _review?.ImportId;
        if (id is { } acceptedId)
        {
            _acceptedTitles[acceptedId] = accepted.Title;
        }

        if (id is { } importId)
        {
            await RefreshImportAsync(importId, deleted: false);
        }

        var files = accepted.AttachedSources && accepted.Result.AttachedFileCount > 0
            ? $" {accepted.Result.AttachedFileCount} source {(accepted.Result.AttachedFileCount == 1 ? "file" : "files")} attached."
            : string.Empty;
        await AnnounceAsync($"Recipe {accepted.Title} created.{files}");
    }

    private string CreatedTitle(RecipeImportSummary r) =>
        (_acceptedTitles.GetValueOrDefault(r.Id) ?? r.DraftTitle) is { Length: > 0 } title
            ? $"Created as the recipe {title}"
            : "Created as a recipe";

    private void GoToRecipes() => Navigation.NavigateTo("/recipes");

    // ── Menu and confirmations ──────────────────────────────────────────────────────────

    /// <summary>Every action, by state and claim; an action the import cannot take is absent, never dimmed.</summary>
    private IReadOnlyList<OdsMenuItem> MenuFor(RecipeImportSummary r)
    {
        var key = RecipeImportText.Display(r).Key;
        var items = new List<OdsMenuItem>();
        if (key == "ready" && _canCreate)
        {
            items.Add(new() { Icon = "rate_review", Label = "Review and create recipe…", OnClick = EventCallback.Factory.Create(this, () => ReviewAsync(r)) });
        }

        if (key == "waiting")
        {
            items.Add(new() { Icon = "block", Label = "Cancel import…", OnClick = EventCallback.Factory.Create(this, () => AskConfirm("cancel", r)) });
        }

        if (key is "failed" or "cancelled" && _canCreate)
        {
            items.Add(new() { Icon = "restart_alt", Label = "Start a new import…", OnClick = EventCallback.Factory.Create(this, () => RestartAsync(r)) });
        }

        if (key == "ready")
        {
            items.Add(new() { Icon = "delete_sweep", Label = "Discard draft recipe…", OnClick = EventCallback.Factory.Create(this, () => AskConfirm("discard", r)) });
        }

        if (key is not ("waiting" or "reading"))
        {
            if (items.Count > 0)
            {
                items.Add(new() { Divider = true });
            }

            items.Add(new() { Icon = "delete", Label = "Delete import…", Danger = true, OnClick = EventCallback.Factory.Create(this, () => AskConfirm("delete", r)) });
        }

        return items;
    }

    private void AskConfirm(string kind, RecipeImportSummary r)
    {
        var processor = _options?.Processor ?? "the AI service";
        var retention = _options?.RetentionDays;
        _confirm = kind switch
        {
            "cancel" => new(kind, r, "Cancel this import?", "block", OdsModalTone.Warning, "Cancel import",
                ["It hasn’t been sent yet, so nothing leaves Odyssey. The import stays in the list as Cancelled."]),
            "discard" => new(kind, r, "Discard the draft recipe?", "delete_sweep", OdsModalTone.Warning, "Discard draft recipe",
            [
                "No recipe is created, and your edits to the draft recipe are lost.",
                retention is { } days
                    ? $"The import stays in the list as Draft discarded, with what you sent, until it is removed after {days} days."
                    : "The import stays in the list as Draft discarded, with what you sent, until its retention period ends.",
            ]),
            _ => new(kind, r, "Delete this import?", "delete", OdsModalTone.Error, "Delete import", DeleteLines(r, processor)),
        };
        _confirmError = null;
        _confirmKey = Guid.NewGuid();
        _confirmOpen = true;
    }

    /// <summary>What goes and what stays, said for THIS import — a delete never touches a recipe.</summary>
    private List<string> DeleteLines(RecipeImportSummary r, string processor)
    {
        var key = RecipeImportText.Display(r).Key;
        var lines = new List<string>
        {
            key switch
            {
                "created" => $"The recipe {r.DraftTitle ?? "created from it"} stays in Recipes, with its files. Only the import goes: the text you pasted and the copy of the draft recipe.",
                "ready" => "The import goes, with its draft recipe — no recipe is created. Nothing in Recipes changes.",
                _ => "The import goes from this list. Nothing in Recipes changes.",
            },
            "Files uploaded just for this import are removed from Files; files you chose from Files stay there.",
        };
        if (_details.GetValueOrDefault(r.Id)?.DocumentSent ?? r.Status is not (RecipeImportStatus.Queued or RecipeImportStatus.Cancelled))
        {
            lines.Add($"A record that these sources were sent to {processor} is kept — it holds no recipe content.");
        }

        return lines;
    }

    private async Task<bool> ConfirmAsync()
    {
        if (_confirm is not { } confirm)
        {
            return true;
        }

        var id = confirm.Import.Id;
        var result = confirm.Kind switch
        {
            "cancel" => await Imports.CancelAsync(id),
            "discard" => await DiscardAsync(id),
            _ => await Imports.DeleteAsync(id),
        };
        if (!result.IsSuccess)
        {
            _confirmError = result.Problem?.Code switch
            {
                RecipeImportErrorCodes.AlreadySent => "It has already been picked up and sent, so it can no longer be cancelled.",
                RecipeImportErrorCodes.InProgress => "It is being sent or read right now. Wait until it finishes, then delete it.",
                RecipeImportErrorCodes.DraftNotPending => "Someone else already created or discarded this draft recipe.",
                _ => result.Problem?.Message ?? "That didn’t work. Try again.",
            };
            StateHasChanged();
            return false;
        }

        await RefreshImportAsync(id, deleted: confirm.Kind == "delete");
        if (confirm.Kind == "delete")
        {
            _openId = null;
        }

        await AnnounceAsync(confirm.Kind switch
        {
            "cancel" => "Import cancelled. Nothing was sent.",
            "discard" => "Draft recipe discarded. No recipe was created.",
            _ => "Import deleted.",
        });
        return true;
    }

    /// <summary>The ⋯ menu works on a closed card too, whose detail (and so its draft id) is not loaded yet.</summary>
    private async Task<Odyssey.ApiClient.ApiResult> DiscardAsync(Guid id)
    {
        if (!_details.ContainsKey(id))
        {
            await LoadDetailAsync(id);
        }

        return _details.GetValueOrDefault(id)?.Drafts.FirstOrDefault() is { } draft
            ? await Imports.DiscardAsync(id, draft.Id)
            : Odyssey.ApiClient.ApiResult.Failure(System.Net.HttpStatusCode.Conflict,
                new Odyssey.ApiClient.ApiProblem { Code = RecipeImportErrorCodes.DraftNotPending, Detail = "There is no draft recipe left to discard." });
    }

    // ── Card text ────────────────────────────────────────────────────────────────────

    private string TitleOf(RecipeImportSummary r, RecipeImportDetail? detail) =>
        r.DraftTitle is { Length: > 0 } title ? title
        : r.Status == RecipeImportStatus.Completed && !r.ContentPurged ? "Untitled draft recipe"
        : r.HasText ? "Pasted recipe"
        : _canFiles && r.FirstFileName is { } name ? name
        : "Recipe import";

    private IReadOnlyList<RenderFragment?> MetaFor(RecipeImportSummary r)
    {
        var sources = string.Join(" · ", new[]
        {
            r.HasText ? "Text" : null,
            r.FileCount > 0 ? $"{r.FileCount} {(r.FileCount == 1 ? "file" : "files")}" : null,
        }.Where(s => s is not null));
        return
        [
            OdsRecordMeta.Text($"Started by {r.RequestedByName ?? "a former member"}"),
            OdsRecordMeta.Text(RecipeImportText.Ago(r.QueuedAt, Now)),
            sources.Length > 0 ? OdsRecordMeta.Text(sources) : null,
        ];
    }

    private static IReadOnlyList<OdsRecordCount>? CountsFor(RecipeImportDetail? detail) =>
        detail?.Drafts.FirstOrDefault()?.Content is { } c && !detail.Summary.ContentPurged
            ?
            [
                new("format_list_bulleted", c.Ingredients.Length.ToString(CultureInfo.InvariantCulture), c.Ingredients.Length == 1 ? "ingredient" : "ingredients"),
                new("format_list_numbered", c.Steps.Length.ToString(CultureInfo.InvariantCulture), c.Steps.Length == 1 ? "step" : "steps"),
            ]
            : null;

    private static string ReadyDetail(RecipeImportDraftItem? draft) =>
        draft?.UpdatedByName is { } editor
            ? $"The AI’s suggestion, made from your sources and last edited by {editor}. It isn’t in Recipes until you review it and select Create recipe."
            : "The AI’s suggestion, made from your sources. It isn’t in Recipes until you review it and select Create recipe.";

    private string FailedDetail(RecipeImportSummary r, RecipeImportDetail? detail)
    {
        var sent = detail?.DocumentSent == true
            ? $"Your sources were sent to {_options?.Processor ?? "the AI service"}."
            : "Nothing was sent.";
        return $"{RecipeImportText.Failure(r.FailureCode)} {sent}";
    }

    private async Task AnnounceAsync(string message)
    {
        if (_announcer is not null)
        {
            await _announcer.Announce(message);
        }
    }
}
