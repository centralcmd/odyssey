using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
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

    /// <summary>
    /// Which folder on the file store an upload goes to (issue #449): <c>Journal</c> from journal entries and tasks,
    /// <c>Finance</c> from every finance surface. Chosen by the host, never by the user, and placement only.
    /// </summary>
    [Parameter, EditorRequired] public FileUploadArea Area { get; set; }

    private string _tab = UploadTab;
    private List<OdsUploadFile> _uploads = [];
    private string? _error;
    private bool _busy;

    // The dialog's one announcement for a failure or warning while it is open (issue #449 §6): a role="alert" line,
    // re-keyed per batch so an identical second message is announced again. No failure or warning toast is raised
    // while the dialog is open — a toast sits outside the modal, is read twice and has a close control a keyboard user
    // cannot reach.
    private string? _notice;
    private int _noticeKey;
    private readonly string _noticeId = $"adm-notice-{Guid.NewGuid():N}";

    private void Notice(string? text)
    {
        _notice = text;
        if (text is not null)
            _noticeKey++;
    }

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

    // The name each stored row carries on the server, so a rename edited between tries is applied on
    // the retry rather than lost with the first store.
    private readonly Dictionary<string, string> _storedNames = [];

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
        // EditorRequired is a compile error under TreatWarningsAsErrors; this catches a value cast from 0.
        if (!Enum.IsDefined(Area))
            throw new InvalidOperationException("AttachDocumentsDialog needs an Area: Finance or Journal.");

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
        Notice(null);
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
        var rejected = new List<string>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (f.Source is not null && !Extensions.Contains(ext))
            {
                rejected.Add(RestrictToDocumentTypes
                    ? $"“{UploadFailureText.Clean(f.Name)}” can’t be attached. Only {DocumentContentTypes.Label} files are accepted."
                    : $"“{UploadFailureText.Clean(f.Name)}” can’t be attached. Allowed: {ExtensionLabel}.");
                continue;
            }
            if (f.SizeBytes > _uploadLimits.MaxUploadBytes)
            {
                rejected.Add($"“{UploadFailureText.Clean(f.Name)}” exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.");
                continue;
            }
            kept.Add(f);
        }

        _uploads = kept;
        if (_error is not null && _uploads.Count > 0)
            _error = null;

        // Every rejection of one pick in one alert (WCAG 3.3.1); a pick or removal with none clears a stale one.
        Notice(rejected.Count > 0 ? UploadFailureText.Several(rejected, itemised: true, "can’t be added") : null);
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
        Notice(null);
        var attached = new List<AttachDocumentItem>();
        var failedUploads = new List<OdsUploadFile>();
        var perFile = new List<string>();
        var warnings = new List<string>();
        string? stoppedText = null;
        try
        {
            if (_tab == UploadTab)
            {
                var queue = _uploads.Where(f => f.Source is not null).ToList();
                for (var i = 0; i < queue.Count; i++)
                {
                    var file = queue[i];
                    Guid id;
                    if (_stored.TryGetValue(file.Uid, out var stored))
                    {
                        id = stored;
                    }
                    else
                    {
                        var upload = await StoreAsync(file);
                        if (!upload.IsSuccess)
                        {
                            // 507 / 503 / 429 stop the batch (issue #449 §3.2): the cause applies to every later file, so
                            // the failed file and every untried one stay in the list for a retry. Anything else is this
                            // file's own failure and the batch goes on.
                            var stop = UploadFailureText.StopOf(upload.Status);
                            if (stop != UploadStop.None)
                            {
                                failedUploads.AddRange(queue.Skip(i));
                                stoppedText = UploadFailureText.Stopped(stop, queue.Count - i, upload.RetryAfter);
                                break;
                            }

                            perFile.Add(UploadFailureText.PerFile(upload.Status, upload.Problem?.Detail, file.Name, _uploadLimits.MaxUploadMegabytes));
                            failedUploads.Add(file);
                            continue;
                        }

                        id = upload.Value!.Id;
                        _storedNames[file.Uid] = file.Source!.Name;
                    }

                    _stored[file.Uid] = id;
                    if (await ApplyNameAsync(id, file) is { } warning)
                        warnings.Add(warning);

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

            // One alert for the whole batch, so nothing in it overwrites another part: why it stopped, the files'
            // own failures, then any rename warnings.
            var parts = new List<string>();
            if (stoppedText is not null)
                parts.Add(stoppedText);
            if (perFile.Count > 0)
                parts.Add(UploadFailureText.Several(perFile, itemised: true));
            parts.AddRange(warnings);
            Notice(parts.Count > 0 ? string.Join(' ', parts) : null);

            if (attached.Count > 0)
            {
                if (Announce)
                    Snackbar.Add(attached.Count == 1 ? "Document attached." : $"{attached.Count} documents attached.", Severity.Success);
                await OnAttached.InvokeAsync(attached);
            }

            var failed = _tab == UploadTab ? failedUploads.Count : _picked.Count;
            if (failed == 0 && _notice is null)
            {
                await OpenChanged.InvokeAsync(false);
                return;
            }

            // Everything attached but a rename failed: the warning belongs on the alert line (issue #449 §6), so the
            // dialog stays open to show it, with nothing left to retry. Cancel closes it.
            if (failed == 0)
            {
                if (_tab == UploadTab)
                    _uploads = failedUploads;
                return;
            }

            // Partly failed: what linked is reported to the host; only the failures stay, for a retry
            // that neither re-stores nor re-links what already worked.
            if (_tab == UploadTab)
                _uploads = failedUploads;
            // An upload failure is already on the alert line; only an attach failure needs the generic one.
            if (_notice is null)
            {
                // The error line is role="alert", which announces it; a second copy in the live region would
                // read it twice.
                _error = failed == 1
                    ? "One document wasn’t attached. Try again, or remove it."
                    : $"{failed} documents weren’t attached. Try again, or remove them.";
            }
        }
        finally
        {
            _busy = false;
        }
    }

    // Stores one upload under its picked file name, in the host's area. The caller words a failure.
    private Task<ApiResult<FileUploadResponse>> StoreAsync(OdsUploadFile file) =>
        FilesApi.UploadAsync(file.Source!.ToApiUpload(_uploadLimits.MaxUploadBytes), Area);

    // Applies an in-dropzone rename when the row's name differs from what the server holds — on the first
    // try and again on a retry after the reader edited it. A failed rename keeps the stored name, says so,
    // and does not block the attach.
    private async Task<string?> ApplyNameAsync(Guid id, OdsUploadFile file)
    {
        var wanted = file.Name.Trim();
        var current = _storedNames.GetValueOrDefault(file.Uid, file.Source!.Name);
        if (string.IsNullOrEmpty(wanted) || wanted == current)
            return null;

        var renamed = await FilesApi.UpdateMetadataAsync(id, null, wanted);
        if (!renamed.IsSuccess)
        {
            file.Name = current;
            return $"“{UploadFailureText.Clean(current)}” was uploaded but couldn’t be renamed to “{UploadFailureText.Clean(wanted)}”: {UploadFailureText.Clean(renamed.Error)}";
        }
        _storedNames[file.Uid] = wanted;
        return null;
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);
}
