using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Shared upload / load / preview / download / detach machinery for the file
/// collapsibles on account and transaction detail panels. Subclasses supply the
/// markup (the DS shell + row layout) and the handful of type-specific hooks:
/// the API path, the parent noun, how to read a row's metadata, how to attach an
/// uploaded blob, and how to surface a preview.
/// </summary>
public abstract class FilesSectionBase<TFile> : ComponentBase
{
    [Inject] protected IFilesApiClient Files { get; set; } = default!;
    [Inject] protected ISnackbar Snackbar { get; set; } = default!;
    [Inject] protected IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] protected IClipboardService Clipboard { get; set; } = default!;
    [Inject] protected IDialogService DialogService { get; set; } = default!;
    [Inject] protected IUploadLimitsCache UploadLimits { get; set; } = default!;

    [Parameter] public bool CanUpload { get; set; }
    [Parameter] public bool CanDelete { get; set; }
    [Parameter] public bool CanDownload { get; set; }

    protected List<TFile> items = [];
    protected bool isLoading;
    protected bool isUploading;
    protected readonly HashSet<Guid> deletingFiles = [];
    protected readonly HashSet<Guid> downloadingFiles = [];
    protected readonly HashSet<Guid> previewingFiles = [];

    protected const int MaxFileCount = 64;

    /// <summary>
    /// The admin-editable upload cap (issue #421 Wave 4). Held as a field rather than read per call
    /// because the derived sections state it in their rendered hint text as well as enforcing it.
    /// Seeded with the shipped fallback so a first render that beats the fetch still shows a sane
    /// number. Matches this file's existing plain-camelCase protected fields (`items`, `isLoading`).
    /// </summary>
    protected UploadLimitsDto uploadLimits = UploadLimitsCache.Fallback;
    private static readonly string[] AllowedExtensions = [".pdf", ".jpg", ".jpeg", ".png"];
    private static readonly string[] PreviewableContentTypes =
        ["image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp", "application/pdf"];

    /// <summary>The collection endpoint for this parent, e.g. <c>api/accounts/{id}/files</c>.</summary>
    /// <summary>Loads this parent's attachments through its own typed client.</summary>
    protected abstract Task<ApiResult<List<TFile>>> ListFilesAsync();

    /// <summary>Detaches one attachment through this parent's typed, parent-routed client.</summary>
    protected abstract Task<ApiResult> DetachFileAsync(Guid fileId);

    /// <summary>The parent noun used in user-facing messages ("account", "transaction").</summary>
    protected abstract string EntityNoun { get; }

    /// <summary>Reads the shared metadata off a row of the concrete file type.</summary>
    protected abstract ExistingFileMetadata MetadataOf(TFile file);

    /// <summary>Attaches a freshly uploaded blob to this parent with the selected file type.</summary>
    protected abstract Task AttachAsync(Guid fileMetadataId);

    /// <summary>Builds and opens the concrete preview dialog once a blob URL is ready.</summary>
    protected abstract void ShowPreview(TFile file, string blobUrl, string contentType);

    protected static bool IsPreviewable(string contentType) =>
        PreviewableContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser()) return;
        uploadLimits = await UploadLimits.GetAsync();
        await LoadFilesAsync();
    }

    protected async Task LoadFilesAsync()
    {
        isLoading = true;
        items = (await ListFilesAsync()).ItemsOrToast(Snackbar, "files");
        isLoading = false;
    }

    protected async Task UploadFilesAsync(IReadOnlyList<IBrowserFile>? files)
    {
        if (files is null || files.Count == 0) return;

        // Re-read on each upload, not just at init: a long-lived page could otherwise enforce a cap
        // an administrator changed minutes ago.
        uploadLimits = await UploadLimits.GetAsync();

        var valid = new List<IBrowserFile>();
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
            {
                Snackbar.Add($"{file.Name}: unsupported type. Allowed: .pdf, .jpg, .jpeg, .png", Severity.Warning);
                continue;
            }
            if (file.Size > uploadLimits.MaxUploadBytes)
            {
                Snackbar.Add($"{file.Name}: exceeds the {uploadLimits.MaxUploadMegabytes} MB limit.", Severity.Warning);
                continue;
            }
            valid.Add(file);
        }

        if (items.Count + valid.Count > MaxFileCount)
        {
            Snackbar.Add($"Cannot exceed {MaxFileCount} files per {EntityNoun}.", Severity.Warning);
            valid = valid.Take(MaxFileCount - items.Count).ToList();
        }

        if (valid.Count == 0) return;

        isUploading = true;
        var uploaded = 0;
        try
        {
            foreach (var file in valid)
            {
                try { await UploadSingleFileAsync(file, uploadLimits.MaxUploadBytes); uploaded++; }
                catch (Exception ex) { Snackbar.Add(ex.Message, Severity.Error); }
            }

            if (uploaded > 0)
            {
                Snackbar.Add($"{uploaded} file(s) uploaded successfully.", Severity.Success);
                await LoadFilesAsync();
            }
        }
        finally { isUploading = false; }
    }

    private async Task UploadSingleFileAsync(IBrowserFile file, long maxUploadBytes)
    {
        var result = await Files.UploadAsync(file.ToApiUpload(maxUploadBytes));
        await AttachAsync(result.Id);
    }

    protected async Task PreviewFileAsync(TFile file)
    {
        var meta = MetadataOf(file);
        if (!previewingFiles.Add(meta.Id)) return;

        StateHasChanged();
        try
        {
            var content = await Files.GetContentAsync(meta.Id);
            if (content is null) { Snackbar.Add("Preview failed.", Severity.Error); return; }

            var contentType = content.ContentType ?? meta.ContentType;
            var blobUrl = await JsRuntime.InvokeAsync<string>("createBlobUrl", content.Bytes, contentType);
            ShowPreview(file, blobUrl, contentType);
        }
        catch (Exception ex) { Snackbar.Add($"Preview failed: {ex.Message}", Severity.Error); }
        finally { previewingFiles.Remove(meta.Id); }
    }

    protected async Task DownloadFileAsync(TFile file)
    {
        var meta = MetadataOf(file);
        if (!downloadingFiles.Add(meta.Id)) return;

        StateHasChanged();
        try
        {
            var content = await Files.GetContentAsync(meta.Id);
            if (content is null) { Snackbar.Add("Download failed.", Severity.Error); return; }

            var contentType = content.ContentType ?? "application/octet-stream";
            await JsRuntime.InvokeVoidAsync("downloadFileFromBytes", content.Bytes, meta.FileName, contentType);
        }
        catch (Exception ex) { Snackbar.Add($"Download failed: {ex.Message}", Severity.Error); }
        finally { downloadingFiles.Remove(meta.Id); }
    }

    protected async Task ConfirmDeleteAsync(TFile file)
    {
        var meta = MetadataOf(file);
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Detach file",
            $"Detach '{meta.FileName}' from this {EntityNoun}?",
            yesText: "Detach", cancelText: "Cancel");

        if (confirmed == true) await DeleteFileAsync(file);
    }

    private async Task DeleteFileAsync(TFile file)
    {
        var meta = MetadataOf(file);
        if (!deletingFiles.Add(meta.Id)) return;

        StateHasChanged();
        try
        {
            if ((await DetachFileAsync(meta.Id)).Toast(Snackbar, "Delete failed", "File detached."))
                items.RemoveAll(f => MetadataOf(f).Id == meta.Id);
        }
        finally { deletingFiles.Remove(meta.Id); }
    }

}
