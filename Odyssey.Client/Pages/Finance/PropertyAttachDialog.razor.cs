using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class PropertyAttachDialog
{
    internal const string UploadTab = "upload";
    internal const string LibraryTab = "library";

    /// <summary>A library row's attachability — disabled rows say why in text, never colour alone.</summary>
    internal enum LibraryState
    {
        Available,
        AlreadyAttached,
        TypeNotAllowed,
    }

    private static readonly IReadOnlyList<OdsSegmentedOption> Tabs =
    [
        new() { Value = UploadTab, Label = "Upload new", Icon = "upload_file" },
        new() { Value = LibraryTab, Label = "From Files", Icon = "folder" },
    ];

    // The PropertyFileType vocabulary, projected to the OdsFileUpload kind shape (per-file picker).
    private static readonly IReadOnlyList<OdsFileKind> Kinds =
        [.. OdsTypeRegistries.PropertyFileTypes.Select(t => new OdsFileKind
        {
            Key = t.Key, Label = t.Label, Icon = t.Icon, Color = t.Color, Soft = t.Soft,
        })];

    private static readonly string[] AllowedExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".webp"];

    [Inject] private IFilesApiClient FilesApi { get; set; } = default!;
    [Inject] private IPropertiesApiClient Properties { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IUploadLimitsCache UploadLimits { get; set; } = default!;
    [Inject] private IReferenceDataCache ReferenceData { get; set; } = default!;
    [Inject] private IContactQuickCreate ContactCreator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;

    [Parameter, EditorRequired] public ExistingProperty Property { get; set; } = default!;

    /// <summary>The property's current documents — a library file among them reads "Already attached".</summary>
    [Parameter] public IReadOnlyList<ExistingPropertyFile> Attached { get; set; } = [];

    /// <summary>
    /// Offers the "Upload new" tab (<c>files.create</c>). Without it the dialog opens straight onto
    /// "From Files" — the attach itself needs <c>files.read</c>, which that tab reads under.
    /// </summary>
    [Parameter] public bool CanUpload { get; set; }

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after at least one attach succeeded so the host re-reads its documents.</summary>
    [Parameter] public EventCallback OnAttached { get; set; }

    private string _tab = UploadTab;
    private List<OdsUploadFile> _uploads = [];
    private string? _error;
    private bool _busy;

    // Picked library files, keyed by FileMetadata id, in the shape the validity editor edits.
    private readonly Dictionary<Guid, OdsUploadFile> _picked = [];
    private readonly HashSet<string> _metaOpen = [];

    private List<FileListItem> _library = [];
    private bool _libraryLoaded;
    private bool _libraryLoading;
    private bool _libraryFailed;
    private string _query = string.Empty;

    private IReadOnlyList<OdsOption> _issuerOptions = [];
    private bool _canReadContacts;
    private bool _canCreateContact;

    // Seeded with the shipped fallback so a render that beats the fetch still validates sanely.
    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback;

    private string Noun => Property.Type == PropertyType.Vehicle
        ? "the registration, an inspection, the insurance certificate or a warranty"
        : "the deed, the purchase agreement, a valuation or a warranty";

    private string SubmitText => _tab == UploadTab
        ? (_uploads.Count > 1 ? $"Upload and attach {_uploads.Count}" : "Upload and attach")
        : (_picked.Count > 1 ? $"Attach {_picked.Count} documents" : "Attach document");

    private IReadOnlyList<FileListItem> LibraryRows => string.IsNullOrWhiteSpace(_query)
        ? _library
        : [.. _library.Where(f => f.FileName.Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase))];

    protected override async Task OnInitializedAsync()
    {
        if (!CanUpload)
            _tab = LibraryTab;

        if (!OperatingSystem.IsBrowser())
            return;

        _uploadLimits = await UploadLimits.GetAsync();

        var user = await AuthenticationState.GetUserAsync();
        _canReadContacts = user.HasPermission(PermissionClaims.ContactsRead);
        _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
        ContactCreator.OnCreateFailed = OnContactCreateFailed;
        if (_canReadContacts)
            _issuerOptions = OdsContactOptions.Active(await ReferenceData.ContactsAsync());

        if (_tab == LibraryTab)
            await LoadLibraryAsync();
    }

    private async Task OnTabChanged(string? tab)
    {
        _tab = tab == LibraryTab ? LibraryTab : UploadTab;
        _error = null;
        if (_tab == LibraryTab && !_libraryLoaded)
            await LoadLibraryAsync();
    }

    /// <summary>Reads the Files store once. Public as a render-test seam, like the sections' LoadAsync.</summary>
    public async Task LoadLibraryAsync()
    {
        _libraryLoading = true;
        _libraryFailed = false;
        var result = await FilesApi.ListAllAsync();
        _libraryFailed = !result.IsSuccess;
        _library = [.. result.ValueOr([]).OrderByDescending(f => f.UploadedAtUtc)];
        _libraryLoaded = result.IsSuccess;
        _libraryLoading = false;
        StateHasChanged();
    }

    internal LibraryState StateOf(FileListItem file) =>
        Attached.Any(a => a.FileMetadata.Id == file.Id) ? LibraryState.AlreadyAttached
        : !DocumentContentTypes.IsAllowed(file.ContentType) ? LibraryState.TypeNotAllowed
        : LibraryState.Available;

    private static string? ReasonOf(LibraryState state, FileListItem file) => state switch
    {
        LibraryState.AlreadyAttached => "Already attached",
        LibraryState.TypeNotAllowed => $"{ContentTypeShort(file.ContentType)} not accepted",
        _ => null,
    };

    private static string CheckIcon(LibraryState state, bool picked) => state switch
    {
        LibraryState.AlreadyAttached => "link",
        LibraryState.TypeNotAllowed => "block",
        _ => picked ? "check_box" : "check_box_outline_blank",
    };

    internal static string ContentTypeIcon(string? contentType) =>
        contentType == "application/pdf" ? "picture_as_pdf"
        : contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? "image"
        : contentType == "text/html" ? "code"
        : "description";

    internal static string ContentTypeShort(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "application/pdf" => "PDF",
        "image/png" => "PNG",
        "image/jpeg" => "JPEG",
        "image/webp" => "WebP",
        "text/html" => "HTML",
        null or "" => "Unknown type",
        var other => other.Split('/')[^1].Split('.')[^1].ToUpperInvariant(),
    };

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);

    private void TogglePick(FileListItem file)
    {
        if (StateOf(file) != LibraryState.Available)
            return;

        if (!_picked.Remove(file.Id))
        {
            _picked[file.Id] = new OdsUploadFile
            {
                Uid = file.Id.ToString(),
                Name = file.FileName,
                Kind = PropertyFileTypeGuess.GuessKey(file.FileName),
                SizeBytes = file.SizeBytes,
            };
        }

        _error = null;
    }

    // Controlled list — filter out anything outside the allow-list or over the cap before it lands.
    private void OnUploadsChanged(IReadOnlyList<OdsUploadFile> files)
    {
        var kept = new List<OdsUploadFile>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (f.Source is not null && !AllowedExtensions.Contains(ext))
            {
                Snackbar.Add($"“{f.Name}” can’t be attached. Property documents accept {DocumentContentTypes.Label} only.", Severity.Warning);
                continue;
            }
            if (f.SizeBytes > _uploadLimits.MaxUploadBytes)
            {
                Snackbar.Add($"“{f.Name}” exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.", Severity.Warning);
                continue;
            }
            kept.Add(f);
        }

        _uploads = kept;
        if (_error is not null && _uploads.Count > 0)
            _error = null;
    }

    private void ToggleMeta(OdsUploadFile file)
    {
        if (!_metaOpen.Remove(file.Uid))
            _metaOpen.Add(file.Uid);
    }

    private static Task Patch(OdsUploadFileExtraContext ctx, Action<OdsUploadFile> set)
    {
        set(ctx.File);
        return ctx.Changed.InvokeAsync();
    }

    private static bool RangeBad(OdsUploadFile f) =>
        f.ValidFrom is not null && f.ValidTo is not null && f.ValidTo < f.ValidFrom;

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
        foreach (var f in _uploads.Concat(_picked.Values).Where(f => f.IssuedBy == tempId))
            f.IssuedBy = null;
        StateHasChanged();
    }

    private static PropertyFileType TypeOf(OdsUploadFile f) =>
        Enum.TryParse<PropertyFileType>(f.Kind, out var t) ? t : PropertyFileType.Other;

    private AttachPropertyFileRequest RequestFor(Guid fileId, OdsUploadFile f) => new()
    {
        FileMetadataId = fileId,
        FileType = TypeOf(f),
        ValidFrom = f.ValidFrom,
        ValidTo = f.ValidTo,
        IssuedAt = f.IssuedAt,
        // A temp id from an inline create maps to the id the server issued; a failed create maps to
        // null, so a bogus issuer is never posted.
        IssuedBy = Guid.TryParse(ContactCreator.Resolve(f.IssuedBy), out var id) ? id : null,
    };

    private async Task SubmitAsync()
    {
        if (_busy)
            return;

        var batch = _tab == UploadTab ? _uploads : [.. _picked.Values];
        if (batch.Count == 0)
        {
            _error = _tab == UploadTab ? "Add at least one document to upload." : "Pick at least one file to attach.";
            return;
        }
        if (batch.Any(RangeBad))
        {
            _error = "A document’s “Valid to” can’t be before its “Valid from”.";
            return;
        }

        await ContactCreator.WhenSettledAsync();

        _busy = true;
        var attached = 0;
        try
        {
            if (_tab == UploadTab)
            {
                foreach (var file in _uploads.Where(f => f.Source is not null))
                {
                    try
                    {
                        var uploaded = await FilesApi.UploadAsync(file.Source!.ToApiUpload(_uploadLimits.MaxUploadBytes));
                        var finalName = file.Name.Trim();
                        if (!string.IsNullOrEmpty(finalName) && finalName != file.Source!.Name)
                            await FilesApi.UpdateMetadataAsync(uploaded.Id, null, finalName);

                        if ((await Properties.AttachFileAsync(Property.PropertyId, RequestFor(uploaded.Id, file)))
                            .Toast(Snackbar, $"Couldn’t attach “{file.Name}”"))
                        {
                            attached++;
                        }
                    }
                    catch (Exception)
                    {
                        Snackbar.Add($"Couldn’t upload “{file.Name}”.", Severity.Error);
                    }
                }
            }
            else
            {
                foreach (var (fileId, file) in _picked)
                {
                    if ((await Properties.AttachFileAsync(Property.PropertyId, RequestFor(fileId, file)))
                        .Toast(Snackbar, $"Couldn’t attach “{file.Name}”"))
                    {
                        attached++;
                    }
                }
            }

            if (attached > 0)
            {
                Snackbar.Add(attached == 1 ? "Document attached." : $"{attached} documents attached.", Severity.Success);
                await OnAttached.InvokeAsync();
                await OpenChanged.InvokeAsync(false);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);
}
