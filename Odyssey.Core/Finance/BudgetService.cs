using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Context = Odyssey.Context;

namespace Odyssey.Core.Finance;

public class BudgetService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;

    public BudgetService(OdysseyContext context, IContactLookup contactLookup, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-side paged list (issue #277): name search + allowlisted sort; counts computed over the page slice.</summary>
    public async Task<PagedResult<ExistingBudget>> ListAsync(
        BudgetsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.Budgets.AsNoTracking().Include(b => b.BudgetItems).AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(b => EF.Functions.Like(b.Name, pattern));
        }

        q = query.Status switch
        {
            ArchivalStatus.Archived => q.Where(b => b.Archived != null),
            ArchivalStatus.Active => q.Where(b => b.Archived == null),
            _ => q,
        };

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is BudgetSortBy.Name);
        IOrderedQueryable<Budget> sorted = query.SortBy switch
        {
            BudgetSortBy.Name => ascending ? q.OrderBy(b => b.Name) : q.OrderByDescending(b => b.Name),
            BudgetSortBy.EndDate => ascending ? q.OrderBy(b => b.EndDate) : q.OrderByDescending(b => b.EndDate),
            _ => ascending ? q.OrderBy(b => b.StartDate) : q.OrderByDescending(b => b.StartDate),
        };
        q = sorted.ThenBy(b => b.BudgetId);

        var (safeOffset, safeLimit) = ListQuery.ResolveWindow(query.Offset, query.Limit);
        var totalCount = await q.CountAsync(cancellationToken);

        var budgets = await q
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        var counts = await ComputeTransactionCounts(budgets, cancellationToken);
        var dtos = budgets.Adapt<List<ExistingBudget>>();
        foreach (var dto in dtos)
            dto.TransactionCount = counts.GetValueOrDefault(dto.BudgetId);

        return new PagedResult<ExistingBudget>
        {
            Items = dtos,
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = totalCount,
        };
    }

    /// <summary>
    /// Summary rollup for the page header (issue #372): how many budgets are live, and their combined
    /// planned balance. Two SQL aggregates — no budget (let alone budget item) rows are materialised.
    /// </summary>
    public async Task<BudgetSummary> GetSummary(CancellationToken cancellationToken = default)
    {
        var byStatus = await context.Budgets
            .AsNoTracking()
            .GroupBy(b => b.Archived != null)
            .Select(g => new { IsArchived = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Planned income minus planned expenses, over the live budgets only — the archived ones are
        // excluded from the header figure exactly as they are from the count.
        var plannedByCategory = await context.BudgetItems
            .AsNoTracking()
            .Where(i => i.Budget!.Archived == null)
            .GroupBy(i => i.CategoryType)
            .Select(g => new { Category = g.Key, Total = g.Sum(i => i.PlannedAmount) })
            .ToListAsync(cancellationToken);

        decimal PlannedFor(Context.BudgetCategoryType category) =>
            plannedByCategory.FirstOrDefault(row => row.Category == category)?.Total ?? 0m;

        var archivedCount = byStatus.FirstOrDefault(row => row.IsArchived)?.Count ?? 0;
        var activeCount = byStatus.FirstOrDefault(row => !row.IsArchived)?.Count ?? 0;

        return new BudgetSummary
        {
            TotalBudgets = activeCount + archivedCount,
            ActiveCount = activeCount,
            ArchivedCount = archivedCount,
            PlannedBalance = PlannedFor(Context.BudgetCategoryType.Income) - PlannedFor(Context.BudgetCategoryType.Expense),
        };
    }

    public async Task<ExistingBudget?> Get(Guid budgetId, CancellationToken cancellationToken = default)
    {
        var budget = await context.Budgets
            .AsNoTracking()
            .Include(b => b.BudgetItems)
            .FirstOrDefaultAsync(l => l.BudgetId == budgetId, cancellationToken);
        if (budget is null)
        {
            return null;
        }

        var counts = await ComputeTransactionCounts([budget], cancellationToken);

        var dto = budget.Adapt<ExistingBudget>();
        dto.TransactionCount = counts.GetValueOrDefault(budget.BudgetId);
        return dto;
    }

    // Count transactions matching each budget's item tags, base currency and date range.
    // One transaction query covers the whole set; per-budget filtering is done in memory.
    private async Task<Dictionary<Guid, int>> ComputeTransactionCounts(IReadOnlyCollection<Budget> budgets, CancellationToken cancellationToken = default)
    {
        var counts = budgets.ToDictionary(b => b.BudgetId, _ => 0);

        var allTagIds = budgets
            .SelectMany(b => b.BudgetItems)
            .Where(i => i.TransactionTagId != null)
            .Select(i => i.TransactionTagId!.Value)
            .Distinct()
            .ToList();

        if (allTagIds.Count == 0)
        {
            return counts;
        }

        var minDate = budgets.Min(b => b.StartDate);
        var maxDate = budgets.Max(b => b.EndDate);

        // One row per (transaction, matching tag) link. A multi-tagged transaction yields several rows,
        // so the per-budget count must de-duplicate by transaction id to avoid double-counting.
        var candidates = await context.TransactionTagLinks
            .Where(link => allTagIds.Contains(link.TransactionTagId))
            .Where(link => link.Transaction!.TimeStamp >= minDate && link.Transaction.TimeStamp <= maxDate)
            .Select(link => new
            {
                link.TransactionId,
                link.TransactionTagId,
                link.Transaction!.TimeStamp,
                link.Transaction.CurrencyCode,
            })
            .ToListAsync(cancellationToken);

        foreach (var budget in budgets)
        {
            var tagIds = budget.BudgetItems
                .Where(i => i.TransactionTagId != null)
                .Select(i => i.TransactionTagId!.Value)
                .ToHashSet();

            if (tagIds.Count == 0)
            {
                continue;
            }

            counts[budget.BudgetId] = candidates
                .Where(c =>
                    tagIds.Contains(c.TransactionTagId)
                    && c.TimeStamp >= budget.StartDate
                    && c.TimeStamp <= budget.EndDate
                    && c.CurrencyCode == budget.BaseCurrencyCode)
                .Select(c => c.TransactionId)
                .Distinct()
                .Count();
        }

        return counts;
    }

    public async Task<ExistingBudget> Create(NewBudget newBudget, CancellationToken cancellationToken = default)
    {
        await CurrencyValidationService.EnsureSupportedAndActive(context, newBudget.BaseCurrencyCode, nameof(newBudget.BaseCurrencyCode));

        var budget = new Budget
        {
            Name = newBudget.Name,
            Description = newBudget.Description,
            StartDate = DateTimeNormalization.NormalizeToUtc(newBudget.StartDate),
            EndDate = DateTimeNormalization.NormalizeToUtc(newBudget.EndDate),
            Archived = null,
            BaseCurrencyCode = CurrencyValidationService.Normalize(newBudget.BaseCurrencyCode),
        };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync(cancellationToken);

        return budget.Adapt<ExistingBudget>();
    }

    public async Task<ExistingBudget?> Update(Guid id, NewBudget putBudget, CancellationToken cancellationToken = default)
    {
        var budget = await context.Budgets.FirstOrDefaultAsync(e => e.BudgetId == id, cancellationToken);
        if (budget is null)
        {
            return null;
        }

        var normalizedCurrencyCode = CurrencyValidationService.Normalize(putBudget.BaseCurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(context, normalizedCurrencyCode, nameof(putBudget.BaseCurrencyCode));

        budget.Name = putBudget.Name;
        budget.Description = putBudget.Description;
        budget.StartDate = DateTimeNormalization.NormalizeToUtc(putBudget.StartDate);
        budget.EndDate = DateTimeNormalization.NormalizeToUtc(putBudget.EndDate);
        budget.BaseCurrencyCode = normalizedCurrencyCode;
        ApplyArchiveTransition(budget, putBudget.Archived);

        await context.SaveChangesAsync(cancellationToken);

        return budget.Adapt<ExistingBudget>();
    }

    public async Task Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var budget = await context.Budgets.FirstOrDefaultAsync(e => e.BudgetId == id, cancellationToken);
        if (budget is null)
        {
            return;
        }

        context.Budgets.Remove(budget);
        await context.SaveChangesAsync(cancellationToken);
    }
    
    public async Task<BudgetReport?> GetTransactions(Guid budgetId, CancellationToken cancellationToken = default)
    {
        var budget = await context.Budgets
            .AsNoTracking()
            .Include(b => b.BudgetItems)
            .ThenInclude(i => i.TransactionTag)
            .FirstOrDefaultAsync(l => l.BudgetId == budgetId, cancellationToken);
        
        if (budget is null)
        {
            return null;
        }
        
        var transactionTags = budget.BudgetItems
            .Where(i => i.TransactionTagId != null)
            .Select(i => i.TransactionTag)
            .ToList();
        
        var transactionTagIds = transactionTags
            .Select(i => i!.TransactionTagId)
            .ToList();
        
        // Any-of match through the join: a transaction belongs to the report if it carries at least one
        // of the budget's item tags. Each transaction appears once in the de-duplicated list below.
        var allTransactions = await context.Transactions
            .AsNoTracking()
            .Include(t => t.Account)
            .Include(t => t.TransactionTags)
            .Where(t => t.TransactionTags.Any(tag => transactionTagIds.Contains(tag.TransactionTagId)))
            .Where(t => t.TimeStamp >= budget.StartDate && t.TimeStamp <= budget.EndDate)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var transactions = allTransactions
            .Where(t => t.CurrencyCode == budget.BaseCurrencyCode)
            .ToList();

        var excludedTransactions = allTransactions
            .Where(t => t.CurrencyCode != budget.BaseCurrencyCode)
            .ToList();

        // One bucket per budget tag, summing each carrying transaction's full amount (no proportional
        // splitting in v1). A multi-tagged transaction contributes to every matching bucket, so the
        // sum of buckets can legitimately exceed the de-duplicated transaction total.
        var sums = new List<ExistingTransactionReport>();
        foreach (var transactionTag in transactionTags)
        {
            sums.Add(new ExistingTransactionReport
            {
                ExistingTransactionTag = transactionTag.Adapt<ExistingTransactionTag>()!,
                Sum = transactions
                    .Where(t => t.TransactionTags.Any(tag => tag.TransactionTagId == transactionTag!.TransactionTagId))
                    .Sum(t => t.Amount)
            });
        }

        var transactionDtos = transactions.Adapt<List<ExistingTransaction>>();

        var contactIds = transactions
            .Where(t => t.ContactId != null)
            .Select(t => t.ContactId!.Value)
            .Distinct()
            .ToList();
        if (contactIds.Count > 0)
        {
            var contacts = await contactLookup.ResolveContactsAsync(contactIds, cancellationToken);
            foreach (var dto in transactionDtos)
            {
                if (dto.ContactId is { } contactId && contacts.TryGetValue(contactId, out var contact))
                {
                    dto.Contact = contact;
                }
            }
        }

        return new BudgetReport
        {
            Transactions = transactionDtos,
            ExistingTransactionReport = sums,
            CurrencyCode = budget.BaseCurrencyCode,
            ExcludedTransactionCount = excludedTransactions.Count,
            ExcludedCurrencies = excludedTransactions
                .GroupBy(t => t.CurrencyCode)
                .ToDictionary(group => group.Key, group => group.Count()),
        };
    }
    private void ApplyArchiveTransition(Budget budget, bool requestedArchived)
    {
        var currentArchived = budget.Archived is not null;

        if (!currentArchived && requestedArchived)
        {
            budget.Archived = timeProvider.GetUtcNow().UtcDateTime;
            return;
        }

        if (currentArchived && !requestedArchived)
        {
            budget.Archived = null;
        }
    }

}
