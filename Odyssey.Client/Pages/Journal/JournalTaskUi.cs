using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>Shared UI constants for the task surfaces (kept in one place so the status vocabulary is
/// declared once for both the tasks page filter and the create/edit dialog's status select).</summary>
public static class JournalTaskUi
{
    /// <summary>
    /// The kanban lifecycle statuses as picker options (values are the enum names), each with the status
    /// chip's glyph and tone, so a filter row reads the same as the chip on the task.
    /// </summary>
    public static readonly IReadOnlyList<OdsOption> StatusOptions =
    [
        .. new[] { JournalTaskStatus.Backlog, JournalTaskStatus.Doing, JournalTaskStatus.Done, JournalTaskStatus.Archived }
            .Select(status =>
            {
                var meta = OdsTodoStatusChip.Meta(status);
                return new OdsOption(status.ToString(), meta.Label) { Icon = meta.Icon, IconColor = OdsBreakdown.Tone(meta.Tone) };
            }),
    ];
}
