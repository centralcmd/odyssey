using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Odyssey.Client.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  Shared option / data types for the Odyssey design-system component library
//  (the Ods* components) that do not belong to a larger family: the chart point
//  and slice records, the option shapes the form controls take, and the upload /
//  breakdown records. Each type mirrors the matching design-system contract in
//  "Odyssey Design System/components/*.d.ts".
//
//  The larger families live beside this file — OdsEnums.cs, OdsTableModels.cs,
//  OdsSortHelpers.cs, OdsPagerMath.cs and OdsTypeRegistries.cs.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One point of an <see cref="OdsLineChart"/> series — a category-axis label and its
/// y value. Order the series oldest → newest; points with a null <see cref="Value"/>
/// are skipped (mirrors the DS <c>LineChartPoint</c>).
/// </summary>
public sealed record OdsLinePoint(string Label, decimal? Value);

/// <summary>A single slice of a <see cref="OdsDonut"/> / <see cref="OdsDonutLegend"/>.</summary>
public sealed record OdsDonutSlice
{
    /// <summary>Slice / legend-row name.</summary>
    public required string Label { get; init; }

    /// <summary>Slice value (zero-values are dropped). Always positive — magnitude drives the ring.</summary>
    public required decimal Value { get; init; }

    /// <summary>Override the auto-assigned <c>--chart-*</c> color for this slice.</summary>
    public string? Color { get; init; }

    /// <summary>
    /// Optional per-slice payload available to the donut's <c>Format</c> callback — e.g. the
    /// source record so the legend can render a signed amount in the slice's own currency.
    /// </summary>
    public object? Tag { get; init; }
}

/// <summary>
/// Shared helpers for the donut charts — the default categorical palette plus
/// the slice-filter / color-assignment rules both OdsDonut and OdsDonutLegend
/// apply so swatch colors line up by order.
/// </summary>
public static class OdsDonutPalette
{
    /// <summary>The categorical chart palette (tide → sea → mint → violet → coral → amber).</summary>
    public static readonly IReadOnlyList<string> Default =
    [
        "var(--chart-1)", "var(--chart-2)", "var(--chart-3)",
        "var(--chart-4)", "var(--chart-5)", "var(--chart-6)",
    ];

    /// <summary>Drops zero / negative slices, preserving caller order.</summary>
    public static IReadOnlyList<OdsDonutSlice> Filter(IReadOnlyList<OdsDonutSlice> data) =>
        data.Where(d => d.Value > 0).ToList();

    /// <summary>The color for a slice — its explicit override, else the palette stop at its index.</summary>
    public static string ColorFor(OdsDonutSlice slice, int index, IReadOnlyList<string>? colors)
    {
        if (!string.IsNullOrEmpty(slice.Color))
            return slice.Color;
        var palette = colors is { Count: > 0 } ? colors : Default;
        return palette[index % palette.Count];
    }
}

/// <summary>A {value,label} option used by Select, Combobox, MultiSelect and RadioGroup.</summary>
public sealed record OdsOption(string Value, string Label)
{
    /// <summary>Convenience for plain-string option lists.</summary>
    public static OdsOption From(string s) => new(s, s);

    /// <summary>Optional leading Material Icons ligature shown on the option row (and the Select trigger).</summary>
    public string? Icon { get; init; }

    /// <summary>Color for the leading <see cref="Icon"/> (any CSS color, e.g. a category oklch). Inherits otherwise.</summary>
    public string? IconColor { get; init; }

    /// <summary>
    /// Secondary text after the label on an option row — a type ("Person", "Property"), an account
    /// number. What tells two same-named records apart in a picker, which is the whole job of the
    /// insurance link pickers. Rendered by <c>OdsTagMultiSelect</c>; ignored elsewhere.
    /// </summary>
    public string? Sub { get; init; }
}

/// <summary>One option in a <see cref="OdsRadioGroup"/>.</summary>
public sealed record OdsRadioOption(string Value, string Label)
{
    public bool Disabled { get; init; }
}

/// <summary>One option in a <see cref="OdsSegmentedControl"/>.</summary>
public sealed record OdsSegmentedOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
    /// <summary>Leading Material Icons ligature.</summary>
    public string? Icon { get; init; }
    /// <summary>Tints the option when selected — income (mint) / expense (coral).</summary>
    public OdsChipTone? Tone { get; init; }
    public bool Disabled { get; init; }
}

/// <summary>One tab in a <see cref="OdsTabs"/> strip.</summary>
public sealed record OdsTabItem(string Value, string Label);

/// <summary>One item in a <see cref="OdsMenu"/> — a normal item, a divider, or a group header.</summary>
public sealed class OdsMenuItem
{
    /// <summary>Visible label. Omitted for dividers / headers.</summary>
    public string? Label { get; set; }

