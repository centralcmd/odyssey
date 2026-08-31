using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace Odyssey.Client.Components;

/// <summary>
/// Parameters and state for <see cref="OdsRecordCard"/> — the expandable record card behind every
/// record list (Odyssey Design System · components/RecordCard). The markup, and the reasoning behind
/// the fixed row height, the fixed body order and the one-accent-per-record rule, live in
/// OdsRecordCard.razor.
/// </summary>
public partial class OdsRecordCard
{
    /// <summary>DOM id for the card — the jump-to-record target of a header problem rollup.</summary>
    [Parameter] public string? Id { get; set; }

    /// <summary>Material Icons ligature for the record's TYPE, or its type-equivalent — a
    /// categorical registry the record always has exactly one of (Accounts: account type;
    /// Subscriptions: billing interval). Never derived state.</summary>
    [Parameter] public string? Icon { get; set; }

    /// <summary>The type's (or type-equivalent's) colour, any CSS color. Sets <c>--rec</c> on the
    /// card, inherited by every icon and single-series chart inside it. Omit for the brand accent.</summary>
    [Parameter] public string? Accent { get; set; }

    /// <summary>The type's soft/background tint (usually the accent at 16%). Sets <c>--rec-soft</c>.</summary>
    [Parameter] public string? AccentSoft { get; set; }

    /// <summary>The record's name — the one thing a user scans for.</summary>
    [Parameter, EditorRequired] public string? Name { get; set; }

    /// <summary>Status / problem chips beside the name.</summary>
    [Parameter] public RenderFragment? Chips { get; set; }

    /// <summary>The single meta line, joined with "·" separators — build the entries with
    /// <see cref="OdsRecordMeta"/>. Null entries are dropped, so an absent fact leaves no stray
    /// separator. Ellipsised, never wrapped.</summary>
    [Parameter] public IReadOnlyList<RenderFragment?>? Meta { get; set; }

    /// <summary>Sub-collection counts — the body's table of contents, in the same order as the
    /// sections, with the same glyphs, kept live while the body edits them.</summary>
    [Parameter] public IReadOnlyList<OdsRecordCount>? Counts { get; set; }

    /// <summary>The right-hand headline figure, already formatted (tabular, ISO currency, "−" for
    /// negatives). Omit for records that have none (journal entries) — never invent one.</summary>
    [Parameter] public RenderFragment? Figure { get; set; }

    /// <summary>Small uppercase caption under the figure — "Est. value", "USD / month".</summary>
    [Parameter] public string? FigureCaption { get; set; }

    /// <summary>Colour role for the figure — the finance vocabulary, never the record's accent.</summary>
    [Parameter] public OdsRecordFigureTone FigureTone { get; set; } = OdsRecordFigureTone.Neutral;

    /// <summary>Row actions (an <see cref="OdsMenu"/>). Sits outside the trigger, so it stays
    /// independently clickable and its clicks never toggle the card.</summary>
    [Parameter] public RenderFragment? Actions { get; set; }

    /// <summary>Body slot 1 — an OdsProblemAlert / OdsAlert. Always first: it is why the card was
    /// opened.</summary>
    [Parameter] public RenderFragment? Alert { get; set; }

    /// <summary>Body slot 2 — the record's full field set, as an <see cref="OdsInfoTileGrid"/> of
    /// <see cref="OdsInfoTile"/>s. Fields the header already shows are repeated here on purpose: a
    /// labelled tile reads as a field, not an echo.</summary>
    [Parameter] public RenderFragment? Details { get; set; }

    /// <summary>Body slot 3 — description / notes / entry text, in one Wide <see cref="OdsInfoTile"/>.
    /// Prose is not a field: never squeeze it into a tile column, never render it as a bare
    /// paragraph.</summary>
    [Parameter] public RenderFragment? Content { get; set; }

    /// <summary>Body slot 4 — the sub-collections, each introduced by an
    /// <see cref="OdsSectionDivider"/>, in the header's count order. (The DS calls this slot
    /// <c>children</c>; it is named here so a record body cannot land in the wrong slot by
    /// accident.)</summary>
    [Parameter] public RenderFragment? Sections { get; set; }

    /// <summary>Controlled open state — pair with OnToggle / <c>@bind-Open</c>. A list owns ONE open
    /// id and passes <c>Open="@(_openId == r.Id)"</c>: opening a record closes its siblings.</summary>
    [Parameter] public bool? Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Fires with the next open state.</summary>
    [Parameter] public EventCallback<bool> OnToggle { get; set; }

    /// <summary>Initial open state when uncontrolled.</summary>
    [Parameter] public bool DefaultOpen { get; set; }

    /// <summary>Closed / archived records: fades the header.</summary>
    [Parameter] public bool Dimmed { get; set; }

    /// <summary>One-shot attention ring, e.g. when a problems rollup jumps to this record.</summary>
    [Parameter] public bool Highlight { get; set; }

    /// <summary>Heading level wrapping the trigger (the ARIA accordion pattern). 0 opts out.</summary>
    [Parameter] public int HeadingLevel { get; set; } = 2;

    /// <summary>Extra CSS class(es) appended to the root (e.g. a page-specific modifier).</summary>
    [Parameter] public string? Class { get; set; }

    private readonly string _bodyId = $"odc-record-{Guid.NewGuid():N}";

    private bool _internal;

    protected override void OnInitialized() => _internal = DefaultOpen;

    private bool IsOpen => Open ?? _internal;

    private IReadOnlyList<RenderFragment> MetaItems =>
        Meta?.Where(m => m is not null).Select(m => m!).ToList() ?? [];

    private IReadOnlyList<OdsRecordCount> CountItems => Counts ?? [];

    private string? HeadingRole => HeadingLevel > 0 ? "heading" : null;

    private string? HeadingLevelAttr =>
        HeadingLevel > 0 ? HeadingLevel.ToString(CultureInfo.InvariantCulture) : null;

    private string RootClass =>
        string.Join(' ', new[]
        {
            "odc-record",
            IsOpen ? "open" : null,
            Dimmed ? "dimmed" : null,
            Highlight ? "flash" : null,
            Class,
        }.Where(c => !string.IsNullOrEmpty(c)));

    /// <summary>--rec / --rec-soft, the two custom properties everything inside the card reads.</summary>
    private string? AccentStyle
    {
        get
        {
            var rec = string.IsNullOrEmpty(Accent) ? null : $"--rec:{Accent};";
            var soft = string.IsNullOrEmpty(AccentSoft) ? null : $"--rec-soft:{AccentSoft};";
            return rec is null && soft is null ? null : $"{rec}{soft}";
        }
    }

    private string? FigureToneClass => FigureTone switch
    {
        OdsRecordFigureTone.Income => "income",
        OdsRecordFigureTone.Expense => "expense",
        OdsRecordFigureTone.Pending => "pending",
        _ => null,
    };

    /// <summary>A Material Icons ligature is all-lowercase ASCII letters, digits and underscores
    /// (e.g. "attach_file"); anything else (e.g. "§") renders as a literal glyph.</summary>
    private static bool IsLigature(string value) =>
        !string.IsNullOrEmpty(value) && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private async Task ToggleAsync()
    {
        var next = !IsOpen;
        if (Open is null)
            _internal = next;
        await OpenChanged.InvokeAsync(next);
        await OnToggle.InvokeAsync(next);
    }
}
