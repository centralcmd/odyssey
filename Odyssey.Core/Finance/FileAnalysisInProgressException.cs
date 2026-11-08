using System.Net;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance;

/// <summary>
/// <c>409 analysis_in_progress</c> (issue #444 §5): an open (Queued or Running) job already exists for the
/// file. The body carries that job's id, so the client attaches to it instead of starting a second
/// transfer. Echoing an opaque GUID to a caller who already holds <c>file-analysis.create</c> for the file
/// is not an existence oracle in a single-tenant domain.
/// </summary>
public sealed class FileAnalysisInProgressException : DomainException
{
    public FileAnalysisInProgressException(Guid analysisJobId)
        : base("An analysis of this file is already in progress.")
    {
        AnalysisJobId = analysisJobId;
    }

    public Guid AnalysisJobId { get; }

    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code => FileAnalysisCodes.AnalysisInProgress;

    public override IReadOnlyDictionary<string, object?>? Extensions =>
        new Dictionary<string, object?> { [FileAnalysisCodes.AnalysisJobIdExtension] = AnalysisJobId };
}

/// <summary>
/// <c>422 file_too_large_for_analysis</c> (issue #444 v4 §5): the file is larger than
/// <see cref="FileAnalysisJobTiming.MaxAnalysisFileBytes"/>. The upload cap is admin-editable up to the
/// transport ceiling, so it cannot bound the worker's memory; this ceiling does. Nothing is enqueued. The
/// limit travels in the body (<c>limitBytes</c>) so the client interpolates it rather than holding a copy.
/// </summary>
public sealed class FileAnalysisTooLargeException : DomainException
{
    public FileAnalysisTooLargeException(long limitBytes)
        : base($"This file is too large to analyze. The limit is {limitBytes / (1024 * 1024)} MiB.")
    {
        LimitBytes = limitBytes;
        Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["file"] = [Message] };
    }

    public long LimitBytes { get; }

    public override int StatusCode => (int)HttpStatusCode.UnprocessableEntity;

    public override string? Code => FileAnalysisCodes.FileTooLargeForAnalysis;

    public override IReadOnlyDictionary<string, object?>? Extensions =>
        new Dictionary<string, object?> { [FileAnalysisCodes.LimitBytesExtension] = LimitBytes };
}
