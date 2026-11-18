namespace Odyssey.Dtos.Finance;

/// <summary>
/// How far a match run had to shrink its vocabulary to stay inside the payload budget (issue #433 §10).
/// Never a count cap: matching always runs, and names are always sent.
/// </summary>
public enum FileAnalysisVocabularyReduction
{
    None = 0,
    AliasesOmitted = 1,
    AliasesAndDescriptionsOmitted = 2,
}
