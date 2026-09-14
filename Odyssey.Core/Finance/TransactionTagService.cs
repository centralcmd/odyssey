using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Core.Journal;
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
        await EnsureNameIsUnique(newTransactionTag.Name, null, cancellationToken);

        var transactionTag = new TransactionTag
        {
            Name = newTransactionTag.Name,
            Description = newTransactionTag.Description,
            Archived = null,
        };

        context.TransactionTags.Add(transactionTag);
        await SaveGuardingDuplicateName(newTransactionTag.Name, cancellationToken);

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

        await EnsureNameIsUnique(putTransactionTag.Name, id, cancellationToken);

        transactionTag.Name = putTransactionTag.Name;
        transactionTag.Description = putTransactionTag.Description;
        ApplyArchiveTransition(transactionTag, putTransactionTag.Archived);

        await SaveGuardingDuplicateName(putTransactionTag.Name, cancellationToken);

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

        // A tag planned for by a budget item cannot be deleted (issue #75 §7.9). The RESTRICT key says
        // the same on MariaDB, but its violation reaches GlobalExceptionHandler as a generic 409 naming
        // no surface — and the EF InMemory tiers enforce no foreign keys at all, so there the delete
        // would simply succeed. This pre-check is what makes the refusal explain itself, and what makes
        // it happen on every tier. It names a COUNT and not the budgets: naming them would reach past
        // transactions.tags.delete's own boundary.
        var plannedFor = await context.BudgetItems
            .CountAsync(item => item.TransactionTagId == id, cancellationToken);

        if (plannedFor > 0)
        {
            throw new DomainConflictException(
                $"This tag is planned for by {plannedFor} budget item{(plannedFor == 1 ? "" : "s")}. "
                + "Remove those items on the Budgets page first.");
        }

        context.TransactionTags.Remove(transactionTag);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A tag name is unique case-insensitively across ALL tags, archived included (issue #75 §5.11) —
    /// it is an identity now, naming every budget item that plans for the tag, so two same-named tags
    /// would render as two indistinguishable budget rows.
    /// </summary>
    /// <remarks>
    /// The comparison is <see cref="StringComparison.OrdinalIgnoreCase"/> rather than a database
    /// collation, so it holds on the EF InMemory tiers, which have no collation at all. The unique
    /// index is the other half; this is what turns the violation into an explaining, field-keyed
    /// <c>409</c>.
    /// </remarks>
    private async Task EnsureNameIsUnique(string name, Guid? transactionTagIdToIgnore, CancellationToken cancellationToken)
    {
        var candidate = (name ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            return;
        }

        // Materialised rather than compared in SQL: EF cannot translate OrdinalIgnoreCase, and the tag
        // table is small reference data the pickers already load whole.
        var clash = (await context.TransactionTags
                .AsNoTracking()
                .Where(tag => tag.TransactionTagId != transactionTagIdToIgnore)
                .Select(tag => new { tag.TransactionTagId, tag.Name, tag.Archived })
                .ToListAsync(cancellationToken))
            .FirstOrDefault(tag => string.Equals(tag.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (clash is null)
        {
            return;
        }

        throw DuplicateName(clash.Name, clash.Archived is not null);
    }

    /// <summary>
    /// Saves, translating the unique index's duplicate-key error into the <b>same</b> field-keyed
    /// <c>409</c> the pre-check throws, so the concurrent loser of a race is not handed a conflict the
    /// form cannot attach to a control.
    /// </summary>
    private async Task SaveGuardingDuplicateName(string name, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // The failed write is still tracked; leaving it would flush on the next save. The winner's
            // archived state is not known here, so the message takes the plain form.
            context.ChangeTracker.Clear();
            throw DuplicateName((name ?? string.Empty).Trim(), archived: false);
        }
    }

    // Keyed to `name` so the tag dialogs render it at the field rather than as an unattributed toast.
    // Naming the archived case matters: an archived clash has no inline remedy — restoring or renaming
    // the archived tag is an action on the transaction tags page.
    private static DomainConflictException DuplicateName(string name, bool archived) =>
        new(archived
                ? $"A tag called '{name}' already exists but is archived. Restore it, or rename it, to reuse the name."
                : $"A tag called '{name}' already exists. Pick a different name.",
            nameof(NewTransactionTag.Name));
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
