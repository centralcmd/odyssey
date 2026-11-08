namespace Odyssey.Dtos.Finance;

public enum FileAnalysisJobStatus
{
    New = 0,
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// Whether an analysis job's document may have left the deployment (issue #444 §5.2), computed by the
/// server from the job's transfer stamp — never derived by a client from a failure code. <c>No</c>: the
/// transfer never started. <c>Yes</c>: extraction completed. <c>Possibly</c>: the transfer started (or the
/// job predates the stamp) and did not complete — a connect failure and a provider error look alike.
/// </summary>
public enum FileAnalysisDocumentSent
{
    No = 0,
    Yes = 1,
    Possibly = 2,
}
