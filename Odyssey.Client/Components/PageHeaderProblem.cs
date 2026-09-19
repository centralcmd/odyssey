using Microsoft.AspNetCore.Components;

namespace Odyssey.Client.Components;

/// <summary>
/// Severity of a <see cref="PageHeaderProblem"/>, following the Odyssey design-system
/// convention: information = sea/cyan, warning = amber, error = coral.
/// </summary>
public enum PageHeaderSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// One row in the <see cref="PageHeader"/> problem rollup. The page supplies the text
/// and (optionally) what the "View" action does; the header renders the severity-tinted
/// alert and a count badge on the toggle.
/// </summary>
public sealed class PageHeaderProblem
{
    /// <summary>Tint + icon of the alert row and its contribution to the toggle severity.</summary>
    public PageHeaderSeverity Severity { get; set; } = PageHeaderSeverity.Warning;

    /// <summary>
    /// Optional heading this row sits under. Consecutive rows sharing a group are rendered beneath one
    /// heading; the heading changes when this value does, so the page controls grouping purely by the
    /// ORDER it supplies rows in. A panel where every row leaves this null renders exactly as before.
    ///
    /// <para>
    /// A group is a reading aid, not a severity boundary: each row keeps its own tint, and the toggle
    /// still takes the worst severity in the whole panel. Grouping rows of different meanings under
    /// one heading — "a term ran out" beside "a charge falls due" — is the point; they belong to one
    /// question ("what is coming?") and to one count.
    /// </para>
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Optional replacement for the standard alert row. When set, the header renders this instead of
    /// the severity-tinted alert, still inside the panel, still under <see cref="Group"/>, and still
    /// contributing its <see cref="Severity"/> to the toggle and its 1 to the count.
    ///
    /// <para>
    /// This exists for a row with its own ANATOMY rather than its own wording — a dated,
    /// money-carrying charge row reads as a small table, not as a sentence — and keeping it in the
    /// same collection is what keeps one count, one severity and one open/closed state. A row
    /// supplying this owns its own click handling; <see cref="OnView"/> is not wired for it.
    /// </para>
    /// </summary>
    public RenderFragment? Row { get; set; }

    /// <summary>Optional bold lead-in shown before the message (e.g. the record's name).</summary>
    public string? Lead { get; set; }

    /// <summary>The problem description shown in the row.</summary>
    public required string Message { get; set; }

    /// <summary>
    /// Optional dimmed, italic suffix naming WHERE the affected row lives (Odyssey Design System ·
    /// account-signals.css, <c>.signal-where</c>). For a rollup whose rows are scattered across a page
    /// by subject, the title alone does not tell a reader where to look, and the jump target may be
    /// below the fold. Rendered as text, so it is available to a screen reader like the rest of the
    /// message — the styling only de-emphasises it.
    /// </summary>
    public string? Where { get; set; }

    /// <summary>Invoked when the row / "View" action is activated. When unset, no action is shown.</summary>
    public EventCallback OnView { get; set; }

    /// <summary>Label of the quiet action link on the right of the row.</summary>
    public string ViewLabel { get; set; } = "View";

    internal bool HasView => OnView.HasDelegate;
}
