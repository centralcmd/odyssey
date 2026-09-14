using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Journal;
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

    /// <summary>
    /// Server-side paged list (issue #277). Search and the <c>Name</c> sort key operate on the JOINED
    /// tag's text (issue #75): the item has no name of its own, and the query-string spelling stays
    /// <c>Name</c> because it is a wire contract.
    /// </summary>
    public async Task<PagedResult<ExistingBudgetItem>> ListAsync(
        BudgetItemsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.BudgetItems.AsNoTracking().Include(i => i.TransactionTag).AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(i =>
                EF.Functions.Like(i.TransactionTag!.Name, pattern) ||
                (i.TransactionTag!.Description != null && EF.Functions.Like(i.TransactionTag.Description, pattern)));
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
            _ => ascending ? q.OrderBy(i => i.TransactionTag!.Name) : q.OrderByDescending(i => i.TransactionTag!.Name),
        };
        q = sorted.ThenBy(i => i.BudgetItemId);

        return await q.ToPagedResultAsync(
            query.Offset, query.Limit, i => i.Adapt<ExistingBudgetItem>(), cancellationToken);
    }

    public async Task<ExistingBudgetItem?> Get(Guid budgetItemId, CancellationToken cancellationToken = default)
    {
        var budgetItem = await context.BudgetItems
            .Include(i => i.TransactionTag)
            .FirstOrDefaultAsync(l => l.BudgetItemId == budgetItemId, cancellationToken);

        return budgetItem?.Adapt<ExistingBudgetItem>();
    }

    public async Task<ExistingBudgetItem> Create(NewBudgetItem newBudgetItem, CancellationToken cancellationToken = default)
    {
        var tag = await ResolveTagForWrite(newBudgetItem, existing: null, cancellationToken);
        await EnsureTransactionTagIsUniqueWithinBudget(
            newBudgetItem.BudgetId,
            newBudgetItem.TransactionTagId,
            null, cancellationToken);

        var budgetItem = new BudgetItem
        {
            BudgetId = newBudgetItem.BudgetId,
            CategoryType = newBudgetItem.CategoryType.Adapt<ContextBudgetCategoryType>(),
            PlannedAmount = newBudgetItem.PlannedAmount,
            TransactionTagId = newBudgetItem.TransactionTagId,
        };

        context.BudgetItems.Add(budgetItem);
        await SaveGuardingDuplicateTag(newBudgetItem.TransactionTagId, cancellationToken);

        return Project(budgetItem, tag);
    }

    public async Task<ExistingBudgetItem?> Update(Guid id, NewBudgetItem putBudgetItem, CancellationToken cancellationToken = default)
    {
        var budgetItem = await context.BudgetItems.FirstOrDefaultAsync(e => e.BudgetItemId == id, cancellationToken);
        if (budgetItem is null)
        {
            return null;
        }

        var tag = await ResolveTagForWrite(putBudgetItem, budgetItem, cancellationToken);
        await EnsureTransactionTagIsUniqueWithinBudget(
            putBudgetItem.BudgetId,
            putBudgetItem.TransactionTagId,
            id, cancellationToken);

        budgetItem.BudgetId = putBudgetItem.BudgetId;
        budgetItem.CategoryType = putBudgetItem.CategoryType.Adapt<ContextBudgetCategoryType>();
        budgetItem.PlannedAmount = putBudgetItem.PlannedAmount;
        budgetItem.TransactionTagId = putBudgetItem.TransactionTagId;

        await SaveGuardingDuplicateTag(putBudgetItem.TransactionTagId, cancellationToken);

        return Project(budgetItem, tag);
    }

    /// <summary>
    /// The write paths' read model. The tag is grafted onto the DTO rather than onto the entity's
    /// navigation: the write entity is TRACKED, and assigning it a detached <see cref="TransactionTag"/>
    /// would present that row to the change tracker as an insert on the next save.
    /// </summary>
    private static ExistingBudgetItem Project(BudgetItem budgetItem, TransactionTag tag)
    {
        var projected = budgetItem.Adapt<ExistingBudgetItem>();
        projected.Tag = tag.Adapt<ExistingTransactionTag>();
        return projected;
    }

    /// <summary>
    /// The tag a write names must exist, and must not be archived — unless the item already carries
    /// that tag <b>and</b> stays in the same budget (issue #75 §9). A <c>PUT</c> may change
    /// <c>BudgetId</c>, and moving an archived tag into a budget that never planned for it is a new
    /// link, not a retained one.
    /// </summary>
    /// <remarks>
    /// Checked in the service rather than left to the foreign key, because the EF InMemory provider
    /// enforces no foreign keys at all — this is the only implementation the fast tiers run, and it is
    /// what turns the failure into an explaining <c>400</c> rather than a <c>500</c>.
    /// </remarks>
    private async Task<TransactionTag> ResolveTagForWrite(
        NewBudgetItem write, BudgetItem? existing, CancellationToken cancellationToken)
    {
        if (write.TransactionTagId == Guid.Empty)
        {
            throw new DomainValidationException(
                "Choose a transaction tag.", code: null, field: nameof(NewBudgetItem.TransactionTagId));
        }

        var tag = await context.TransactionTags
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TransactionTagId == write.TransactionTagId, cancellationToken);

        if (tag is null)
        {
            throw new DomainValidationException(
                $"Transaction tag '{write.TransactionTagId}' does not exist.",
                code: null, field: nameof(NewBudgetItem.TransactionTagId));
        }

        var keepsExistingLink = existing is not null
            && existing.TransactionTagId == write.TransactionTagId
            && existing.BudgetId == write.BudgetId;

        if (tag.Archived is not null && !keepsExistingLink)
        {
            throw new DomainValidationException(
                $"Transaction tag '{write.TransactionTagId}' is archived and cannot be planned for.",
                code: null, field: nameof(NewBudgetItem.TransactionTagId));
        }

        return tag;
    }

    /// <summary>
    /// The in-memory half of "one item per tag per budget". The unique index added in issue #75 is the
    /// other half and does not make this redundant: the EF InMemory provider enforces no unique index
    /// across a relationship of this shape, so this is the only implementation the fast tiers run, and
    /// it is what turns the violation into an explaining, field-keyed <c>409</c>.
    /// </summary>
    private async Task EnsureTransactionTagIsUniqueWithinBudget(Guid budgetId, Guid transactionTagId, Guid? budgetItemIdToIgnore, CancellationToken cancellationToken = default)
    {
        var duplicateExists = await context.BudgetItems
            .AnyAsync(item =>
                item.BudgetId == budgetId
                && item.TransactionTagId == transactionTagId
                && item.BudgetItemId != budgetItemIdToIgnore, cancellationToken);

        if (!duplicateExists)
        {
            return;
        }

        throw DuplicateTagInBudget(transactionTagId);
    }

    /// <summary>
    /// Saves, translating the unique index's duplicate-key error into the <b>same</b> field-keyed
    /// <c>409</c> the pre-check throws, so the race and the pre-check are indistinguishable to a
    /// client (issue #75 §9). Without this the concurrent loser would get a generic conflict the form
    /// could not attach to a control.
    /// </summary>
    private async Task SaveGuardingDuplicateTag(Guid transactionTagId, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // The failed write is still tracked; leaving it would flush on the next save.
            context.ChangeTracker.Clear();
            throw DuplicateTagInBudget(transactionTagId);
        }
    }

    // Names the tag's GUID and never its name: GUIDs are non-enumerable and the domain is
    // single-tenant, so this is not a meaningful existence oracle (issue #75 §10.6).
    private static DomainConflictException DuplicateTagInBudget(Guid transactionTagId) =>
        new($"Transaction tag '{transactionTagId}' is already used by another budget item in this budget.",
            nameof(NewBudgetItem.TransactionTagId));

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
