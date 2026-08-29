using Odyssey.Dtos.Finance;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the contracts endpoints (issue #174), plus the <b>scoped, parent-routed</b> file
/// download the spec mandates (§7/§10): downloads hit <c>/api/contracts/{id}/files/{fileId}</c> —
/// never a generic by-file-id route — so the IDOR-free guarantee holds. The same scoping applies to
/// the party and attachment writes: every sub-resource route is built from its parent contract id
/// here, so no caller can address a party or file by id alone.
/// </summary>
public interface IContractsApiClient
{
    /// <summary>
    /// Lists contracts (lean projection) with server-side search, multi-type/status filters and sort
    /// (issue #277).
    /// </summary>
    Task<ApiResult<List<ContractListItem>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Loads one contract with parties, files and derived status. Returns null on failure.</summary>
    Task<ExistingContract?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads the summary rollup (counts by status + by type). Returns null on failure.</summary>
    Task<ContractSummary?> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Downloads a contract attachment via the contract-scoped route.</summary>
    Task<ApiResult<ApiFile>> DownloadFileAsync(Guid contractId, Guid fileId, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewContract contract, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, UpdateContract contract, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a party (a contact in a given role) to the contract.</summary>
    Task<ApiResult> AddPartyAsync(Guid contractId, AddContractPartyRequest request, CancellationToken ct = default);

    Task<ApiResult> RemovePartyAsync(Guid contractId, Guid partyId, CancellationToken ct = default);

    /// <summary>Attaches an already-uploaded file to the contract.</summary>
    Task<ApiResult> AttachFileAsync(Guid contractId, AttachContractFileRequest request, CancellationToken ct = default);

    Task<ApiResult> DetachFileAsync(Guid contractId, Guid fileId, CancellationToken ct = default);
}

/// <inheritdoc cref="IContractsApiClient" />
public sealed class ContractsApiClient(IOdysseyApi api) : IContractsApiClient
{
    private const string Base = "api/contracts";

    public Task<ApiResult<List<ContractListItem>>> ListAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<ContractListItem>(
            PagedQuery.For(Base)
                .Add("search", search)
                .AddMany("types", types)
                .AddMany("statuses", statuses)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public async Task<ExistingContract?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingContract>($"{Base}/{id}", ct)).Value;

    public async Task<ContractSummary?> GetSummaryAsync(CancellationToken ct = default) =>
        (await api.GetAsync<ContractSummary>($"{Base}/summary", ct)).Value;

    public Task<ApiResult<ApiFile>> DownloadFileAsync(Guid contractId, Guid fileId, CancellationToken ct = default) =>
        api.GetFileAsync($"{Base}/{contractId}/files/{fileId}", "contract-file", ct: ct);

    public Task<ApiResult> CreateAsync(NewContract contract, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, contract, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdateContract contract, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", contract, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    public Task<ApiResult> AddPartyAsync(Guid contractId, AddContractPartyRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{contractId}/parties", request, ct);

    public Task<ApiResult> RemovePartyAsync(Guid contractId, Guid partyId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{contractId}/parties/{partyId}", null, ct);

    public Task<ApiResult> AttachFileAsync(Guid contractId, AttachContractFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{contractId}/files", request, ct);

    public Task<ApiResult> DetachFileAsync(Guid contractId, Guid fileId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{contractId}/files/{fileId}", null, ct);
}
