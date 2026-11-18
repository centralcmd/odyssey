using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Models;

/// <summary>A failed or stopped analysis a Files row reports (its own job, or the summary's <c>LatestFailure</c>).</summary>
public sealed record FileAnalysisRowFailure(Guid JobId, bool Stopped, FileAnalysisDocumentSent DocumentSent);

/// <summary>
/// The Files row's one analysis matrix (issue #445 §3; Odyssey Design System · Background analysis): from the
/// file's resumable summary, what the row says and offers. The chip, the menu and the dialog's start mode all read
/// this one value, so they cannot disagree.
/// </summary>
/// <param name="OpenJobId">A <c>Queued</c>/<c>Running</c> job: the row says <i>Analyzing</i> and offers View progress.</param>
/// <param name="PendingReview">A completed job with pending candidates: <i>Review pending · N</i> and Resume review.</param>
/// <param name="Failure">The last attempt failed or was stopped: <i>Analysis failed</i>/<i>stopped</i> and View result / View last attempt.</param>
/// <param name="PriorDocumentSent">The latest job's sent status — whether analyzing again is a second transfer.</param>
public sealed record FileAnalysisRowState(
    Guid? OpenJobId,
    ResumableAnalysisSummary? PendingReview,
    FileAnalysisRowFailure? Failure,
    FileAnalysisDocumentSent? PriorDocumentSent)
{
    public static readonly FileAnalysisRowState None = new(null, null, null, null);

    /// <summary>A failure beside a pending review is the review's "last attempt"; on its own it is the "result".</summary>
    public bool FailureIsLastAttempt => Failure is not null && PendingReview is not null;

    public static FileAnalysisRowState For(ResumableAnalysisSummary? summary)
    {
        if (summary is null)
        {
            return None;
        }

        switch (summary.Status)
        {
            case FileAnalysisJobStatus.Queued or FileAnalysisJobStatus.Running or FileAnalysisJobStatus.New:
                // An open job dominates: never "Review pending · 0", never Resume review, and nothing new is sent.
                return new(summary.AnalysisJobId, null, null, summary.DocumentSent);

            case FileAnalysisJobStatus.Completed when summary.PendingCount > 0:
                var latest = summary.LatestFailure is { Status: FileAnalysisJobStatus.Failed or FileAnalysisJobStatus.Cancelled } f
                    ? new FileAnalysisRowFailure(f.AnalysisJobId, f.Status == FileAnalysisJobStatus.Cancelled, f.DocumentSent)
                    : null;
                // The latest job is the failure when there is one, else the completed review (which was sent).
                return new(null, summary, latest, latest?.DocumentSent ?? summary.DocumentSent);

            case FileAnalysisJobStatus.Failed or FileAnalysisJobStatus.Cancelled:
                return new(null, null,
                    new FileAnalysisRowFailure(summary.AnalysisJobId, summary.Status == FileAnalysisJobStatus.Cancelled, summary.DocumentSent),
                    summary.DocumentSent);

            default:
                return None;
        }
    }
}
