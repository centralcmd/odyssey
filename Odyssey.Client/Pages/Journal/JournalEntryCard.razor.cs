using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

public partial class JournalEntryCard
{
    /// <summary>A contact link as the card renders it. A null <see cref="ContactLink.Name"/> is an id the
    /// caller cannot resolve — the chip keeps its place and states "Unavailable" rather than leaking a GUID.</summary>
    public sealed record ContactLink(Guid Id, string? Name, string? Type, bool Archived);

    [Parameter, EditorRequired] public JournalEntrySummary Entry { get; set; } = default!;

    /// <summary>The card's DOM id — the focus-return anchor, and the deep-link scroll target.</summary>
    [Parameter] public string? Id { get; set; }

    [Parameter] public IReadOnlyList<ContactLink> Contacts { get; set; } = [];

    [Parameter] public IReadOnlyList<string> Tags { get; set; } = [];

    [Parameter] public IReadOnlyList<JournalPhotoGallery.Photo> Photos { get; set; } = [];

    /// <summary>Hydrated attachment metadata, or <c>null</c> while the host is still fetching it.</summary>
    [Parameter] public IReadOnlyList<FileMetadataResponse>? Attachments { get; set; }

    [Parameter] public IReadOnlyList<OdsMenuItem> Actions { get; set; } = [];

    [Parameter] public bool CanReadFiles { get; set; }

    /// <summary>Raised the first time the attachment disclosure opens, so the host hydrates then.</summary>
    [Parameter] public EventCallback OnFilesOpened { get; set; }

    [Parameter] public EventCallback<JournalPhotoGallery.Photo> OnOpenPhoto { get; set; }

    /// <summary>Detach a file from the entry. Unset = no Remove item on the file menus.</summary>
    [Parameter] public EventCallback<Guid>? OnRemoveFile { get; set; }

    private bool _filesOpen;

    // Focus return across a file removal (WCAG 2.4.3). The row's ⋯ menu is destroyed by its own action,
    // and the whole row list is unmounted when the last file goes — so the CARD, which outlives both,
    // is what picks the landing spot beforehand and puts focus back afterwards. Same shape as
    // InsurancePolicyLinkTiles, which solved this for the policy party tiles.
    private string? _focusAfterRemoval;
    private bool _pendingFocus;
    private IJSObjectReference? _focusReturnJs;

    private List<string> _tagNames = [];
    private string _leafDow = string.Empty;
    private string _leafDay = "—";
    private string _leafMon = string.Empty;
    private string _leafAccessible = string.Empty;

    private bool Archived => Entry.Archived is not null;

    private string FilesListId => $"jec-files-{Entry.JournalEntryId}";

    // Only when the entry has actually been revised — a create writes both stamps, so comparing them is
    // what keeps "edited" off every untouched entry.
    private bool Edited => Entry.UpdatedAt > Entry.CreatedAt;

    // Named only when the editor is someone other than the author; the stable ids decide, the resolved
    // name displays.
    private string EditedBy =>
        !string.IsNullOrWhiteSpace(Entry.UpdatedByUserId)
        && Entry.UpdatedByUserId != Entry.CreatedByUserId
        && !string.IsNullOrWhiteSpace(Entry.UpdatedByName)
            ? $" by {Entry.UpdatedByName}"
            : string.Empty;

    protected override void OnParametersSet()
    {
        _tagNames = [.. Tags];

        var d = Entry.EntryDate.ToLocalTime();
        _leafDow = d.ToString("ddd", CultureInfo.CurrentCulture);
        // The day ALONE. "d" on its own is .NET's standard short-date pattern ("5/25/2026"), which
        // overflows the rail; the day number has to be taken from the value itself.
        _leafDay = d.Day.ToString(CultureInfo.CurrentCulture);
        _leafMon = d.ToString("MMM yy", CultureInfo.CurrentCulture);
        // The leaf's three lines are one date; read as one rather than as three fragments.
        _leafAccessible = d.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private async Task ToggleFiles()
    {
        _filesOpen = !_filesOpen;
        if (_filesOpen && Attachments is null)
        {
            await OnFilesOpened.InvokeAsync();
        }
    }

    // The next file's menu, else the previous one's, else the disclosure that opened the list — which
    // is where focus belongs once the list it controls has emptied, and finally the card's own menu.
    // The neighbour is resolved BEFORE the write: afterwards the removed row is gone with its position.
    private async Task RemoveFileAsync(Guid fileId)
    {
        if (OnRemoveFile is not { } remove)
        {
            return;
        }

        var ids = (Attachments ?? []).Select(a => a.Id).ToList();
        var index = ids.IndexOf(fileId);
        if (index < 0)
        {
            _focusAfterRemoval = null;
        }
        else if (index + 1 < ids.Count)
        {
            _focusAfterRemoval = JournalFileRows.MenuId(FilesListId, ids[index + 1]);
        }
        else
        {
            _focusAfterRemoval = index > 0 ? JournalFileRows.MenuId(FilesListId, ids[index - 1]) : null;
        }

        await remove.InvokeAsync(fileId);
        _pendingFocus = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_pendingFocus)
        {
            return;
        }

        _pendingFocus = false;
        var neighbour = _focusAfterRemoval;
        _focusAfterRemoval = null;

        try
        {
            _focusReturnJs ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/focus-return.js");
            string?[] candidates =
            [
                neighbour is null ? null : $"#{neighbour} button",
                Id is null ? null : $"#{Id} .jec-filecount",
                Id is null ? null : $"#{Id} .je-cardmenu button",
            ];
            await _focusReturnJs.InvokeVoidAsync("focusFirst", candidates);
        }
        catch (Exception)
        {
            // Best-effort: the removal is already announced through the page's live region, so a failed
            // focus return degrades rather than losing the outcome.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_focusReturnJs is not null)
        {
            try { await _focusReturnJs.DisposeAsync(); } catch (Exception) { /* JS already gone on teardown */ }
        }
    }
}
