using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

public partial class CreateJournalEntryDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Active journal-tag options (id → name).</summary>
    [Parameter] public IReadOnlyList<OdsOption> TagOptions { get; set; } = [];

    /// <summary>Active contact options (id → name).</summary>
    [Parameter] public IReadOnlyList<OdsOption> ContactOptions { get; set; } = [];

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>When set, the dialog edits this entry. Null = create mode.</summary>
    [Parameter] public ExistingJournalEntry? Entry { get; set; }

    private bool IsEdit => Entry is not null;
    private bool _loadingDetail;

    private JournalEntryDraft _draft = new();

    private bool _canCreateTag;
    private bool _canCreateContact;
    private bool _canDownloadFiles;
    private bool _canReadLocations;
    private bool _canCreateLocations;
    private OdsLiveAnnouncer? _announcer;

    /// <summary>
    /// Announces through the announcer's own method, never by setting its Message: an identical string (the
    /// same place failing to create twice — the usual retry) renders nothing new and would be silent the
    /// second time (WCAG 4.1.3; accessibility review 2).
    /// </summary>
    private void Announce(string message) => _ = _announcer?.Announce(message);

    // Members created inline, ahead of the host's option-list refresh, so their chips resolve at once.
    private readonly List<OdsOption> _createdTags = [];
    private readonly List<OdsOption> _createdContacts = [];

    private IReadOnlyList<OdsOption> _tagOpts => Merge(_createdTags, TagOptions);
    private IReadOnlyList<OdsOption> _contactOpts => Merge(_createdContacts, ContactOptions);

    private static IReadOnlyList<OdsOption> Merge(List<OdsOption> created, IReadOnlyList<OdsOption> known) =>
        created.Count == 0 ? known : [.. created.Where(c => known.All(o => o.Value != c.Value)), .. known];

    protected override async Task OnInitializedAsync()
    {
        ContactCreator.OnCreateFailed = OnContactCreateFailed;
        TagCreator.OnCreateFailed = OnTagCreateFailed;

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreateTag = user.HasPermission(PermissionClaims.JournalTagsCreate);
        _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
        _canDownloadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canReadLocations = user.HasPermission(PermissionClaims.LocationsRead);
        _canCreateLocations = _canReadLocations && user.HasPermission(PermissionClaims.LocationsCreate);

        if (Entry is not { } entry)
            return;

        _loadingDetail = true;
        try
        {
            var photos = new List<JournalDraftPhoto>();
            foreach (var p in entry.Photos.OrderBy(p => p.Position))
            {
                var meta = await Files.GetMetadataAsync(p.FileId);
                photos.Add(new JournalDraftPhoto(p.FileId, meta?.FileName ?? "Photo", p.PhotoId, p.ThumbnailsReady));
            }

            // A dangling or unreadable attachment id is carried as a placeholder row rather than
            // dropped: the update replaces the whole set, so omitting it would silently unlink it.
            var attachments = new List<FileMetadataResponse>();
            foreach (var a in entry.Attachments)
            {
                attachments.Add(await Files.GetMetadataAsync(a.FileId)
                    ?? new FileMetadataResponse(a.FileId, a.FileId.ToString(), "application/octet-stream", 0, string.Empty, DateTime.UtcNow, null));
            }

            _draft = JournalEntryDraft.From(entry, photos, attachments);
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    // ── Inline member create ─────────────────────────────────────────────────
    // Both pickers hand their option back synchronously; the shared creators POST behind them and the
    // temp ids are mapped to real ones at save. A failed create drops its member.
    private OdsOption? CreateTagOption(string text, string? kind)
    {
        var option = TagCreator.Begin(text);
        if (option is not null)
        {
            _createdTags.Add(option);
            // The picker's ValueChanged re-renders only JournalEntryFields, which would keep the
            // stale option list and render the new member as "Unknown".
            StateHasChanged();
        }
        return option;
    }

    private OdsOption? CreateContactOption(string text, string? kind)
    {
        var option = ContactCreator.Begin(text, kind);
        if (option is not null)
        {
            _createdContacts.Add(option);
            StateHasChanged();
        }
        return option;
    }

    private void OnTagCreateFailed(string tempId)
    {
        _createdTags.RemoveAll(o => o.Value == tempId);
        _draft.TagIds = [.. _draft.TagIds.Where(id => id != tempId)];
        StateHasChanged();
    }

    private void OnContactCreateFailed(string tempId)
    {
        _createdContacts.RemoveAll(o => o.Value == tempId);
        _draft.ContactIds = [.. _draft.ContactIds.Where(id => id != tempId)];
        StateHasChanged();
    }

    private async Task<bool> SaveAsync()
    {
        if (!_draft.Validate())
            return false;

        // Let any in-flight inline creates land, then map the staged ids to the ones the server
        // issued; a create that failed resolves to null and is dropped rather than posted.
        await Task.WhenAll(TagCreator.WhenSettledAsync(), ContactCreator.WhenSettledAsync());
        _draft.TagIds = [.. _draft.TagIds.Select(TagCreator.Resolve).Where(id => id is not null).Select(id => id!)];
        _draft.ContactIds = [.. _draft.ContactIds.Select(ContactCreator.Resolve).Where(id => id is not null).Select(id => id!)];

        // A staged place is created immediately before the record write; a failed create stops the save, and a
        // retry after a failed record write reuses the created place (issue #383 §3.2, §3.2a).
        if (!await _draft.Place.EnsureCreatedAsync(Locations, _canReadLocations))
        {
            Announce($"Couldn’t add {_draft.Place.StagedName}");
            StateHasChanged();
            return false;
        }

        var link = _draft.Place.Link(_canReadLocations);
        if (IsEdit)
        {
            var update = JournalWrite.ToUpdate(_draft, Entry!.Archived is not null, link);
            return (await Journal.UpdateAsync(Entry.JournalEntryId, update))
                .PlacedOrToast(Snackbar, "Unable to update entry", "Entry updated.", _draft.Place, link);
        }

        var entry = JournalWrite.ToNew(_draft, link);
        return (await Journal.CreateAsync(entry))
            .PlacedOrToast(Snackbar, "Unable to create entry", "Entry created.", _draft.Place, link);
    }
}
