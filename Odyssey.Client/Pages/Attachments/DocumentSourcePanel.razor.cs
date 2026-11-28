using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;

namespace Odyssey.Client.Pages.Attachments;

/// <summary>A file the From Files tab may offer: the five fields every listing serves.</summary>
public sealed record DocumentSourceFile(Guid Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAtUtc);

/// <summary>Why a From Files row can or cannot be picked.</summary>
public enum DocumentSourceState
{
    Available,
    AlreadyAttached,
    TypeNotAllowed,
    AtCap,
}

public partial class DocumentSourcePanel
{
    public const string UploadTab = "upload";
    public const string LibraryTab = "library";

    private string _query = string.Empty;

    [Parameter] public string Tab { get; set; } = UploadTab;

    [Parameter] public EventCallback<string> TabChanged { get; set; }

    /// <summary>Upload new is offered (the caller holds <c>files.create</c>).</summary>
    [Parameter] public bool ShowUpload { get; set; } = true;

    /// <summary>From Files is offered (the caller holds <c>files.read</c>).</summary>
    [Parameter] public bool ShowLibrary { get; set; } = true;

    /// <summary>Each tab's label carries its file count.</summary>
    [Parameter] public bool Counts { get; set; }

    [Parameter] public string UploadLabel { get; set; } = "Upload new";

    [Parameter] public string LibraryLabel { get; set; } = "From Files";

    [Parameter] public IReadOnlyList<OdsUploadFile> Uploads { get; set; } = [];

    [Parameter] public EventCallback<IReadOnlyList<OdsUploadFile>> UploadsChanged { get; set; }

    [Parameter] public string? Accept { get; set; }

    [Parameter] public IReadOnlyList<OdsFileKind>? Kinds { get; set; }

    [Parameter] public Func<string, string>? GuessKind { get; set; }

    [Parameter] public string DefaultKind { get; set; } = "Other";

    [Parameter] public string? UploadHint { get; set; }

    [Parameter] public RenderFragment<OdsUploadFileExtraContext>? RenderFileExtra { get; set; }

    [Parameter] public string? Error { get; set; }

    /// <summary>The rows From Files may offer, already filtered by whoever loaded them.</summary>
    [Parameter] public IReadOnlyList<DocumentSourceFile> Pool { get; set; } = [];

    [Parameter] public bool LibraryLoading { get; set; }

    [Parameter] public bool LibraryFailed { get; set; }

    [Parameter] public EventCallback OnRetry { get; set; }

    [Parameter] public IReadOnlySet<Guid> PickedIds { get; set; } = new HashSet<Guid>();

    [Parameter] public EventCallback<DocumentSourceFile> OnToggle { get; set; }

    /// <summary>The host's rule for a row (already attached, a type off its allow-list); null means available.</summary>
    [Parameter] public Func<DocumentSourceFile, DocumentSourceState>? StateOf { get; set; }

    /// <summary>How many files may still be picked; at the cap every other row reads "At most n files".</summary>
    [Parameter] public int? Cap { get; set; }

    /// <summary>The reason a row past the cap reads, when the cap counts more than this tab ("At most 10 files").</summary>
    [Parameter] public string? CapReason { get; set; }

    /// <summary>A content type in words for a row's meta line and its reason; the short type by default.</summary>
    [Parameter] public Func<string?, string>? TypeLabel { get; set; }

    /// <summary>What a picked row shows under it (a document type, the validity fields).</summary>
    [Parameter] public RenderFragment<DocumentSourceFile>? PickedExtra { get; set; }

    [Parameter] public RenderFragment? Footnote { get; set; }

    /// <summary>The empty-list sentence when nothing is searched; "No files match …" otherwise.</summary>
    [Parameter] public string EmptyLibraryText { get; set; } = "Files is empty.";

    [Parameter] public string? DescribedBy { get; set; }

    private string ActiveTab => !ShowUpload ? LibraryTab : !ShowLibrary ? UploadTab : Tab;

    private IReadOnlyList<OdsSegmentedOption> TabOptions =>
    [
        new() { Value = UploadTab, Label = Counts && Uploads.Count > 0 ? $"{UploadLabel} ({Uploads.Count})" : UploadLabel, Icon = "upload_file" },
        new() { Value = LibraryTab, Label = Counts && PickedIds.Count > 0 ? $"{LibraryLabel} ({PickedIds.Count})" : LibraryLabel, Icon = "folder" },
    ];

    private IReadOnlyList<DocumentSourceFile> Rows => string.IsNullOrWhiteSpace(_query)
        ? Pool
        : [.. Pool.Where(f => f.FileName.Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase))];

    private string EmptyText => string.IsNullOrWhiteSpace(_query) ? EmptyLibraryText : $"No files match “{_query}”.";

    private Task OnTabChanged(string? tab) => TabChanged.InvokeAsync(tab == LibraryTab ? LibraryTab : UploadTab);

    private Task OnUploadsChanged(IReadOnlyList<OdsUploadFile> files) => UploadsChanged.InvokeAsync(files);

    internal DocumentSourceState StateFor(DocumentSourceFile file)
    {
        var state = StateOf?.Invoke(file) ?? DocumentSourceState.Available;
        return state == DocumentSourceState.Available && Cap is { } cap && !PickedIds.Contains(file.Id) && PickedIds.Count >= cap
            ? DocumentSourceState.AtCap
            : state;
    }

    private string TypeLabelOf(string? contentType) => TypeLabel?.Invoke(contentType) ?? ContentTypeShort(contentType);

    private string? ReasonOf(DocumentSourceState state, DocumentSourceFile file) => state switch
    {
        DocumentSourceState.AlreadyAttached => "Already attached",
        DocumentSourceState.TypeNotAllowed => $"{TypeLabelOf(file.ContentType)} not accepted",
        DocumentSourceState.AtCap => CapReason ?? $"At most {Cap} files",
        _ => null,
    };

    private static string CheckIcon(DocumentSourceState state, bool picked) => state switch
    {
        DocumentSourceState.AlreadyAttached => "link",
        DocumentSourceState.TypeNotAllowed or DocumentSourceState.AtCap => "block",
        _ => picked ? "check_box" : "check_box_outline_blank",
    };

    internal static string ContentTypeIcon(string? contentType) =>
        contentType == "application/pdf" ? "picture_as_pdf"
        : contentType == "application/epub+zip" ? "menu_book"
        : contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? "image"
        : contentType == "text/html" ? "code"
        : "description";

    internal static string ContentTypeShort(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "application/pdf" => "PDF",
        "application/epub+zip" => "EPUB",
        "image/png" => "PNG",
        "image/jpeg" => "JPEG",
        "image/webp" => "WebP",
        "text/html" => "HTML",
        null or "" => "Unknown type",
        var other => other.Split('/')[^1].Split('.')[^1].ToUpperInvariant(),
    };
}
