using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Dtos.Application;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AddFileDialog
{
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after at least one successful upload so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    [Parameter] public Guid AccountId { get; set; }
    [Parameter] public string AccountName { get; set; } = string.Empty;
    [Parameter] public string? AccountNumber { get; set; }

    private const int MaxFileCount = 64;
    private static readonly string[] AllowedExtensions = [".pdf", ".jpg", ".jpeg", ".png"];

    // The AccountFileType vocabulary projected to the OdsFileUpload kind shape (per-file picker).
    private static readonly IReadOnlyList<OdsFileKind> _kinds =
        [.. OdsTypeRegistries.AccountFileTypes.Select(t => new OdsFileKind
        {
            Key = t.Key, Label = t.Label, Icon = t.Icon, Color = t.Color, Soft = t.Soft,
        })];

    private List<OdsUploadFile> _files = [];
    private string? _error;
    private bool _isUploading;

    // Files whose per-row validity editor is expanded (keyed by Uid).
    private readonly HashSet<string> _metaOpen = [];

    private IReadOnlyList<OdsOption> _issuerOptions = [];

    // Prefetched rather than read at validation time: OnFilesChanged is a synchronous handler, and the
    // cap is admin-editable (issue #421 Wave 4). Seeded with the shipped fallback so a dialog that
    // renders before the fetch resolves still validates against a sane number rather than zero.
    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;
        _uploadLimits = await UploadLimits.GetAsync();
        var contacts = await ReferenceData.ContactsAsync();
        _issuerOptions = [.. contacts
            .Where(c => c.Archived is null)
            .Select(c => new OdsOption(c.ContactId.ToString(), c.ResolvedDisplayName))];
    }

    private Task CancelClicked() => OpenChanged.InvokeAsync(false);

    // Controlled list — enforce the allow-list, per-file size cap and the account file-count cap
    // before anything lands in the picker.
    private void OnFilesChanged(IReadOnlyList<OdsUploadFile> files)
    {
        var kept = new List<OdsUploadFile>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (f.Source is not null && !AllowedExtensions.Contains(ext))
            {
                Snackbar.Add($"{f.Name}: unsupported type. Allowed: .pdf, .jpg, .jpeg, .png", Severity.Warning);
                continue;
            }
            if (f.SizeBytes > _uploadLimits.MaxUploadBytes)
            {
                Snackbar.Add($"{f.Name}: exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.", Severity.Warning);
                continue;
            }
            if (kept.Count >= MaxFileCount)
            {
                Snackbar.Add($"Cannot exceed {MaxFileCount} files per account.", Severity.Warning);
                break;
            }
            kept.Add(f);
        }
        _files = kept;
        if (_error is not null && _files.Count > 0)
            _error = null;
    }

    private void ToggleMeta(OdsUploadFile file)
    {
        if (!_metaOpen.Remove(file.Uid))
            _metaOpen.Add(file.Uid);
    }

    private Task Patch(OdsUploadFileExtraContext ctx, Action<OdsUploadFile> set)
    {
        set(ctx.File);
        return ctx.Changed.InvokeAsync();
    }

    private static bool RangeBad(OdsUploadFile f) =>
        f.ValidFrom is not null && f.ValidTo is not null && f.ValidTo < f.ValidFrom;

    private static string GuessKind(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".pdf" ? nameof(AccountFileType.Statement) : nameof(AccountFileType.Other);
    }

    private static AccountFileType TypeOf(OdsUploadFile f) =>
        Enum.TryParse<AccountFileType>(f.Kind, out var t) ? t : AccountFileType.Other;

    private async Task SubmitAsync()
    {
        if (_files.Count == 0 || _isUploading)
            return;

        if (_files.Any(RangeBad))
        {
            Snackbar.Add("A file’s “Valid to” can’t be before its “Valid from”.", Severity.Warning);
            return;
        }

        _isUploading = true;
        var uploaded = 0;
        try
        {
            foreach (var item in _files.ToList())
            {
                if (item.Source is null)
                    continue;
                try
                {
                    await UploadSingleAsync(item);
                    uploaded++;
                }
                catch (Exception ex)
                {
                    Snackbar.Add(ex.Message, Severity.Error);
                }
            }

            if (uploaded > 0)
            {
                Snackbar.Add($"{uploaded} file(s) added.", Severity.Success);
                await OnSaved.InvokeAsync();
                await OpenChanged.InvokeAsync(false);
            }
        }
        finally
        {
            _isUploading = false;
        }
    }

    private async Task UploadSingleAsync(OdsUploadFile item)
    {
        var result = await Files.UploadAsync(item.Source!.ToApiUpload(_uploadLimits.MaxUploadBytes));
        // Honour an in-dropzone rename (the picker lets the user edit the display name).
        var finalName = item.Name.Trim();
        if (!string.IsNullOrEmpty(finalName) && finalName != item.Source!.Name)
            await Files.UpdateMetadataAsync(result.Id, null, finalName);

        var issuedBy = Guid.TryParse(item.IssuedBy, out var id) ? id : (Guid?)null;
        await Files.AttachToAccountAsync(AccountId, result.Id, TypeOf(item),
            item.ValidFrom, item.ValidTo, item.IssuedAt, issuedBy);
    }
}
