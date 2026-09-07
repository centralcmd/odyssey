using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
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

    /// <summary>The effective per-file upload cap, for the dropzone hints. Seeded from the cache's
    /// fallback so the hint is never blank before the lookup resolves.</summary>
    private int _maxUploadMb = UploadLimitsCache.Fallback.MaxUploadMegabytes;

    private bool _canCreateTag;
    private bool _canCreateContact;

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

        // Ahead of the create-mode early return: both modes show the dropzones.
        _maxUploadMb = (await UploadLimits.GetAsync()).MaxUploadMegabytes;

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreateTag = user.HasPermission(PermissionClaims.JournalTagsCreate);
        _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);

        if (Entry is not { } entry)
            return;

        _loadingDetail = true;
        try
        {
            var photoUploads = new List<OdsUploadFile>();
            foreach (var p in entry.Photos.OrderBy(p => p.Position))
                photoUploads.Add(await ToUploadAsync(p.FileId, "Image"));

            var attachUploads = new List<OdsUploadFile>();
            foreach (var a in entry.Attachments)
                attachUploads.Add(await ToUploadAsync(a.FileId, "File"));

            _draft = JournalEntryDraft.From(entry, photoUploads, attachUploads);
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    private async Task<OdsUploadFile> ToUploadAsync(Guid fileId, string kind)
    {
        var meta = await Files.GetMetadataAsync(fileId);
        return new OdsUploadFile
        {
            Uid = fileId.ToString(),
            Name = meta?.FileName ?? fileId.ToString(),
            Kind = kind,
            SizeBytes = meta?.SizeBytes,
        };
    }

    // ── Inline member create ─────────────────────────────────────────────────
    // Both pickers hand their option back synchronously; the shared creators POST behind them and the
    // temp ids are mapped to real ones at save. A failed create drops its member.
    private OdsOption? CreateTagOption(string text, string? kind)
    {
        var option = TagCreator.Begin(text);
        if (option is not null)
            _createdTags.Add(option);
        return option;
    }

    private OdsOption? CreateContactOption(string text, string? kind)
    {
        var option = ContactCreator.Begin(text, kind);
        if (option is not null)
            _createdContacts.Add(option);
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

        if (IsEdit)
        {
            UpdateJournalEntry update;
            try
            {
                update = await JournalWrite.ToUpdateAsync(_draft, Files, UploadLimits, Entry!.Archived is not null);
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Couldn't upload an attached file: {ex.Message}", Severity.Error);
                return false;
            }

            return (await Journal.UpdateAsync(Entry.JournalEntryId, update)).Toast(Snackbar, "Unable to update entry", "Entry updated.");
        }

        NewJournalEntry entry;
        try
        {
            entry = await JournalWrite.ToNewAsync(_draft, Files, UploadLimits);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Couldn't upload an attached file: {ex.Message}", Severity.Error);
            return false;
        }

        return (await Journal.CreateAsync(entry)).Toast(Snackbar, "Unable to create entry", "Entry created.");
    }
}
