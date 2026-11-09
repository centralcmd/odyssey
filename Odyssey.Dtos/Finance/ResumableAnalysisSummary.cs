namespace Odyssey.Dtos.Finance;

/// <summary>
/// A file's latest analysis state for the Files surface (issue #444 §5.3; before it, only a resumable
/// review). The <em>primary</em> job, by precedence: an open (Queued or Running) job; else the latest
/// Completed job with pending candidates; else the newest job if it ended Failed or Cancelled. Counts only
/// (no candidate free-text, merchant or amount); a never-analysed or fully reviewed file is absent.
/// </summary>
public sealed record ResumableAnalysisSummary(
    Guid FileId,
    Guid AnalysisJobId,
    FileAnalysisJobStatus Status,
    DateTime? StartedAt,
    int CandidateCount,
    int PendingCount,
    // The primary job's server-computed sent status and terminal code (issue #444).
    FileAnalysisDocumentSent DocumentSent = FileAnalysisDocumentSent.Possibly,
    string? FailureCode = null,
    // The file's newest job when it ended Failed / Cancelled BESIDE a different primary (an open job or a
    // pending review): a failed re-analysis never hides Resume review.
    LatestAnalysisFailure? LatestFailure = null
);

/// <summary>A failed or stopped attempt carried beside a file's pending review (issue #444 §5.3).</summary>
public sealed record LatestAnalysisFailure(
    Guid AnalysisJobId,
    FileAnalysisJobStatus Status,
    string? FailureCode,
    FileAnalysisDocumentSent DocumentSent,
    DateTime? At
);
