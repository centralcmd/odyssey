using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Dtos.Application;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class TaxStatementFilesSection
{
    [Parameter, EditorRequired] public Guid StatementId { get; set; }

    /// <summary>Statement name — shown as the context line in the file viewer.</summary>
    [Parameter] public string? StatementName { get; set; }

    /// <summary>The documents currently attached to this statement (embedded on the statement DTO).</summary>
    [Parameter] public List<ExistingTaxStatementFile> Files { get; set; } = [];

    [Parameter] public bool CanUpload { get; set; }
    [Parameter] public bool CanDownload { get; set; }
    [Parameter] public bool CanDelete { get; set; }

    /// <summary>Raised after an attach/detach so the host re-fetches the statement.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private bool _isOpen;
    private bool _isUploading;

    // Bound to OdsTaxStatementFileTypeSelect (the DS picker keys on the enum name).
    private string _selectedFileType = nameof(TaxStatementFileType.Other);
    private TaxStatementFileType SelectedFileTypeEnum =>
        Enum.TryParse<TaxStatementFileType>(_selectedFileType, out var fileType) ? fileType : TaxStatementFileType.Other;

    private static OdsFileKindMeta KindMeta(TaxStatementFileType type)
    {
        var t = OdsTypeRegistries.TaxStatementFileTypeOf(type);
        return new OdsFileKindMeta(t.Icon, t.Color, t.Soft);
    }

    private static readonly string[] AllowedExtensions = [".pdf", ".jpg", ".jpeg", ".png", ".webp"];
    private static readonly string[] PreviewableContentTypes =
        ["image/jpeg", "image/png", "image/webp", "application/pdf"];

    private IEnumerable<OdsFilesRow> FileRows => Files.Select(f => new OdsFilesRow
    {
        Id = f.FileMetadata.Id.ToString(),
        Name = f.FileMetadata.FileName,
        // The enum key (not the registry label) drives the kind chip + avatar.
        Kind = f.FileType.ToString(),
        SizeBytes = f.FileMetadata.SizeBytes,
        UploadedAtUtc = f.FileMetadata.UploadedAtUtc,
    });

    private ExistingTaxStatementFile FileById(string id) =>
        Files.First(f => f.FileMetadata.Id.ToString() == id);

    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback;

    private async Task UploadFilesAsync(IReadOnlyList<IBrowserFile>? files)
    {
        // Read per invocation (issue #421 Wave 4): this handler is already async, so the cap needs no
        // prefetch, and the message below names the number actually in force.
        _uploadLimits = await UploadLimits.GetAsync();

        if (files is null || files.Count == 0)
            return;

        var valid = new List<IBrowserFile>();
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
            {
                Snackbar.Add($"{file.Name}: unsupported type. Allowed: .pdf, .jpg, .jpeg, .png, .webp", Severity.Warning);
                continue;
            }
            if (file.Size > _uploadLimits.MaxUploadBytes)
            {
                Snackbar.Add($"{file.Name}: exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.", Severity.Warning);
                continue;
            }
            valid.Add(file);
        }

        if (valid.Count == 0)
            return;

        _isUploading = true;
        var uploaded = 0;
        try
        {
            foreach (var file in valid)
            {
                try
                {
                    var result = await FilesApi.UploadAsync(file.ToApiUpload(_uploadLimits.MaxUploadBytes));
                    await AttachAsync(result.Id);
                    uploaded++;
                }
                catch (Exception ex)
                {
                    Snackbar.Add(ex.Message, Severity.Error);
                }
            }

            if (uploaded > 0)
            {
                Snackbar.Add($"{uploaded} document(s) attached.", Severity.Success);
                await OnChanged.InvokeAsync();
            }
        }
        finally
        {
            _isUploading = false;
        }
    }

    private async Task AttachAsync(Guid fileId)
    {
        var attach = await TaxStatements.AttachFileAsync(
            StatementId, new AttachTaxStatementFileRequest(fileId, SelectedFileTypeEnum));
        if (!attach.IsSuccess)
            throw new InvalidOperationException($"Failed to attach document: {attach.Error}");
    }

    private IReadOnlyList<OdsMenuItem> BuildMenu(OdsFilesRow row)
    {
        var file = FileById(row.Id);
        var menu = new List<OdsMenuItem>();

        if (CanDownload && PreviewableContentTypes.Contains(file.FileMetadata.ContentType, StringComparer.OrdinalIgnoreCase))
            menu.Add(new OdsMenuItem { Icon = "visibility", Label = "Preview", OnClick = EventCallback.Factory.Create(this, () => PreviewAsync(file)) });

        if (CanDownload)
            menu.Add(new OdsMenuItem { Icon = "download", Label = "Download", OnClick = EventCallback.Factory.Create(this, () => DownloadAsync(file)) });

        menu.Add(new OdsMenuItem { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(file.FileMetadata.Id.ToString(), "File ID copied.")) });

        return menu;
    }

    private async Task DownloadAsync(ExistingTaxStatementFile file)
    {
        var content = await FilesApi.GetContentAsync(file.FileMetadata.Id);
        if (content is null)
        { Snackbar.Add("Download failed.", Severity.Error); return; }

        var contentType = content.ContentType ?? "application/octet-stream";
        await JsRuntime.InvokeVoidAsync("downloadFileFromBytes", content.Bytes, file.FileMetadata.FileName, contentType);
    }

    // "Preview" opens the design-system file viewer (FilePreviewDialog), the same
    // in-app document viewer the account/transaction file sections use.
    private async Task PreviewAsync(ExistingTaxStatementFile file)
    {
        var content = await FilesApi.GetContentAsync(file.FileMetadata.Id);
        if (content is null)
        { Snackbar.Add("Preview failed.", Severity.Error); return; }

        var contentType = content.ContentType ?? file.FileMetadata.ContentType;
        var blobUrl = await JsRuntime.InvokeAsync<string>("createBlobUrl", content.Bytes, contentType);
        _preview = new PreviewState(blobUrl, contentType, file.FileMetadata.FileName,
            file.FileMetadata.SizeBytes, file.FileMetadata.UploadedAtUtc);
        _previewKey = Guid.NewGuid();
        _previewOpen = true;
    }

    private PreviewState? _preview;
    private Guid _previewKey;
    private bool _previewOpen;

    private sealed record PreviewState(
        string BlobUrl, string ContentType, string FileName, long SizeBytes, DateTime UploadedAtUtc);

    private EventCallback<OdsFilesRow> DeleteAction =>
        CanDelete ? EventCallback.Factory.Create<OdsFilesRow>(this, row => ConfirmDetachAsync(FileById(row.Id))) : default;

    private async Task ConfirmDetachAsync(ExistingTaxStatementFile file)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Detach document",
            $"Detach '{file.FileMetadata.FileName}' from this tax statement?",
            yesText: "Detach", cancelText: "Cancel");

        if (confirmed == true
            && (await TaxStatements.DetachFileAsync(StatementId, file.FileMetadata.Id)).Toast(Snackbar, "Detach failed", "Document detached."))
        {
            await OnChanged.InvokeAsync();
        }
    }
}
