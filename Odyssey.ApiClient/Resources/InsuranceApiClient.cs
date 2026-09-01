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
    /// Lists policies (lean projection) with server-side search, multi-type/status filters, sort
    /// (issue #277) and the API-only contact filter (issue #27), which matches a policy naming the
    /// contact as an insurer, an insured contact or a beneficiary.
    /// </summary>
    Task<ApiResult<List<InsurancePolicyListItem>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        IReadOnlyCollection<Guid>? contactIds = null,
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

    // ── Parties ──────────────────────────────────────────────────────────────
    // One link at a time, with the term the full-set update has nowhere to put. A party is addressed
    // by its ROLE and its TARGET — the pair the read model already carries — not by a link-row id.

    /// <summary>Links one contact or account to a policy in one of its four roles.</summary>
    Task<ApiResult> AddPartyAsync(Guid policyId, InsurancePolicyPartyRequest party, CancellationToken ct = default);

    /// <summary>Re-writes one party: its role, its target, its dates, or any combination. The route
    /// names the link as it stands; <paramref name="party"/> names what it should become.</summary>
    Task<ApiResult> UpdatePartyAsync(
        Guid policyId, InsurancePartyRole role, Guid targetId, InsurancePolicyPartyRequest party, CancellationToken ct = default);

    /// <summary>Detaches one party. The linked contact or account itself is untouched.</summary>
    Task<ApiResult> RemovePartyAsync(Guid policyId, InsurancePartyRole role, Guid targetId, CancellationToken ct = default);

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
        IReadOnlyCollection<Guid>? contactIds = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<InsurancePolicyListItem>(
            PagedQuery.For(Base)
                .Add("search", search)
                .AddMany("types", types)
                .AddMany("statuses", statuses)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                // API-only in v1 (issue #27 Non-Goal 7): no page reads it, but a consumer asking
                // "which policies is this contact on?" has an answer.
                .AddMany("contactIds", contactIds?.Select(id => id.ToString()).ToList())
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

    // ── Parties ──────────────────────────────────────────────────────────────

    public Task<ApiResult> AddPartyAsync(Guid policyId, InsurancePolicyPartyRequest party, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Parties(policyId), party, ct);

    public Task<ApiResult> UpdatePartyAsync(
        Guid policyId, InsurancePartyRole role, Guid targetId, InsurancePolicyPartyRequest party, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Parties(policyId)}/{role}/{targetId}", party, ct);

    public Task<ApiResult> RemovePartyAsync(Guid policyId, InsurancePartyRole role, Guid targetId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Parties(policyId)}/{role}/{targetId}", null, ct);

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

    private static string Parties(Guid policyId) => $"{Base}/{policyId}/parties";
}
