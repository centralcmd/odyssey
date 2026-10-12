using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// The contract-scoped smart-tag transaction match (issue #226). A transaction belongs to a contract's
/// <em>Smart tags</em> section only when ALL of these hold:
/// <list type="number">
///   <item><b>R1 · tag</b> — it carries at least one of the contract's smart tags (archived tags included);</item>
///   <item><b>R2 · scope</b> — it is dated inside the contract's smart-tag scope (issue #308), a date
///   range stored on the contract independently of its term (<see cref="ComputeWindow"/>);</item>
///   <item><b>R3 · merchant</b> — its contact holds a party role on the contract (any role, any party
///   dates, any contact type).</item>
/// </list>
///
/// <para>
/// The rule is declared here, once, server-side: R2 and R3 are contract-domain rules, and composing them
/// in the client would copy a server rule into the WASM app. R1 and R3 are <b>sub-queries</b> over the
/// contract's own rows, never materialised id arrays — both caps are admin-editable (smart tags up to
/// 50, parties up to 100 000), so a parameter list is not a safe shape.
/// </para>
///
/// <para>
/// Search, sort, paging and the projection are <see cref="TransactionService.ListFilteredAsync"/> — the
/// same pipeline <c>GET /api/transactions</c> uses — so the two lists cannot drift.
/// </para>
/// </summary>
public class ContractSmartTagTransactionService
{
    private readonly OdysseyContext context;
    private readonly TransactionService transactionService;

    public ContractSmartTagTransactionService(OdysseyContext context, TransactionService transactionService)
    {
        this.context = context;
        this.transactionService = transactionService;
    }

    /// <summary>
    /// Returns the matched transactions for one contract, paged, with the scope applied and a
    /// per-currency summary of the whole matched set; <c>null</c> if the contract does not exist.
    /// </summary>
    public async Task<ContractSmartTagTransactionsResult?> ListAsync(
        Guid contractId,
        ContractSmartTagTransactionsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts
            .AsNoTracking()
            .Where(c => c.ContractId == contractId)
            .Select(c => new { c.SmartTagScopeStart, c.SmartTagScopeEnd })
            .FirstOrDefaultAsync(cancellationToken);
        if (contract is null)
            return null;

        var smartTagCount = await context.ContractSmartTags
            .CountAsync(s => s.ContractId == contractId, cancellationToken);
        var partyContactCount = await context.ContractParties
            .Where(p => p.ContractId == contractId && p.ContactId != null)
            .Select(p => p.ContactId)
            .Distinct()
            .CountAsync(cancellationToken);

        var (from, toExclusive) = ComputeWindow(contract.SmartTagScopeStart, contract.SmartTagScopeEnd);
        // InvalidTerm (3) is never emitted: CK_Contracts_SmartTagScopeOrder makes an inverted scope
        // unrepresentable, and the term no longer takes part in the match.
        var emptyReason = smartTagCount == 0 ? ContractSmartTagEmptyReason.NoSmartTags
            : partyContactCount == 0 ? ContractSmartTagEmptyReason.NoContactParties
            : ContractSmartTagEmptyReason.None;

        var scope = new ContractSmartTagScope
        {
            From = from,
            ToExclusive = toExclusive,
            SmartTagCount = smartTagCount,
            PartyContactCount = partyContactCount,
            EmptyReason = emptyReason,
        };

        if (emptyReason != ContractSmartTagEmptyReason.None)
        {
            var (offset, limit) = ListQuery.ResolveWindow(query.Offset, query.Limit);
            return new ContractSmartTagTransactionsResult
            {
                Scope = scope,
                Summary = new ContractSmartTagSummary(),
                Page = new PagedResult<ExistingTransaction> { Offset = offset, Limit = limit },
            };
        }

        // Both scope rules are uncorrelated IN (sub-query) shapes, which MariaDB executes as semi-joins
        // driven from the small contract-side tables rather than as a per-row EXISTS probe.
        var smartTagIds = context.ContractSmartTags
            .Where(s => s.ContractId == contractId)
            .Select(s => s.TransactionTagId);
        var taggedTransactionIds = context.Set<TransactionTagLink>()
            .Where(link => smartTagIds.Contains(link.TransactionTagId))
            .Select(link => link.TransactionId);
        var partyContactIds = context.ContractParties
            .Where(p => p.ContractId == contractId && p.ContactId != null)
            .Select(p => p.ContactId);

        var matched = context.Transactions.Where(t =>
            taggedTransactionIds.Contains(t.TransactionId) &&
            t.ContactId != null &&
            partyContactIds.Contains(t.ContactId));
        if (from is { } lower)
            matched = matched.Where(t => t.TimeStamp >= lower);
        if (toExclusive is { } upper)
            matched = matched.Where(t => t.TimeStamp < upper);

        var summary = await SummarizeAsync(matched, cancellationToken);
        var page = await transactionService.ListFilteredAsync(
            matched, query.Search, query.SortBy, query.SortDir, query.Offset, query.Limit, cancellationToken);

        return new ContractSmartTagTransactionsResult { Scope = scope, Summary = summary, Page = page };
    }

