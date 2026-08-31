using Microsoft.AspNetCore.Components;

namespace Odyssey.Client.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  Table families — the column/row contracts for OdsTable, OdsRecordTable and
//  OdsFilesTable (Odyssey Design System · components/Table, RecordTable +
//  SortHeader / MetaTile, FilesTable): the sortable / expandable / editable
//  admin-ledger primitives, their per-row render contexts, and the file-row
//  shape the attachments table renders.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One denormalized file row for <c>OdsFilesTable</c> — plain data, no store
/// lookups. The host flattens its DTO (the flat <c>FileListItem</c>, a per-account or
/// per-transaction file) into this shape so the same table reads identically everywhere.</summary>
public sealed record OdsFilesRow
{
    /// <summary>Stable identity — row key and Copy-ID target.</summary>
    public required string Id { get; init; }

    /// <summary>File display name, e.g. "statement-2026-04.pdf".</summary>
    public required string Name { get; init; }

    /// <summary>File-kind key — fed to <c>TypeFor</c>, shown in the Type chip (e.g. "PDF", "Statement").</summary>
    public required string Kind { get; init; }

    /// <summary>Size in bytes; rendered via the table's size formatter.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Upload timestamp (UTC); rendered via the table's date formatter.</summary>
    public DateTime UploadedAtUtc { get; init; }

    /// <summary>Optional second line under the name.</summary>
    public string? Description { get; init; }

    /// <summary>When the document takes effect (e.g. policy start). Optional — surfaces in the detail well / editor.</summary>
    public DateTime? ValidFrom { get; init; }

    /// <summary>When the document expires (e.g. policy end, warranty expiry). Optional.</summary>
    public DateTime? ValidTo { get; init; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; init; }

    /// <summary>Issuing contact id (e.g. bank, insurer). Optional.</summary>
    public Guid? IssuedBy { get; init; }

    /// <summary>
    /// Optional additive status indicator rendered as an <c>OdsChip</c> next to the file name —
    /// e.g. a "Review pending · 12" hint that the file has an open, resumable analysis review.
    /// Meaning is carried in the chip text (never icon/colour alone). Absent on rows without a
    /// badge, so the table renders exactly as before.
    /// </summary>
    public OdsFilesRowStatusBadge? StatusBadge { get; init; }
}

/// <summary>
/// The additive per-row status chip for <c>OdsFilesTable</c> (Odyssey Design System ·
/// components/FilesTable). Meaning lives in <see cref="Text"/>; any <see cref="Icon"/> is decorative
/// (aria-hidden) and a status dot shows when it is omitted. Supply <see cref="AriaLabel"/> for the
/// full accessible name (file + count).
/// </summary>
public sealed record OdsFilesRowStatusBadge
{
    /// <summary>Visible chip text, e.g. "Review pending · 12".</summary>
    public required string Text { get; init; }

    /// <summary>Chip tone — defaults to <see cref="OdsChipTone.Pending"/> (amber).</summary>
    public OdsChipTone Tone { get; init; } = OdsChipTone.Pending;

    /// <summary>Optional decorative leading Material icon; when omitted a status dot shows.</summary>
    public string? Icon { get; init; }

    /// <summary>Full accessible name (file + count) for screen readers.</summary>
    public string? AriaLabel { get; init; }
}

/// <summary>A file kind's visuals for <c>OdsFilesTable</c> — the registry shape (icon ·
/// foreground color · soft tint) mirroring the upload picker so a kind reads the same
/// in the table avatar/chip as it does where the file was added.</summary>
public sealed record OdsFileKindMeta(string Icon, string Color, string Soft);

/// <summary>
/// The mount contract for <c>OdsFilesTable</c>'s Edit-file dialog, handed to a host's
/// <c>RenderEdit</c> template (Odyssey Design System · components/FilesTable → FTEditModal).
/// The table owns which row is being edited, the dialog's open state and the post-save "Saved"
/// flash; the template owns only the fields.
/// </summary>
public sealed class OdsFileEditContext
{
    /// <summary>Per-edit identity — put it on the dialog's <c>@@key</c> so a different row re-initialises it.</summary>
    public required object Key { get; init; }

    /// <summary>The dialog's visibility — bind to its <c>Open</c>.</summary>
    public required bool Open { get; init; }

    /// <summary>Raised when the dialog closes — bind to its <c>OpenChanged</c>.</summary>
    public required EventCallback<bool> OpenChanged { get; init; }

    /// <summary>Commit the patch — raises the table's <c>OnSave</c> and flashes "Saved" on the row.</summary>
    public required EventCallback<object?> OnSave { get; init; }
}

/// <summary>
/// The patch raised by <c>OdsFilesTable</c>'s default Edit-file dialog — a file's
/// only mutable fields (display name + document-type key). Hosts read it off the
/// <see cref="OdsRecordSaveEventArgs.Patch"/>; surfaces that edit different fields
/// (e.g. the flat Files page edits name + description) supply their own
/// <c>RenderEdit</c> and patch shape instead.
/// </summary>
public sealed record OdsFileEdit(string Name, string Kind)
{
    /// <summary>When the document takes effect (e.g. policy start). Optional document-validity metadata.</summary>
    public DateTime? ValidFrom { get; init; }

    /// <summary>When the document expires (e.g. policy end, warranty expiry). Optional.</summary>
    public DateTime? ValidTo { get; init; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; init; }

