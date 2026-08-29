using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Context = Odyssey.Context;

namespace Odyssey.Core.Finance;

public class TransactionService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;

    public TransactionService(OdysseyContext context, IContactLookup contactLookup, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }
    
    /// <summary>Server-side paged list (issue #277): search + account/status/tag/direction/date filters + allowlisted sort.</summary>
    public async Task<PagedResult<ExistingTransaction>> ListAsync(
        TransactionsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.Transactions
            .AsNoTracking()
            .Include(transaction => transaction.Account)
            .Include(transaction => transaction.TransactionTags)
            .Include(transaction => transaction.TransactionFiles)
            .ThenInclude(tf => tf.FileMetadata)
            .AsSplitQuery()
            .AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            var contactMatchIds = (await contactLookup.SearchIdsByNameAsync(term, cancellationToken)).ToHashSet();
            q = q.Where(t =>
                EF.Functions.Like(t.Description, pattern) ||
                (t.Account != null && EF.Functions.Like(t.Account.Name, pattern)) ||
                (t.ContactId != null && contactMatchIds.Contains(t.ContactId.Value)) ||
                t.TransactionTags.Any(tag => EF.Functions.Like(tag.Name, pattern)));
        }

        if (query.AccountIds is { Length: > 0 } accountIds)
        {
            q = q.Where(t => accountIds.Contains(t.AccountId));
        }

        if (query.Statuses is { Length: > 0 } statuses)
        {
            q = q.Where(t => statuses.Contains(t.Status));
        }

        if (query.TagIds is { Length: > 0 } tagIds)
        {
            q = q.Where(t => t.TransactionTags.Any(tag => tagIds.Contains(tag.TransactionTagId)));
        }

        q = query.Direction switch
        {
            TransactionDirection.Income => q.Where(t => t.Amount >= 0),
            TransactionDirection.Expense => q.Where(t => t.Amount < 0),
            _ => q,
        };

        if (query.From is { } from)
        {
            q = q.Where(t => t.TimeStamp >= from);
        }
        if (query.To is { } to)
        {
            q = q.Where(t => t.TimeStamp <= to);
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is TransactionSortBy.Desc or TransactionSortBy.Contact or TransactionSortBy.Account or TransactionSortBy.Status);
        IOrderedQueryable<Transaction> sorted = query.SortBy switch
        {
            TransactionSortBy.Amount => ascending ? q.OrderBy(t => t.Amount) : q.OrderByDescending(t => t.Amount),
            TransactionSortBy.Desc => ascending ? q.OrderBy(t => t.Description) : q.OrderByDescending(t => t.Description),
            // NOTE: contact-name sort is still id-order. It degraded when Contact moved to its own
            // context and a name sort stopped being expressible in SQL; merging the contexts makes it
            // expressible again, but restoring it changes result ordering, so it belongs to its own
            // change rather than to the merge. Nulls-last is preserved.
            TransactionSortBy.Contact => ascending
                ? q.OrderBy(t => t.ContactId == null).ThenBy(t => t.ContactId)
                : q.OrderBy(t => t.ContactId == null).ThenByDescending(t => t.ContactId),
            TransactionSortBy.Account => ascending
                ? q.OrderBy(t => t.Account!.Name) : q.OrderByDescending(t => t.Account!.Name),
            TransactionSortBy.Status => ascending ? q.OrderBy(t => t.Status) : q.OrderByDescending(t => t.Status),
            _ => ascending ? q.OrderBy(t => t.TimeStamp) : q.OrderByDescending(t => t.TimeStamp),
        };
        q = sorted.ThenBy(t => t.TransactionId);

        var result = await q.ToPagedResultAsync(query.Offset, query.Limit, t => t.Adapt<ExistingTransaction>(), cancellationToken);
        await EnrichContactsAsync(result.Items, cancellationToken);
        return result;
    }

    /// <summary>
    /// Summary rollup for the page header (issue #372): status buckets, direction buckets and the
    /// money in/out totals, aggregated in SQL. Unfiltered by design — the header reflects the whole
    /// ledger while the grid stays paged, which is the point of not fetching every row to count them.
    /// </summary>
    public async Task<TransactionSummary> GetSummary(CancellationToken cancellationToken = default)
    {
        var byStatus = await context.Transactions
            .AsNoTracking()
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Grouping on the sign keeps this to a single GROUP BY that every provider translates,
        // rather than conditional SUMs over the whole table.
        var byDirection = await context.Transactions
            .AsNoTracking()
            .GroupBy(t => t.Amount >= 0)
            .Select(g => new { IsIncome = g.Key, Count = g.Count(), Total = g.Sum(t => t.Amount) })
            .ToListAsync(cancellationToken);

        var counts = new TransactionStatusCounts();
        foreach (var row in byStatus)
        {
            switch (row.Status)
            {
                case TransactionStatus.New: counts.New += row.Count; break;
                case TransactionStatus.Approved: counts.Approved += row.Count; break;
                case TransactionStatus.Flagged: counts.Flagged += row.Count; break;
            }
        }

        var income = byDirection.FirstOrDefault(r => r.IsIncome);
        var expense = byDirection.FirstOrDefault(r => !r.IsIncome);

        return new TransactionSummary
        {
            TotalTransactions = byStatus.Sum(r => r.Count),
            CountsByStatus = counts,
            IncomeCount = income?.Count ?? 0,
            ExpenseCount = expense?.Count ?? 0,
            TotalIn = income?.Total ?? 0m,
            TotalOut = Math.Abs(expense?.Total ?? 0m),
        };
    }

    // The Contact navigation was removed when Contact moved to OdysseyContext, so Mapster can no longer
    // populate ExistingTransaction.Contact from the entity graph. Resolve the full contacts cross-context
    // by id and attach them to the projected DTOs.
    private async Task EnrichContactsAsync(IReadOnlyCollection<ExistingTransaction> transactions, CancellationToken cancellationToken)
    {
        var contactIds = transactions
            .Where(t => t.ContactId is not null)
            .Select(t => t.ContactId!.Value)
            .Distinct()
            .ToList();

        if (contactIds.Count == 0)
        {
            return;
        }

        var contacts = await contactLookup.ResolveContactsAsync(contactIds, cancellationToken);
        foreach (var transaction in transactions)
        {
            if (transaction.ContactId is { } contactId)
            {
                transaction.Contact = contacts.GetValueOrDefault(contactId);
            }
        }
    }
    
    public async Task<ExistingTransaction?> Get(Guid transactionId, CancellationToken cancellationToken = default)
    {
        var transaction = await context.Transactions
            .AsNoTracking()
            .Include(t => t.Account)
            .Include(t => t.TransactionTags)
            .Include(t => t.TransactionFiles)
            .ThenInclude(tf => tf.FileMetadata)
            .AsSplitQuery()
            .FirstOrDefaultAsync(l => l.TransactionId == transactionId, cancellationToken);
        var dto = transaction.Adapt<ExistingTransaction>();
        if (dto is not null)
        {
            await EnrichContactsAsync([dto], cancellationToken);
        }
        return dto;
    }
    
    public async Task<ExistingTransaction> Create(NewTransaction newTransaction, CancellationToken cancellationToken = default)
    {
        var normalizedCurrencyCode = CurrencyValidationService.Normalize(newTransaction.CurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(context, normalizedCurrencyCode, nameof(newTransaction.CurrencyCode));
        await EnsureAccountIsValid(newTransaction.AccountId, normalizedCurrencyCode, cancellationToken);

        var tags = await ResolveTags(newTransaction.TransactionTagIds, cancellationToken);

        var transaction = new Transaction
        {
            Description = newTransaction.Description,
            TimeStamp = newTransaction.TimeStamp is { } timeStamp
                ? DateTimeNormalization.NormalizeToUtc(timeStamp)
                : timeProvider.GetUtcNow().UtcDateTime,
            Amount = newTransaction.Amount,
            AccountId = newTransaction.AccountId,
            ContactId = newTransaction.ContactId,
            ExternalId = newTransaction.ExternalId,
            InternalId = newTransaction.InternalId,
            ExtraData = newTransaction.ExtraData,
            Status = newTransaction.Status,
            StatusComment = newTransaction.StatusComment,
            StatusChangedAt = timeProvider.GetUtcNow().UtcDateTime,
            CurrencyCode = normalizedCurrencyCode,
            TransactionTags = tags,
        };

        await EnsureContactIsValid(newTransaction.ContactId, cancellationToken);

        context.Transactions.Add(transaction);
        await context.SaveChangesAsync(cancellationToken);

        return (await Get(transaction.TransactionId, cancellationToken))!;
    }
    
    public async Task<ExistingTransaction?> Update(Guid id, NewTransaction putTransaction, CancellationToken cancellationToken = default)
    {
        var transaction = await context.Transactions
            .Include(e => e.TransactionTags)
            .FirstOrDefaultAsync(e => e.TransactionId == id, cancellationToken);
        if (transaction is null)
        {
            return null;
        }

        var normalizedCurrencyCode = CurrencyValidationService.Normalize(putTransaction.CurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(context, normalizedCurrencyCode, nameof(putTransaction.CurrencyCode));

        transaction.Description = putTransaction.Description;
        transaction.Amount = putTransaction.Amount;
        transaction.TimeStamp = putTransaction.TimeStamp is { } timeStamp
            ? DateTimeNormalization.NormalizeToUtc(timeStamp)
            : timeProvider.GetUtcNow().UtcDateTime;
        transaction.AccountId = putTransaction.AccountId;
        transaction.CurrencyCode = normalizedCurrencyCode;
        await EnsureAccountIsValid(putTransaction.AccountId, normalizedCurrencyCode, cancellationToken);
        await EnsureContactIsValid(putTransaction.ContactId, cancellationToken);

        await ReconcileTags(transaction, putTransaction.TransactionTagIds, cancellationToken);
        transaction.ContactId = putTransaction.ContactId;
        transaction.ExternalId = putTransaction.ExternalId;
        transaction.InternalId = putTransaction.InternalId;
        transaction.ExtraData = putTransaction.ExtraData;

        var statusChanged = transaction.Status != putTransaction.Status;
        transaction.Status = putTransaction.Status;
        transaction.StatusComment = putTransaction.StatusComment;

        if (statusChanged)
        {
            transaction.StatusChangedAt = timeProvider.GetUtcNow().UtcDateTime;
        }

        await context.SaveChangesAsync(cancellationToken);

        return (await Get(transaction.TransactionId, cancellationToken))!;
    }

    private async Task EnsureAccountIsValid(Guid accountId, string transactionCurrencyCode, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
        if (account is null)
        {
            throw new DomainValidationException($"Account ID {accountId} was not found.");
        }

        if (account.Closed is not null || account.Archived is not null)
        {
            throw new DomainValidationException("Transactions cannot be added to a closed or archived account.");
        }

        if (account.CurrencyCode != transactionCurrencyCode)
        {
            throw new DomainValidationException("Transaction currency must match account currency.");
        }
    }
    
    // Resolve a set of requested tag ids to tracked TransactionTag entities, validating that each one
    // exists and is not archived. Duplicate ids are de-duplicated; an empty/null request yields no tags.
    private async Task<List<TransactionTag>> ResolveTags(IEnumerable<Guid>? tagIds, CancellationToken cancellationToken = default)
    {
        var distinctIds = tagIds?.Distinct().ToList() ?? [];
        if (distinctIds.Count == 0)
        {
            return [];
        }

        var tags = await context.TransactionTags
            .Where(tag => distinctIds.Contains(tag.TransactionTagId) && tag.Archived == null)
            .ToListAsync(cancellationToken);

        var missing = distinctIds.Except(tags.Select(tag => tag.TransactionTagId)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainValidationException(
                $"Transaction tag ID(s) {string.Join(", ", missing)} are invalid or archived.");
        }

        return tags;
    }

    // Diff the requested tag set against the transaction's current tags, inserting missing links and
    // removing dropped ones (rather than delete-all-then-reinsert, which churns the join rows).
    private async Task ReconcileTags(Transaction transaction, IEnumerable<Guid>? tagIds, CancellationToken cancellationToken = default)
    {
        var desiredTags = await ResolveTags(tagIds, cancellationToken);
        var desiredIds = desiredTags.Select(tag => tag.TransactionTagId).ToHashSet();
        var currentIds = transaction.TransactionTags.Select(tag => tag.TransactionTagId).ToHashSet();

        foreach (var removed in transaction.TransactionTags.Where(tag => !desiredIds.Contains(tag.TransactionTagId)).ToList())
        {
            transaction.TransactionTags.Remove(removed);
        }

        foreach (var added in desiredTags.Where(tag => !currentIds.Contains(tag.TransactionTagId)))
        {
            transaction.TransactionTags.Add(added);
        }
    }

    private async Task EnsureContactIsValid(Guid? contactId, CancellationToken cancellationToken = default)
    {
        if (contactId is null)
        {
            return;
        }

        var refs = await contactLookup.ResolveRefsAsync([contactId.Value], cancellationToken);
        var isValidContact = refs.TryGetValue(contactId.Value, out var contactRef) && contactRef.Archived == null;

        if (!isValidContact)
        {
            throw new DomainValidationException($"Contact ID {contactId} is invalid or archived.");
        }
    }

    public async Task Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var transaction = await context.Transactions.FirstOrDefaultAsync(e => e.TransactionId == id, cancellationToken);
        if (transaction is null)
        {
            return;
        }
        
        context.Transactions.Remove(transaction);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TransactionFile?> AttachFileToTransaction(Guid transactionId, Guid fileId, string userId, Context.TransactionFileType type = Context.TransactionFileType.Other, CancellationToken cancellationToken = default)
    {
        var transaction = await context.Transactions.FirstOrDefaultAsync(t => t.TransactionId == transactionId, cancellationToken);
        if (transaction is null)
        {
            throw new DomainNotFoundException($"Transaction with ID {transactionId} was not found.");
        }

        var existingAssociation = await context.TransactionFiles.FirstOrDefaultAsync(tf =>
            tf.TransactionId == transactionId
            && tf.FileMetadataId == fileId, cancellationToken);

        if (existingAssociation is not null)
        {
            if (existingAssociation.Type != type)
            {
                existingAssociation.Type = type;
                await context.SaveChangesAsync(cancellationToken);
            }
            return existingAssociation;
        }

        var transactionFile = new TransactionFile
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileMetadataId = fileId,
            AttachedByUserId = userId,
            AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            Type = type
        };

        context.TransactionFiles.Add(transactionFile);
        await context.SaveChangesAsync(cancellationToken);

        return transactionFile;
    }

    public async Task<TransactionFile?> DetachFileFromTransaction(Guid transactionId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var transaction = await context.Transactions.FirstOrDefaultAsync(t => t.TransactionId == transactionId, cancellationToken);
        if (transaction is null)
        {
            return null;
        }

        var association = await context.TransactionFiles
            .FirstOrDefaultAsync(tf => tf.TransactionId == transactionId && tf.FileMetadataId == fileId, cancellationToken);
        
        if (association is not null)
        {
            context.TransactionFiles.Remove(association);
            await context.SaveChangesAsync(cancellationToken);
        }

        return association;
    }
}