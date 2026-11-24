using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Photos;

public partial class PhotoDetailDialog
{
    /// <summary>Whether the dialog is shown. Default <c>true</c> for a host that mounts it under an <c>@if</c>;
    /// <see cref="PhotoViewer"/> keeps it mounted and toggles this, so the modal restores focus on close.</summary>
    [Parameter] public bool Open { get; set; } = true;

    /// <summary>Forwarded to <see cref="OdsModal.ReturnFocusToId"/>.</summary>
    [Parameter] public string? ReturnFocusToId { get; set; }

    [Parameter, EditorRequired] public IReadOnlyList<PhotoSummary> Photos { get; set; } = [];
    [Parameter] public int Index { get; set; }
    [Parameter] public EventCallback<int> IndexChanged { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<Guid> OnEdit { get; set; }
    [Parameter] public EventCallback<Guid> OnToggleFavourite { get; set; }
    [Parameter] public EventCallback<Guid> OnToggleArchive { get; set; }
    [Parameter] public EventCallback<Guid> OnDelete { get; set; }
    [Parameter] public bool CanUpdate { get; set; }
    [Parameter] public bool CanDelete { get; set; }
    [Parameter] public Func<Guid, string> TagName { get; set; } = id => id.ToString();
    /// <summary>A tag's stored icon key, or <c>null</c> for none — its chip then shows the generic glyph.</summary>
    [Parameter] public Func<Guid, string?> TagIcon { get; set; } = _ => null;
    /// <summary>A tag's colour, or <c>null</c> for none — its chip then has no dot (issue #394).</summary>
    [Parameter] public Func<Guid, string?> TagColor { get; set; } = _ => null;
    [Parameter] public Func<Guid, string> PersonName { get; set; } = id => id.ToString();
    /// <summary>An album as its chip (name, icon, colour), or <c>null</c> for one the host does not hold — it is then left out.</summary>
    [Parameter] public Func<Guid, OdsTagChips.Tag?> AlbumChip { get; set; } = _ => null;

    private ExistingPhoto? _detail;
    private IReadOnlyList<OdsTagChips.Tag> _tagChips = [];
    private IReadOnlyList<OdsTagChips.Tag> _albumChips = [];
    private Guid _loadedFor;
    private ElementReference _body;
    private bool _focused;
    private OdsLiveAnnouncer? _announcer;

    private PhotoSummary? Current => Index >= 0 && Index < Photos.Count ? Photos[Index] : null;

    // Always the library glyph: a tag's icon shows on its own chip in the Tags group, never in the title area.
    private const string LeadIcon = "photo_library";

    private string Label(PhotoSummary? p) => p is null ? "Photo" : (string.IsNullOrWhiteSpace(p.Title) ? "Photo" : p.Title!);


    // OdsModal fires this on Esc / scrim / close button (Open→false); tell the host to unmount us.
    private Task OnOpenChanged(bool open) => open ? Task.CompletedTask : OnClose.InvokeAsync();

    protected override async Task OnParametersSetAsync()
    {
        if (!Open)
        {
            // Closed: the next opening focuses the stage again and re-reads its photo.
            _focused = false;
            _loadedFor = Guid.Empty;
            _detail = null;
            return;
        }

        if (Current is { PhotoId: var id } && id != _loadedFor)
        {
            _loadedFor = id;
            _detail = await Api.GetAsync(id);
        }

        // Built once per parameter set rather than per render: a host refreshes its tag names by
        // re-rendering us, which is exactly when this runs again.
        _tagChips = _detail is null ? [] : [.. _detail.TagIds.Select(t => new OdsTagChips.Tag(TagName(t), TagIcon(t), TagColor(t)))];
        _albumChips = _detail is null ? [] : [.. _detail.AlbumIds.Select(AlbumChip).OfType<OdsTagChips.Tag>()];
    }

    // Focus the stage once the teleported dialog body is in the DOM so ←/→ navigation works without a
    // click first (retries across renders until the element exists; this modal has no form field for
    // OdsModal's autofocus to land on).
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Open && !_focused)
        {
            try
            { await _body.FocusAsync(); _focused = true; }
            catch { /* not teleported into the DOM yet — retry next render */ }
        }
    }

    private async Task Move(int delta)
    {
        if (Photos.Count == 0)
        {
            return;
        }

        var next = (Index + delta + Photos.Count) % Photos.Count;
        await IndexChanged.InvokeAsync(next);
        if (_announcer is not null && next < Photos.Count)
        {
            _ = _announcer.Announce(MoveAnnouncement(next));
        }
    }

    /// <summary>What a move to <paramref name="index"/> says: "{title}, n of N".</summary>
    internal string MoveAnnouncement(int index) => $"{Label(Photos[index])}, {index + 1} of {Photos.Count}";

    private async Task DownloadAsync()
    {
        if (Current is { PhotoId: var pid })
        {
            await Js.InvokeVoidAsync("open", Api.ContentUrl(pid), "_blank", "noopener");
        }
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowRight":
                await Move(1);
                break;
            case "ArrowLeft":
                await Move(-1);
                break;
        }
    }

    private static RenderFragment Row(string icon, string text) => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "pl-vrow");
        builder.OpenElement(2, "span");
        builder.AddAttribute(3, "class", "material-icons");
        builder.AddAttribute(4, "aria-hidden", "true");
        builder.AddContent(5, icon);
        builder.CloseElement();
        builder.OpenElement(6, "span");
        builder.AddContent(7, text);
        builder.CloseElement();
        builder.CloseElement();
    };
}
