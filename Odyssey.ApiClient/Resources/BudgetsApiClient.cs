using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the budgets endpoints and their per-budget transaction report. Budget items are a
/// sibling resource with its own route (<c>api/budget-items</c>) rather than a nested one, so they get
/// their own client — see <see cref="IBudgetItemsApiClient"/>.
/// </summary>
public interface IBudgetsApiClient
{
    Task<ApiResult<PagedResult<ExistingBudget>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? status = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    Task<ApiResult<List<ExistingBudget>>> ListAllAsync(CancellationToken ct = default);

    Task<ApiResult<ExistingBudget>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The summary rollup behind the page header (issue #372): active / archived counts and the
    /// combined planned balance, so the header no longer costs a whole-table fetch. Null on failure.
    /// </summary>
    Task<BudgetSummary?> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// The budget's actuals — per-tag transaction sums for its period. Lazily loaded when a budget row
    /// is expanded, so failure degrades to "no actuals" rather than blocking the list.
    /// </summary>
    Task<ApiResult<BudgetReport>> GetReportAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult> CreateAsync(NewBudget budget, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, NewBudget budget, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IBudgetsApiClient" />
public sealed class BudgetsApiClient(IOdysseyApi api) : IBudgetsApiClient
{
    private const string Base = "api/budgets";

    public Task<ApiResult<PagedResult<ExistingBudget>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? status = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingBudget>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .AddSingle("status", status)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult<List<ExistingBudget>>> ListAllAsync(CancellationToken ct = default) =>
        api.GetAllAsync<ExistingBudget>(PagedQuery.For(Base).Build(), ct);

    public Task<ApiResult<ExistingBudget>> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<ExistingBudget>($"{Base}/{id}", ct);

    public async Task<BudgetSummary?> GetSummaryAsync(CancellationToken ct = default) =>
        (await api.GetAsync<BudgetSummary>($"{Base}/summary", ct)).Value;

    public Task<ApiResult<BudgetReport>> GetReportAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<BudgetReport>($"{Base}/{id}/transactions", ct);

    public Task<ApiResult> CreateAsync(NewBudget budget, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, budget, ct);

    public Task<ApiResult> UpdateAsync(Guid id, NewBudget budget, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", budget, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}

/// <summary>
/// Typed client for the budget-item endpoints. Items are read as part of their parent budget, so this
/// carries only the writes — adding a list method here would be unused surface.
/// </summary>
public interface IBudgetItemsApiClient
{
    Task<ApiResult> CreateAsync(NewBudgetItem item, CancellationToken ct = default);

    Task<ApiResult> UpdateAsync(Guid id, NewBudgetItem item, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <inheritdoc cref="IBudgetItemsApiClient" />
public sealed class BudgetItemsApiClient(IOdysseyApi api) : IBudgetItemsApiClient
{
    private const string Base = "api/budget-items";

    public Task<ApiResult> CreateAsync(NewBudgetItem item, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, Base, item, ct);

    public Task<ApiResult> UpdateAsync(Guid id, NewBudgetItem item, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{id}", item, ct);

    public Task<ApiResult> DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{id}", null, ct);
}
