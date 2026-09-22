using System.Globalization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the accounts endpoints and their sub-resources — files, transactions, smart tags,
/// rate/fee terms, and value estimates. Terms, estimates and smart tags live on their own server-side
/// controllers but are all routed under <c>api/accounts/{accountId}/…</c>, so they belong here rather
/// than in clients of their own.
/// </summary>
/// <remarks>
/// Every sub-resource route is built from its parent account id, so a file, term or estimate can never
/// be addressed by its own id alone — the same scoping the contract client uses.
/// </remarks>
public interface IAccountsApiClient
{
    /// <summary>One page of accounts with the server-side filter surface (issue #277).</summary>
    Task<ApiResult<PagedResult<ExistingAccount>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>
    /// Every matching account in one window, with the same filter surface as <see cref="ListAsync"/>.
    /// The Accounts page renders its whole filtered set rather than paging, and the pickers want all
    /// rows, so this is the more used of the two.
    /// </summary>
    Task<ApiResult<List<ExistingAccount>>> ListAllAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    Task<ExistingAccount?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The summary rollup behind the page header (issue #372): status/type counts, the value
    /// aggregates and the per-account allocation rows the donuts render — so the header no longer
    /// costs a whole-table fetch. Returns null on failure.
    /// </summary>
    Task<AccountSummary?> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// The portfolio totals converted into <paramref name="mainCurrency"/>, measured as of now.
    ///
    /// <para>
    /// A missing exchange rate is <b>not</b> an error: the account contributes 0 and is named in
    /// <see cref="AccountTotals.UnconvertedAccounts"/>, so the figure comes back <c>200</c> and
    /// understated, and the caller is expected to disclose that. (This comment used to claim a
    /// <c>503</c>; the endpoint has never answered one — corrected with issue #90 G7.)
    /// </para>
    ///
    /// <para>
    /// An unsupported or archived <paramref name="mainCurrency"/> answers <c>400</c> — callers branch
    /// on <see cref="ApiResult{T}.Status"/>.
    /// </para>
    /// </summary>
    Task<ApiResult<AccountTotals>> GetTotalsAsync(string mainCurrency, CancellationToken ct = default);

    /// <summary>
    /// Net worth over time, reconstructed on read from stored transactions, estimates and rates
    /// (issue #90). Every point is a figure as of that point's own instant, so the series can fall and
    /// can go negative, and a series ending today ends on the same figure
    /// <see cref="GetTotalsAsync"/> returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every argument is optional and the server's defaults are the ones the dashboard wants — 24
    /// monthly points ending today, in NOK unless the caller names a currency. Sending no
    /// <paramref name="from"/>/<paramref name="to"/> is also the safer call from a browser: a
    /// <c>to</c> built from local time is tomorrow-in-UTC anywhere east of UTC, which the server
    /// rejects outright.
    /// </para>
    /// <para>
    /// An empty <see cref="NetWorthHistory.Points"/> always carries a
    /// <see cref="NetWorthHistory.EmptyReason"/>; it is the only thing that discriminates the causes,
    /// and the caller is expected to say which one rather than "no data".
    /// </para>
    /// </remarks>
    Task<ApiResult<NetWorthHistory>> GetNetWorthHistoryAsync(
        string? mainCurrency = null,
        NetWorthInterval? interval = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewAccount account, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, NewAccount account, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    // ── Files ────────────────────────────────────────────────────────────────

    Task<ApiResult<List<ExistingAccountFile>>> ListFilesAsync(Guid accountId, CancellationToken ct = default);

    Task<ApiResult> AttachFileAsync(Guid accountId, AttachAccountFileRequest request, CancellationToken ct = default);

    Task<ApiResult> UpdateFileAsync(Guid accountId, Guid fileId, UpdateAccountFileRequest request, CancellationToken ct = default);

    Task<ApiResult> DetachFileAsync(Guid accountId, Guid fileId, CancellationToken ct = default);

    // ── Transactions ─────────────────────────────────────────────────────────

    Task<ApiResult<PagedResult<ExistingTransaction>>> ListTransactionsAsync(
        Guid accountId, int page, int pageSize, CancellationToken ct = default);

    // ── Smart tags ───────────────────────────────────────────────────────────

    Task<ApiResult<List<ExistingTransactionTag>>> ListSmartTagsAsync(Guid accountId, CancellationToken ct = default);

    Task<ApiResult> AddSmartTagAsync(Guid accountId, Guid tagId, CancellationToken ct = default);

    Task<ApiResult> RemoveSmartTagAsync(Guid accountId, Guid tagId, CancellationToken ct = default);

    // ── Terms (rate & fees) ──────────────────────────────────────────────────

    Task<ApiResult<List<ExistingTerm>>> ListTermsAsync(Guid accountId, CancellationToken ct = default);

    Task<ApiResult> AddTermAsync(Guid accountId, NewTerm term, CancellationToken ct = default);

    Task<ApiResult> UpdateTermAsync(Guid accountId, Guid termId, NewTerm term, CancellationToken ct = default);

    Task<ApiResult> DeleteTermAsync(Guid accountId, Guid termId, CancellationToken ct = default);

    // ── Estimates ────────────────────────────────────────────────────────────

    Task<ApiResult<List<ExistingAccountEstimate>>> ListEstimatesAsync(Guid accountId, CancellationToken ct = default);

    Task<ApiResult> AddEstimateAsync(Guid accountId, NewAccountEstimate estimate, CancellationToken ct = default);

    Task<ApiResult> UpdateEstimateAsync(Guid accountId, Guid estimateId, NewAccountEstimate estimate, CancellationToken ct = default);

    Task<ApiResult> DeleteEstimateAsync(Guid accountId, Guid estimateId, CancellationToken ct = default);

    // ── File analysis (account-scoped half) ──────────────────────────────────

    /// <summary>
    /// Analysis jobs for this account that can be resumed. Gated by the file-analysis feature flag,
    /// which answers <c>503</c> when off — the dialog distinguishes that from a genuine failure.
    /// </summary>
    Task<ApiResult<List<ResumableAnalysisSummary>>> GetResumableAnalysisJobsAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Starts (or restarts) analysis of one of the account's files.</summary>
    Task<ApiResult<AnalyzeFileResponse>> AnalyzeFileAsync(
        Guid accountId, Guid fileId, AnalyzeFileRequest request, CancellationToken ct = default);
}

/// <inheritdoc cref="IAccountsApiClient" />
public sealed class AccountsApiClient(IOdysseyApi api) : IAccountsApiClient
{
    private const string Base = "api/accounts";

    public Task<ApiResult<PagedResult<ExistingAccount>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingAccount>(
            Query(search, types, statuses, sortBy, sortDir).Window(page, pageSize).Build(), ct);

    public Task<ApiResult<List<ExistingAccount>>> ListAllAsync(
        string? search = null,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyCollection<string>? statuses = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<ExistingAccount>(Query(search, types, statuses, sortBy, sortDir).Build(), ct);

    // Statuses is an array on AccountsQueryParams (unlike the single-value `status` most other
    // resources take), so it binds as repeated `statuses=` pairs.
    private static PagedQuery Query(
        string? search,
        IReadOnlyCollection<string>? types,
        IReadOnlyCollection<string>? statuses,
        string? sortBy,
        string? sortDir) =>
        PagedQuery.For(Base)
            .Add("search", search)
            .AddMany("types", types)
            .AddMany("statuses", statuses)
            .Add("sortBy", sortBy)
            .Add("sortDir", sortDir);

    public async Task<ExistingAccount?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingAccount>($"{Base}/{id}", ct)).Value;

    public async Task<AccountSummary?> GetSummaryAsync(CancellationToken ct = default) =>
        (await api.GetAsync<AccountSummary>($"{Base}/summary", ct)).Value;

    public Task<ApiResult<AccountTotals>> GetTotalsAsync(string mainCurrency, CancellationToken ct = default) =>
        api.GetAsync<AccountTotals>($"{Base}/totals?mainCurrency={Uri.EscapeDataString(mainCurrency)}", ct);

    public Task<ApiResult<NetWorthHistory>> GetNetWorthHistoryAsync(
        string? mainCurrency = null,
        NetWorthInterval? interval = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default)
    {
        // Built by hand rather than through PagedQuery: this is not a list endpoint and PagedQuery
        // always emits offset/limit, which would be two parameters the server does not bind.
        //
        // ISO-8601 round-trip on the dates. DateOnly's TypeConverter parses under the CURRENT culture
        // server-side, so a locale-formatted date would bind to a different day or not at all — "o"
        // is what makes the wire form independent of either end's culture.
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(mainCurrency))
            parts.Add($"mainCurrency={Uri.EscapeDataString(mainCurrency)}");
        if (interval is { } resolution)
            parts.Add($"interval={Uri.EscapeDataString(resolution.ToString())}");
        if (from is { } lower)
            parts.Add($"from={lower.ToString("o", CultureInfo.InvariantCulture)}");
        if (to is { } upper)
            parts.Add($"to={upper.ToString("o", CultureInfo.InvariantCulture)}");

        var path = $"{Base}/net-worth-history";
        return api.GetAsync<NetWorthHistory>(
            parts.Count == 0 ? path : $"{path}?{string.Join("&", parts)}", ct);
    }

    public Task<ApiResult> CreateAsync(NewAccount account, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, account, ct);

    public Task<ApiResult> UpdateAsync(Guid id, NewAccount account, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", account, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    // ── Files ────────────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingAccountFile>>> ListFilesAsync(Guid accountId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingAccountFile>>(Files(accountId), ct);

    public Task<ApiResult> AttachFileAsync(Guid accountId, AttachAccountFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Files(accountId), request, ct);

    public Task<ApiResult> UpdateFileAsync(Guid accountId, Guid fileId, UpdateAccountFileRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Files(accountId)}/{fileId}", request, ct);

    public Task<ApiResult> DetachFileAsync(Guid accountId, Guid fileId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Files(accountId)}/{fileId}", null, ct);

    // ── Transactions ─────────────────────────────────────────────────────────

    public Task<ApiResult<PagedResult<ExistingTransaction>>> ListTransactionsAsync(
        Guid accountId, int page, int pageSize, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingTransaction>(
            PagedQuery.For($"{Base}/{accountId}/transactions").Window(page, pageSize).Build(), ct);

    // ── Smart tags ───────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingTransactionTag>>> ListSmartTagsAsync(Guid accountId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingTransactionTag>>(SmartTags(accountId), ct);

    public Task<ApiResult> AddSmartTagAsync(Guid accountId, Guid tagId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"{SmartTags(accountId)}/{tagId}", null, ct);

    public Task<ApiResult> RemoveSmartTagAsync(Guid accountId, Guid tagId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{SmartTags(accountId)}/{tagId}", null, ct);

    // ── Terms ────────────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingTerm>>> ListTermsAsync(Guid accountId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingTerm>>(Terms(accountId), ct);

    public Task<ApiResult> AddTermAsync(Guid accountId, NewTerm term, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Terms(accountId), term, ct);

    public Task<ApiResult> UpdateTermAsync(Guid accountId, Guid termId, NewTerm term, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Terms(accountId)}/{termId}", term, ct);

    public Task<ApiResult> DeleteTermAsync(Guid accountId, Guid termId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Terms(accountId)}/{termId}", null, ct);

    // ── Estimates ────────────────────────────────────────────────────────────

    public Task<ApiResult<List<ExistingAccountEstimate>>> ListEstimatesAsync(Guid accountId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingAccountEstimate>>(Estimates(accountId), ct);

    public Task<ApiResult> AddEstimateAsync(Guid accountId, NewAccountEstimate estimate, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Estimates(accountId), estimate, ct);

    public Task<ApiResult> UpdateEstimateAsync(Guid accountId, Guid estimateId, NewAccountEstimate estimate, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Estimates(accountId)}/{estimateId}", estimate, ct);

    public Task<ApiResult> DeleteEstimateAsync(Guid accountId, Guid estimateId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Estimates(accountId)}/{estimateId}", null, ct);

    // ── File analysis (account-scoped half) ──────────────────────────────────

    public Task<ApiResult<List<ResumableAnalysisSummary>>> GetResumableAnalysisJobsAsync(Guid accountId, CancellationToken ct = default) =>
        api.GetAsync<List<ResumableAnalysisSummary>>($"{Files(accountId)}/analysis/resumable", ct);

    public Task<ApiResult<AnalyzeFileResponse>> AnalyzeFileAsync(
        Guid accountId, Guid fileId, AnalyzeFileRequest request, CancellationToken ct = default) =>
        api.SendAsync<AnalyzeFileResponse>(HttpMethod.Post, $"{Files(accountId)}/{fileId}/analyze", request, ct);

    private static string Files(Guid accountId) => $"{Base}/{accountId}/files";
    private static string SmartTags(Guid accountId) => $"{Base}/{accountId}/smart-tags";
    private static string Terms(Guid accountId) => $"{Base}/{accountId}/terms";
    private static string Estimates(Guid accountId) => $"{Base}/{accountId}/estimates";
}
