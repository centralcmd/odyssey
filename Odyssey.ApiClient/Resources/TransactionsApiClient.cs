using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the transactions endpoints. Replaces the hand-built <c>"api/transactions?…"</c>
/// URLs the pages used to assemble individually — five call sites loaded "every transaction" with
/// three different (and, because <see cref="IOdysseyApi.GetAllAsync{T}"/> owns its own window,
/// inert) limits.
/// </summary>
public interface ITransactionsApiClient
{
    /// <summary>One page of transactions with the full server-side filter surface (issue #277).</summary>
    Task<ApiResult<PagedResult<ExistingTransaction>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? accountIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? direction = null,
        DateTime? from = null,
        DateTime? to = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>
    /// Every matching transaction in one window — for totals, charts and the sections that render an
    /// unpaginated list. Same filter surface as <see cref="ListAsync"/>.
    /// </summary>
    Task<ApiResult<List<ExistingTransaction>>> ListAllAsync(
        string? search = null,
        IReadOnlyCollection<string>? accountIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? direction = null,
        DateTime? from = null,
        DateTime? to = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    /// <summary>Loads one transaction. Null on failure.</summary>
    Task<ExistingTransaction?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The summary rollup behind the page header (issue #372): whole-ledger counts by status and
    /// direction plus the money in / out totals. Unfiltered — the header reflects every transaction
    /// while the grid stays paged, so opening the page no longer downloads the table. Null on failure.
    /// </summary>
    Task<TransactionSummary?> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a transaction. The API returns <c>201</c> with an empty body, so the new id is on
    /// <see cref="ApiResult.CreatedId"/> — which the file-attach flow needs.
    /// </summary>
    Task<ApiResult> CreateAsync(NewTransaction transaction, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, NewTransaction transaction, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);

    // ── Files ────────────────────────────────────────────────────────────────
    // Parent-routed like the account and insurance attachments: a file is only ever addressed
    // through the transaction it belongs to.

    Task<ApiResult<List<ExistingTransactionFile>>> ListFilesAsync(Guid transactionId, CancellationToken ct = default);

    Task<ApiResult> DetachFileAsync(Guid transactionId, Guid fileId, CancellationToken ct = default);
}

/// <inheritdoc cref="ITransactionsApiClient" />
public sealed class TransactionsApiClient(IOdysseyApi api) : ITransactionsApiClient
{
    private const string Base = "api/transactions";

    public Task<ApiResult<PagedResult<ExistingTransaction>>> ListAsync(
        int page,
        int pageSize,
        string? search = null,
        IReadOnlyCollection<string>? accountIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? direction = null,
        DateTime? from = null,
        DateTime? to = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingTransaction>(
            Query(search, accountIds, statuses, tagIds, direction, from, to, sortBy, sortDir)
                .Window(page, pageSize)
                .Build(),
            ct);

    public Task<ApiResult<List<ExistingTransaction>>> ListAllAsync(
        string? search = null,
        IReadOnlyCollection<string>? accountIds = null,
        IReadOnlyCollection<string>? statuses = null,
        IReadOnlyCollection<string>? tagIds = null,
        IReadOnlyCollection<string>? direction = null,
        DateTime? from = null,
        DateTime? to = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetAllAsync<ExistingTransaction>(
            Query(search, accountIds, statuses, tagIds, direction, from, to, sortBy, sortDir).Build(), ct);

    // Direction is a two-value toggle (income/expense): AddSingle filters only when exactly one is
    // selected, so "neither" and "both" correctly mean no filter.
    private static PagedQuery Query(
        string? search,
        IReadOnlyCollection<string>? accountIds,
        IReadOnlyCollection<string>? statuses,
        IReadOnlyCollection<string>? tagIds,
        IReadOnlyCollection<string>? direction,
        DateTime? from,
        DateTime? to,
        string? sortBy,
        string? sortDir) =>
        PagedQuery.For(Base)
            .Add("search", search)
            .AddMany("accountIds", accountIds)
            .AddMany("statuses", statuses)
            .AddMany("tagIds", tagIds)
            .AddSingle("direction", direction)
            .Add("from", from)
            .Add("to", to)
            .Add("sortBy", sortBy)
            .Add("sortDir", sortDir);

    public async Task<ExistingTransaction?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await api.GetAsync<ExistingTransaction>($"{Base}/{id}", ct)).Value;

    public async Task<TransactionSummary?> GetSummaryAsync(CancellationToken ct = default) =>
        (await api.GetAsync<TransactionSummary>($"{Base}/summary", ct)).Value;

    public Task<ApiResult> CreateAsync(NewTransaction transaction, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, transaction, ct);

    public Task<ApiResult> UpdateAsync(Guid id, NewTransaction transaction, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", transaction, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);

    public Task<ApiResult<List<ExistingTransactionFile>>> ListFilesAsync(Guid transactionId, CancellationToken ct = default) =>
        api.GetAsync<List<ExistingTransactionFile>>($"{Base}/{transactionId}/files", ct);

    public Task<ApiResult> DetachFileAsync(Guid transactionId, Guid fileId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{transactionId}/files/{fileId}", null, ct);
}

/// <summary>Typed client for the transaction-tag endpoints.</summary>
public interface ITransactionTagsApiClient
{
    /// <summary>One page of tags for the admin list.</summary>
    Task<ApiResult<PagedResult<ExistingTransactionTag>>> ListAsync(
        int page, int pageSize, string? search = null, string? status = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    /// <summary>Every tag in one window — the tag picker and filter options.</summary>
    Task<ApiResult<List<ExistingTransactionTag>>> ListAllAsync(string? status = null, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewTransactionTag tag, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, NewTransactionTag tag, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="ITransactionTagsApiClient" />
public sealed class TransactionTagsApiClient(IOdysseyApi api) : ITransactionTagsApiClient
{
    private const string Base = "api/transaction-tags";

    public Task<ApiResult<PagedResult<ExistingTransactionTag>>> ListAsync(
        int page, int pageSize, string? search = null, string? status = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingTransactionTag>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .Add("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult<List<ExistingTransactionTag>>> ListAllAsync(string? status = null, CancellationToken ct = default) =>
        api.GetAllAsync<ExistingTransactionTag>(PagedQuery.For(Base).Add("status", status).Build(), ct);

    public Task<ApiResult> CreateAsync(NewTransactionTag tag, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, tag, ct);

    public Task<ApiResult> UpdateAsync(Guid id, NewTransactionTag tag, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", tag, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}
