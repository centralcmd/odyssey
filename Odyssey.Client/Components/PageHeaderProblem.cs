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
