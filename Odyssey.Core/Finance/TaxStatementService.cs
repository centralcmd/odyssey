using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using AccountType = Odyssey.Context.AccountType;
using FinanceDtos = Odyssey.Dtos.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// CRUD, status/tag management and reconciliation reporting for yearly tax statements.
/// The reconciliation report contrasts the figures <em>declared</em> on the official statement,
/// the figures <em>derived</em> from Odyssey's own accounts/transactions, and the differences.
/// </summary>
public class TaxStatementService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public TaxStatementService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-side paged list (issue #277): name search + status filter + allowlisted sort.</summary>
    public async Task<PagedResult<ExistingTaxStatement>> ListAsync(
        TaxStatementsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.TaxStatements
            .AsNoTracking()
            .Include(s => s.TaxStatementTags)
            .Include(s => s.TaxStatementFiles)
                .ThenInclude(f => f.FileMetadata)
            .AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(s => EF.Functions.Like(s.Name, pattern));
        }

        // `Archived` is a derived (column) state, not a stored TaxStatementStatus: hidden by default,
        // included only when explicitly requested. The stored statuses (New/Approved/Flagged) apply to
        // non-archived statements and map to TaxStatementStatus by value.
        if (query.Statuses is { Length: > 0 } statuses)
        {
            var wantArchived = statuses.Contains(TaxStatementStatusFilter.Archived);
            var storedWanted = statuses
                .Where(s => s != TaxStatementStatusFilter.Archived)
                .Select(s => (TaxStatementStatus)(int)s)
                .ToList();
            q = q.Where(s =>
                (wantArchived && s.Archived != null) ||
                (s.Archived == null && storedWanted.Contains(s.Status)));
        }
        else
        {
            q = q.Where(s => s.Archived == null);
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is TaxStatementSortBy.Name);
        IOrderedQueryable<TaxStatement> sorted = query.SortBy switch
        {
            TaxStatementSortBy.Name => ascending ? q.OrderBy(s => s.Name) : q.OrderByDescending(s => s.Name),
            TaxStatementSortBy.Status => ascending ? q.OrderBy(s => s.Status) : q.OrderByDescending(s => s.Status),
            _ => ascending ? q.OrderBy(s => s.FiscalYear) : q.OrderByDescending(s => s.FiscalYear),
        };
        q = sorted.ThenBy(s => s.TaxStatementId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, ToDto, cancellationToken);
    }

    /// <summary>
    /// Summary rollup for the page header (issue #372): the years-on-file count, the fiscal-year
    /// bounds, and the per-year declared figures the overview charts plot. A lean projection — the
    /// tag links, file metadata and notes the full list carries are never touched.
    /// </summary>
    public async Task<TaxStatementSummary> GetSummary(CancellationToken cancellationToken = default)
    {
        var total = await context.TaxStatements.CountAsync(cancellationToken);

        // Archived statements drop out of the header count and the charts, matching the page's rule.
        var years = await context.TaxStatements
            .AsNoTracking()
            .Where(s => s.Archived == null)
            .OrderBy(s => s.FiscalYear)
            .Select(s => new TaxStatementYearFigures
            {
                FiscalYear = s.FiscalYear,
                BaseCurrencyCode = s.BaseCurrencyCode,
                DeclaredTotalAssets = s.DeclaredTotalAssets,
                DeclaredTotalLiabilities = s.DeclaredTotalLiabilities,
                DeclaredNetWorth = s.DeclaredNetWorth,
                DeclaredTotalIncome = s.DeclaredTotalIncome,
                AssessedTax = s.AssessedTax,
                SettlementAmount = s.SettlementAmount,
            })
            .ToListAsync(cancellationToken);

        return new TaxStatementSummary
        {
            TotalStatements = total,
            ActiveCount = years.Count,
            FirstFiscalYear = years.Count == 0 ? null : years[0].FiscalYear,
            LatestFiscalYear = years.Count == 0 ? null : years[^1].FiscalYear,
            Years = years,
        };
    }

    public async Task<ExistingTaxStatement?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var statement = await LoadWithDetails(id, cancellationToken);
        return statement is null ? null : ToDto(statement);
    }

    public async Task<ExistingTaxStatement> Create(NewTaxStatement request, CancellationToken cancellationToken = default)
    {
        var normalizedCurrency = CurrencyValidationService.Normalize(request.BaseCurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(context, normalizedCurrency, nameof(request.BaseCurrencyCode));
        Validate(request.StartDate, request.EndDate, request.DeclaredTotalAssets,
            request.DeclaredTotalLiabilities, request.DeclaredTotalIncome, request.AssessedTax);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var statement = new TaxStatement
        {
            Name = request.Name,
            FiscalYear = request.FiscalYear,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            BaseCurrencyCode = normalizedCurrency,
            DeclaredTotalAssets = request.DeclaredTotalAssets,
            DeclaredTotalLiabilities = request.DeclaredTotalLiabilities,
            DeclaredNetWorth = request.DeclaredNetWorth,
            DeclaredTotalIncome = request.DeclaredTotalIncome,
            AssessedTax = request.AssessedTax,
            SettlementAmount = request.SettlementAmount,
            SettledAtUtc = request.SettledAtUtc,
            FiledAtUtc = request.FiledAtUtc,
            TaxOfficeApprovedAtUtc = request.TaxOfficeApprovedAtUtc,
            Notes = request.Notes,
            Status = TaxStatementStatus.New,
            StatusChangedAt = now,
            CreatedAtUtc = now,
        };

        context.TaxStatements.Add(statement);
        await context.SaveChangesAsync(cancellationToken);

        return ToDto(statement);
    }

    public async Task<ExistingTaxStatement?> Update(Guid id, UpdateTaxStatement request, CancellationToken cancellationToken = default)
    {
        var statement = await LoadWithDetailsForUpdate(id, cancellationToken);
        if (statement is null)
        {
            return null;
        }

        var normalizedCurrency = CurrencyValidationService.Normalize(request.BaseCurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(context, normalizedCurrency, nameof(request.BaseCurrencyCode));
        Validate(request.StartDate, request.EndDate, request.DeclaredTotalAssets,
            request.DeclaredTotalLiabilities, request.DeclaredTotalIncome, request.AssessedTax);

        statement.Name = request.Name;
        statement.FiscalYear = request.FiscalYear;
        statement.StartDate = request.StartDate;
        statement.EndDate = request.EndDate;
        statement.BaseCurrencyCode = normalizedCurrency;
        statement.DeclaredTotalAssets = request.DeclaredTotalAssets;
        statement.DeclaredTotalLiabilities = request.DeclaredTotalLiabilities;
        statement.DeclaredNetWorth = request.DeclaredNetWorth;
        statement.DeclaredTotalIncome = request.DeclaredTotalIncome;
        statement.AssessedTax = request.AssessedTax;
        statement.SettlementAmount = request.SettlementAmount;
        statement.SettledAtUtc = request.SettledAtUtc;
        statement.FiledAtUtc = request.FiledAtUtc;
        statement.TaxOfficeApprovedAtUtc = request.TaxOfficeApprovedAtUtc;
        statement.Notes = request.Notes;
        ApplyArchiveTransition(statement, request.Archived);

        await context.SaveChangesAsync(cancellationToken);

        return ToDto(statement);
    }

    public async Task<ExistingTaxStatement?> UpdateStatus(Guid id, UpdateTaxStatementStatus request, CancellationToken cancellationToken = default)
    {
        var statement = await LoadWithDetailsForUpdate(id, cancellationToken);
        if (statement is null)
        {
            return null;
        }

        statement.Status = request.Status;
        statement.StatusComment = request.StatusComment;
        statement.StatusChangedAt = timeProvider.GetUtcNow().UtcDateTime;

        await context.SaveChangesAsync(cancellationToken);

        return ToDto(statement);
    }

    public async Task<ExistingTaxStatement?> UpdateTags(Guid id, UpdateTaxStatementTags request, CancellationToken cancellationToken = default)
    {
        var statement = await LoadWithDetailsForUpdate(id, cancellationToken);
        if (statement is null)
        {
            return null;
        }

        var taxTagIds = request.TaxTagIds.Distinct().ToList();
        var incomeTagIds = request.IncomeTagIds.Distinct().ToList();
        await EnsureTagsExist(taxTagIds.Concat(incomeTagIds).Distinct().ToList(), cancellationToken);

        var existing = await context.TaxStatementTags
            .Where(t => t.TaxStatementId == id)
            .ToListAsync(cancellationToken);
        context.TaxStatementTags.RemoveRange(existing);

        foreach (var tagId in taxTagIds)
        {
            context.TaxStatementTags.Add(new TaxStatementTag
            {
                TaxStatementId = id,
                TransactionTagId = tagId,
                Role = TaxStatementTagRole.TaxPayment,
            });
        }

        foreach (var tagId in incomeTagIds)
        {
            context.TaxStatementTags.Add(new TaxStatementTag
            {
                TaxStatementId = id,
                TransactionTagId = tagId,
                Role = TaxStatementTagRole.Income,
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        return await Get(id, cancellationToken);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var statement = await context.TaxStatements.FirstOrDefaultAsync(s => s.TaxStatementId == id, cancellationToken);
        if (statement is null)
        {
            return false;
        }

        if (statement.Archived is null)
        {
            statement.Archived = timeProvider.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    // ── Reconciliation report ─────────────────────────────────────────────────

    public async Task<TaxStatementReport?> GetReport(Guid id, CancellationToken cancellationToken = default)
    {
        var statement = await context.TaxStatements
            .AsNoTracking()
            .Include(s => s.TaxStatementTags)
            .FirstOrDefaultAsync(s => s.TaxStatementId == id, cancellationToken);

        if (statement is null)
        {
            return null;
        }

        var taxTagIds = statement.TaxStatementTags
            .Where(t => t.Role == TaxStatementTagRole.TaxPayment)
            .Select(t => t.TransactionTagId)
            .ToHashSet();

        var incomeTagIds = statement.TaxStatementTags
            .Where(t => t.Role == TaxStatementTagRole.Income)
            .Select(t => t.TransactionTagId)
            .ToHashSet();

        var allTagIds = taxTagIds.Concat(incomeTagIds).Distinct().ToList();

        // One row per (transaction, matching tag) link within the period. A multi-tagged transaction
        // yields several rows, so SumByRole de-duplicates by transaction id to count its amount once
        // per role even when it carries more than one of that role's tags.
        var candidates = allTagIds.Count == 0
            ? new List<TransactionRow>()
            : await context.TransactionTagLinks
                .Where(link => allTagIds.Contains(link.TransactionTagId))
                .Where(link => link.Transaction!.TimeStamp >= statement.StartDate && link.Transaction.TimeStamp <= statement.EndDate)
                .Select(link => new TransactionRow(link.TransactionId, link.TransactionTagId, link.Transaction!.Amount, link.Transaction.CurrencyCode))
                .ToListAsync(cancellationToken);

        var excluded = new Dictionary<string, int>();

        var paidTax = SumByRole(candidates, taxTagIds, statement.BaseCurrencyCode, excluded);
        var actualIncome = SumByRole(candidates, incomeTagIds, statement.BaseCurrencyCode, excluded);

        var derived = await ComputeDerivedBalances(statement.BaseCurrencyCode, cancellationToken);
        derived.PaidTax = paidTax;
        derived.ActualIncome = actualIncome;

        var reconciliation = BuildReconciliation(statement, derived);

        return new TaxStatementReport
        {
            TaxStatementId = statement.TaxStatementId,
            FiscalYear = statement.FiscalYear,
            BaseCurrencyCode = statement.BaseCurrencyCode,
            Status = statement.Status,
            FiledAtUtc = statement.FiledAtUtc,
            TaxOfficeApprovedAtUtc = statement.TaxOfficeApprovedAtUtc,
            Declared = new TaxStatementDeclaredFigures
            {
                TotalAssets = statement.DeclaredTotalAssets,
                TotalLiabilities = statement.DeclaredTotalLiabilities,
                NetWorth = statement.DeclaredNetWorth,
                TotalIncome = statement.DeclaredTotalIncome,
                AssessedTax = statement.AssessedTax,
                SettlementAmount = statement.SettlementAmount,
                SettledAtUtc = statement.SettledAtUtc,
            },
            Derived = derived,
            Reconciliation = reconciliation,
            ExcludedTransactionCount = excluded.Values.Sum(),
            ExcludedCurrencies = excluded,
        };
    }

    // Advance/within-year tax and actual income share this in-period, base-currency sum;
    // off-currency matches are excluded and tallied (mirrors BudgetReport).
    private static decimal SumByRole(
        IReadOnlyCollection<TransactionRow> candidates,
        IReadOnlySet<Guid> tagIds,
        string baseCurrency,
        Dictionary<string, int> excluded)
    {
        if (tagIds.Count == 0)
        {
            return 0m;
        }

        // De-duplicate by transaction id: a transaction carrying several of this role's tags must
        // still contribute its amount only once to the role total.
        var sum = 0m;
        var counted = new HashSet<Guid>();
        foreach (var row in candidates.Where(c => tagIds.Contains(c.TagId)))
        {
            if (!counted.Add(row.TransactionId))
            {
                continue;
            }

            if (row.CurrencyCode == baseCurrency)
            {
                sum += row.Amount;
            }
            else
            {
                excluded[row.CurrencyCode] = excluded.GetValueOrDefault(row.CurrencyCode) + 1;
            }
        }

        return sum;
    }

    // Derived assets/liabilities = sums of base-currency account balances grouped by AccountType.
    // No cross-currency conversion (off-currency accounts are excluded), mirroring the report's
    // tag-based sums. Balances are computed from transaction amounts.
    private async Task<TaxStatementDerivedFigures> ComputeDerivedBalances(string baseCurrency, CancellationToken cancellationToken = default)
    {
        var accounts = await context.Accounts
            .Where(a => a.Archived == null && a.CurrencyCode == baseCurrency)
            .Select(a => new { a.AccountId, a.AccountType })
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return new TaxStatementDerivedFigures
            {
                Available = true,
                TotalAssets = 0m,
                TotalLiabilities = 0m,
                NetWorth = 0m,
            };
        }

        var accountIds = accounts.Select(a => a.AccountId).ToList();
        var balances = await context.Transactions
            .Where(t => accountIds.Contains(t.AccountId))
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Balance = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Balance, cancellationToken);

        var totalAssets = 0m;
        var totalLiabilities = 0m;
        foreach (var account in accounts)
        {
            var balance = balances.GetValueOrDefault(account.AccountId);
            if (IsAsset(account.AccountType))
            {
                totalAssets += balance;
            }
            else if (IsLiability(account.AccountType))
            {
                totalLiabilities += Math.Abs(balance);
            }
        }

        return new TaxStatementDerivedFigures
        {
            Available = true,
            TotalAssets = totalAssets,
            TotalLiabilities = totalLiabilities,
            NetWorth = totalAssets - totalLiabilities,
        };
    }

    private static TaxStatementReconciliation BuildReconciliation(TaxStatement statement, TaxStatementDerivedFigures derived)
    {
        decimal? outstandingTax = statement.AssessedTax is { } assessed ? assessed - derived.PaidTax : null;
        decimal? incomeVariance = statement.DeclaredTotalIncome is { } income ? income - derived.ActualIncome : null;
        decimal? netWorthVariance = statement.DeclaredNetWorth is { } declaredNetWorth && derived.NetWorth is { } derivedNetWorth
            ? declaredNetWorth - derivedNetWorth
            : null;
        decimal? settlementVariance = statement.SettlementAmount is { } settlement && outstandingTax is { } outstanding
            ? settlement - outstanding
            : null;

        return new TaxStatementReconciliation
        {
            OutstandingTax = outstandingTax,
            IncomeVariance = incomeVariance,
            NetWorthVariance = netWorthVariance,
            SettlementVariance = settlementVariance,
        };
    }

    // ── File attachments ──────────────────────────────────────────────────────

    public async Task<IList<ExistingTaxStatementFile>> GetFiles(Guid id, CancellationToken cancellationToken = default)
    {
        var files = await context.TaxStatementFiles
            .AsNoTracking()
            .Where(f => f.TaxStatementId == id)
            .Include(f => f.FileMetadata)
            .OrderBy(f => f.AttachedAtUtc)
            .ToListAsync(cancellationToken);

        return files.Select(ToFileDto).ToList();
    }

    public async Task<TaxStatementFile> AttachFile(Guid id, Guid fileId, string userId, FinanceDtos.TaxStatementFileType fileType = FinanceDtos.TaxStatementFileType.Other, CancellationToken cancellationToken = default)
    {
        var existing = await context.TaxStatementFiles
            .Include(f => f.FileMetadata)
            .FirstOrDefaultAsync(f => f.TaxStatementId == id && f.FileMetadataId == fileId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var association = new TaxStatementFile
        {
            TaxStatementId = id,
            FileMetadataId = fileId,
            AttachedByUserId = userId,
            AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            FileType = fileType.Adapt<Context.TaxStatementFileType>(),
        };

        context.TaxStatementFiles.Add(association);
        await context.SaveChangesAsync(cancellationToken);

        return association;
    }

    public async Task<bool> DetachFile(Guid id, Guid fileId, CancellationToken cancellationToken = default)
    {
        var association = await context.TaxStatementFiles
            .FirstOrDefaultAsync(f => f.TaxStatementId == id && f.FileMetadataId == fileId, cancellationToken);

        if (association is null)
        {
            return false;
        }

        context.TaxStatementFiles.Remove(association);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> Exists(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.TaxStatements.AnyAsync(s => s.TaxStatementId == id, cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Only Get reads without writing; the three update paths need their rows tracked.
    private async Task<TaxStatement?> LoadWithDetails(Guid id, CancellationToken cancellationToken = default) =>
        await WithDetails(context.TaxStatements.AsNoTracking())
            .FirstOrDefaultAsync(s => s.TaxStatementId == id, cancellationToken);

    private async Task<TaxStatement?> LoadWithDetailsForUpdate(Guid id, CancellationToken cancellationToken = default) =>
        await WithDetails(context.TaxStatements)
            .FirstOrDefaultAsync(s => s.TaxStatementId == id, cancellationToken);

    private static IQueryable<TaxStatement> WithDetails(IQueryable<TaxStatement> statements) => statements
        .Include(s => s.TaxStatementTags)
        .Include(s => s.TaxStatementFiles)
            .ThenInclude(f => f.FileMetadata);

    private async Task EnsureTagsExist(IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default)
    {
        if (tagIds.Count == 0)
        {
            return;
        }

        var found = await context.TransactionTags
            .Where(t => tagIds.Contains(t.TransactionTagId) && t.Archived == null)
            .Select(t => t.TransactionTagId)
            .ToListAsync(cancellationToken);

        var missing = tagIds.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new DomainValidationException(
                $"Unknown or archived transaction tag(s): {string.Join(", ", missing)}.");
        }
    }

    private static void Validate(
        DateTime startDate,
        DateTime endDate,
        decimal? totalAssets,
        decimal? totalLiabilities,
        decimal? totalIncome,
        decimal? assessedTax)
    {
        if (endDate < startDate)
        {
            throw new DomainValidationException("EndDate must be on or after StartDate.");
        }

        EnsureNonNegative(totalAssets, nameof(TaxStatement.DeclaredTotalAssets));
        EnsureNonNegative(totalLiabilities, nameof(TaxStatement.DeclaredTotalLiabilities));
        EnsureNonNegative(totalIncome, nameof(TaxStatement.DeclaredTotalIncome));
        EnsureNonNegative(assessedTax, nameof(TaxStatement.AssessedTax));
    }

    private static void EnsureNonNegative(decimal? value, string fieldName)
    {
        if (value is < 0)
        {
            throw new DomainValidationException($"{fieldName} must not be negative.");
        }
    }

    private void ApplyArchiveTransition(TaxStatement statement, bool requestedArchived)
    {
        var currentArchived = statement.Archived is not null;

        if (!currentArchived && requestedArchived)
        {
            statement.Archived = timeProvider.GetUtcNow().UtcDateTime;
        }
        else if (currentArchived && !requestedArchived)
        {
            statement.Archived = null;
        }
    }

    private static ExistingTaxStatement ToDto(TaxStatement statement)
    {
        var dto = statement.Adapt<ExistingTaxStatement>();
        dto.TaxTagIds = statement.TaxStatementTags
            .Where(t => t.Role == TaxStatementTagRole.TaxPayment)
            .Select(t => t.TransactionTagId)
            .ToList();
        dto.IncomeTagIds = statement.TaxStatementTags
            .Where(t => t.Role == TaxStatementTagRole.Income)
            .Select(t => t.TransactionTagId)
            .ToList();
        dto.Files = statement.TaxStatementFiles
            .Where(f => f.FileMetadata is not null)
            .OrderBy(f => f.AttachedAtUtc)
            .Select(ToFileDto)
            .ToList();
        return dto;
    }

    private static ExistingTaxStatementFile ToFileDto(TaxStatementFile file) => new()
    {
        Id = file.Id,
        TaxStatementId = file.TaxStatementId,
        FileMetadata = file.FileMetadata!.Adapt<ExistingFileMetadata>(),
        AttachedByUserId = file.AttachedByUserId,
        AttachedAtUtc = file.AttachedAtUtc,
        FileType = file.FileType.Adapt<FinanceDtos.TaxStatementFileType>(),
    };

    private static bool IsAsset(AccountType type) => type is >= AccountType.Cash and <= AccountType.OtherAsset;

    private static bool IsLiability(AccountType type) => type is >= AccountType.CreditCard and <= AccountType.OtherLiability;

    private readonly record struct TransactionRow(Guid TransactionId, Guid TagId, decimal Amount, string CurrencyCode);
}
