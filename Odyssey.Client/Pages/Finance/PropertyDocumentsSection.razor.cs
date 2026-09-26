using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

public partial class PropertyDocumentsSection
{
    [Inject] private IPropertiesApiClient Properties { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private IClipboardService Clipboard { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private IReferenceDataCache ReferenceData { get; set; } = default!;
    [Inject] private IContactQuickCreate ContactCreator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;

    [Parameter, EditorRequired] public ExistingProperty Property { get; set; } = default!;

    /// <summary>Edit and Detach (<c>properties.update</c>).</summary>
    [Parameter] public bool CanUpdate { get; set; }

    /// <summary>
    /// Attach (<c>properties.update</c> AND <c>files.read</c>, §7.2) — the section's own empty-state copy
    /// and the attach dialog both key on it.
    /// </summary>
    [Parameter] public bool CanAttach { get; set; }

    /// <summary>The attach dialog's "Upload new" tab (<c>files.create</c>).</summary>
    [Parameter] public bool CanUpload { get; set; }

    /// <summary>
    /// A fresh token opens the attach dialog — how the row menu's "Attach documents" reaches a section
    /// it does not own, the <see cref="PropertyEstimatesSection.NewEstimateRequestToken"/> shape.
    /// </summary>
    [Parameter] public Guid? AttachRequestToken { get; set; }

    /// <summary>Raised with the document count after every (re)load.</summary>
    [Parameter] public EventCallback<int> OnCountChanged { get; set; }

    private Guid? _handledAttachToken;

    private List<ExistingPropertyFile> _files = [];
    private bool _isLoading = true;
    private bool _loadFailed;

    private IReadOnlyList<ExistingContact> _contacts = [];
    private IReadOnlyList<OdsOption> _issuerOptions = [];
    private bool _canReadContacts;
    private bool _canCreateContact;

    private Guid _attachKey = Guid.Empty;
    private bool _attachOpen;

    private string SectionMeta => _isLoading && _files.Count == 0 ? "" : $"{_files.Count} file{(_files.Count == 1 ? "" : "s")}";

    private string EmptyText => !CanAttach
        ? "No documents are attached to this property."
        : Property.Type == PropertyType.Vehicle
            ? "No documents yet — attach the registration, an inspection, the insurance certificate or a warranty."
            : "No documents yet — attach the deed, the purchase agreement, a valuation or a warranty.";

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        var user = await AuthenticationState.GetUserAsync();
        _canReadContacts = user.HasPermission(PermissionClaims.ContactsRead);
        _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
        ContactCreator.OnCreateFailed = OnContactCreateFailed;

        if (_canReadContacts)
        {
            _contacts = [.. await ReferenceData.ContactsAsync()];
            _issuerOptions = OdsContactOptions.Active(_contacts);
        }

        await LoadAsync();
    }

    protected override void OnParametersSet()
    {
        if (AttachRequestToken is not { } token || token == Guid.Empty || token == _handledAttachToken)
            return;

        _handledAttachToken = token;
        if (CanAttach)
            OpenAttach();
    }

    /// <summary>
    /// Re-reads the documents. Public because <c>OnInitializedAsync</c> early-returns outside the
    /// browser, so a render test has no other way in — the seam the estimates section exposes too.
    /// </summary>
    public async Task LoadAsync()
    {
        _isLoading = true;
        var result = await Properties.ListFilesAsync(Property.PropertyId);
        _loadFailed = !result.IsSuccess;
        if (result.IsSuccess)
            _files = result.ValueOr([]);
        _isLoading = false;
        if (!_loadFailed)
            await OnCountChanged.InvokeAsync(_files.Count);
        StateHasChanged();
    }

    /// <summary>Test seam: the contacts the issuer column resolves against, and whether it may.</summary>
    internal void UseContacts(IReadOnlyList<ExistingContact> contacts, bool canRead)
    {
        _canReadContacts = canRead;
        _contacts = canRead ? contacts : [];
        _issuerOptions = OdsContactOptions.Active(_contacts);
    }

    private void OpenAttach()
    {
        _attachKey = Guid.NewGuid();
        _attachOpen = true;
    }

    private static OdsFileKindMeta KindMeta(PropertyFileType type)
    {
        var t = OdsTypeRegistries.PropertyFileTypeOf(type);
        return new OdsFileKindMeta(t.Icon, t.Color, t.Soft);
    }

    private IEnumerable<OdsFilesRow> FileRows => _files.Select(f => new OdsFilesRow
    {
        Id = f.FileMetadata.Id.ToString(),
        Name = f.FileMetadata.FileName,
        // The enum key (not the registry label) so the edit dialog's type picker round-trips on it.
        Kind = f.FileType.ToString(),
        SizeBytes = f.FileMetadata.SizeBytes,
        UploadedAtUtc = f.FileMetadata.UploadedAtUtc,
        ValidFrom = f.ValidFrom,
        ValidTo = f.ValidTo,
        IssuedAt = f.IssuedAt,
        IssuedBy = f.IssuedBy,
    });

    private ExistingPropertyFile FileById(string id) => _files.First(f => f.FileMetadata.Id.ToString() == id);

    /// <summary>
    /// The issuer's display name. The response carries the contact id alone (§7.3), so the name comes
    /// from the caller's own contacts read: without <c>contacts.read</c> it says so rather than printing
    /// a dash that would read as "no issuer".
    /// </summary>
    private string? IssuerName(Guid? issuedBy)
    {
        if (issuedBy is null)
            return null;
        if (!_canReadContacts)
            return "Contact (no access)";
        return _contacts.FirstOrDefault(c => c.ContactId == issuedBy)?.ResolvedDisplayName ?? "Unknown contact";
    }

    private OdsOption? CreateContactOption(string text, string kind)
    {
        var option = ContactCreator.Begin(text, kind);
        if (option is not null)
            _issuerOptions = [.. _issuerOptions, option];
        return option;
    }

    private void OnContactCreateFailed(string tempId)
    {
        _issuerOptions = [.. _issuerOptions.Where(o => o.Value != tempId)];
        StateHasChanged();
    }

    private IReadOnlyList<OdsMenuItem> BuildMenu(OdsFilesRow row)
    {
        var file = FileById(row.Id);
        return
        [
            new OdsMenuItem
            {
                Icon = "download",
                Label = "Download",
                OnClick = EventCallback.Factory.Create(this, () => DownloadAsync(file)),
            },
            new OdsMenuItem
            {
                Icon = "fingerprint",
                TrailingIcon = "content_copy",
                Label = "Copy ID",
                OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(file.FileMetadata.Id.ToString(), "File ID copied.")),
            },
        ];
    }

