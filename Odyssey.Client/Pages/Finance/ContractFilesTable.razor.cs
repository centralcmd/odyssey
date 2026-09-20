using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

public partial class ContractFilesTable
{
    /// <summary>The owning contract id (the download/update/detach route's <c>{id}</c>).</summary>
    [Parameter, EditorRequired] public Guid ContractId { get; set; }

    /// <summary>The attached documents (flattened from the contract DTO).</summary>
    [Parameter] public IReadOnlyList<ContractFileItem> Files { get; set; } = [];

    [Parameter] public bool CanDownload { get; set; }

    [Parameter] public bool CanUpdate { get; set; }

    [Parameter] public bool CanDelete { get; set; }

    /// <summary>
    /// True when the contract is archived. Both document writes are refused with a <c>400</c> then,
    /// so Edit and Delete are withheld rather than offered and then failed.
    /// </summary>
    [Parameter] public bool Archived { get; set; }

    /// <summary>Raised after a detach so the host re-fetches the contract.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>
    /// Raised with this contract's documents, freshly read, after a metadata update — the targeted
    /// refresh that replaces a whole-contract refetch. Unset falls back to <see cref="OnChanged"/>.
    /// </summary>
    [Parameter] public EventCallback<List<ExistingContractFile>> OnFilesRefreshed { get; set; }

    /// <summary>Full-width content shown when there are no rows.</summary>
    [Parameter] public RenderFragment? Empty { get; set; }

    private IReadOnlyList<ExistingContact> _contacts = [];
    private IReadOnlyList<OdsOption> _issuerOptions = [];
    private bool _canCreateContact;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        _contacts = [.. await ReferenceData.ContactsAsync()];
        _issuerOptions = OdsContactOptions.Active(_contacts);

