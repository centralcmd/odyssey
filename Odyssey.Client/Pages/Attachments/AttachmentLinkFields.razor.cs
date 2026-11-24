using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Photos;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Attachments;

/// <summary>Which of the two link kinds a change touched.</summary>
public enum AttachmentLinkKind
{
    Photos,
    Files,
}

/// <summary>
/// The wording of the announcements. The journal keeps its own sentences unchanged (issue #522 AC 13); the calendar
/// speaks the spec's (§3.1 "Announcements").
/// </summary>
public enum AttachmentLinkVoice
{
    Journal,
    Calendar,
}

public partial class AttachmentLinkFields
{
    /// <summary>The linked photos, edited in place.</summary>
    [Parameter, EditorRequired] public List<JournalDraftPhoto> Photos { get; set; } = default!;

    /// <summary>The linked files, edited in place.</summary>
    [Parameter, EditorRequired] public List<FileMetadataResponse> Attachments { get; set; } = default!;

    /// <summary>"entry", "event" or "series" — the record the lowered-cap help line and the journal's sentences name.</summary>
    [Parameter] public string Noun { get; set; } = "entry";

    [Parameter] public AttachmentLinkVoice Voice { get; set; } = AttachmentLinkVoice.Journal;

    /// <summary>
    /// The live per-kind link cap (<c>ImportLimitsDto.CalendarEventMaxLinksPerKind</c>), or null: no pre-check and no
    /// number anywhere, the server's <c>422</c> being the backstop (§3.1 "Cap").
    /// </summary>
    [Parameter] public int? Cap { get; set; }

    /// <summary><c>files.read</c>: without it the field is the count line only.</summary>
    [Parameter] public bool CanReadFiles { get; set; } = true;

    /// <summary><c>photos.read</c>: without it the field is the count line only.</summary>
    [Parameter] public bool CanReadPhotos { get; set; } = true;

    /// <summary>The stored file count, for the count line a viewer without <c>files.read</c> sees.</summary>
    [Parameter] public int AttachmentCount { get; set; }

    /// <summary>The stored photo count, for the count line a viewer without <c>photos.read</c> sees.</summary>
    [Parameter] public int PhotoCount { get; set; }

    /// <summary>The lists with no add or remove control (an occurrence's series links, a viewer who cannot save).</summary>
    [Parameter] public bool ReadOnly { get; set; }

    /// <summary>The links are still loading: a skeleton under <c>aria-busy</c>, Add aria-disabled (v3 §C).</summary>
    [Parameter] public bool Loading { get; set; }

    /// <summary>Lays the two fields out side by side in one form row (the journal's dialog).</summary>
    [Parameter] public bool Paired { get; set; }

    /// <summary>Offers Download on a file row (<c>files.read</c>).</summary>
    [Parameter] public bool CanDownload { get; set; }

    /// <summary>The file rows' Remove item — "Remove from entry" or "Remove from event".</summary>
    [Parameter] public string RemoveLabel { get; set; } = "Remove from entry";

    [Parameter] public string PhotosSubtitle { get; set; } = "Add photos to this entry. Photos stay in your library; adding links them here.";

    [Parameter] public string FilesSubtitle { get; set; } = "Keep documents with this entry. Files stay in Files; attaching links them here.";

    /// <summary>The host's one polite announcer: add, remove and cap messages go through it, once each.</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    /// <summary>Raised after an add or a remove, with the kind it touched.</summary>
    [Parameter] public EventCallback<AttachmentLinkKind> OnChanged { get; set; }

    private bool _pickingFiles;
    private bool _pickingPhotos;
    private PhotoImageAccess _imageAccess = new(false);

