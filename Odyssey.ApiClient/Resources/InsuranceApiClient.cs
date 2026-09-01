using Odyssey.Dtos.Finance;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the insurance-policies endpoints (issue #175), plus the <b>scoped, parent-routed</b>
/// file downloads the spec mandates (§3 client notes / §10): a download hits
/// <c>/api/insurance-policies/{id}/renewals/{renewalId}/files/{fileId}</c> — never the generic
/// by-file-id route — so the IDOR-free guarantee holds. The same scoping applies to the renewal and
/// attachment writes: every sub-resource route is built from its parent id here, so no caller can
/// address a file by id alone. Since issue #26 a document belongs to a period and nowhere else, so
/// the period is the only scope there is.
/// </summary>
public interface IInsuranceApiClient
{
    /// <summary>
    /// Lists policies (lean projection) with server-side search, multi-type/status filters and sort
    /// (issue #277).
    /// </summary>
    Task<ApiResult<List<InsurancePolicyListItem>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Loads one policy with its renewal periods, their documents and derived status.
    /// Returns null on failure.</summary>
    Task<ExistingInsurancePolicy?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads the portfolio rollup. When <paramref name="baseCurrency"/> is given the API adds
    /// converted grand totals. Returns null on failure.</summary>
    Task<InsurancePortfolioSummary?> GetSummaryAsync(string? baseCurrency = null, CancellationToken ct = default);

    /// <summary>Downloads a renewal-level attachment via the renewal-scoped route.</summary>
    Task<ApiResult<ApiFile>> DownloadRenewalFileAsync(Guid policyId, Guid renewalId, Guid fileId, CancellationToken ct = default);

    // ── Policies ─────────────────────────────────────────────────────────────

    Task<ApiResult> CreateAsync(NewInsurancePolicy policy, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, UpdateInsurancePolicy policy, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    // ── Renewals ─────────────────────────────────────────────────────────────

    Task<ApiResult> AddRenewalAsync(Guid policyId, NewPolicyRenewal renewal, CancellationToken ct = default);

    Task<ApiResult> UpdateRenewalAsync(Guid policyId, Guid renewalId, UpdatePolicyRenewal renewal, CancellationToken ct = default);

    Task<ApiResult> DeleteRenewalAsync(Guid policyId, Guid renewalId, CancellationToken ct = default);

    // ── Attachments ──────────────────────────────────────────────────────────

    /// <summary>Attaches an already-uploaded file to one of the policy's renewal periods.</summary>
    Task<ApiResult> AttachRenewalFileAsync(Guid policyId, Guid renewalId, AttachInsurancePolicyFileRequest request, CancellationToken ct = default);

    Task<ApiResult> DetachRenewalFileAsync(Guid policyId, Guid renewalId, Guid fileId, CancellationToken ct = default);
}

/// <inheritdoc cref="IInsuranceApiClient" />
public sealed class InsuranceApiClient(IOdysseyApi api) : IInsuranceApiClient
{
    private const string Base = "api/insurance-policies";

    public Task<ApiResult<List<InsurancePolicyListItem>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<InsurancePolicyListItem>(
            PagedQuery.For(Base)
                .Add("search", search)
                .AddMany("types", types)
                .AddMany("statuses", statuses)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public async Task<ExistingInsurancePolicy?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingInsurancePolicy>($"{Base}/{id}", ct)).Value;

    public async Task<InsurancePortfolioSummary?> GetSummaryAsync(string? baseCurrency = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(baseCurrency)
            ? $"{Base}/summary"
            : $"{Base}/summary?baseCurrency={Uri.EscapeDataString(baseCurrency)}";
        return (await api.GetAsync<InsurancePortfolioSummary>(url, ct)).Value;
    }

    public Task<ApiResult<ApiFile>> DownloadRenewalFileAsync(Guid policyId, Guid renewalId, Guid fileId, CancellationToken ct = default) =>
        api.GetFileAsync($"{Base}/{policyId}/renewals/{renewalId}/files/{fileId}", "renewal-file", ct: ct);

    // ── Policies ─────────────────────────────────────────────────────────────

    public Task<ApiResult> CreateAsync(NewInsurancePolicy policy, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, policy, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdateInsurancePolicy policy, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", policy, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    // ── Renewals ─────────────────────────────────────────────────────────────

    public Task<ApiResult> AddRenewalAsync(Guid policyId, NewPolicyRenewal renewal, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Renewals(policyId), renewal, ct);

    public Task<ApiResult> UpdateRenewalAsync(Guid policyId, Guid renewalId, UpdatePolicyRenewal renewal, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Renewals(policyId)}/{renewalId}", renewal, ct);

    public Task<ApiResult> DeleteRenewalAsync(Guid policyId, Guid renewalId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Renewals(policyId)}/{renewalId}", null, ct);

    // ── Attachments ──────────────────────────────────────────────────────────

    public Task<ApiResult> AttachRenewalFileAsync(Guid policyId, Guid renewalId, AttachInsurancePolicyFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Renewals(policyId)}/{renewalId}/files", request, ct);

    public Task<ApiResult> DetachRenewalFileAsync(Guid policyId, Guid renewalId, Guid fileId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Renewals(policyId)}/{renewalId}/files/{fileId}", null, ct);

    private static string Renewals(Guid policyId) => $"{Base}/{policyId}/renewals";
}
