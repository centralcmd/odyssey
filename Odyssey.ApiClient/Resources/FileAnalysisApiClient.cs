using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the job half of file analysis — polling a job, running the match step, and
/// committing the reviewed candidates. Starting a job is account-scoped and lives on
/// <see cref="IAccountsApiClient.AnalyzeFileAsync"/>, mirroring the server's own split.
/// </summary>
/// <remarks>
/// This surface is feature-flagged (<c>FileAnalysis:Enabled</c>, the one pre-convention toggle in the
/// codebase) and answers <c>503</c> when off. That is a distinct outcome from a genuine failure, and
/// the dialog branches on it — so these methods return the status rather than collapsing everything
/// into "it didn't work". Likewise a <c>409</c> on import means the job was already committed.
/// </remarks>
public interface IFileAnalysisApiClient
{
    /// <summary>Fetches a job's current state, including its candidate transactions.</summary>
    Task<ApiResult<ExistingFileAnalysisJob>> GetJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Runs the second-pass match step, which resolves candidate contact and tag names against
    /// existing records. Returns the job with its match results applied.
    /// </summary>
    Task<ApiResult<ExistingFileAnalysisJob>> MatchAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Commits the reviewed candidates as transactions.</summary>
    Task<ApiResult<ImportResponse>> ImportAsync(Guid jobId, ImportRequest request, CancellationToken ct = default);

    /// <summary>
    /// The admin transfer-audit trail (gated on <c>file-analysis.audit</c>, Admin-only), newest first.
    /// Returned as one window — the log page renders its whole filtered set rather than paging.
    /// </summary>
    Task<ApiResult<List<FileAnalysisAuditEntry>>> ListAuditAsync(
        string? search = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IFileAnalysisApiClient" />
public sealed class FileAnalysisApiClient(IOdysseyApi api) : IFileAnalysisApiClient
{
    private const string Base = "api/file-analysis";

    public Task<ApiResult<ExistingFileAnalysisJob>> GetJobAsync(Guid jobId, CancellationToken ct = default) =>
        api.GetAsync<ExistingFileAnalysisJob>($"{Base}/{jobId}", ct);

    public Task<ApiResult<ExistingFileAnalysisJob>> MatchAsync(Guid jobId, CancellationToken ct = default) =>
        api.SendAsync<ExistingFileAnalysisJob>(HttpMethod.Post, $"{Base}/{jobId}/match", null, ct);

    public Task<ApiResult<ImportResponse>> ImportAsync(Guid jobId, ImportRequest request, CancellationToken ct = default) =>
        api.SendAsync<ImportResponse>(HttpMethod.Post, $"{Base}/{jobId}/import", request, ct);

    public Task<ApiResult<List<FileAnalysisAuditEntry>>> ListAuditAsync(
        string? search = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<FileAnalysisAuditEntry>(
            PagedQuery.For($"{Base}/audit")
                .Add("search", search)
                .AddMany("statuses", statuses)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);
}
