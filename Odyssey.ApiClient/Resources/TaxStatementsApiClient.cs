using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the yearly tax-statement endpoints (issue #173), including the reconciliation
/// report and the statement-scoped attachments.
/// </summary>
/// <remarks>
/// Attachments are addressed through their parent statement (<c>{id}/files/{fileId}</c>), never by
/// file id alone — the same scoping the account, insurance and contract clients use.
/// </remarks>
public interface ITaxStatementsApiClient
{
    /// <summary>One page of statements with search, year, status and tag filters (issue #277).</summary>
    Task<ApiResult<PagedResult<ExistingTaxStatement>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? years = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Every matching statement in one window, same filter surface as <see cref="ListAsync"/>.</summary>
    Task<ApiResult<List<ExistingTaxStatement>>> ListAllAsync(
        string? search = null,
        IReadOnlyCollection<string>? years = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    Task<ApiResult<ExistingTaxStatement>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The summary rollup behind the page header (issue #372): years on file, the fiscal-year bounds
    /// and the per-year declared figures the overview charts plot — a lean projection that replaces
    /// the header's former whole-table fetch. Null on failure.
    /// </summary>
    Task<TaxStatementSummary?> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// The server-computed reconciliation report — derived net worth against the declared figures,
    /// plus the problems list. Returned as a result so a page can tell "no report" from "load failed".
    /// </summary>
    Task<ApiResult<TaxStatementReport>> GetReportAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewTaxStatement statement, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, UpdateTaxStatement statement, CancellationToken ct = default);

    /// <summary>Moves the statement through its lifecycle (draft → filed → …) with an optional comment.</summary>
    Task<ApiResult> UpdateStatusAsync(Guid id, UpdateTaxStatementStatus status, CancellationToken ct = default);

    /// <summary>Replaces the statement's tag set wholesale (the endpoint is a PUT, not a patch).</summary>
    Task<ApiResult> UpdateTagsAsync(Guid id, UpdateTaxStatementTags tags, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    // ── Attachments ──────────────────────────────────────────────────────────

    Task<ApiResult> AttachFileAsync(Guid statementId, AttachTaxStatementFileRequest request, CancellationToken ct = default);

    Task<ApiResult<ApiFile>> DownloadFileAsync(Guid statementId, Guid fileId, CancellationToken ct = default);

    Task<ApiResult> DetachFileAsync(Guid statementId, Guid fileId, CancellationToken ct = default);
}

/// <inheritdoc cref="ITaxStatementsApiClient" />
public sealed class TaxStatementsApiClient(IOdysseyApi api) : ITaxStatementsApiClient
{
    private const string Base = "api/tax-statements";

    public Task<ApiResult<PagedResult<ExistingTaxStatement>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? years = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingTaxStatement>(
            Query(search, years, statuses, sortBy, sortDir).Window(page, pageSize).Build(), ct);

    public Task<ApiResult<List<ExistingTaxStatement>>> ListAllAsync(
        string? search = null,
        IReadOnlyCollection<string>? years = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<ExistingTaxStatement>(Query(search, years, statuses, sortBy, sortDir).Build(), ct);

    private static PagedQuery Query(
        string? search,
        IReadOnlyCollection<string>? years,
        IReadOnlyCollection<string>? statuses,
        string? sortBy,
        string? sortDir) =>
        PagedQuery.For(Base)
            .Add("search", search)
            .AddMany("years", years)
            .AddSingle("status", statuses)
            .Add("sortBy", sortBy)
            .Add("sortDir", sortDir);

    public Task<ApiResult<ExistingTaxStatement>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingTaxStatement>($"{Base}/{id}", ct);

    public async Task<TaxStatementSummary?> GetSummaryAsync(CancellationToken ct = default) =>
        (await api.GetAsync<TaxStatementSummary>($"{Base}/summary", ct)).Value;

    public Task<ApiResult<TaxStatementReport>> GetReportAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<TaxStatementReport>($"{Base}/{id}/report", ct);

    public Task<ApiResult> CreateAsync(NewTaxStatement statement, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, statement, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdateTaxStatement statement, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", statement, ct);

    public Task<ApiResult> UpdateStatusAsync(Guid id, UpdateTaxStatementStatus status, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Patch, $"{Base}/{id}/status", status, ct);

    public Task<ApiResult> UpdateTagsAsync(Guid id, UpdateTaxStatementTags tags, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}/tags", tags, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    // ── Attachments ──────────────────────────────────────────────────────────

    public Task<ApiResult> AttachFileAsync(Guid statementId, AttachTaxStatementFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Files(statementId), request, ct);

    public Task<ApiResult<ApiFile>> DownloadFileAsync(Guid statementId, Guid fileId, CancellationToken ct = default) =>
        api.GetFileAsync($"{Files(statementId)}/{fileId}", "tax-statement-file", ct: ct);

    public Task<ApiResult> DetachFileAsync(Guid statementId, Guid fileId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Files(statementId)}/{fileId}", null, ct);

    private static string Files(Guid statementId) => $"{Base}/{statementId}/files";
}