    /// <summary>Issuing contact id. Optional.</summary>
    public Guid? IssuedBy { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  RecordTable family (Odyssey Design System · components/RecordTable, SortHeader,
//  MetaTile). The sortable / expandable / editable admin-ledger table primitive
//  and its supporting field-well + sort-header atoms.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Per-row state passed to a <see cref="OdsRecordColumn{TRow}"/> cell renderer.</summary>
/// <param name="Expanded">The row is currently expanded.</param>
/// <param name="Editing">The row is in edit mode (its panel shows the edit form).</param>
/// <param name="JustSaved">The row just saved — show the transient "Saved" flash.</param>
public readonly record struct OdsRecordCellContext(bool Expanded, bool Editing, bool JustSaved);

/// <summary>Row actions passed to a RecordTable's <c>Actions</c> builder for the overflow menu.</summary>
public sealed class OdsRecordActionContext
{
    /// <summary>The row is currently expanded.</summary>
    public bool Expanded { get; init; }

    /// <summary>The row is in edit mode.</summary>
    public bool Editing { get; init; }

    /// <summary>Expand / collapse this row.</summary>
    public required Action Toggle { get; init; }

    /// <summary>Open this row and switch it into edit mode.</summary>
    public required Action StartEdit { get; init; }

    /// <summary>Delete this row (clears its open/edit state, then raises <c>OnDelete</c>).</summary>
    public required Action Remove { get; init; }
}

/// <summary>Commit / cancel callbacks passed to a RecordTable's <c>RenderEdit</c> template.</summary>
public sealed class OdsRecordEditContext
{
    /// <summary>Commit a patch — raises <c>OnSave</c>, exits edit mode, and flashes "Saved".</summary>
    public required Action<object?> Save { get; init; }

    /// <summary>Leave edit mode without saving (the row stays expanded on its detail view).</summary>
    public required Action Cancel { get; init; }
}

/// <summary>Payload for a <see cref="OdsRecordTable{TRow}"/> save — the row's key and the edit patch.</summary>
public sealed record OdsRecordSaveEventArgs(object Key, object? Patch);

/// <summary>
/// One column of a <see cref="OdsRecordTable{TRow}"/>. Like <see cref="OdsTableColumn{TRow}"/>
/// but the cell renderer receives per-row <see cref="OdsRecordCellContext"/>, and sortable
/// columns supply a <see cref="SortValue"/> comparable (the table sorts in-place).
/// </summary>
public sealed class OdsRecordColumn<TRow>
{
    /// <summary>Stable column id (also the sort key for sortable columns).</summary>
    public required string Key { get; set; }

    /// <summary>Header label.</summary>
    public RenderFragment? Header { get; set; }

    /// <summary>Plain-text header shortcut (used when <see cref="Header"/> is null).</summary>
    public string? HeaderText { get; set; }

    /// <summary>End right-aligns the column and renders cells as monospace tabular figures.</summary>
    public OdsAlign Align { get; set; } = OdsAlign.Start;

    /// <summary>When true the header renders a sort button — supply <see cref="SortValue"/>.</summary>
    public bool Sortable { get; set; }

    /// <summary>Field data type of a sortable column — drives the default direction on a column change
    /// (§8.4) and the derived <see cref="OdsSortField{TRow}"/> when a toolbar <c>OdsSortSelect</c> reads
    /// its options from the columns. Defaults to <see cref="OdsSortType.Text"/>.</summary>
    public OdsSortType SortType { get; set; } = OdsSortType.Text;

    /// <summary>Override the column's <see cref="SortType"/> natural default direction.</summary>
    public OdsSortDirection? SortDefaultDir { get; set; }

    /// <summary>Fixed column width for a non-sortable header, e.g. "160px", "20%".</summary>
    public string? Width { get; set; }

    /// <summary>Extra class on every cell in this column.</summary>
    public string? CellClass { get; set; }

    /// <summary>Per-row cell renderer; receives the row + its expand/edit/saved state.</summary>
    public Func<TRow, OdsRecordCellContext, RenderFragment>? Cell { get; set; }

    /// <summary>Comparable value used to sort this column (required for sortable columns).</summary>
    public Func<TRow, IComparable?>? SortValue { get; set; }
}

/// <summary>
/// One column of a <see cref="OdsTable{TRow}"/>. <see cref="Cell"/> is the per-row
/// renderer; when omitted nothing is drawn (supply a Cell for every column).
/// </summary>
public sealed class OdsTableColumn<TRow>
{
    /// <summary>Stable column id (also the sort key for sortable columns).</summary>
    public required string Key { get; set; }

    /// <summary>Header label.</summary>
    public RenderFragment? Header { get; set; }

    /// <summary>Plain-text header shortcut (used when <see cref="Header"/> is null).</summary>
    public string? HeaderText { get; set; }

    /// <summary>End right-aligns the column and renders cells as monospace tabular figures.</summary>
    public OdsAlign Align { get; set; } = OdsAlign.Start;

    /// <summary>When true the header renders a sort button — pair with the table's Sort + OnSort.</summary>
    public bool Sortable { get; set; }

    /// <summary>Per-row cell renderer.</summary>
    public RenderFragment<TRow>? Cell { get; set; }

    /// <summary>Fixed column width, e.g. "1%", "160px", "20%".</summary>
    public string? Width { get; set; }

    /// <summary>Extra class on every cell in this column.</summary>
    public string? CellClass { get; set; }
}
