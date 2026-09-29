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

namespace Odyssey.Client.Pages.Attachments;

/// <summary>Where a file handed to <see cref="AttachDocumentsDialog.Attach"/> came from.</summary>
public enum AttachDocumentSource
{
    Upload,
    Library,
}

/// <summary>
/// One file the dialog hands to its host — the DS <c>onSubmit</c> item. <see cref="IssuedBy"/> is
/// already resolved: an inline-created contact's temp id maps to the id the server issued, and a
/// create that failed maps to null, so a host never posts a bogus issuer.
/// </summary>
public sealed record AttachDocumentItem(
    AttachDocumentSource Source,
    Guid FileId,
    string Name,
    string Kind,
    long? SizeBytes,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    DateTime? IssuedAt,
    Guid? IssuedBy)
{
    /// <summary>The item's kind parsed into a surface's file-type enum, falling back to <paramref name="fallback"/>.</summary>
    public TEnum KindAs<TEnum>(TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Kind, out var value) ? value : fallback;
}

public partial class AttachDocumentsDialog
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

    private static readonly IReadOnlyList<string> DocumentExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".webp"];

    [Inject] private IFilesApiClient FilesApi { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IUploadLimitsCache UploadLimits { get; set; } = default!;
    [Inject] private IReferenceDataCache ReferenceData { get; set; } = default!;
    [Inject] private IContactQuickCreate ContactCreator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>What the documents are kept with — the one line of copy that differs per surface.</summary>
    [Parameter, EditorRequired] public string Subtitle { get; set; } = string.Empty;

    /// <summary>The surface's file-type vocabulary. Null for a surface that records no type (journal, tasks).</summary>
    [Parameter] public IReadOnlyList<OdsTypeOption>? Kinds { get; set; }

    /// <summary>Guesses a file's type from its name. Unset → <see cref="DefaultKind"/>.</summary>
    [Parameter] public Func<string, string>? GuessKind { get; set; }

    /// <summary>The type a file takes when nothing is guessed — every vocabulary's zero-risk member.</summary>
    [Parameter] public string DefaultKind { get; set; } = "Other";

    /// <summary>Offers the optional Valid from / to · Issued · Issued by fields per file.</summary>
    [Parameter] public bool Validity { get; set; }

    /// <summary>FileMetadata ids already linked to the record — shown disabled as "Already attached".</summary>
    [Parameter] public IReadOnlyCollection<Guid> AttachedIds { get; set; } = [];

    /// <summary>
    /// Reads the linked ids when the host does not hold them (an account row, whose files load with its
    /// expanded section). Unioned with <see cref="AttachedIds"/>; a failed read leaves the rows enabled
    /// and the server's own duplicate check answers.
    /// </summary>
    [Parameter] public Func<Task<IReadOnlyCollection<Guid>>>? LoadAttachedIds { get; set; }

    /// <summary>
    /// Applies the server's document allow-list (<see cref="DocumentContentTypes"/>) to both tabs — the
    /// contract and property attach endpoints refuse anything else with a 400.
    /// </summary>
    [Parameter] public bool RestrictToDocumentTypes { get; set; }

    /// <summary>The extensions the upload tab accepts. Ignored under <see cref="RestrictToDocumentTypes"/>.</summary>
    [Parameter] public IReadOnlyList<string>? UploadExtensions { get; set; }

    /// <summary>
    /// A surface's own, tighter per-file cap in megabytes. The effective cap is the smaller of it and
    /// the instance-wide one — a surface may tighten, never loosen.
    /// </summary>
    [Parameter] public int? SurfaceMaxMegabytes { get; set; }

    /// <summary>Links one file to the record; returns whether it succeeded. The host reports its own failure.</summary>
    [Parameter, EditorRequired] public Func<AttachDocumentItem, Task<bool>> Attach { get; set; } = default!;

    /// <summary>Raised with the items that linked, after at least one did, so the host re-reads its documents.</summary>
    [Parameter] public EventCallback<IReadOnlyList<AttachDocumentItem>> OnAttached { get; set; }

    /// <summary>Announces the attach in a snackbar. Off for a host that only stages the ids until its own save.</summary>
    [Parameter] public bool Announce { get; set; } = true;

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

    private HashSet<Guid> _loadedAttachedIds = [];

    // Upload rows already stored, by row Uid. A retry after a partial failure links these again rather
    // than storing a second copy of the bytes.
    private readonly Dictionary<string, Guid> _stored = [];

    private string _announce = string.Empty;

    private bool _canUpload;
    private bool _canBrowse;

    private IReadOnlyList<OdsFileKind>? _fileKinds;
    private IReadOnlyList<OdsOption>? _kindOptions;

    private IReadOnlyList<OdsOption> _issuerOptions = [];
    private bool _canReadContacts;
    private bool _canCreateContact;

    // Seeded with the shipped fallback so a render that beats the fetch still validates sanely.
    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback;

    private bool ShowTabs => _canUpload && _canBrowse;

    private string Title => _canUpload ? "Attach documents" : "Choose from Files";

    private IReadOnlyList<string> Extensions =>
        RestrictToDocumentTypes ? DocumentExtensions : UploadExtensions ?? DocumentExtensions;

    private string AcceptAttribute => string.Join(',', Extensions);

    private string UploadHint =>
        $"{(RestrictToDocumentTypes ? DocumentContentTypes.Label : ExtensionLabel)} · up to {_uploadLimits.MaxUploadMegabytes} MB each · multiple at once";

    private string ExtensionLabel =>
        string.Join(", ", Extensions.Where(e => e != ".jpeg").Select(e => e.TrimStart('.').ToUpperInvariant()));

    private string EmptyLibraryText => string.IsNullOrWhiteSpace(_query)
        ? (_canUpload ? "Files is empty — upload a document instead." : "Files is empty.")
        : $"No files match “{_query}”.";

    private string SubmitText => _tab == UploadTab
        ? (_uploads.Count > 1 ? $"Upload and attach {_uploads.Count}" : "Upload and attach")
        : (_picked.Count > 1 ? $"Attach {_picked.Count} documents" : "Attach document");

    private IReadOnlyList<FileListItem> LibraryRows => string.IsNullOrWhiteSpace(_query)
        ? _library
        : [.. _library.Where(f => f.FileName.Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase))];

    protected override void OnParametersSet()
    {
        _fileKinds = Kinds is null
            ? null
            : [.. Kinds.Select(t => new OdsFileKind { Key = t.Key, Label = t.Label, Icon = t.Icon, Color = t.Color, Soft = t.Soft })];
        _kindOptions = Kinds is null ? null : OdsTypeRegistries.ToOptions(Kinds);
    }

    protected override async Task OnInitializedAsync()
    {
        var user = await AuthenticationState.GetUserAsync();
        _canUpload = user.HasPermission(PermissionClaims.FilesCreate);
        _canBrowse = user.HasPermission(PermissionClaims.FilesRead);
        if (!_canUpload)
            _tab = LibraryTab;

        _uploadLimits = await UploadLimits.GetAsync();
        if (SurfaceMaxMegabytes is { } surface)
            _uploadLimits = _uploadLimits.TightenTo(surface);

        if (Validity)
        {
            _canReadContacts = user.HasPermission(PermissionClaims.ContactsRead);
            _canCreateContact = user.HasPermission(PermissionClaims.ContactsCreate);
            ContactCreator.OnCreateFailed = OnContactCreateFailed;
            if (_canReadContacts)
                _issuerOptions = OdsContactOptions.Active(await ReferenceData.ContactsAsync());
        }

        if (LoadAttachedIds is not null)
        {
            try
            {
                _loadedAttachedIds = [.. await LoadAttachedIds()];
            }
            catch (Exception)
            {
                // A failed read leaves the rows enabled; the server's own duplicate check answers.
            }
        }

        if (_tab == LibraryTab)
            await LoadLibraryAsync();
    }

    private string GuessKindOrDefault(string fileName) => GuessKind?.Invoke(fileName) ?? DefaultKind;

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
        if (!_canBrowse)
            return;

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
        AttachedIds.Contains(file.Id) || _loadedAttachedIds.Contains(file.Id) ? LibraryState.AlreadyAttached
        : RestrictToDocumentTypes && !DocumentContentTypes.IsAllowed(file.ContentType) ? LibraryState.TypeNotAllowed
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
                Kind = GuessKindOrDefault(file.FileName),
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
            if (f.Source is not null && !Extensions.Contains(ext))
            {
                Snackbar.Add(RestrictToDocumentTypes
                    ? $"“{f.Name}” can’t be attached. Only {DocumentContentTypes.Label} files are accepted."
                    : $"“{f.Name}” can’t be attached. Allowed: {ExtensionLabel}.", Severity.Warning);
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

    private AttachDocumentItem ItemFor(AttachDocumentSource source, Guid fileId, OdsUploadFile f) => new(
        source,
        fileId,
        f.Name.Trim(),
        string.IsNullOrEmpty(f.Kind) ? DefaultKind : f.Kind,
        f.SizeBytes,
        Validity ? f.ValidFrom : null,
        Validity ? f.ValidTo : null,
        Validity ? f.IssuedAt : null,
        Validity && Guid.TryParse(ContactCreator.Resolve(f.IssuedBy), out var id) ? id : null);

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
        if (batch.Any(f => string.IsNullOrWhiteSpace(f.Name)))
        {
            _error = "Every document needs a name.";
            return;
        }
        if (Validity && batch.Any(RangeBad))
        {
            _error = "A document’s “Valid to” can’t be before its “Valid from”.";
            return;
        }

        if (Validity)
            await ContactCreator.WhenSettledAsync();

        _busy = true;
        _error = null;
        var attached = new List<AttachDocumentItem>();
        var failedUploads = new List<OdsUploadFile>();
        try
        {
            if (_tab == UploadTab)
            {
                foreach (var file in _uploads.Where(f => f.Source is not null))
                {
                    var fileId = _stored.TryGetValue(file.Uid, out var stored) ? stored : await StoreAsync(file);
                    if (fileId is not { } id)
                    {
                        failedUploads.Add(file);
                        continue;
                    }
                    _stored[file.Uid] = id;

                    var item = ItemFor(AttachDocumentSource.Upload, id, file);
                    if (await Attach(item))
                        attached.Add(item);
                    else
                        failedUploads.Add(file);
                }
            }
            else
            {
                foreach (var (fileId, file) in _picked.ToList())
                {
                    var item = ItemFor(AttachDocumentSource.Library, fileId, file);
                    if (await Attach(item))
                    {
                        attached.Add(item);
                        _picked.Remove(fileId);
                    }
                }
            }

            if (attached.Count > 0)
            {
                if (Announce)
                    Snackbar.Add(attached.Count == 1 ? "Document attached." : $"{attached.Count} documents attached.", Severity.Success);
                await OnAttached.InvokeAsync(attached);
            }

            var failed = _tab == UploadTab ? failedUploads.Count : _picked.Count;
            if (failed == 0)
            {
                await OpenChanged.InvokeAsync(false);
                return;
            }

            // Partly failed: what linked is reported to the host; only the failures stay, for a retry
            // that neither re-stores nor re-links what already worked.
            if (_tab == UploadTab)
                _uploads = failedUploads;
            _error = failed == 1
                ? "One document wasn’t attached. Try again, or remove it."
                : $"{failed} documents weren’t attached. Try again, or remove them.";
            _announce = _error;
        }
        finally
        {
            _busy = false;
        }
    }

    // Stores one upload and applies an in-dropzone rename. Null when the upload failed; a failed rename
    // still returns the id (the file is stored under its original name) and says so.
    private async Task<Guid?> StoreAsync(OdsUploadFile file)
    {
        Guid id;
        try
        {
            id = (await FilesApi.UploadAsync(file.Source!.ToApiUpload(_uploadLimits.MaxUploadBytes))).Id;
        }
        catch (Exception)
        {
            Snackbar.Add($"Couldn’t upload “{file.Name}”.", Severity.Error);
            return null;
        }

        var finalName = file.Name.Trim();
        if (!string.IsNullOrEmpty(finalName) && finalName != file.Source!.Name)
        {
            var renamed = await FilesApi.UpdateMetadataAsync(id, null, finalName);
            if (renamed is null)
            {
                Snackbar.Add($"“{file.Source.Name}” was uploaded but couldn’t be renamed to “{finalName}”.", Severity.Warning);
                file.Name = file.Source.Name;
            }
        }

        return id;
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);
}