    /// <summary>Leading Material Icons ligature name.</summary>
    public string? Icon { get; set; }

    /// <summary>Right-aligned Material Icons ligature revealed on row hover/focus — the
    /// <c>content_copy</c> affordance on a "Copy ID" item (Odyssey Design System · ActionMenu).</summary>
    public string? TrailingIcon { get; set; }

    /// <summary>Invoked when the item is chosen.</summary>
    public EventCallback OnClick { get; set; }

    /// <summary>Destructive action — renders in the error color.</summary>
    public bool Danger { get; set; }

    public bool Disabled { get; set; }

    /// <summary>Renders a hairline separator instead of an item.</summary>
    public bool Divider { get; set; }

    /// <summary>Renders an uppercase group label instead of an item.</summary>
    public string? Header { get; set; }

    /// <summary>
    /// One line saying WHY an item is unavailable, rendered under <see cref="Label"/> (issue #439).
    ///
    /// <para>
    /// A disabled item that only greys out conveys its meaning by colour alone, so the reason is
    /// stated in text. Since issue #26 that text is a <b>sibling</b> note wired to the item through
    /// <c>aria-describedby</c>, matching the design system's Menu — not content inside the item,
    /// which folded the reason into the item's accessible <em>name</em>.
    /// </para>
    ///
    /// <para>
    /// Setting this alongside <see cref="Disabled"/> also changes how the item is disabled: it keeps
    /// <c>aria-disabled</c> but drops MudBlazor's <c>disabled</c> treatment, so it stays reachable by
    /// keyboard and a screen-reader user can actually get to the explanation rather than skipping a
    /// silent item. <see cref="OdsMenu"/> suppresses the action and the menu-close itself.
    /// </para>
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>A file-kind descriptor for <see cref="OdsFileUpload"/> (Statement / Document / Receipt / Tax).</summary>
public sealed record OdsFileKind
{
    /// <summary>Stable key stored on each file's <see cref="OdsUploadFile.Kind"/>.</summary>
    public required string Key { get; init; }
    /// <summary>Visible label in the picker.</summary>
    public required string Label { get; init; }
    /// <summary>Material Icons ligature name.</summary>
    public required string Icon { get; init; }
    /// <summary>Icon foreground color (any CSS color).</summary>
    public required string Color { get; init; }
    /// <summary>Icon background tint (any CSS color).</summary>
    public required string Soft { get; init; }
}

/// <summary>
/// The default file-kind registry and helpers for <see cref="OdsFileUpload"/>,
/// mirroring the design system's FileUpload.KINDS / fmtSize / guessKind.
/// </summary>
public static class OdsFileUploadDefaults
{
    /// <summary>Statement / Document / Receipt / Tax — label + Material glyph + accent colors.</summary>
    public static readonly IReadOnlyList<OdsFileKind> Kinds =
    [
        new() { Key = "Statement", Label = "Statement", Icon = "description",       Color = "oklch(0.79 0.115 188)", Soft = "oklch(0.79 0.115 188 / 0.16)" },
        new() { Key = "Document",  Label = "Document",  Icon = "insert_drive_file", Color = "oklch(0.74 0.02 250)",  Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Receipt",   Label = "Receipt",   Icon = "receipt_long",      Color = "oklch(0.80 0.15 150)",  Soft = "oklch(0.80 0.15 150 / 0.16)" },
        new() { Key = "Tax",       Label = "Tax",       Icon = "request_quote",     Color = "oklch(0.75 0.16 330)",  Soft = "oklch(0.75 0.16 330 / 0.16)" },
    ];

    /// <summary>Format a byte count as "B / KB / MB". Null shows "—".</summary>
    public static string FmtSize(long? bytes)
    {
        if (bytes is null) return "—";
        var b = bytes.Value;
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024)
        {
            var kb = b / 1024.0;
            return kb < 10 ? $"{kb:0.0} KB" : $"{Math.Round(kb)} KB";
        }
        return $"{b / (1024.0 * 1024.0):0.0} MB";
    }

    /// <summary>Guess a kind key from a filename extension.</summary>
    public static string GuessKind(string name)
    {
        var ext = (name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : string.Empty).ToLowerInvariant();
        if (ext is "jpg" or "jpeg" or "png" or "heic" or "webp" or "gif" or "tiff") return "Receipt";
        if (ext == "pdf") return "Statement";
        return "Document";
    }
}

/// <summary>One ready file in a <see cref="OdsFileUpload"/> list.</summary>
public sealed class OdsUploadFile
{
    /// <summary>Stable unique id for the row.</summary>
    public required string Uid { get; set; }
    /// <summary>Editable display name.</summary>
    public required string Name { get; set; }
    /// <summary>File-kind key — matches an <see cref="OdsFileKind.Key"/>.</summary>
    public required string Kind { get; set; }
    /// <summary>Size in bytes; rendered human-readable. Null shows "—".</summary>
    public long? SizeBytes { get; set; }
    /// <summary>
    /// The underlying picked browser file, when this row came from a real file selection. Lets a host
    /// that performs an actual upload read the bytes (the design mock has no real file). Not serialized.
    /// </summary>
    public IBrowserFile? Source { get; set; }