    private async Task DownloadAsync(ExistingPropertyFile file)
    {
        var result = await Properties.DownloadFileAsync(Property.PropertyId, file.FileMetadata.Id);
        if (result.OrToast(Snackbar, "Download failed") is not { } content)
            return;

        var contentType = content.ContentType ?? file.FileMetadata.ContentType ?? "application/octet-stream";
        await JsRuntime.InvokeVoidAsync("downloadFileFromBytes", content.Bytes, file.FileMetadata.FileName, contentType);
    }

    private EventCallback<OdsRecordSaveEventArgs> SaveAction =>
        CanUpdate ? EventCallback.Factory.Create<OdsRecordSaveEventArgs>(this, HandleSaveAsync) : default;

    private EventCallback<OdsFilesRow> DetachAction =>
        CanUpdate ? EventCallback.Factory.Create<OdsFilesRow>(this, row => ConfirmDetachAsync(FileById(row.Id))) : default;

    /// <summary>
    /// <c>PUT …/files/{fileId}</c> — a full replacement of the link's type and validity, so a date the
    /// dialog left empty clears the stored value. Nothing here touches the FileMetadata.
    /// </summary>
    private async Task HandleSaveAsync(OdsRecordSaveEventArgs args)
    {
        if (args.Patch is not OdsFileEdit patch || args.Key is not string key)
            return;

        var file = _files.FirstOrDefault(f => f.FileMetadata.Id.ToString() == key);
        if (file is null)
            return;

        // Let an in-flight inline issuer create land, then map its staged id to the one the server
        // issued; a create that failed resolves to null rather than posting an id no server issued.
        await ContactCreator.WhenSettledAsync();
        var issuedBy = Guid.TryParse(ContactCreator.Resolve(patch.IssuedBy?.ToString()), out var issuer)
            ? issuer
            : (Guid?)null;

        var newType = Enum.TryParse<PropertyFileType>(patch.Kind, out var parsed) ? parsed : file.FileType;
        var unchanged =
            newType == file.FileType && patch.ValidFrom == file.ValidFrom && patch.ValidTo == file.ValidTo
            && patch.IssuedAt == file.IssuedAt && issuedBy == file.IssuedBy;
        if (unchanged)
            return;

        var result = await Properties.UpdateFileAsync(Property.PropertyId, file.FileMetadata.Id, new UpdatePropertyFileRequest
        {
            FileType = newType,
            ValidFrom = patch.ValidFrom,
            ValidTo = patch.ValidTo,
            IssuedAt = patch.IssuedAt,
            IssuedBy = issuedBy,
        });

        if (result.Toast(Snackbar, "Unable to update document", "Document updated."))
            await LoadAsync();
    }

    private async Task ConfirmDetachAsync(ExistingPropertyFile file)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Detach document",
            $"Detach '{file.FileMetadata.FileName}' from {Property.Name}? The file itself is kept in Files.",
            yesText: "Detach", cancelText: "Cancel");
        if (confirmed != true)
            return;

        if ((await Properties.DetachFileAsync(Property.PropertyId, file.FileMetadata.Id)).Toast(Snackbar, "Detach failed", "Document detached."))
            await LoadAsync();
    }
}
