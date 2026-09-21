using Odyssey.Dtos;
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

    /// <summary>
    /// Loads the summary rollup — counts by status and by type, the recurring-cost run rate and the
    /// derived upcoming charges. <paramref name="baseCurrency"/> is the display currency the run rate
    /// converts into; blank lets the server pick the most common one. Returns null on failure.
    /// </summary>
    Task<ContractSummary?> GetSummaryAsync(string? baseCurrency = null, CancellationToken ct = default);

    /// <summary>Downloads a contract attachment via the contract-scoped route.</summary>
    Task<ApiResult<ApiFile>> DownloadFileAsync(Guid contractId, Guid fileId, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewContract contract, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, UpdateContract contract, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a party (an account or contact, in a role, optionally for a term) to the contract.</summary>
    Task<ApiResult> AddPartyAsync(Guid contractId, ContractPartyRequest request, CancellationToken ct = default);

    /// <summary>
    /// Re-writes one party in place (issue #121). The body is a <b>full replacement</b> of the link —
    /// role, target and both dates — so a caller that omits a date clears it and one that omits the
    /// role resets it to <c>Unspecified</c>.
    /// </summary>
    Task<ApiResult> UpdatePartyAsync(
        Guid contractId, Guid partyId, ContractPartyRequest request, CancellationToken ct = default);

    Task<ApiResult> RemovePartyAsync(Guid contractId, Guid partyId, CancellationToken ct = default);

    /// <summary>
    /// The documents attached to the contract, with their validity metadata (issue #146). Unpaged and
    /// bounded by the per-contract file cap. Serves a targeted refresh after a document write, where
    /// <see cref="GetAsync"/> would refetch parties, terms and events too.
    /// </summary>
    Task<ApiResult<List<ExistingContractFile>>> ListFilesAsync(Guid contractId, CancellationToken ct = default);

    /// <summary>Attaches an already-uploaded file to the contract.</summary>
    Task<ApiResult> AttachFileAsync(Guid contractId, AttachContractFileRequest request, CancellationToken ct = default);

    /// <summary>
    /// Updates an attached document's type and validity metadata (issue #146). The body is a
    /// <b>full replacement</b> — an omitted date or issuer <b>clears</b> it — and <c>fileType</c> may
    /// not be omitted. <paramref name="fileId"/> is the file-metadata id, the same one the download
    /// and detach routes take.
    /// </summary>
    Task<ApiResult> UpdateFileAsync(
        Guid contractId, Guid fileId, UpdateContractFileRequest request, CancellationToken ct = default);

    Task<ApiResult> DetachFileAsync(Guid contractId, Guid fileId, CancellationToken ct = default);

    // ── Terms (rates & fees) ─────────────────────────────────────────────────
    //
    // Contract-scoped exactly like the party and file routes above: a term is addressed as
    // {contractId}/terms/{termId}, never by term id alone, so the owner is always named by the route.

    /// <summary>The contract's full term history, newest effective date first.</summary>
    Task<ApiResult<List<ExistingTerm>>> ListTermsAsync(Guid contractId, CancellationToken ct = default);

    /// <summary>The in-force entry of each of the contract's term series, as of now.</summary>
    Task<ApiResult<List<CurrentTerm>>> ListCurrentTermsAsync(Guid contractId, CancellationToken ct = default);

    Task<ApiResult> AddTermAsync(Guid contractId, NewTerm term, CancellationToken ct = default);

    Task<ApiResult> UpdateTermAsync(Guid contractId, Guid termId, NewTerm term, CancellationToken ct = default);

    Task<ApiResult> DeleteTermAsync(Guid contractId, Guid termId, CancellationToken ct = default);

    // ── Events (the contract's own log) ───────────────────────────────────────
    //
    // Contract-scoped exactly like the party, term and file routes above: an event is addressed as
    // {contractId}/events/{eventId}, never by event id alone, so the owner is always named by the route.

    /// <summary>
    /// One page of the contract's event log. Newest first by default; the search term spans the title,
    /// the description <b>and</b> the notes.
    /// </summary>
    /// <param name="page">1-based page number, paired with <paramref name="pageSize"/>.</param>
    /// <param name="pageSize">Rows per page; <see cref="PagedQuery.SizeAll"/> requests the whole log.</param>
    /// <param name="source">
    /// Restricts the page to hand-written or to server-recorded events (issue #154 §5.1); omitted
    /// returns both. <b>Library parity with the API contract, not a UI deliverable</b> — no call site
    /// passes it in v1, and the optional parameter is what lets the eventual filter be a page change
    /// rather than a client change. It is APPENDED LAST, immediately before the
    /// <see cref="CancellationToken"/>: every call site uses named arguments today, so either position
    /// compiles, but a ten-parameter method is exactly where a positional call will eventually be
    /// written.
    /// </param>
    Task<ApiResult<PagedResult<ExistingContractEvent>>> ListEventsAsync(
        Guid contractId,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        DateTime? from = null,
        DateTime? to = null,
        string? sortBy = null,
        string? sortDir = null,
        int page = 1,
        int pageSize = PagedQuery.SizeAll,
        ContractEventSource? source = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records one event on the contract. <c>createdBy</c> and <c>createdAtUtc</c> are server-stamped
    /// and ignored if present in the body, and <c>occurredAt</c> must not be in the future (a
    /// 60-second forward tolerance allows for a client clock that runs slightly fast).
    /// </summary>
    Task<ApiResult> AddEventAsync(Guid contractId, NewContractEvent request, CancellationToken ct = default);

    /// <summary>
    /// Replaces one event. The body is a <b>full replacement</b> — <c>null</c> means "clear this
    /// field", <b>not</b> "leave unchanged", which is the opposite of <see cref="UpdateAsync"/>. A
    /// caller that omits <c>description</c> or <c>notes</c> <b>clears</b> it, and one that omits
    /// <c>type</c> resets it to <see cref="ContractEventType.Other"/>. <c>createdBy</c> and
    /// <c>createdAtUtc</c> are never rewritten by an update.
    /// </summary>
    Task<ApiResult> UpdateEventAsync(
        Guid contractId, Guid eventId, UpdateContractEvent request, CancellationToken ct = default);

    /// <summary>Removes one event from the contract's log. The contract itself is untouched.</summary>
    Task<ApiResult> DeleteEventAsync(Guid contractId, Guid eventId, CancellationToken ct = default);

    // ── Smart tags (issue #166) ──────────────────────────────────────────────

    /// <summary>
    /// The transaction tags this contract watches, oldest association first. An empty list is a
    /// healthy result; a missing contract is a <c>404</c>.
    /// </summary>
    Task<ApiResult<List<ExistingTransactionTag>>> ListSmartTagsAsync(
        Guid contractId, CancellationToken ct = default);

    /// <summary>
    /// Associates an existing, non-archived tag with the contract. No request body — both ids are
    /// route parameters. An already-linked pair is a <c>409</c>; an archived tag or an over-cap add is
    /// a <c>422</c>.
    /// </summary>
    Task<ApiResult> AddSmartTagAsync(Guid contractId, Guid tagId, CancellationToken ct = default);

    /// <summary>
    /// Removes one smart-tag association. The tag itself and the contract are untouched.
    /// </summary>
    Task<ApiResult> RemoveSmartTagAsync(Guid contractId, Guid tagId, CancellationToken ct = default);
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

    public async Task<ContractSummary?> GetSummaryAsync(string? baseCurrency = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(baseCurrency)
            ? $"{Base}/summary"
            : $"{Base}/summary?baseCurrency={Uri.EscapeDataString(baseCurrency)}";
        return (await api.GetAsync<ContractSummary>(url, ct)).Value;
    }

    public Task<ApiResult<ApiFile>> DownloadFileAsync(Guid contractId, Guid fileId, CancellationToken ct = default) =>
        api.GetFileAsync($"{Base}/{contractId}/files/{fileId}", "contract-file", ct: ct);

    public Task<ApiResult> CreateAsync(NewContract contract, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, contract, ct);

    public Task<ApiResult> UpdateAsync(Guid id, UpdateContract contract, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", contract, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    public Task<ApiResult> AddPartyAsync(Guid contractId, ContractPartyRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{contractId}/parties", request, ct);

    public Task<ApiResult> UpdatePartyAsync(
        Guid contractId, Guid partyId, ContractPartyRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{contractId}/parties/{partyId}", request, ct);

    public Task<ApiResult> RemovePartyAsync(Guid contractId, Guid partyId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{contractId}/parties/{partyId}", null, ct);

    public Task<ApiResult<List<ExistingContractFile>>> ListFilesAsync(Guid contractId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingContractFile>>($"{Base}/{contractId}/files", ct);

    public Task<ApiResult> AttachFileAsync(Guid contractId, AttachContractFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{Base}/{contractId}/files", request, ct);

    public Task<ApiResult> UpdateFileAsync(
        Guid contractId, Guid fileId, UpdateContractFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{contractId}/files/{fileId}", request, ct);

    public Task<ApiResult> DetachFileAsync(Guid contractId, Guid fileId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{contractId}/files/{fileId}", null, ct);

    // ── Terms ────────────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingTerm>>> ListTermsAsync(Guid contractId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingTerm>>(Terms(contractId), ct);

    public Task<ApiResult<List<CurrentTerm>>> ListCurrentTermsAsync(Guid contractId, CancellationToken ct = default) =>
        api.GetAsync<List<CurrentTerm>>($"{Terms(contractId)}/current", ct);

    public Task<ApiResult> AddTermAsync(Guid contractId, NewTerm term, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Terms(contractId), term, ct);

    public Task<ApiResult> UpdateTermAsync(Guid contractId, Guid termId, NewTerm term, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Terms(contractId)}/{termId}", term, ct);

    public Task<ApiResult> DeleteTermAsync(Guid contractId, Guid termId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Terms(contractId)}/{termId}", null, ct);

    private static string Terms(Guid contractId) => $"{Base}/{contractId}/terms";

    // ── Events ───────────────────────────────────────────────────────────────

    public Task<ApiResult<PagedResult<ExistingContractEvent>>> ListEventsAsync(
        Guid contractId,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        DateTime? from = null,
        DateTime? to = null,
        string? sortBy = null,
        string? sortDir = null,
        int page = 1,
        int pageSize = PagedQuery.SizeAll,
        ContractEventSource? source = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingContractEvent>(
            PagedQuery.For(Events(contractId))
                .Window(page, pageSize)
                .Add("search", search)
                .AddMany("types", types)
                .Add("from", from)
                .Add("to", to)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Add("source", source?.ToString())
                .Build(),
            ct);

    public Task<ApiResult> AddEventAsync(
        Guid contractId, NewContractEvent request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Events(contractId), request, ct);

    public Task<ApiResult> UpdateEventAsync(
        Guid contractId, Guid eventId, UpdateContractEvent request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Events(contractId)}/{eventId}", request, ct);

    public Task<ApiResult> DeleteEventAsync(Guid contractId, Guid eventId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Events(contractId)}/{eventId}", null, ct);

    private static string Events(Guid contractId) => $"{Base}/{contractId}/events";

    // ── Smart tags ───────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingTransactionTag>>> ListSmartTagsAsync(
        Guid contractId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingTransactionTag>>(SmartTags(contractId), ct);

    public Task<ApiResult> AddSmartTagAsync(Guid contractId, Guid tagId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{SmartTags(contractId)}/{tagId}", null, ct);

    public Task<ApiResult> RemoveSmartTagAsync(Guid contractId, Guid tagId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{SmartTags(contractId)}/{tagId}", null, ct);

    private static string SmartTags(Guid contractId) => $"{Base}/{contractId}/smart-tags";
}
