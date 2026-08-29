namespace Odyssey.Context;

/// <summary>
/// Tracks the AI <em>match</em> step independently of the extraction <see cref="FileAnalysisJobStatus"/>.
/// Extraction status governs importability; this only governs whether merchant/category suggestions
/// exist — so a match failure never blocks importing the extracted candidates.
/// </summary>
public enum FileAnalysisMatchStatus
{
    NotRun = 0,
    Running = 1,
    Completed = 2,
    Skipped = 3,
    Failed = 4
}

/// <summary>
/// Provenance of the contact/tag values currently stored on a candidate.
/// <see cref="Llm"/> = the LLM auto-applied at least one field whose confidence reached the auto-link
/// threshold; <see cref="Manual"/> = the reviewer changed it (incl. inline-create or applying a
/// sub-threshold suggestion); <see cref="None"/> = never matched / cleared, or only sub-threshold
/// suggestions are stored (the ids are suggestions, not applied values).
/// </summary>
public enum MatchMethod
{
    None = 0,
    Llm = 1,
    Manual = 2
}
