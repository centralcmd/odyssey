using System.Net;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

public partial class RecipesPage
{
    // ── New / Edit ──
    private Guid _dialogKey;
    private bool _dialogOpen;
    private ExistingRecipe? _dialogRecipe;

    private void NewClicked()
    {
        _dialogRecipe = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task EditAsync(RecipeSummary r)
    {
        // The dialog edits what is stored, so it opens on a fresh UNSCALED read and its version.
        if (await FreshAsync(r.RecipeId) is not { } fresh)
        {
            return;
        }

        _dialogRecipe = fresh;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private void OnDialogOpenChanged(bool open) => _dialogOpen = open;

    private async Task OnRecipeSaved(ExistingRecipe saved)
    {
        var created = _dialogRecipe is null;
        if (created)
        {
            _activeCount = _activeCount is { } n ? n + 1 : null;
            await CollapseAsync();
            var shown = true;
            if (CreateNeedsRefetch(HasFilters, _sort))
            {
                // The new recipe's place depends on the filters and the sort, which only the server knows.
                await ResetAsync();
                shown = _rows.Any(x => x.RecipeId == saved.RecipeId);
            }
            else
            {
                _rows.Insert(0, SummaryOf(saved));
                _totalCount = (_totalCount ?? 0) + 1;
            }

            if (!shown)
            {
                await Announce($"Recipe {saved.Title} created. It doesn’t match the current filters.");
                FocusAfterRender(PageHeader.HeadingId);
                return;
            }

            _expandedId = saved.RecipeId;
            var cook = Cook(saved.RecipeId);
            cook.Detail = saved;
            cook.Phase = RecipeCardPhase.Ready;
            await Announce($"Recipe {saved.Title} created.");
            FocusAfterRender(CardId(saved.RecipeId));
            return;
        }

        await ApplySavedAsync(saved);
        await Announce($"{saved.Title} saved.");
        FocusAfterRender(MenuId(saved.RecipeId));
    }

    /// <summary>
    /// Whether a new recipe needs a fresh first page rather than a local insert at the top: any active filter (it may
    /// not match) and any sort but newest-first by Updated or Added (it belongs elsewhere).
    /// </summary>
    internal static bool CreateNeedsRefetch(bool hasFilters, OdsTableSort sort) =>
        hasFilters || sort.Dir != OdsSortDirection.Desc || sort.Key is not ("updated" or "created");

    /// <summary>The list row as the saved read describes it.</summary>
    internal static RecipeSummary SummaryOf(ExistingRecipe d) => new()
    {
        RecipeId = d.RecipeId,
        Title = d.Title,
        Description = d.Description,
        Servings = d.Servings,
        ServingsLabel = d.ServingsLabel,
        PrepMinutes = d.PrepMinutes,
        CookMinutes = d.CookMinutes,
        TotalMinutes = d.TotalMinutes,
        IngredientCount = d.Ingredients.Count,
        StepCount = d.Steps.Count,
        AttachmentCount = d.Attachments?.Count,
        CoverPhotoId = d.Photos?.OrderBy(p => p.Position).FirstOrDefault()?.PhotoId,
        CoverThumbnailsReady = d.Photos?.OrderBy(p => p.Position).FirstOrDefault()?.ThumbnailsReady ?? false,
        TagIds = [.. d.TagIds],
        DisplayIcon = d.DisplayIcon,
        DisplayColor = d.DisplayColor,
        UpdatedAt = d.UpdatedAt,
        Archived = d.Archived,
    };

    /// <summary>Puts a saved (unscaled) read on its row and card; a scaled card re-reads at its servings.</summary>
    private async Task ApplySavedAsync(ExistingRecipe saved)
    {
        var index = _rows.FindIndex(x => x.RecipeId == saved.RecipeId);
        if (index >= 0)
        {
            _rows[index] = SummaryOf(saved);
        }

        var cook = Cook(saved.RecipeId);
        if (cook.Servings is null)
        {
            cook.Detail = saved;
            cook.Phase = RecipeCardPhase.Ready;
        }
        else if (_expandedId == saved.RecipeId)
        {
            await LoadDetailAsync(saved.RecipeId);
        }

        StateHasChanged();
    }

    /// <summary>
    /// A record action through the recipe's full <c>PUT</c>: a fresh unscaled read, then <paramref name="build"/> on it
    /// with the current version. A 409 means someone saved in between: the card re-reads and the region says so.
    /// </summary>
    private async Task<ExistingRecipe?> PutAsync(Guid id, Func<ExistingRecipe, UpdateRecipe> build, string failureLead)
    {
        if (await FreshAsync(id) is not { } fresh)
        {
            return null;
        }

        var result = await Recipes.UpdateAsync(id, build(fresh));
        if (result.IsSuccess && result.Value is { } saved)
        {
            await ApplySavedAsync(saved);
            return saved;
        }

        if (result.Status == HttpStatusCode.Conflict && result.Problem?.Code == RecipeErrorCodes.VersionConflict)
        {
            await LoadDetailAsync(id);
            await Announce("Someone else changed this recipe just now. It shows the latest version; try again.");
            return null;
        }

        Snackbar.Add($"{failureLead}: {result.Error}", Severity.Error);
        return null;
    }

    // ── Archive / Restore ──
    private async Task ToggleArchiveAsync(RecipeSummary r)
    {
        var archive = r.Archived is null;
        if (await PutAsync(r.RecipeId, fresh => RecipeWrites.From(fresh, archived: archive), archive ? "Couldn’t archive the recipe" : "Couldn’t restore the recipe") is not { } saved)
        {
            return;
        }

        _activeCount = _activeCount is { } n ? n + (archive ? -1 : 1) : null;
        await Announce(archive ? $"{saved.Title} archived. Find it again with Status → Archived." : $"{saved.Title} restored.");
        FocusAfterRender(MenuId(saved.RecipeId));
    }

    // ── Delete ──
    private RecipeSummary? _deleteRecipe;
    private Guid _deleteKey;
    private bool _deleteOpen;
    private string? _deleteError;
    private Guid? _deleteNeighbour;

    private void DeleteClicked(RecipeSummary r)
    {
        _deleteRecipe = r;
        _deleteError = null;
        _deleteKey = Guid.NewGuid();
        _deleteOpen = true;
        // The neighbour is recorded before anything is sent.
        var index = _rows.FindIndex(x => x.RecipeId == r.RecipeId);
        _deleteNeighbour = index + 1 < _rows.Count ? _rows[index + 1].RecipeId : index > 0 ? _rows[index - 1].RecipeId : null;
    }

    private async Task<bool> ConfirmDeleteAsync()
    {
        if (_deleteRecipe is not { } r)
        {
            return true;
        }

        var result = await Recipes.DeleteAsync(r.RecipeId);
        if (result.IsSuccess || result.Status == HttpStatusCode.NotFound)
        {
            _rows.RemoveAll(x => x.RecipeId == r.RecipeId);
            _totalCount = _totalCount is { } t ? Math.Max(0, t - 1) : null;
            _activeCount = r.Archived is null && _activeCount is { } n ? n - 1 : _activeCount;
            if (_expandedId == r.RecipeId)
            {
                await CollapseAsync();
            }

            _cook.Remove(r.RecipeId);
            await Announce($"Recipe {r.Title} deleted.");
            FocusAfterRender(_deleteNeighbour is { } next ? [MenuId(next), PageHeader.HeadingId] : [PageHeader.HeadingId]);
            return true;
        }

        _deleteError = result.Status == HttpStatusCode.Conflict
            ? "Someone else changed this recipe just now. Close this, check the latest version, and delete again if you still want to."
            : $"The recipe couldn’t be deleted: {result.Error}";
        return false;
    }

    // ── Photos (record action; every change is a PUT with only photoIds set) ──
    private RecipeSummary? _attachPhotosFor;
    private RecipeSummary? _attachFilesFor;
    private Guid _attachKey;
    private bool _attachPhotosOpen;
    private bool _attachFilesOpen;

    private async Task AddPhotosAsync(RecipeSummary r)
    {
        await EnsureOpenAsync(r);
        _attachPhotosFor = r;
        _attachKey = Guid.NewGuid();
        _attachPhotosOpen = true;
    }

    private IReadOnlyCollection<Guid> AttachedPhotoIds(Guid id) =>
        [.. Cook(id).Detail?.Photos?.Select(p => p.PhotoId) ?? []];

    private async Task OnPhotosPickedAsync(Guid id, IReadOnlyList<Odyssey.Client.Pages.Attachments.AttachPhotoItem> items)
    {
        var before = AttachedPhotoIds(id).Count;
        var kept = 0;
        var refused = 0;
        var saved = await PutAsync(id, fresh =>
        {
            var have = fresh.Photos?.OrderBy(p => p.Position).Select(p => p.PhotoId).ToList() ?? [];
            var added = items.Select(i => i.PhotoId).Where(p => !have.Contains(p)).Distinct().ToList();
            var room = Math.Max(0, RecipeLimits.MaxPhotos - have.Count);
            kept = Math.Min(room, added.Count);
            refused = added.Count - kept;
            return RecipeWrites.From(fresh, photoIds: [.. have, .. added.Take(kept)]);
        }, "Couldn’t add the photos");
        if (saved is not null)
        {
            await Announce(PhotosAddedMessage(kept, refused, before == 0));
        }
    }

    /// <summary>"2 photos added. 1 not added: a recipe holds at most 20 photos. The first is the cover."</summary>
    internal static string PhotosAddedMessage(int kept, int refused, bool wasEmpty) =>
        $"{RecipeFormat.Count(kept, "photo")} added."
        + (refused > 0 ? $" {refused} not added: a recipe holds at most {RecipeLimits.MaxPhotos} photos." : "")
        + (wasEmpty && kept > 0 ? " The first is the cover." : "");

    private async Task SavePhotosAsync(Guid id, Guid[] photoIds, string message)
    {
        if (await PutAsync(id, fresh => RecipeWrites.From(fresh, photoIds: photoIds), "Couldn’t change the photos") is not null)
        {
            await Announce(message);
        }
    }

    // ── Files (record action; every change is a PUT with only attachmentFileIds set) ──
    private int _filesRefused;

    private async Task AttachFilesAsync(RecipeSummary r)
    {
        await EnsureOpenAsync(r);
        _attachFilesFor = r;
        _filesRefused = 0;
        _attachKey = Guid.NewGuid();
        _attachFilesOpen = true;
    }

    private IReadOnlyCollection<Guid> AttachedFileIds(Guid id) =>
        [.. Cook(id).Detail?.Attachments?.Select(a => a.FileId) ?? []];

    // One write per file, each on a fresh read and version, so a failed file keeps its row in the dialog for a retry.
    // Past the cap a file is not linked; the closing announcement says how many.
    private async Task<bool> AttachOneAsync(Guid id, Odyssey.Client.Pages.Attachments.AttachDocumentItem item)
    {
        var full = false;
        var saved = await PutAsync(id, fresh =>
        {
            var have = fresh.Attachments?.Select(a => a.FileId).ToList() ?? [];
            full = !have.Contains(item.FileId) && have.Count >= RecipeLimits.MaxAttachments;
            return RecipeWrites.From(fresh, attachmentFileIds: full || have.Contains(item.FileId) ? [.. have] : [.. have, item.FileId]);
        }, $"Couldn’t attach “{item.Name}”");
        if (full)
        {
            _filesRefused++;
        }

        return saved is not null;
    }

    private async Task OnFilesAttached(Guid id, IReadOnlyList<Odyssey.Client.Pages.Attachments.AttachDocumentItem> items)
    {
        var linked = items.Count - _filesRefused;
        await Announce($"{RecipeFormat.Count(linked, "file")} attached."
            + (_filesRefused > 0 ? $" {_filesRefused} not attached: a recipe holds at most {RecipeLimits.MaxAttachments} files." : ""));
        _filesRefused = 0;
        StateHasChanged();
    }

    private async Task DetachFileAsync(Guid id, RecipeAttachmentDto file)
    {
        var saved = await PutAsync(id, fresh => RecipeWrites.From(fresh,
            attachmentFileIds: [.. (fresh.Attachments ?? []).Select(a => a.FileId).Where(f => f != file.FileId)]), $"Couldn’t detach “{file.FileName}”");
        if (saved is not null)
        {
            await Announce($"{file.FileName} detached. The file stays in Files.");
        }
    }

    private async Task DownloadAsync(RecipeAttachmentDto file)
    {
        await Announce($"Downloading {file.FileName}.");
        var content = await Files.GetContentAsync(file.FileId);
        if (content is null)
        {
            Snackbar.Add("Download failed.", Severity.Error);
            return;
        }

        await Js.SaveFileAsync(content, file.FileName, file.ContentType);
    }
}
