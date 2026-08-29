namespace Odyssey.Dtos.Finance;

public sealed record ExistingFileAnalysisJob(
    Guid Id,
    Guid AccountFileId,
    FileAnalysisJobStatus Status,
    string? FileTypeDetected,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? FailureCode,
    string? FailureMessage,
    string AnalyzerProvider,
    string? AnalyzerModel,
    string? PromptVersion,
    List<ExistingFileAnalysisCandidateTransaction> Candidates,
    // ── AI matching (issue #266) ──
    FileAnalysisMatchStatus MatchStatus,
    string? MatchFailureMessage,
    // Effective server-side match policy, echoed so the client never hardcodes (or drifts from) it.
    double AutoLinkThreshold,
    int MaxVocabulary
);
