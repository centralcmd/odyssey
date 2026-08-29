using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

public class TransactionTagService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public TransactionTagService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-side paged list (issue #277): name search + status filter + name/description/status sort.</summary>
    public async Task<PagedResult<ExistingTransactionTag>> ListAsync(
        TransactionTagsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.TransactionTags.AsNoTracking().AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(t => EF.Functions.Like(t.Name, pattern));
        }

        q = query.Status switch
        {
            ArchivalStatus.Archived => q.Where(t => t.Archived != null),
            ArchivalStatus.Active => q.Where(t => t.Archived == null),
            _ => q,
        };

        // Status sorts on the derived archival flag (active before archived when ascending).
        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: true);
        IOrderedQueryable<TransactionTag> sorted = query.SortBy switch
        {
            TransactionTagSortBy.Description => ascending ? q.OrderBy(t => t.Description) : q.OrderByDescending(t => t.Description),
            TransactionTagSortBy.Status => ascending ? q.OrderBy(t => t.Archived != null) : q.OrderByDescending(t => t.Archived != null),
            _ => ascending ? q.OrderBy(t => t.Name) : q.OrderByDescending(t => t.Name),
        };
        q = sorted.ThenBy(t => t.TransactionTagId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, t => t.Adapt<ExistingTransactionTag>(), cancellationToken);
    }

    public async Task<ExistingTransactionTag?> Get(Guid transactionTagId, CancellationToken cancellationToken = default)
    {
        var transactionTag = await context.TransactionTags
            .FirstOrDefaultAsync(tag => tag.TransactionTagId == transactionTagId, cancellationToken);

        return transactionTag?.Adapt<ExistingTransactionTag>();
    }

    public async Task<ExistingTransactionTag> Create(NewTransactionTag newTransactionTag, CancellationToken cancellationToken = default)
    {
        var transactionTag = new TransactionTag
        {
            Name = newTransactionTag.Name,
            Description = newTransactionTag.Description,
            Archived = null,
        };

        context.TransactionTags.Add(transactionTag);
        await context.SaveChangesAsync(cancellationToken);

        return transactionTag.Adapt<ExistingTransactionTag>();
    }

    public async Task<ExistingTransactionTag?> Update(Guid id, NewTransactionTag putTransactionTag, CancellationToken cancellationToken = default)
    {
        var transactionTag = await context.TransactionTags
            .FirstOrDefaultAsync(tag => tag.TransactionTagId == id, cancellationToken);

        if (transactionTag is null)
        {
            return null;
        }

        transactionTag.Name = putTransactionTag.Name;
        transactionTag.Description = putTransactionTag.Description;
        ApplyArchiveTransition(transactionTag, putTransactionTag.Archived);

        await context.SaveChangesAsync(cancellationToken);

        return transactionTag.Adapt<ExistingTransactionTag>();
    }

    public async Task Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var transactionTag = await context.TransactionTags
            .FirstOrDefaultAsync(tag => tag.TransactionTagId == id, cancellationToken);

        if (transactionTag is null)
        {
            return;
        }

        context.TransactionTags.Remove(transactionTag);
        await context.SaveChangesAsync(cancellationToken);
    }
    private void ApplyArchiveTransition(TransactionTag transactionTag, bool requestedArchived)
    {
        var currentArchived = transactionTag.Archived is not null;

        if (!currentArchived && requestedArchived)
        {
            transactionTag.Archived = timeProvider.GetUtcNow().UtcDateTime;
            return;
        }

        if (currentArchived && !requestedArchived)
        {
            transactionTag.Archived = null;
        }
    }

}
