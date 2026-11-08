namespace Odyssey.Dtos.Finance;

/// <summary>
/// What a job's consent covered for the match step (issue #433). Stamped once, at analyze. The
/// ordinals are permanent: a stored value is a record of what a user agreed to. <c>0</c> is not a
/// member, and a job without a stamp (created before this feature) is read as <see cref="NamesOnly"/>.
/// </summary>
public enum FileAnalysisMatchVocabularyScope
{
    /// <summary>Contact names and tag names only.</summary>
    NamesOnly = 1,

    /// <summary>Contact names and aliases, tag names and descriptions.</summary>
    NamesAliasesDescriptions = 2,
}
