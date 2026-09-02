using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using ContextAccountFileType = Odyssey.Context.AccountFileType;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextTermKind = Odyssey.Context.TermKind;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoAccountFileType = Odyssey.Dtos.Finance.AccountFileType;
using DtoTermKind = Odyssey.Dtos.Finance.TermKind;
using DtoTermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using DtoBillingPeriod = Odyssey.Dtos.Finance.BillingPeriod;

namespace Odyssey.Core.Finance;

public class AccountService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;

    public AccountService(OdysseyContext context, IContactLookup contactLookup, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Maps the slim, cross-context custodian reference onto the description-free Custodian DTO. Only the
    // Custodian columns are ever carried across — the omitted Contact.Description/Notes can never leak
    // onto the account read path (§6 / data minimisation).
    private static Custodian ToCustodian(ContactRef r) => new Custodian
    {
        ContactId = r.ContactId,
        Name = r.Name,
        NormalizedName = r.NormalizedName,
        Type = r.Type,
        OrganizationNumber = r.OrganizationNumber,
        Archived = r.Archived,
    };
    
    /// <summary>
    /// Server-side paged list (issue #277): search + type/status filters + allowlisted sort. The
    /// <c>balance</c> and <c>txnCount</c> keys are expressed as correlated subqueries so the ordering
    /// happens in SQL <b>before</b> the page slice (they were previously aggregated post-materialisation).
    /// </summary>
    public async Task<PagedResult<ExistingAccount>> ListAsync(
        AccountsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.Accounts.AsNoTracking().AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(a =>
                EF.Functions.Like(a.Name, pattern) ||
                EF.Functions.Like(a.Description, pattern) ||
                (a.AccountNumber != null && EF.Functions.Like(a.AccountNumber, pattern)));
        }

        var typeFilter = (query.Types ?? []).Select(t => t.Adapt<ContextAccountType>()).ToList();
        if (typeFilter.Count > 0)
        {
            q = q.Where(a => typeFilter.Contains(a.AccountType));
        }

        // Status is derived from the Archived/Closed date columns; translate the requested set to a
        // predicate over those columns (Open = neither set, Closed = closed but not archived).
        if (query.Statuses is { Length: > 0 } statuses)
        {
            var wantOpen = statuses.Contains(AccountStatus.Open);
            var wantClosed = statuses.Contains(AccountStatus.Closed);
            var wantArchived = statuses.Contains(AccountStatus.Archived);
            q = q.Where(a =>
                (wantArchived && a.Archived != null) ||
                (wantClosed && a.Archived == null && a.Closed != null) ||
                (wantOpen && a.Archived == null && a.Closed == null));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or AccountSortBy.Name or AccountSortBy.Type);
        IOrderedQueryable<Account> sorted = query.SortBy switch
        {
            AccountSortBy.Balance => ascending
                ? q.OrderBy(a => a.Transactions.Sum(t => (decimal?)t.Amount) ?? 0m)
                : q.OrderByDescending(a => a.Transactions.Sum(t => (decimal?)t.Amount) ?? 0m),
            AccountSortBy.TxnCount => ascending
                ? q.OrderBy(a => a.Transactions.Count())
                : q.OrderByDescending(a => a.Transactions.Count()),
            AccountSortBy.Type => ascending ? q.OrderBy(a => a.AccountType) : q.OrderByDescending(a => a.AccountType),
            AccountSortBy.Opened => ascending ? q.OrderBy(a => a.Opened) : q.OrderByDescending(a => a.Opened),
            _ => ascending ? q.OrderBy(a => a.Name) : q.OrderByDescending(a => a.Name),
        };
        q = sorted.ThenBy(a => a.AccountId);

        var totalCount = await q.CountAsync(cancellationToken);
        var (safeOffset, safeLimit) = ListQuery.ResolveWindow(query.Offset, query.Limit);

        var accounts = await q
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        var dtos = accounts.Adapt<List<ExistingAccount>>();
        await EnrichAccountsAsync(accounts, dtos, cancellationToken);

        return new PagedResult<ExistingAccount>
        {
            Items = dtos,
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = totalCount,
        };
    }

    /// <summary>
    /// Summary rollup for the page header (issue #372): status/type counts, the value aggregates, and
    /// the per-account allocation rows the donuts render. Unfiltered by design — the header reflects
    /// every account while the list stays filtered.
    /// </summary>
    /// <remarks>
    /// Two round-trips regardless of account count: a lean projection (no navigations, balance as a
    /// correlated subquery) plus the batched in-force estimate lookup. Nothing the page doesn't plot —
    /// custodians, badge counts, rate terms, descriptions — is fetched.
    /// </remarks>
    public async Task<AccountSummary> GetSummary(CancellationToken cancellationToken = default)
    {
        var rows = await context.Accounts
            .AsNoTracking()
            .Select(a => new
            {
                a.AccountId,
                a.Name,
                a.AccountType,
                a.CurrencyCode,
                a.Closed,
                a.Archived,
                Balance = a.Transactions.Sum(t => (decimal?)t.Amount) ?? 0m,
            })
            .ToListAsync(cancellationToken);

        // The net-worth replace policy (issue #182 §9): an in-force estimate supersedes the
        // transaction balance, so the header agrees with each row's headline figure.
        var estimates = await GetCurrentEstimates([.. rows.Select(r => r.AccountId)], cancellationToken);

        var counts = new AccountStatusCounts();
        var byType = new Dictionary<DtoAccountType, int>();
        var allocations = new List<AccountAllocation>();
        decimal assets = 0m, liabilities = 0m;

        foreach (var row in rows)
        {
            if (row.Archived is not null)
            {
                counts.Archived++;
                // Archived accounts are counted above and excluded from everything below.
                continue;
            }

            if (row.Closed is not null)
            {
                counts.Closed++;
            }
            else
            {
                counts.Open++;
            }

            var dtoType = row.AccountType.Adapt<DtoAccountType>();
            byType[dtoType] = byType.GetValueOrDefault(dtoType) + 1;

            var value = estimates.TryGetValue(row.AccountId, out var estimate) ? estimate.Value : row.Balance;
            if (value > 0)
            {
                assets += value;
            }
            else if (value < 0)
            {
                liabilities += value;
            }

            if (value != 0)
            {
                allocations.Add(new AccountAllocation
                {
                    AccountId = row.AccountId,
                    Name = row.Name,
                    CurrencyCode = row.CurrencyCode,
                    Value = value,
                });
            }
        }

        return new AccountSummary
        {
            TotalAccounts = rows.Count,
            CountsByStatus = counts,
            CountsByType = byType
                .OrderBy(kv => kv.Key)
                .Select(kv => new AccountTypeCount { Type = kv.Key, Count = kv.Value })
                .ToList(),
            CombinedValue = assets + liabilities,
            TotalAssets = assets,
            TotalLiabilities = liabilities,
            Allocations = [.. allocations.OrderByDescending(a => a.Value)],
        };
    }

    /// <summary>Populate per-account aggregates (balance, counts, current rate/estimate, custodian) over a materialised page.</summary>
    private async Task EnrichAccountsAsync(
        IReadOnlyList<Account> accounts, IReadOnlyList<ExistingAccount> dtos, CancellationToken cancellationToken)
    {
        // Populate per-account transaction counts and balances with a single grouped
        // query rather than including (and materializing) every transaction row.
        var accountIds = accounts.Select(a => a.AccountId).ToList();
        var aggregates = await context.Transactions
            .Where(t => accountIds.Contains(t.AccountId))
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count(), Balance = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x, cancellationToken);

        // Populate per-account file counts with a single grouped query rather than
        // including (and materializing) every account-file row.
        var fileCounts = await context.AccountFiles
            .Where(af => accountIds.Contains(af.AccountId))
            .GroupBy(af => af.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);

        // Per-account estimate / term / smart-tag counts for the row-header badges — one grouped
        // query each, same shape as the file counts above.
        var estimateCounts = await context.AccountEstimates
            .Where(e => accountIds.Contains(e.AccountId))
            .GroupBy(e => e.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);

        var termCounts = await context.AccountTerms
            .Where(t => accountIds.Contains(t.AccountId))
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);

        var smartTagCounts = await context.AccountSmartTags
            .Where(s => accountIds.Contains(s.AccountId))
            .GroupBy(s => s.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);

        // Resolve the in-force rate (interest rate, else expected return) per account with one
        // query over the term composite index, for the account-header subtitle.
        var currentTermsByAccount = await GetCurrentTerms(accountIds, cancellationToken);

        // Resolve the in-force estimated value per account with one query over the estimate
        // composite index, for the account-header headline value (no per-account follow-up call).
        var estimateByAccount = await GetCurrentEstimates(accountIds, cancellationToken);

        // Resolve every linked custodian on the page with a single batched lookup over the distinct
        // non-null custodian ids (keyed by the Contact PK) — no N+1, and only the slim ContactRef
        // fields cross the context boundary (Description never leaves the journal context).
        var custodianIds = accounts
            .Where(a => a.CustodianId is not null)
            .Select(a => a.CustodianId!.Value)
            .Distinct()
            .ToList();
        var custodianRefsById = custodianIds.Count == 0
            ? (IReadOnlyDictionary<Guid, ContactRef>)new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(custodianIds, cancellationToken);

        foreach (var dto in dtos)
        {
            if (dto.CustodianId is { } custodianId && custodianRefsById.TryGetValue(custodianId, out var custodianRef))
            {
                dto.Custodian = ToCustodian(custodianRef);
            }

            if (aggregates.TryGetValue(dto.AccountId, out var agg))
            {
                dto.TransactionCount = agg.Count;
                dto.Balance = agg.Balance;
            }

            if (fileCounts.TryGetValue(dto.AccountId, out var fileCount))
            {
                dto.FileCount = fileCount;
            }

            dto.EstimateCount = estimateCounts.GetValueOrDefault(dto.AccountId);
            dto.TermCount = termCounts.GetValueOrDefault(dto.AccountId);
            dto.SmartTagCount = smartTagCounts.GetValueOrDefault(dto.AccountId);

            if (currentTermsByAccount.TryGetValue(dto.AccountId, out var currentTerms))
            {
                dto.CurrentTerms = [.. currentTerms.Select(ToCurrentTerm)];
                if (RateTermOf(currentTerms) is { } rateTerm)
                {
                    dto.CurrentInterestRate = rateTerm.Value;
                    dto.CurrentInterestRateKind = rateTerm.TermKind.Adapt<DtoTermKind>();
                }
            }

            if (estimateByAccount.TryGetValue(dto.AccountId, out var estimate))
            {
                dto.CurrentEstimatedValue = estimate.Value;
                dto.CurrentEstimatedValueCurrencyCode = estimate.CurrencyCode;
                dto.CurrentEstimatedValueEffectiveFrom = estimate.EffectiveFrom;
            }
        }
    }
    
    public async Task<ExistingAccount?> Get(Guid accountId, CancellationToken cancellationToken = default)
    {
        // Materialise the account together with its badge counts and balance in a single round-trip
        // (correlated subqueries over the navigation collections) rather than one query per aggregate.
        var projection = await context.Accounts
            .AsNoTracking()
            .Where(l => l.AccountId == accountId)
            .Select(a => new
            {
                Account = a,
                TransactionCount = a.Transactions.Count(),
                Balance = a.Transactions.Sum(t => (decimal?)t.Amount) ?? 0m,
                FileCount = a.AccountFiles.Count(),
                EstimateCount = a.AccountEstimates.Count(),
                TermCount = a.AccountTerms.Count(),
                SmartTagCount = a.SmartTags.Count(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (projection is null)
            return null;

        var account = projection.Account;
        var dto = account.Adapt<ExistingAccount>();
        dto.TransactionCount = projection.TransactionCount;
        dto.Balance = projection.Balance;
        dto.FileCount = projection.FileCount;
        dto.EstimateCount = projection.EstimateCount;
        dto.TermCount = projection.TermCount;
        dto.SmartTagCount = projection.SmartTagCount;

        var currentTermsByAccount = await GetCurrentTerms([accountId], cancellationToken);
        if (currentTermsByAccount.TryGetValue(accountId, out var currentTerms))
        {
            dto.CurrentTerms = [.. currentTerms.Select(ToCurrentTerm)];
            if (RateTermOf(currentTerms) is { } rateTerm)
            {
                dto.CurrentInterestRate = rateTerm.Value;
                dto.CurrentInterestRateKind = rateTerm.TermKind.Adapt<DtoTermKind>();
            }
        }

        var estimateByAccount = await GetCurrentEstimates([accountId], cancellationToken);
        if (estimateByAccount.TryGetValue(accountId, out var estimate))
        {
            dto.CurrentEstimatedValue = estimate.Value;
            dto.CurrentEstimatedValueCurrencyCode = estimate.CurrencyCode;
            dto.CurrentEstimatedValueEffectiveFrom = estimate.EffectiveFrom;
        }

        dto.Custodian = await ResolveCustodian(account.CustodianId, cancellationToken);

        return dto;
    }

    /// <summary>Resolves the slim <see cref="Custodian"/> for a single custodian id, or <c>null</c> when
    /// the id is null or no longer references a contact. Resolved via <see cref="IContactLookup"/> so only
    /// the slim ContactRef fields cross the context boundary (Description never leaves the journal context).</summary>
    private async Task<Custodian?> ResolveCustodian(Guid? custodianId, CancellationToken cancellationToken = default)
    {
        if (custodianId is null)
            return null;

        var id = custodianId.Value;
        var refs = await contactLookup.ResolveRefsAsync([id], cancellationToken);
        return refs.TryGetValue(id, out var custodianRef) ? ToCustodian(custodianRef) : null;
    }

    /// <summary>
    /// Validates a custodian link that is being <em>set or changed</em> to a non-null value: the
    /// referenced contact must exist and must not be archived. A <c>null</c> id (clearing the
    /// link) is always allowed; callers must short-circuit a no-op resave (incoming == persisted)
    /// before calling this so an already-linked custodian archived <em>after</em> linking is not
    /// re-validated (§9 archived-on-change rule).
    /// </summary>
    private async Task ValidateCustodianTarget(Guid? custodianId, CancellationToken cancellationToken = default)
    {
        if (custodianId is null)
            return;

        var id = custodianId.Value;
        var refs = await contactLookup.ResolveRefsAsync([id], cancellationToken);

        if (!refs.TryGetValue(id, out var custodian))
            throw new DomainValidationException($"Contact with ID {id} was not found.");

        if (custodian.Archived is not null)
            throw new DomainValidationException($"Contact with ID {id} is archived and cannot be set as a custodian.");
    }

    /// <summary>
    /// Resolves the currently-effective rate term for each of the given accounts: the latest
    /// <see cref="ContextTermKind.InterestRate"/> entry on or before now, or the latest
    /// <see cref="ContextTermKind.ExpectedReturn"/> if there is no interest rate. Returns only
    /// accounts that have a rate in force. Backs the account-header rate subtitle.
    /// </summary>
    /// <summary>
    /// The in-force terms per account — one per <see cref="ContextTermKind"/>, ordered by the registry's
    /// own kind order so the card's Current band reads the same way on every account.
    ///
    /// <para>
    /// This is the query that used to fetch the rate terms alone. Widening it from two kinds to all of
    /// them is what feeds the record card's Current band, and it stays <b>one</b> query over the term
    /// composite index across every account on the page — the alternative, a per-account follow-up, is
    /// the N+1 the whole enrichment exists to avoid.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, List<AccountTerm>>> GetCurrentTerms(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken = default)
    {
        if (accountIds.Count == 0)
            return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var terms = await context.AccountTerms
            .AsNoTracking()
            .Where(t => accountIds.Contains(t.AccountId) && t.EffectiveFrom <= now)
            .ToListAsync(cancellationToken);

        return terms
            .GroupBy(t => t.AccountId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(t => t.TermKind)
                    .Select(byKind => byKind.MostEffective()!)
                    .OrderBy(t => t.TermKind)
                    .ToList());
    }

    /// <summary>
    /// The single rate the collapsed row headlines on, picked out of the in-force set: interest rate
    /// wins over expected return when both apply (registry order).
    /// </summary>
    private static AccountTerm? RateTermOf(IReadOnlyList<AccountTerm> currentTerms) =>
        currentTerms.FirstOrDefault(t => t.TermKind == ContextTermKind.InterestRate)
        ?? currentTerms.FirstOrDefault(t => t.TermKind == ContextTermKind.ExpectedReturn);

    private static AccountCurrentTerm ToCurrentTerm(AccountTerm term) => new()
    {
        TermKind = term.TermKind.Adapt<DtoTermKind>(),
        ValueUnit = term.ValueUnit.Adapt<DtoTermValueUnit>(),
        Value = term.Value,
        CurrencyCode = term.CurrencyCode,
        BillingPeriod = term.BillingPeriod?.Adapt<DtoBillingPeriod>(),
        EffectiveFrom = term.EffectiveFrom,
    };

    /// <summary>
    /// Resolves the currently-effective estimate for each of the given accounts: the latest entry on
    /// or before now (tie-broken by greatest <c>CreatedAtUtc</c>). Returns only accounts that have an
    /// estimate in force. Backs the account-header headline value and the net-worth fold-in.
    /// </summary>
    private async Task<Dictionary<Guid, AccountEstimate>> GetCurrentEstimates(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken = default)
    {
        if (accountIds.Count == 0)
            return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var estimates = await context.AccountEstimates
            .AsNoTracking()
            .Where(e => accountIds.Contains(e.AccountId) && e.EffectiveFrom <= now)
            .ToListAsync(cancellationToken);

        return estimates
            .GroupBy(e => e.AccountId)
            .Select(group => group.MostEffective()!)
            .ToDictionary(e => e.AccountId, e => e);
    }

    /// <summary>
    /// Returns the files attached to the given account, or <c>null</c> if the account does not exist.
    /// Backs <c>GET /api/accounts/{id}/files</c> now that <see cref="ExistingAccount"/> no longer
    /// embeds the file collection.
    /// </summary>
    public async Task<IList<ExistingAccountFile>?> GetAccountFiles(Guid accountId, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var files = await context.AccountFiles
            .AsNoTracking()
            .Include(af => af.FileMetadata)
            .Where(af => af.AccountId == accountId)
            .ToListAsync(cancellationToken);

        return files.Adapt<List<ExistingAccountFile>>();
    }

    /// <summary>
    /// Returns the transactions belonging to the given account, or <c>null</c> if the account does
    /// not exist. Backs <c>GET /api/accounts/{id}/transactions</c>. The <see cref="ExistingTransaction.Account"/>
    /// back-reference is cleared to avoid returning (and serializing) a circular account graph.
    /// </summary>
    public async Task<IList<ExistingTransaction>?> GetTransactions(Guid accountId, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var transactions = await context.Transactions
            .AsNoTracking()
            .Include(t => t.TransactionTags)
            .Include(t => t.TransactionFiles)
                .ThenInclude(tf => tf.FileMetadata)
            .Where(t => t.AccountId == accountId)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var dtos = transactions.Adapt<List<ExistingTransaction>>();

        // Contact moved to OdysseyContext: resolve the full contact for each transaction via the lookup
        // (was an EF navigation include) in one batched call.
        var contactIds = dtos.Where(d => d.ContactId.HasValue).Select(d => d.ContactId!.Value).Distinct().ToList();
        if (contactIds.Count > 0)
        {
            var contacts = await contactLookup.ResolveContactsAsync(contactIds, cancellationToken);
            foreach (var dto in dtos.Where(d => d.ContactId.HasValue))
            {
                dto.Contact = contacts.GetValueOrDefault(dto.ContactId!.Value);
            }
        }

        foreach (var dto in dtos)
        {
            dto.Account = null;
        }

        return dtos;
    }
    
    public async Task<ExistingAccount> Create(NewAccount newAccount, CancellationToken cancellationToken = default)
    {
        await CurrencyValidationService.EnsureSupportedAndActive(context, newAccount.CurrencyCode, nameof(newAccount.CurrencyCode));

        // On create there is no persisted value, so any non-null custodian is a "set" and is fully
        // validated (exists + not archived). Only the scalar id is bound — the nested custodian
        // object is response-only and never read from the request (over-posting guard).
        await ValidateCustodianTarget(newAccount.CustodianId, cancellationToken);

        var account = new Account
        {
            Description = newAccount.Description,
            Name = newAccount.Name,
            AccountNumber = newAccount.AccountNumber,
            AccountType = newAccount.AccountType.Adapt<ContextAccountType>(),
            Opened = newAccount.Opened ?? timeProvider.GetUtcNow().UtcDateTime,
            Closed = newAccount.Closed,
            Archived = null,
            CurrencyCode = CurrencyValidationService.Normalize(newAccount.CurrencyCode),
            CustodianId = newAccount.CustodianId,
        };

        context.Accounts.Add(account);
        await context.SaveChangesAsync(cancellationToken);

        var dto = account.Adapt<ExistingAccount>();
        dto.Custodian = await ResolveCustodian(account.CustodianId, cancellationToken);
        return dto;
    }
    
    public async Task<ExistingAccount?> Update(Guid id, NewAccount putAccount, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts
            .FirstOrDefaultAsync(e => e.AccountId == id, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var normalizedCurrencyCode = CurrencyValidationService.Normalize(putAccount.CurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(context, normalizedCurrencyCode, nameof(putAccount.CurrencyCode));

        if (account.CurrencyCode != normalizedCurrencyCode)
        {
            // An estimate is always stored in the account currency (see AccountEstimate), and net-worth
            // totals convert it with the account's rate — so changing the currency would silently
            // reinterpret existing amounts. Block the change while either source value exists.
            if (await context.Transactions.AnyAsync(t => t.AccountId == id, cancellationToken))
            {
                throw new DomainValidationException("Account currency cannot be changed when account has transactions.");
            }
            if (await context.AccountEstimates.AnyAsync(e => e.AccountId == id, cancellationToken))
            {
                throw new DomainValidationException("Account currency cannot be changed when account has value estimates.");
            }
        }

        // Archived-on-change rule (§9): only validate the custodian when the incoming id is an actual
        // change from the persisted value. A no-op resave (incoming == persisted) is never
        // re-validated, so editing an account whose already-linked custodian was archived after
        // linking still succeeds; clearing (null) is always allowed.
        if (putAccount.CustodianId != account.CustodianId)
        {
            await ValidateCustodianTarget(putAccount.CustodianId, cancellationToken);
        }

        account.Description = putAccount.Description;
        account.Name = putAccount.Name;
        account.AccountNumber = putAccount.AccountNumber;
        account.AccountType = putAccount.AccountType.Adapt<ContextAccountType>();
        account.Opened = putAccount.Opened ?? timeProvider.GetUtcNow().UtcDateTime;
        account.Closed = putAccount.Closed;
        account.CurrencyCode = normalizedCurrencyCode;
        account.CustodianId = putAccount.CustodianId;
        ApplyArchiveTransition(account, putAccount.Archived);

        await context.SaveChangesAsync(cancellationToken);

        var dto = account.Adapt<ExistingAccount>();
        dto.Custodian = await ResolveCustodian(account.CustodianId, cancellationToken);
        return dto;
    }
    
    public async Task Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts.FirstOrDefaultAsync(e => e.AccountId == id, cancellationToken);
        if (account is null)
        {
            return;
        }

        // The insured-account link rows cascade with the account in MariaDB. Removed explicitly here as
        // well because the EF InMemory provider enforces no foreign keys at all, so without this the
        // fast test tiers would leave an orphan link the insurance read path would then meet
        // (issue #27 §6). Tracked RemoveRange, never ExecuteDelete — that throws on InMemory.
        var insuredLinks = await context.InsurancePolicyInsuredAccounts
            .Where(link => link.AccountId == id)
            .ToListAsync(cancellationToken);
        context.InsurancePolicyInsuredAccounts.RemoveRange(insuredLinks);

        context.Accounts.Remove(account);
        await context.SaveChangesAsync(cancellationToken);
    }
    public async Task<AccountFile?> AttachFileToAccount(Guid accountId, Guid fileId, string userId, DtoAccountFileType fileType = DtoAccountFileType.Other, AttachAccountFileRequest? validity = null, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
        if (account is null)
        {
            throw new DomainNotFoundException($"Account with ID {accountId} was not found.");
        }

        await EnsureIssuerExists(validity?.IssuedBy, cancellationToken);

        var existingAssociation = await context.AccountFiles.FirstOrDefaultAsync(af =>
            af.AccountId == accountId
            && af.FileMetadataId == fileId, cancellationToken);

        if (existingAssociation is not null)
        {
            return existingAssociation;
        }

        var accountFile = new AccountFile
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FileMetadataId = fileId,
            AttachedByUserId = userId,
            AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            FileType = fileType.Adapt<ContextAccountFileType>(),
            ValidFrom = validity?.ValidFrom,
            ValidTo = validity?.ValidTo,
            IssuedAt = validity?.IssuedAt,
            IssuedBy = validity?.IssuedBy,
        };

        context.AccountFiles.Add(accountFile);
        await context.SaveChangesAsync(cancellationToken);

        return accountFile;
    }

    public async Task<AccountFile?> UpdateAccountFileType(Guid accountId, Guid fileId, UpdateAccountFileRequest request, CancellationToken cancellationToken = default)
    {
        var association = await context.AccountFiles
            .FirstOrDefaultAsync(af => af.AccountId == accountId && af.FileMetadataId == fileId, cancellationToken);

        if (association is null)
        {
            return null;
        }

        await EnsureIssuerExists(request.IssuedBy, cancellationToken);

        association.FileType = request.FileType.Adapt<ContextAccountFileType>();
        association.ValidFrom = request.ValidFrom;
        association.ValidTo = request.ValidTo;
        association.IssuedAt = request.IssuedAt;
        association.IssuedBy = request.IssuedBy;
        await context.SaveChangesAsync(cancellationToken);

        return association;
    }

    private async Task EnsureIssuerExists(Guid? issuedBy, CancellationToken cancellationToken = default)
    {
        if (issuedBy is null)
        {
            return;
        }

        var id = issuedBy.Value;
        var existing = await contactLookup.ExistingIdsAsync([id], cancellationToken);
        if (!existing.Contains(id))
        {
            throw new DomainValidationException($"Contact with ID {id} was not found.");
        }
    }

    public async Task<AccountFile?> DetachFileFromAccount(Guid accountId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var association = await context.AccountFiles
            .FirstOrDefaultAsync(af => af.AccountId == accountId && af.FileMetadataId == fileId, cancellationToken);

        if (association is not null)
        {
            context.AccountFiles.Remove(association);
            await context.SaveChangesAsync(cancellationToken);
        }

        return association;
    }

    private void ApplyArchiveTransition(Account account, bool requestedArchived)
    {
        var currentArchived = account.Archived is not null;

        if (!currentArchived && requestedArchived)
        {
            account.Archived = timeProvider.GetUtcNow().UtcDateTime;
            return;
        }

        if (currentArchived && !requestedArchived)
        {
            account.Archived = null;
        }
    }

}
