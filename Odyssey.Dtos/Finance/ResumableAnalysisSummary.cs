namespace Odyssey.Dtos.Finance;

/// <summary>
/// A minimal, per-file summary of the latest <em>resumable</em> file-analysis job for an account —
/// enough for the Files surface to offer "Resume review" without re-sending the document. Carries
/// counts only (no candidate free-text/merchant/amount) for data minimisation; files with no
/// resumable job are simply absent from the collection (never an existence oracle).
/// </summary>
public sealed record ResumableAnalysisSummary(
    Guid FileId,
    Guid AnalysisJobId,
    FileAnalysisJobStatus Status,
    DateTime? StartedAt,
    int CandidateCount,
    int PendingCount
);
