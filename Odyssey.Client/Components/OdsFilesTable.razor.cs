using Microsoft.AspNetCore.Components;

namespace Odyssey.Client.Components;

public partial class OdsFilesTable
{
    /// <summary>Rows to render (already filtered by the host).</summary>
    [Parameter, EditorRequired] public IEnumerable<OdsFilesRow> Files { get; set; } = [];

    /// <summary>Resolve a row's kind visuals. Unknown kinds fall back to the neutral document glyph.</summary>
    [Parameter] public Func<OdsFilesRow, OdsFileKindMeta>? TypeFor { get; set; }

    /// <summary>
    /// File-specific overflow-menu items — Preview / Download / Analyze / Copy ID —
    /// slotted between the built-in Edit and Delete items. "View details" (expand) and
    /// Delete are owned by the table; supply only the file-specific items here.
    /// </summary>
    [Parameter] public Func<OdsFilesRow, IReadOnlyList<OdsMenuItem>>? Actions { get; set; }

    /// <summary>Override the read-only detail panel (default: File name · Document type · Size · Uploaded).</summary>
    [Parameter] public RenderFragment<OdsFilesRow>? RenderDetail { get; set; }

    /// <summary>Override the inline edit panel (default: name + document-type picker fed by <see cref="Kinds"/>).</summary>
    [Parameter] public Func<OdsFilesRow, OdsRecordEditContext, RenderFragment>? RenderEdit { get; set; }

    /// <summary>
    /// Persist an inline edit. The default panel raises an <see cref="OdsFileEdit"/> patch;
    /// a custom <see cref="RenderEdit"/> raises its own shape. Enables the Edit menu item +
    /// the inline panel. Omit for a read-only surface.
    /// </summary>
    [Parameter] public EventCallback<OdsRecordSaveEventArgs> OnSave { get; set; }

    /// <summary>Document-type vocabulary for the default edit panel's picker. Default: the canonical account-file kinds.</summary>
    [Parameter] public IReadOnlyList<OdsOption>? Kinds { get; set; }

    /// <summary>
    /// Issuing-contact options. When supplied the default edit panel grows the document-validity
    /// row (Valid from / Valid to / Issued / Issued by) — account-file surfaces pass this; transaction
    /// and tax surfaces leave it null for the name + type-only editor.
    /// </summary>
    [Parameter] public IReadOnlyList<OdsOption>? Issuers { get; set; }

    /// <summary>Resolve a row's <c>IssuedBy</c> id to a display name for the detail well. Default: the raw id.</summary>
    [Parameter] public Func<OdsFilesRow, string?>? IssuerFor { get; set; }

    /// <summary>Detach/delete a file — appends the danger Delete item after a divider.</summary>
    [Parameter] public EventCallback<OdsFilesRow> OnDelete { get; set; }

    /// <summary>Size cell renderer. Default: human-readable bytes (B / KB / MB).</summary>
    [Parameter] public Func<long, string>? FormatSize { get; set; }

    /// <summary>Uploaded cell renderer. Default: "Apr 12, 2026" in local time.</summary>
    [Parameter] public Func<DateTime, string>? FormatDate { get; set; }

    /// <summary>Initial sort (uncontrolled seed). Defaults to Uploaded, newest first.</summary>
    [Parameter] public OdsTableSort? DefaultSort { get; set; }

    /// <summary>Controlled sort — bind <c>@bind-Sort</c> to share one state with a toolbar <c>OdsSortSelect</c>.</summary>
    [Parameter] public OdsTableSort? Sort { get; set; }

    /// <summary>Raised with the complete next sort (enables <c>@bind-Sort</c>).</summary>
    [Parameter] public EventCallback<OdsTableSort> SortChanged { get; set; }

    /// <summary>Server-sort pass-through (issue #277): render <see cref="Files"/> verbatim, no client re-sort.</summary>
    [Parameter] public bool ServerSort { get; set; }

    /// <summary>Replaces the whole first-run empty state, for onboarding copy that needs markup.</summary>
    [Parameter] public RenderFragment? Empty { get; set; }

    // ── List states (forwarded verbatim to OdsRecordTable → OdsListStatus) ────
    /// <summary>The first fetch is in flight — renders a spinner instead of rows.</summary>
    [Parameter] public bool Loading { get; set; }

    /// <summary>A background refetch is in flight — renders the indeterminate bar above the table.</summary>
    [Parameter] public bool Refetching { get; set; }

    /// <summary>The fetch failed — renders the error state with a Retry, never the empty state.</summary>
    [Parameter] public bool Error { get; set; }

    /// <summary>Retry the failed fetch.</summary>
    [Parameter] public EventCallback OnRetry { get; set; }

    /// <summary>Optional detail line under the error title.</summary>
    [Parameter] public string? ErrorDescription { get; set; }

    /// <summary>A search or filter is narrowing the rows, so empty means "no matches", not "first run".</summary>
    [Parameter] public bool HasFilters { get; set; }

    /// <summary>Clear every filter and search — offered from the filtered-empty state.</summary>
    [Parameter] public EventCallback OnClearFilters { get; set; }

    /// <summary>Lower-case plural noun for the state copy.</summary>
    [Parameter] public string Noun { get; set; } = "files";

    /// <summary>Material Icons ligature for the first-run empty state.</summary>
    [Parameter] public string EmptyIcon { get; set; } = "folder_open";

    /// <summary>First-run empty title. Defaults to "No {Noun} yet".</summary>
    [Parameter] public string? EmptyTitle { get; set; }

    /// <summary>First-run empty supporting line.</summary>
    [Parameter] public string? EmptyDescription { get; set; }

    /// <summary>The first-run CTA — pass an OdsButton.</summary>
    [Parameter] public RenderFragment? CreateAction { get; set; }

    /// <summary>Utility classes for the refetch bar.</summary>
    [Parameter] public string? BarClass { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Accessible name for the table (no visible caption). Defaults to "Files".</summary>
    [Parameter] public string? AriaLabel { get; set; }

    private static readonly OdsTableSort DefaultUploadedSort = new("uploaded", OdsSortDirection.Desc);

    private static readonly OdsFileKindMeta NeutralKind =
        new("insert_drive_file", "var(--mud-palette-text-secondary)", "var(--mud-palette-action-disabled-background)");

    private List<OdsRecordColumn<OdsFilesRow>> _columns = [];

    protected override void OnInitialized() => _columns = BuildColumns();

    // The edit renderer is the host's RenderEdit when given, otherwise the default
    // name + document-type panel — but only when OnSave can persist the result.
    private Func<OdsFilesRow, OdsRecordEditContext, RenderFragment>? EditRenderer =>
        RenderEdit ?? (OnSave.HasDelegate ? DefaultEdit : null);

    private OdsFileKindMeta ResolveKind(OdsFilesRow file) => TypeFor?.Invoke(file) ?? NeutralKind;

    private string SizeText(OdsFilesRow file) =>
        FormatSize?.Invoke(file.SizeBytes) ?? DefaultFormatSize(file.SizeBytes);

    private string DateText(DateTime uploadedUtc) =>
        FormatDate?.Invoke(uploadedUtc) ?? uploadedUtc.ToLocalTime().ToString("MMM dd, yyyy");
}
