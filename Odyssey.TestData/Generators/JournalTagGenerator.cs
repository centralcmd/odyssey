using Odyssey.Context;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic journal-tag reference data for the Journal module (issue #311). Names double as
/// stable keys: each tag's id is derived from its name via <see cref="IdFor"/>, so
/// <see cref="JournalEntryGenerator"/> can wire tag links without EF fixup and re-seeds reuse the
/// same ids. One archived tag is included so the list surface's archived filter has data to show.
/// </summary>
public static class JournalTagGenerator
{
    private sealed record TagSpec(string Name, string? Description, bool Archived);

    private static readonly TagSpec[] Definitions =
    [
        new("Personal", "Everyday personal notes", false),
        new("Finance", "Money, budgeting and planning", false),
        new("Travel", "Trips and journeys", false),
        new("Home", "House and household", false),
        new("Health", "Wellbeing and appointments", false),
        new("Ideas", "Things to remember or explore", false),
        new("Milestones", "Notable events worth recording", false),
        new("Old Notes", "Superseded notes, kept for the record", true),
    ];

    public static Guid IdFor(string name) => DeterministicGuid.From($"journal-tag::{name}");

    /// <summary>
    /// Builds the journal-tag set. <paramref name="anchor"/> stamps the archived timestamp so the
    /// archived tag's <c>Archived</c> value is stable.
    /// </summary>
    public static List<JournalTag> Generate(DateTime anchor) =>
        Definitions
            .Select(spec => new JournalTag
            {
                JournalTagId = IdFor(spec.Name),
                Name = spec.Name,
                Description = spec.Description,
                Archived = spec.Archived ? anchor.AddMonths(-6) : null,
            })
            .ToList();
}
