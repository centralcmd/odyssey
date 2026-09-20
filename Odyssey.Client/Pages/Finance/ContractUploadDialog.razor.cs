using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class ContractUploadDialog
{
    /// <summary>
    /// This surface's own, tighter product limit. A named constant rather than a bare literal, and
    /// deliberately kept: it is a product decision for this surface, not drift to be flattened. The
    /// effective cap is the smaller of it and the instance-wide cap — a surface may tighten, never
    /// loosen, or an administrator lowering the global cap would not reach this dialog.
    /// </summary>
    private const int SurfaceMaxMegabytes = 25;

    // Admin-editable instance cap (issue #421 Wave 4), narrowed to this surface. Seeded with the
    // shipped fallback so a render that beats the fetch still validates against a sane number.
    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback.TightenTo(SurfaceMaxMegabytes);

    protected override async Task OnInitializedAsync()
    {
        _uploadLimits = (await UploadLimits.GetAsync()).TightenTo(SurfaceMaxMegabytes);
        if (!OperatingSystem.IsBrowser())
            return;

        var contacts = await ReferenceData.ContactsAsync();
        _issuerOptions = OdsContactOptions.Active(contacts);

        var user = await AuthenticationState.GetUserAsync();
        _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
        ContactCreator.OnCreateFailed = OnContactCreateFailed;
    }

    [Parameter, EditorRequired] public ExistingContract Contract { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful attach so the host re-fetches the contract.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private static readonly string[] AllowedExtensions = [".pdf", ".jpg", ".jpeg", ".png", ".webp"];

    // The ContractFileType vocabulary, projected to the OdsFileUpload kind shape (per-file picker).
    private static readonly IReadOnlyList<OdsFileKind> _kinds =
        [.. OdsTypeRegistries.ContractFileTypes.Select(t => new OdsFileKind
        {
            Key = t.Key, Label = t.Label, Icon = t.Icon, Color = t.Color, Soft = t.Soft,
        })];

    private List<OdsUploadFile> _files = [];
    private string? _error;
    private bool _isUploading;

    // Files whose per-row validity editor is expanded (keyed by Uid).
    private readonly HashSet<string> _metaOpen = [];

    private IReadOnlyList<OdsOption> _issuerOptions = [];
    private bool _canCreateContact;

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

    // The picker hands its option back synchronously; the shared creator POSTs behind it and the temp
    // id is mapped when the upload is submitted.
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
        foreach (var f in _files.Where(f => f.IssuedBy == tempId))
            f.IssuedBy = null;
        StateHasChanged();
    }

    private List<ExistingContractFile> _existing => [.. Contract.Files];


    // Controlled list — filter out anything outside the allow-list before it lands in the picker.
    private void OnFilesChanged(IReadOnlyList<OdsUploadFile> files)
    {
        var kept = new List<OdsUploadFile>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (f.Source is not null && !AllowedExtensions.Contains(ext))
            {
                Snackbar.Add($"{f.Name}: unsupported type. Allowed: .pdf, .jpg, .jpeg, .png, .webp", Severity.Warning);
                continue;
            }
            if (f.SizeBytes > _uploadLimits.MaxUploadBytes)
            {
                Snackbar.Add($"{f.Name}: exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.", Severity.Warning);
                continue;
            }
            kept.Add(f);
        }
        _files = kept;
        if (_error is not null && _files.Count > 0) _error = null;
    }

    private static ContractFileType TypeOf(OdsUploadFile f) =>
        Enum.TryParse<ContractFileType>(f.Kind, out var t) ? t : ContractFileType.Other;

    private async Task SubmitAsync()
    {
        if (_isUploading) return;
        if (_files.Count == 0) { _error = "Add at least one document to upload."; return; }
        if (_files.Any(f => string.IsNullOrWhiteSpace(f.Name))) { _error = "Every document needs a name."; return; }
        if (_files.Any(RangeBad)) { _error = "A document’s “Valid to” can’t be before its “Valid from”."; return; }

        // Let any in-flight inline contact creates land so the real issuer ids are posted.
        await ContactCreator.WhenSettledAsync();

        _isUploading = true;
        var attached = 0;
        try
        {
            foreach (var file in _files)
            {
                if (file.Source is null) continue;
                try
                {
                    var uploaded = await FilesApi.UploadAsync(file.Source.ToApiUpload(_uploadLimits.MaxUploadBytes));
                    // Honour an in-dropzone rename (the picker lets the user edit the display name).
                    var finalName = file.Name.Trim();
                    if (!string.IsNullOrEmpty(finalName) && finalName != file.Source.Name)
                        await FilesApi.UpdateMetadataAsync(uploaded.Id, null, finalName);

                    // A temp id from an inline create maps to the id the server issued; a create that
                    // failed maps to null, so a bogus issuer is never posted.
                    var issuedBy = Guid.TryParse(ContactCreator.Resolve(file.IssuedBy), out var id) ? id : (Guid?)null;
                    var request = new AttachContractFileRequest
                    {
                        FileMetadataId = uploaded.Id,
                        FileType = TypeOf(file),
                        ValidFrom = file.ValidFrom,
                        ValidTo = file.ValidTo,
                        IssuedAt = file.IssuedAt,
                        IssuedBy = issuedBy,
                    };
                    var attach = await Contracts.AttachFileAsync(Contract.ContractId, request);
                    if (!attach.IsSuccess)
                        throw new InvalidOperationException(attach.Error);
                    attached++;
                }
                catch (Exception)
                {
                    Snackbar.Add($"Couldn't attach \"{file.Name}\".", Severity.Error);
                }
            }

            if (attached > 0)
            {
                Snackbar.Add($"{attached} document(s) attached.", Severity.Success);
                await OnSaved.InvokeAsync();
                await OpenChanged.InvokeAsync(false);
            }
        }
        finally
        {
            _isUploading = false;
        }
    }

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);
}
