namespace Odyssey.Dtos.Finance;

/// <summary>
/// List-filter outcome bucket for the file-analysis audit log, collapsing the raw
/// <see cref="FileAnalysisJobStatus"/> into the three states the audit view exposes: in-progress
/// jobs (<see cref="Running"/>), successful ones (<see cref="Completed"/>) and failed/cancelled ones
/// (<see cref="Failed"/>).
/// </summary>
public enum FileAnalysisAuditStatus
{
    Running,
    Completed,
    Failed,
}
