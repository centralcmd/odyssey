namespace Odyssey.Dtos.Journal;

/// <summary>
/// Lifecycle of a to-do item on the shared task board. Mirrors the context enum's member order so the
/// two convert 1:1 via the module Mapster config.
/// </summary>
public enum JournalTaskStatus
{
    Backlog,
    Doing,
    Done,
    Archived,
}