    // Per-instance, so two forms on one page never share a DOM id.
    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];

    // One document pick's tally: the dialog hands files over one by one, the announcement is for the whole pick.
    private int _filesAdded;
    private int _filesOverCap;

    private string FilesListId => $"jef-files-{_uid}";
    private string PhotosCapId => $"jef-photos-cap-{_uid}";
    private string FilesCapId => $"jef-files-cap-{_uid}";
    private string PhotoTileId(Guid photoId) => $"alf-ph-{_uid}-{photoId:N}";

    // The shared viewer over the field's photos. Its rows come from the drafts, which carry the label and the library
    // photo's favourite and archive state from the link DTO; the detail dialog reads the open photo itself.
    private PhotoViewer? _viewer;
    private IReadOnlyList<PhotoSummary> _viewerPhotos = [];
    private int? _viewerIndex;
    private Guid? _lastShownPhoto;
    private bool _viewerMounted;

    // The tile image button the viewer hands focus back to.
    private string? ViewerReturnFocusId => _lastShownPhoto is { } id && Photos.Any(p => p.PhotoId == id) ? PhotoTileId(id) : null;

    /// <summary>"Remove from event" → "Remove Beach from event": the item's accessible name carries the photo.</summary>
    internal string RemoveItemLabel(string name) =>
        RemoveLabel.StartsWith("Remove ", StringComparison.Ordinal) ? $"Remove {name} {RemoveLabel["Remove ".Length..]}" : $"{RemoveLabel}: {name}";

    private IReadOnlyList<OdsMenuItem> PhotoMenu(JournalDraftPhoto photo, bool removable)
    {
        var items = new List<OdsMenuItem>
        {
            new() { Icon = "open_in_full", Label = "Open", AriaLabel = $"Open {photo.Name}", OnClick = EventCallback.Factory.Create(this, () => OpenPhoto(photo)) },
        };
        if (removable)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = "link_off",
                Label = RemoveLabel,
                AriaLabel = RemoveItemLabel(photo.Name),
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => RemovePhotoAsync(photo)),
            });
        }

        return items;
    }

    private void OpenPhoto(JournalDraftPhoto photo)
    {
        _viewerPhotos = [.. Photos.Select(p => PhotoMappers.LinkSummary(p.PhotoId, p.Name, p.Favourited, p.Archived, p.ThumbnailsReady))];
        _viewerMounted = true;
        var index = Photos.FindIndex(p => p.PhotoId == photo.PhotoId);
        _viewerIndex = index < 0 ? 0 : index;
        _lastShownPhoto = Photos.Count == 0 ? null : Photos[_viewerIndex.Value].PhotoId;
    }

    private void OnViewerIndexChanged(int? index)
    {
        _viewerIndex = index;
        if (index is { } i && i >= 0 && i < _viewerPhotos.Count)
        {
            _lastShownPhoto = _viewerPhotos[i].PhotoId;
        }
    }

    // A photo deleted from the library leaves the field (its link is gone with it); favourite and archive are patched
    // on the draft in place. An edit may have renamed the photo: the record's dialog re-reads its links on its next
    // open, and the one read here is the edited photo's own, for its new label.
    private async Task OnViewerChangedAsync(PhotoViewerChange change)
    {
        var index = Photos.FindIndex(p => p.PhotoId == change.PhotoId);
        if (index < 0)
        {
            return;
        }

        switch (change.Kind)
        {
            case PhotoViewerChangeKind.Delete:
                Photos.RemoveAt(index);
                await ChangedAsync(AttachmentLinkKind.Photos);
                break;
            case PhotoViewerChangeKind.Archive:
                Photos[index] = Photos[index] with { Archived = PhotoMappers.Toggled(Photos[index].Archived) };
                break;
            case PhotoViewerChangeKind.Favourite:
                Photos[index] = Photos[index] with { Favourited = PhotoMappers.Toggled(Photos[index].Favourited) };
                break;
            case PhotoViewerChangeKind.Edit:
                if (await PhotosApi.GetAsync(change.PhotoId) is { } fresh)
                {
                    Photos[index] = Photos[index] with
                    {
                        Name = PhotoMappers.Label(fresh.Title, fresh.FileName, Photos[index].Name),
                        Favourited = fresh.Favourited,
                        Archived = fresh.Archived,
                    };
                }

                break;
        }
    }

    // Focus return after a removal destroyed the control that had focus (WCAG 2.4.3): a CSS selector chain, resolved
    // before the removal while the removed item still has a neighbour — the next item, else the previous, else Add.
    private string?[]? _focusAfter;
    private IJSObjectReference? _focusReturnJs;

    protected override async Task OnInitializedAsync() =>
        _imageAccess = PhotoImageAccess.From(await Auth.GetUserAsync());

    private PhotoImageSource ImageOf(JournalDraftPhoto p) =>
        PhotoImageSources.For(p.PhotoId, p.ThumbnailsReady, ThumbnailSize.Small, _imageAccess, PhotosApi);

    private bool AtCap(int count) => Cap is { } cap && count >= cap;

    /// <summary>The persistent help line under Add (AX4): the live number, or the lowered-cap sentence; none without a cap.</summary>
    internal string? CapText(int count, string plural) => Cap switch
    {
        null => null,
        { } cap when count > cap => $"This {Noun} has more than the current limit of {cap}. You can remove items but not add more.",
        { } cap => $"Up to {cap} {plural} per event or series.",
    };

    private string PhotosCountLine => CountLine(PhotoCount, "photo", "photos");

    private string FilesCountLine => CountLine(AttachmentCount, "document", "documents");

    /// <summary>"3 photos attached. You don't have permission to view photos." — real plurals, never "(s)" (AX7).</summary>
    internal static string CountLine(int count, string one, string many) =>
        $"{(count == 0 ? $"No {many}" : Plural(count, one, many))} attached. You don't have permission to view {many}.";

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    private Task AnnounceAsync(string message) => OnAnnounce.InvokeAsync(message);

    private Task ChangedAsync(AttachmentLinkKind kind) => OnChanged.InvokeAsync(kind);

    // The picker has already uploaded or picked the file; staging reads its metadata so the row shows the same kind,
    // size and date an attachment on a saved record does. Nothing is linked until save. A file past the cap is consumed
    // (so the picker closes) but not staged, and the pick's announcement says so (§3.1 "A pick that would go over").
    private async Task<bool> StageAttachmentAsync(AttachDocumentItem item)
    {
        if (Attachments.Any(a => a.Id == item.FileId))
        {
            return true;
        }

        if (AtCap(Attachments.Count))
        {
            _filesOverCap++;
            return true;
        }

        var meta = await Files.GetMetadataAsync(item.FileId);
        if (meta is null)
        {
            Snackbar.Add($"Couldn’t read “{item.Name}”.", Severity.Error);
            return false;
        }

        if (Attachments.All(a => a.Id != meta.Id))
        {
            Attachments.Add(meta);
            _filesAdded++;
            await ChangedAsync(AttachmentLinkKind.Files);
            if (Voice == AttachmentLinkVoice.Journal)
            {
                await AnnounceAsync($"{meta.FileName} added to the {Noun}’s attachments.");
            }
        }

        return true;
    }

    private async Task FilesStagedAsync(IReadOnlyList<AttachDocumentItem> _)
    {
        var added = _filesAdded;
        var over = _filesOverCap;
        _filesAdded = _filesOverCap = 0;
        if (Voice != AttachmentLinkVoice.Calendar || added + over == 0)
        {
            return;
        }

        await AnnounceAsync(over > 0 && Cap is { } cap
            ? $"Added {added}. Document limit reached: {cap}."
            : $"Added {Plural(added, "document", "documents")}.");
    }

    private async Task StagePhotosAsync(IReadOnlyList<AttachPhotoItem> items)
    {
        var fresh = items.Where(i => Photos.All(p => p.PhotoId != i.PhotoId)).DistinctBy(i => i.PhotoId).ToList();
        var room = Cap is { } cap ? Math.Max(0, cap - Photos.Count) : fresh.Count;
        var take = fresh.Take(room).ToList();
        foreach (var item in take)
        {
            Photos.Add(new JournalDraftPhoto(item.PhotoId, item.Name, item.ThumbnailsReady, item.Favourited, item.Archived));
        }

        if (take.Count > 0)
        {
            await ChangedAsync(AttachmentLinkKind.Photos);
        }

        if (Voice == AttachmentLinkVoice.Journal)
        {
            if (take.Count > 0)
            {
                await AnnounceAsync(take.Count == 1 ? $"1 photo added to the {Noun}." : $"{take.Count} photos added to the {Noun}.");
            }

            return;
        }

        if (fresh.Count > take.Count && Cap is { } limit)
        {
            await AnnounceAsync($"Added {take.Count}. Photo limit reached: {limit}.");
        }
        else if (take.Count > 0)
        {
            await AnnounceAsync($"Added {Plural(take.Count, "photo", "photos")}.");
        }
    }

    private async Task RemovePhotoAsync(JournalDraftPhoto photo)
    {
        var index = Photos.IndexOf(photo);
        if (index < 0)
        {
            return;
        }

        var neighbour = index + 1 < Photos.Count ? Photos[index + 1]
            : index > 0 ? Photos[index - 1]
            : null;
        Photos.RemoveAt(index);
        // The ⋯ menu that removed it is gone: the next tile's image, else the previous one's, else Add photos.
        _focusAfter = [neighbour is null ? null : $"#{PhotoTileId(neighbour.PhotoId)}", $"#jef-add-photos-{_uid} button"];
        await ChangedAsync(AttachmentLinkKind.Photos);
        await AnnounceAsync(Voice == AttachmentLinkVoice.Journal ? $"{photo.Name} removed from the {Noun}." : $"Removed {photo.Name}.");
    }

    private async Task RemoveAttachmentAsync(Guid fileId)
    {
        var index = Attachments.FindIndex(a => a.Id == fileId);
        if (index < 0)
        {
            return;
        }

        var removed = Attachments[index];
        var neighbour = index + 1 < Attachments.Count ? Attachments[index + 1]
            : index > 0 ? Attachments[index - 1]
            : null;
        Attachments.RemoveAt(index);
        _focusAfter = [neighbour is null ? null : $"#{JournalFileRows.MenuId(FilesListId, neighbour.Id)} button", $"#jef-attach-{_uid} button"];
        await ChangedAsync(AttachmentLinkKind.Files);
        await AnnounceAsync(Voice == AttachmentLinkVoice.Journal
            ? $"{removed.FileName} removed from the {Noun}’s attachments."
            : $"Removed {removed.FileName}.");
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusAfter is not { } candidates)
        {
            return;
        }

        _focusAfter = null;
        try
        {
            _focusReturnJs ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/focus-return.js");
            await _focusReturnJs.InvokeVoidAsync("focusFirstLater", candidates);
        }
        catch (Exception)
        {
            // Best-effort: the removal is already announced through the live region.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_focusReturnJs is not null)
        {
            try
            { await _focusReturnJs.DisposeAsync(); }
            catch (Exception) { /* JS already gone on teardown */ }
        }
    }
}
