using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using ContextBudgetCategoryType = Odyssey.Context.BudgetCategoryType;

namespace Odyssey.Core.Finance;

public class BudgetItemService
{
    private readonly OdysseyContext context;

    public BudgetItemService(OdysseyContext context)
    {
        this.context = context;
    }

    /// <summary>Server-side paged list (issue #277): name/description search + budget/category filters + allowlisted sort.</summary>
    public async Task<PagedResult<ExistingBudgetItem>> ListAsync(
        BudgetItemsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.BudgetItems.AsNoTracking().AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(i =>
                EF.Functions.Like(i.Name, pattern) ||
                (i.Description != null && EF.Functions.Like(i.Description, pattern)));
        }

        if (query.BudgetId is { } budgetId)
        {
            q = q.Where(i => i.BudgetId == budgetId);
        }

        var categoryFilter = (query.Categories ?? []).Select(c => c.Adapt<ContextBudgetCategoryType>()).ToList();
        if (categoryFilter.Count > 0)
        {
            q = q.Where(i => categoryFilter.Contains(i.CategoryType));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or BudgetItemSortBy.Name or BudgetItemSortBy.Category);
        IOrderedQueryable<BudgetItem> sorted = query.SortBy switch
        {
            BudgetItemSortBy.PlannedAmount => ascending ? q.OrderBy(i => i.PlannedAmount) : q.OrderByDescending(i => i.PlannedAmount),
            BudgetItemSortBy.Category => ascending ? q.OrderBy(i => i.CategoryType) : q.OrderByDescending(i => i.CategoryType),
            _ => ascending ? q.OrderBy(i => i.Name) : q.OrderByDescending(i => i.Name),
        };
        q = sorted.ThenBy(i => i.BudgetItemId);

        return await q.ToPagedResultAsync(
            query.Offset, query.Limit, i => i.Adapt<ExistingBudgetItem>(), cancellationToken);
    }

    public async Task<ExistingBudgetItem?> Get(Guid budgetItemId, CancellationToken cancellationToken = default)
    {
        var budgetItem = await context.BudgetItems.FirstOrDefaultAsync(l => l.BudgetItemId == budgetItemId, cancellationToken);
        return budgetItem?.Adapt<ExistingBudgetItem>();
    }

    public async Task<ExistingBudgetItem> Create(NewBudgetItem newBudgetItem, CancellationToken cancellationToken = default)
    {
        await EnsureTransactionTagIsUniqueWithinBudget(
            newBudgetItem.BudgetId,
            newBudgetItem.TransactionTagId,
            null, cancellationToken);

        var budgetItem = new BudgetItem
        {
            BudgetId = newBudgetItem.BudgetId,
            Name = newBudgetItem.Name,
            Description = newBudgetItem.Description,
            CategoryType = newBudgetItem.CategoryType.Adapt<ContextBudgetCategoryType>(),
            PlannedAmount = newBudgetItem.PlannedAmount,
            TransactionTagId = newBudgetItem.TransactionTagId,
        };

        context.BudgetItems.Add(budgetItem);
        await context.SaveChangesAsync(cancellationToken);

        return budgetItem.Adapt<ExistingBudgetItem>();
    }

    public async Task<ExistingBudgetItem?> Update(Guid id, NewBudgetItem putBudgetItem, CancellationToken cancellationToken = default)
    {
        var budgetItem = await context.BudgetItems.FirstOrDefaultAsync(e => e.BudgetItemId == id, cancellationToken);
        if (budgetItem is null)
        {
            return null;
        }

        await EnsureTransactionTagIsUniqueWithinBudget(
            putBudgetItem.BudgetId,
            putBudgetItem.TransactionTagId,
            id, cancellationToken);

        budgetItem.BudgetId = putBudgetItem.BudgetId;
        budgetItem.Name = putBudgetItem.Name;
        budgetItem.Description = putBudgetItem.Description;
        budgetItem.CategoryType = putBudgetItem.CategoryType.Adapt<ContextBudgetCategoryType>();
        budgetItem.PlannedAmount = putBudgetItem.PlannedAmount;
        budgetItem.TransactionTagId = putBudgetItem.TransactionTagId;

        await context.SaveChangesAsync(cancellationToken);

        return budgetItem.Adapt<ExistingBudgetItem>();
    }

    private async Task EnsureTransactionTagIsUniqueWithinBudget(Guid budgetId, Guid? transactionTagId, Guid? budgetItemIdToIgnore, CancellationToken cancellationToken = default)
    {
        if (transactionTagId is null)
        {
            return;
        }

        var duplicateExists = await context.BudgetItems
            .AnyAsync(item =>
                item.BudgetId == budgetId
                && item.TransactionTagId == transactionTagId
                && item.BudgetItemId != budgetItemIdToIgnore, cancellationToken);

        if (!duplicateExists)
        {
            return;
        }

        throw new DomainConflictException(
            $"Transaction tag '{transactionTagId}' is already used by another budget item in this budget.");
    }

    public async Task Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var budgetItem = await context.BudgetItems.FirstOrDefaultAsync(e => e.BudgetItemId == id, cancellationToken);
        if (budgetItem is null)
        {
            return;
        }

        context.BudgetItems.Remove(budgetItem);
        await context.SaveChangesAsync(cancellationToken);
    }
}