    // ── Optional per-file validity metadata, edited via OdsFileUpload.RenderFileExtra
    //    (the account upload's Valid-from/to · Issued · Issued-by editor). Carried through
    //    rename / retype so edits survive those operations.
    /// <summary>When the document takes effect. Optional.</summary>
    public DateTime? ValidFrom { get; set; }
    /// <summary>When the document expires. Optional.</summary>
    public DateTime? ValidTo { get; set; }
    /// <summary>Date the document was issued. Optional.</summary>
    public DateTime? IssuedAt { get; set; }
    /// <summary>Issuing party (opaque key, e.g. a contact id). Optional.</summary>
    public string? IssuedBy { get; set; }
}

/// <summary>Context passed to <see cref="OdsFileUpload"/>'s <c>RenderFileExtra</c> slot — the mutable
/// file row plus a callback to re-commit the list after editing its metadata.</summary>
/// <param name="File">The file being edited (mutate its metadata fields in place).</param>
/// <param name="Changed">Invoke after mutating <paramref name="File"/> to re-commit the list.</param>
public readonly record struct OdsUploadFileExtraContext(OdsUploadFile File, EventCallback Changed);

/// <summary>One row of an <see cref="OdsBreakdownTile"/> — icon · label · count.</summary>
public sealed record OdsBreakdownRow
{
    /// <summary>Material Icons ligature name (optional).</summary>
    public string? Icon { get; set; }
    /// <summary>Icon foreground color (any CSS color / var).</summary>
    public string? IconColor { get; set; }
    /// <summary>Row label.</summary>
    public required string Label { get; set; }
    /// <summary>Right-aligned count (rendered in tabular mono).</summary>
    public required object Count { get; set; }
    /// <summary>Stable @key for the row (falls back to the row index).</summary>
    public object? Key { get; set; }
}

/// <summary>
/// One blocking problem listed by <see cref="OdsErrorSummary"/> — what is wrong, where it lives, and
/// the control to focus.
/// </summary>
/// <param name="Label">What is wrong, phrased as the row's own label plus the failure
/// (e.g. "Privacy notice URL — must be https://").</param>
/// <param name="Section">The section the row sits in, shown as a muted qualifier.</param>
/// <param name="TargetId">Id of the element to focus. It must belong to a RENDERED row — a
/// search-filtered or claim-disabled row is a dead end.</param>
public sealed record OdsErrorSummaryProblem(string Label, string? Section = null, string? TargetId = null);

/// <summary>
/// One sub-collection count on an <see cref="OdsRecordCard"/> header — the record's table of
/// contents (mirrors the DS <c>RecordCardCount</c>). Counts appear in the same order as the
/// body's sections, use the same glyphs, and stay live while the body edits them.
/// </summary>
/// <param name="Icon">Material Icons ligature ("receipt_long"), or a literal glyph such as "§".</param>
/// <param name="Value">The count itself, already formatted.</param>
/// <param name="Label">Accessible name / tooltip — "Transactions", "Files". The header is a
/// dense row of numbers; without this the count is a bare digit to a screen reader.</param>
public sealed record OdsRecordCount(string Icon, string Value, string? Label = null);

/// <summary>
/// Builders for the one meta line of an <see cref="OdsRecordCard"/> header — the DS passes an array
/// of nodes joined with "·" separators, and these are the two shapes nearly every entry takes.
/// A feature with a richer entry (a linked-record pill chip) passes its own fragment instead; the
/// card aligns one wrapper level down so it keeps the meta baseline.
/// </summary>
public static class OdsRecordMeta
{
    /// <summary>A plain meta entry ("Nordea", "Car loan"). Null / blank returns null, which the
    /// card drops — an absent fact must not leave a stray separator.</summary>
    public static RenderFragment? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : builder => builder.AddContent(0, value);

    /// <summary>A tabular meta entry — an account number, a rate, a date range. Mono per the DS,
    /// which sets the family inline rather than through a class.</summary>
    public static RenderFragment? Mono(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "style", "font-family:var(--font-mono)");
            builder.AddContent(2, value);
            builder.CloseElement();
        };
}
