using Odyssey.Context;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic task-tag reference data for the Journal module's to-do list (issue #311). Mirrors
/// <see cref="JournalTagGenerator"/>: names are stable keys (id derived via <see cref="IdFor"/>) so
/// <see cref="JournalTaskGenerator"/> can wire tag links without EF fixup, and one archived tag is
/// included for the archived filter.
/// </summary>
public static class JournalTaskTagGenerator
{
    private sealed record TagSpec(string Name, string? Description, bool Archived);

    private static readonly TagSpec[] Definitions =
    [
        new("Urgent", "Needs attention soon", false),
        new("Finance", "Money-related to-dos", false),
        new("Home", "House and household chores", false),
        new("Errands", "Out-and-about tasks", false),
        new("Waiting", "Blocked on someone else", false),
        new("Someday", "Nice to do eventually", true),
    ];

    public static Guid IdFor(string name) => DeterministicGuid.From($"task-tag::{name}");

    /// <summary>
    /// Builds the task-tag set. <paramref name="anchor"/> stamps the archived timestamp so the
    /// archived tag's <c>Archived</c> value is stable.
    /// </summary>
    public static List<JournalTaskTag> Generate(DateTime anchor) =>
        Definitions
            .Select(spec => new JournalTaskTag
            {
                JournalTaskTagId = IdFor(spec.Name),
                Name = spec.Name,
                Description = spec.Description,
                Archived = spec.Archived ? anchor.AddMonths(-6) : null,
            })
            .ToList();
}
