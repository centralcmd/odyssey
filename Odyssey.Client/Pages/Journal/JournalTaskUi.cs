using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>Shared UI constants for the task surfaces (kept in one place so the status vocabulary is
/// declared once for both the tasks page filter and the create/edit dialog's status select).</summary>
public static class JournalTaskUi
{
    /// <summary>The kanban lifecycle statuses as picker options (values are the enum names).</summary>
    public static readonly IReadOnlyList<OdsOption> StatusOptions =
    [
        new(nameof(JournalTaskStatus.Backlog), "Backlog"),
        new(nameof(JournalTaskStatus.Doing), "Doing"),
        new(nameof(JournalTaskStatus.Done), "Done"),
        new(nameof(JournalTaskStatus.Archived), "Archived"),
    ];
}