        var user = await AuthenticationState.GetUserAsync();
        _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
        ContactCreator.OnCreateFailed = OnContactCreateFailed;
    }

    private static OdsFileKindMeta KindMeta(ContractFileType type)
    {
        var t = OdsTypeRegistries.ContractFileTypeOf(type);
        return new OdsFileKindMeta(t.Icon, t.Color, t.Soft);
    }

    private IEnumerable<OdsFilesRow> FileRows => Files.Select(f => new OdsFilesRow
    {
        Id = f.FileId.ToString(),
        Name = f.FileName,
        // The enum key (not the registry label) so the edit dialog's type picker round-trips on it.
        Kind = f.FileType.ToString(),
        SizeBytes = f.SizeBytes,
        UploadedAtUtc = f.UploadedAtUtc,
        ValidFrom = f.ValidFrom,
        ValidTo = f.ValidTo,
        IssuedAt = f.IssuedAt,
        IssuedBy = f.IssuedBy,
    });

    private ContractFileItem FileById(string id) => Files.First(f => f.FileId.ToString() == id);

    /// <summary>
    /// The issuer's name, or <see langword="null"/> when the id resolves to nothing this caller can
    /// see — the table then prints an em dash. The response carries the contact id alone, never a
    /// name, so resolution is client-side behind <c>contacts.read</c>.
    /// </summary>
    private string? IssuerName(Guid? issuedBy) =>
        issuedBy is null ? null : _contacts.FirstOrDefault(c => c.ContactId == issuedBy)?.ResolvedDisplayName;

    // Issued-by can add a missing issuer inline. The edit dialog commits its patch synchronously, so
    // the staged id is awaited and mapped in HandleSaveAsync before the update is posted.
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
        var menu = new List<OdsMenuItem>();

        if (CanDownload)
        {
            menu.Add(new OdsMenuItem
            {
                Icon = "download",
                Label = "Download",
                OnClick = EventCallback.Factory.Create(this, () => DownloadAsync(file)),
            });
        }

        menu.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            TrailingIcon = "content_copy",
            Label = "Copy ID",
            OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(file.FileId.ToString(), "File ID copied.")),
        });

        return menu;
    }

    private async Task DownloadAsync(ContractFileItem file)
    {
        var result = await Contracts.DownloadFileAsync(ContractId, file.FileId);
        if (result.OrToast(Snackbar, "Download failed") is not { } content)
            return;

        var contentType = content.ContentType ?? file.ContentType ?? "application/octet-stream";
        await JsRuntime.InvokeVoidAsync("downloadFileFromBytes", content.Bytes, file.FileName, contentType);
    }

    private EventCallback<OdsRecordSaveEventArgs> SaveAction =>
        CanUpdate && !Archived ? EventCallback.Factory.Create<OdsRecordSaveEventArgs>(this, HandleSaveAsync) : default;

    /// <summary>
    /// Detach stays live on an archived contract, deliberately unlike Edit. <c>ContractService</c>
    /// guards <c>AttachFile</c> and <c>UpdateFile</c> on the archive state and does <b>not</b> guard
    /// <c>DetachFile</c> — the same asymmetry <c>DeleteParty</c> has, on the same reasoning: detaching
    /// a link needs only the link. Withholding it here would refuse something the API allows.
    /// </summary>
    private EventCallback<OdsFilesRow> DeleteAction =>
        CanDelete ? EventCallback.Factory.Create<OdsFilesRow>(this, row => ConfirmDetachAsync(FileById(row.Id))) : default;

    /// <summary>
    /// <c>PUT …/files/{fileId}</c> — a <b>full replacement</b> of the link row's type and validity,
    /// so a date the dialog left empty clears the stored value. The patch is applied to the
    /// <c>ContractFile</c>, never to the <c>FileMetadata</c> it references: this verb accepts no name.
    /// </summary>
    private async Task HandleSaveAsync(OdsRecordSaveEventArgs args)
    {
        if (args.Patch is not OdsFileEdit patch || args.Key is not string key)
            return;

        var file = Files.FirstOrDefault(f => f.FileId.ToString() == key);
        if (file is null)
            return;

        // Let any in-flight inline issuer create land, then map the staged id to the one the server
        // issued; a create that failed resolves to null and clears the link rather than posting an id
        // no server issued.
        await ContactCreator.WhenSettledAsync();
        var issuedBy = Guid.TryParse(ContactCreator.Resolve(patch.IssuedBy?.ToString()), out var issuer)
            ? issuer
            : (Guid?)null;

        var newType = Enum.TryParse<ContractFileType>(patch.Kind, out var parsed) ? parsed : file.FileType;
        var unchanged =
            newType == file.FileType && patch.ValidFrom == file.ValidFrom && patch.ValidTo == file.ValidTo
            && patch.IssuedAt == file.IssuedAt && issuedBy == file.IssuedBy;

        if (unchanged)
            return;

        var result = await Contracts.UpdateFileAsync(ContractId, file.FileId, new UpdateContractFileRequest
        {
            FileType = newType,
            ValidFrom = patch.ValidFrom,
            ValidTo = patch.ValidTo,
            IssuedAt = patch.IssuedAt,
            IssuedBy = issuedBy,
        });

        if (!result.Toast(Snackbar, "Unable to update document", "Document updated."))
            return;

        // The targeted refresh. A failed re-read falls back to the whole-contract refetch rather
        // than leaving the table showing what the user typed as though it were what was stored.
        if (OnFilesRefreshed.HasDelegate
            && await Contracts.ListFilesAsync(ContractId) is { IsSuccess: true, Value: { } files })
        {
            await OnFilesRefreshed.InvokeAsync(files);
            return;
        }

        await OnChanged.InvokeAsync();
    }

    private async Task ConfirmDetachAsync(ContractFileItem file)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Detach document",
            $"Detach '{file.FileName}'? The file itself is kept in your files.",
            yesText: "Detach", cancelText: "Cancel");

        if (confirmed != true)
            return;

        if ((await Contracts.DetachFileAsync(ContractId, file.FileId)).Toast(Snackbar, "Detach failed", "Document detached."))
            await OnChanged.InvokeAsync();
    }
}