    /// <summary>
    /// The R2 window — the single definition, read from the contract's smart-tag scope and never from
    /// its term (issue #308). The scope is stored as UTC calendar days; the lower bound is that day's
    /// midnight, and the upper bound is EXCLUSIVE at the midnight after the end day so the whole end
    /// day is included — deliberately not the inclusive <c>to</c> filter of <c>GET /api/transactions</c>.
    /// A null bound is open on that side.
    /// </summary>
    public static (DateTime? From, DateTime? ToExclusive) ComputeWindow(DateTime? scopeStart, DateTime? scopeEnd)
    {
        DateTime? from = scopeStart is { } s ? DateTime.SpecifyKind(PeriodBounds.InclusiveStart(s), DateTimeKind.Utc) : null;
        DateTime? toExclusive = scopeEnd is { } e ? DateTime.SpecifyKind(PeriodBounds.ExclusiveEnd(e), DateTimeKind.Utc) : null;
        return (from, toExclusive);
    }

    // Grouping on (currency, sign) keeps this to one GROUP BY every provider translates — the shape
    // TransactionService.GetSummary uses — rather than conditional SUMs. The fold and the ordinal sort
    // run in memory so InMemory and MariaDB collations cannot order the rows differently.
    private static async Task<ContractSmartTagSummary> SummarizeAsync(
        IQueryable<Transaction> matched, CancellationToken cancellationToken)
    {
        var groups = await matched
            .AsNoTracking()
            .GroupBy(t => new { t.CurrencyCode, IsIncome = t.Amount >= 0 })
            .Select(g => new { g.Key.CurrencyCode, g.Key.IsIncome, Count = g.Count(), Total = g.Sum(t => t.Amount) })
            .ToListAsync(cancellationToken);

        var byCurrency = groups
            .GroupBy(g => g.CurrencyCode, StringComparer.Ordinal)
            .Select(currency =>
            {
                var totalIn = currency.Where(g => g.IsIncome).Sum(g => g.Total);
                var totalOut = Math.Abs(currency.Where(g => !g.IsIncome).Sum(g => g.Total));
                return new ContractSmartTagCurrencyTotal
                {
                    CurrencyCode = currency.Key,
                    TransactionCount = currency.Sum(g => g.Count),
                    TotalIn = totalIn,
                    TotalOut = totalOut,
                    Net = totalIn - totalOut,
                };
            })
            .OrderBy(row => row.CurrencyCode, StringComparer.Ordinal)
            .ToList();

        return new ContractSmartTagSummary
        {
            TransactionCount = byCurrency.Sum(row => row.TransactionCount),
            ByCurrency = byCurrency,
        };
    }
}
