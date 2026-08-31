using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using ContextInsurancePolicyType = Odyssey.Context.InsurancePolicyType;
using ContextPolicyFileType = Odyssey.Context.PolicyFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoPolicyFileType = Odyssey.Dtos.Finance.PolicyFileType;
using DtoInsurancePolicyType = Odyssey.Dtos.Finance.InsurancePolicyType;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;

namespace Odyssey.Core.Finance;

/// <summary>
/// CRUD for insurance policies and their renewals, file attach/detach, derived coverage-status
/// computation and portfolio summary aggregation (issue #175). Owns all business validation; the
/// controller owns claim authorization and the file content-type allow-list.
///
/// All time-relative computation uses a single UTC "today" captured once per request from the injected
/// <see cref="TimeProvider"/>, so a policy cannot evaluate to different statuses within one request (§5).
/// </summary>
public class InsuranceService
{
    private readonly OdysseyContext context;
    private readonly CurrencyConversionService conversion;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;
    private readonly ISystemSettingsLookup systemSettingsLookup;

    private readonly IContactMutationLock mutationLock;

    public InsuranceService(
        OdysseyContext context,
        CurrencyConversionService conversion,
        IContactLookup contactLookup,
        TimeProvider timeProvider,
        ISystemSettingsLookup systemSettingsLookup,
        IContactMutationLock? mutationLock = null)
    {
        this.context = context;
        this.conversion = conversion;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
        this.systemSettingsLookup = systemSettingsLookup;
        // Serializes insurer validate-then-persist against a concurrent delete of that contact (the
        // TOCTOU the removed required+RESTRICT FK used to prevent). Defaults to a no-op for direct
        // construction / the in-memory test provider; DI supplies the real MariaDB advisory lock.
        this.mutationLock = mutationLock ?? ContactMutationLock.None;
    }

    private DateTime Today => timeProvider.GetUtcNow().UtcDateTime.Date;

    // ── Policies ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Server-side paged list (issue #277): SQL search (name/insurer)/type/archived, then derive
    /// coverage status and current-renewal figures and sort in memory (derived values), then slice.
    /// </summary>
    public async Task<PagedResult<InsurancePolicyListItem>> ListAsync(
        InsurancePoliciesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var today = Today;
        var window = (await systemSettingsLookup.GetInsurancePolicySettingsAsync(cancellationToken)).ExpiringSoonWindowDays;

        var q = context.InsurancePolicies
            .AsNoTracking()
            .Include(p => p.Renewals)
            .AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            // Pre-resolve the matching insurer ids via the lookup and filter this table by InsurerId
            // membership — OR-combined with the policy-field match, so the term matches on policy name OR
            // insurer name. This shape dates from when Contact lived in a separate context and a JOIN was
            // impossible; the contexts are merged now, so a JOIN would work and would be cheaper on a
            // large contact table. Left as-is deliberately: it is a behaviour-preserving rewrite for its
            // own change, not part of the merge.
            var insurerMatchIds = (await contactLookup.SearchIdsByNameAsync(term, cancellationToken)).ToHashSet();
            q = q.Where(p =>
                EF.Functions.Like(p.Name, pattern) ||
                insurerMatchIds.Contains(p.InsurerId));
        }

        var typeFilter = (query.Types ?? [])
            .Select(t => t.Adapt<ContextInsurancePolicyType>())
            .ToList();
        if (typeFilter.Count > 0)
        {
            q = q.Where(p => typeFilter.Contains(p.Type));
        }

        // Coverage status is derived, so it is filtered after projection (below). Archived policies
        // are shown by default and only excluded when the status filter omits them — matching the
        // design system, which no longer hides archived rows.
        var statusFilter = query.Statuses ?? [];

        var policies = await q.ToListAsync(cancellationToken);

        var policyIds = policies.Select(p => p.InsurancePolicyId).ToList();
        var fileCounts = await context.InsurancePolicyFiles
            .Where(f => policyIds.Contains(f.InsurancePolicyId))
            .GroupBy(f => f.InsurancePolicyId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        // Resolve the insurer (Contact) references in one batched cross-context lookup — the insurer
        // navigation no longer exists on the entity after the Contact move to OdysseyContext.
        var insurerIds = policies.Select(p => p.InsurerId).Distinct().ToList();
        var insurerRefs = await contactLookup.ResolveRefsAsync(insurerIds, cancellationToken);

        var items = policies.Select(p =>
        {
            var (coverageStatus, current) = EvaluateCoverage(p, today, window);
            return new InsurancePolicyListItem
            {
                InsurancePolicyId = p.InsurancePolicyId,
                Name = p.Name,
                PolicyNumber = p.PolicyNumber,
                Type = p.Type.Adapt<DtoInsurancePolicyType>(),
                Insurer = BuildInsurerReference(p.InsurerId, insurerRefs),
                CoverageStatus = coverageStatus,
                CurrentRenewalEndDate = current?.ToDate,
                // The boundary dates a row headlines on when there is no current period. Free here:
                // EvaluateCoverage already has the renewals loaded, so this adds no query.
                LatestRenewalEndDate = p.Renewals.Count == 0 ? null : p.Renewals.Max(r => r.ToDate),
                EarliestRenewalStartDate = p.Renewals.Count == 0 ? null : p.Renewals.Min(r => r.FromDate),
                CurrentPremium = current?.Premium,
                CurrentPremiumCurrencyCode = current?.PremiumCurrencyCode,
                CurrentCoverage = current?.CoverageAmount,
                CurrentCoverageCurrencyCode = current?.CoverageCurrencyCode,
                RenewalCount = p.Renewals.Count,
                FileCount = fileCounts.GetValueOrDefault(p.InsurancePolicyId),
                Archived = p.Archived,
            };
        });

        if (statusFilter.Length > 0)
        {
            items = items.Where(i => statusFilter.Contains(i.CoverageStatus));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or InsuranceSortBy.Name or InsuranceSortBy.Type);
        IOrderedEnumerable<InsurancePolicyListItem> sorted = query.SortBy switch
        {
            InsuranceSortBy.Type => ascending ? items.OrderBy(i => i.Type) : items.OrderByDescending(i => i.Type),
            InsuranceSortBy.RenewalEnd => ascending
                ? items.OrderBy(i => i.CurrentRenewalEndDate is null).ThenBy(i => i.CurrentRenewalEndDate)
                : items.OrderBy(i => i.CurrentRenewalEndDate is null).ThenByDescending(i => i.CurrentRenewalEndDate),
            InsuranceSortBy.Premium => ascending
                ? items.OrderBy(i => i.CurrentPremium is null).ThenBy(i => i.CurrentPremium)
                : items.OrderBy(i => i.CurrentPremium is null).ThenByDescending(i => i.CurrentPremium),
            _ => ascending ? items.OrderBy(i => i.Name) : items.OrderByDescending(i => i.Name),
        };
        var ordered = sorted.ThenBy(i => i.InsurancePolicyId).ToList();
        return ListQuery.ToPagedResult(ordered, query.Offset, query.Limit);
    }

    public async Task<ExistingInsurancePolicy?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var policy = await LoadWithDetails(id, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        var insurer = await ResolveInsurerReference(policy.InsurerId, cancellationToken);
        var window = (await systemSettingsLookup.GetInsurancePolicySettingsAsync(cancellationToken)).ExpiringSoonWindowDays;
        return await WithAccruedPremium(ToDto(policy, insurer, Today, window));
    }

    public async Task<ExistingInsurancePolicy> Create(NewInsurancePolicy request, CancellationToken cancellationToken = default)
    {
        // Hold the per-contact lock across insurer-validate → persist so a concurrent delete of the
        // insurer can't land between the check and the insert (leaving a dangling required insurer).
        await using var _ = await mutationLock.AcquireAsync(request.InsurerId, cancellationToken);

        await EnsureInsurerValid(request.InsurerId, cancellationToken);
        await EnsureInsuredAccountValid(request.InsuredAccountId, cancellationToken);

        var policy = new InsurancePolicy
        {
            Name = request.Name,
            PolicyNumber = request.PolicyNumber,
            Type = request.Type.Adapt<ContextInsurancePolicyType>(),
            InsurerId = request.InsurerId,
            InsuredAccountId = request.InsuredAccountId,
            Notes = request.Notes,
            Archived = null,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.InsurancePolicies.Add(policy);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await LoadWithDetails(policy.InsurancePolicyId, cancellationToken);
        var insurer = await ResolveInsurerReference(loaded!.InsurerId, cancellationToken);
        var window = (await systemSettingsLookup.GetInsurancePolicySettingsAsync(cancellationToken)).ExpiringSoonWindowDays;
        return await WithAccruedPremium(ToDto(loaded, insurer, Today, window));
    }

    public async Task<ExistingInsurancePolicy?> Update(Guid id, UpdateInsurancePolicy request, CancellationToken cancellationToken = default)
    {
        var policy = await LoadWithDetailsForUpdate(id, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        // Same TOCTOU guard as Create: serialize insurer validate → persist against a concurrent delete
        // of the (new) insurer. Held across the save even when the insurer is unchanged, which is cheap.
        await using var _ = await mutationLock.AcquireAsync(request.InsurerId, cancellationToken);

        // Validate the insurer/account references only when they actually change, so an unrelated
        // edit (e.g. renaming or re-archiving) doesn't 400 just because a still-linked contact
        // or account was archived in the meantime.
        if (request.InsurerId != policy.InsurerId)
        {
            await EnsureInsurerValid(request.InsurerId, cancellationToken);
        }
        if (request.InsuredAccountId != policy.InsuredAccountId)
        {
            await EnsureInsuredAccountValid(request.InsuredAccountId, cancellationToken);
        }

        policy.Name = request.Name;
        policy.PolicyNumber = request.PolicyNumber;
        policy.Type = request.Type.Adapt<ContextInsurancePolicyType>();
        policy.InsurerId = request.InsurerId;
        policy.InsuredAccountId = request.InsuredAccountId;
        policy.Notes = request.Notes;
        // Archive (preserving the original archive stamp) or unarchive per the request.
        policy.Archived = request.Archived
            ? policy.Archived ?? timeProvider.GetUtcNow().UtcDateTime
            : null;

        await context.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadWithDetails(id, cancellationToken);
        var insurer = await ResolveInsurerReference(reloaded!.InsurerId, cancellationToken);
        var window = (await systemSettingsLookup.GetInsurancePolicySettingsAsync(cancellationToken)).ExpiringSoonWindowDays;
        return await WithAccruedPremium(ToDto(reloaded, insurer, Today, window));
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        // Hard delete: removes the policy and cascades its renewals + file-join rows. The underlying
        // FileMetadata/blobs are owned by the files API and are left intact (same as detach). Children
        // are loaded so the cascade also applies under the EF InMemory provider (used by tests), which
        // does not enforce database-level cascade.
        var policy = await context.InsurancePolicies
            .Include(p => p.Renewals)
                .ThenInclude(r => r.Files)
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.InsurancePolicyId == id, cancellationToken);
        if (policy is null)
        {
            return false;
        }

        context.InsurancePolicies.Remove(policy);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> Exists(Guid id, CancellationToken cancellationToken = default) =>
        await context.InsurancePolicies.AnyAsync(p => p.InsurancePolicyId == id, cancellationToken);

    // ── Renewals ──────────────────────────────────────────────────────────────

    public async Task<ExistingPolicyRenewal?> AddRenewal(Guid policyId, NewPolicyRenewal request, CancellationToken cancellationToken = default)
    {
        if (!await Exists(policyId, cancellationToken))
        {
            return null;
        }

        ValidateRenewalDates(request.FromDate, request.ToDate);
        await ValidateRenewalCurrencies(request.PremiumCurrencyCode, request.CoverageCurrencyCode, cancellationToken);

        var count = await context.PolicyRenewals.CountAsync(r => r.InsurancePolicyId == policyId, cancellationToken);
        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        if (count >= caps.MaxRenewalsPerPolicy)
        {
            throw new DomainUnprocessableException(
                $"Policy {policyId} already has the maximum of {caps.MaxRenewalsPerPolicy} renewals.");
        }

        var renewal = new PolicyRenewal
        {
            InsurancePolicyId = policyId,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            Premium = request.Premium,
            PremiumCurrencyCode = CurrencyValidationService.Normalize(request.PremiumCurrencyCode),
            CoverageAmount = request.CoverageAmount,
            CoverageCurrencyCode = CurrencyValidationService.Normalize(request.CoverageCurrencyCode),
            Notes = request.Notes,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.PolicyRenewals.Add(renewal);
        await context.SaveChangesAsync(cancellationToken);

        return ToRenewalDto(renewal);
    }

    public async Task<ExistingPolicyRenewal?> UpdateRenewal(Guid policyId, Guid renewalId, UpdatePolicyRenewal request, CancellationToken cancellationToken = default)
    {
        var renewal = await context.PolicyRenewals
            .Include(r => r.Files)
                .ThenInclude(f => f.FileMetadata)
            .FirstOrDefaultAsync(r => r.PolicyRenewalId == renewalId && r.InsurancePolicyId == policyId, cancellationToken);
        if (renewal is null)
        {
            return null;
        }

        ValidateRenewalDates(request.FromDate, request.ToDate);
        await ValidateRenewalCurrencies(request.PremiumCurrencyCode, request.CoverageCurrencyCode, cancellationToken);

        renewal.FromDate = request.FromDate;
        renewal.ToDate = request.ToDate;
        renewal.Premium = request.Premium;
        renewal.PremiumCurrencyCode = CurrencyValidationService.Normalize(request.PremiumCurrencyCode);
        renewal.CoverageAmount = request.CoverageAmount;
        renewal.CoverageCurrencyCode = CurrencyValidationService.Normalize(request.CoverageCurrencyCode);
        renewal.Notes = request.Notes;

        await context.SaveChangesAsync(cancellationToken);

        return ToRenewalDto(renewal);
    }

    public async Task<bool> DeleteRenewal(Guid policyId, Guid renewalId, CancellationToken cancellationToken = default)
    {
        var renewal = await context.PolicyRenewals
            .FirstOrDefaultAsync(r => r.PolicyRenewalId == renewalId && r.InsurancePolicyId == policyId, cancellationToken);
        if (renewal is null)
        {
            return false;
        }

        context.PolicyRenewals.Remove(renewal);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RenewalExists(Guid policyId, Guid renewalId, CancellationToken cancellationToken = default) =>
        await context.PolicyRenewals.AnyAsync(r => r.PolicyRenewalId == renewalId && r.InsurancePolicyId == policyId, cancellationToken);

    // ── Policy-level files ──────────────────────────────────────────────────────

    public async Task<ExistingInsurancePolicyFile> AttachPolicyFile(
        Guid policyId, Guid fileId, string userId, DtoPolicyFileType fileType, DateTime? effectiveDate, CancellationToken cancellationToken = default)
    {
        var existing = await context.InsurancePolicyFiles
            .AnyAsync(f => f.InsurancePolicyId == policyId && f.FileMetadataId == fileId, cancellationToken);
        if (existing)
        {
            throw new DomainConflictException(
                $"File {fileId} is already attached to policy {policyId}.");
        }

        var count = await context.InsurancePolicyFiles.CountAsync(f => f.InsurancePolicyId == policyId, cancellationToken);
        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        if (count >= caps.MaxFilesPerParent)
        {
            throw new DomainUnprocessableException(
                $"Policy {policyId} already has the maximum of {caps.MaxFilesPerParent} attached files.");
        }

        var association = new InsurancePolicyFile
        {
            InsurancePolicyId = policyId,
            FileMetadataId = fileId,
            FileType = fileType.Adapt<ContextPolicyFileType>(),
            EffectiveDate = effectiveDate,
            AttachedByUserId = userId,
            AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.InsurancePolicyFiles.Add(association);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await context.InsurancePolicyFiles
            .Include(f => f.FileMetadata)
            .FirstAsync(f => f.Id == association.Id);
        return ToPolicyFileDto(loaded);
    }

    public async Task<bool> IsFileAttachedToPolicy(Guid policyId, Guid fileId, CancellationToken cancellationToken = default) =>
        await context.InsurancePolicyFiles.AnyAsync(f => f.InsurancePolicyId == policyId && f.FileMetadataId == fileId, cancellationToken);

    public async Task<bool> DetachPolicyFile(Guid policyId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var association = await context.InsurancePolicyFiles
            .FirstOrDefaultAsync(f => f.InsurancePolicyId == policyId && f.FileMetadataId == fileId, cancellationToken);
        if (association is null)
        {
            return false;
        }

        context.InsurancePolicyFiles.Remove(association);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Renewal-level files ─────────────────────────────────────────────────────

    public async Task<ExistingPolicyRenewalFile> AttachRenewalFile(
        Guid renewalId, Guid fileId, string userId, DtoPolicyFileType fileType, DateTime? effectiveDate, CancellationToken cancellationToken = default)
    {
        var existing = await context.PolicyRenewalFiles
            .AnyAsync(f => f.PolicyRenewalId == renewalId && f.FileMetadataId == fileId, cancellationToken);
        if (existing)
        {
            throw new DomainConflictException(
                $"File {fileId} is already attached to renewal {renewalId}.");
        }

        var count = await context.PolicyRenewalFiles.CountAsync(f => f.PolicyRenewalId == renewalId, cancellationToken);
        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        if (count >= caps.MaxFilesPerParent)
        {
            throw new DomainUnprocessableException(
                $"Renewal {renewalId} already has the maximum of {caps.MaxFilesPerParent} attached files.");
        }

        var association = new PolicyRenewalFile
        {
            PolicyRenewalId = renewalId,
            FileMetadataId = fileId,
            FileType = fileType.Adapt<ContextPolicyFileType>(),
            EffectiveDate = effectiveDate,
            AttachedByUserId = userId,
            AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.PolicyRenewalFiles.Add(association);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await context.PolicyRenewalFiles
            .Include(f => f.FileMetadata)
            .FirstAsync(f => f.Id == association.Id);
        return ToRenewalFileDto(loaded);
    }

    public async Task<bool> IsFileAttachedToRenewal(Guid renewalId, Guid fileId, CancellationToken cancellationToken = default) =>
        await context.PolicyRenewalFiles.AnyAsync(f => f.PolicyRenewalId == renewalId && f.FileMetadataId == fileId, cancellationToken);

    public async Task<bool> DetachRenewalFile(Guid renewalId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var association = await context.PolicyRenewalFiles
            .FirstOrDefaultAsync(f => f.PolicyRenewalId == renewalId && f.FileMetadataId == fileId, cancellationToken);
        if (association is null)
        {
            return false;
        }

        context.PolicyRenewalFiles.Remove(association);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Summary ─────────────────────────────────────────────────────────────────

    public async Task<InsurancePortfolioSummary> GetSummary(string? baseCurrency, CancellationToken cancellationToken = default)
    {
        var today = Today;
        var policySettings = await systemSettingsLookup.GetInsurancePolicySettingsAsync(cancellationToken);
        var window = policySettings.ExpiringSoonWindowDays;

        string? normalizedBase = null;
        if (!string.IsNullOrWhiteSpace(baseCurrency))
        {
            normalizedBase = CurrencyValidationService.Normalize(baseCurrency);
            await CurrencyValidationService.EnsureSupportedAndActive(context, normalizedBase, nameof(baseCurrency));
        }

        // Load every policy (archived included) so status counts / the "By status" breakdown can
        // surface archived records — mirrors the Contracts summary. Premium, coverage and the
        // "By type" breakdown are still computed over the live (non-archived) set only.
        var policies = await context.InsurancePolicies
            .Include(p => p.Renewals)
            .OrderBy(p => p.Archived != null)
            .ThenByDescending(p => p.CreatedAtUtc)
            .Take(policySettings.MaxSummaryPolicies)
            .ToListAsync(cancellationToken);

        var counts = new CoverageStatusCounts();
        var byType = new Dictionary<DtoInsurancePolicyType, int>();
        var premiumByCurrency = new Dictionary<string, decimal>();
        var coverageByCurrency = new Dictionary<string, decimal>();
        var liveCount = 0;

        foreach (var policy in policies)
        {
            var (status, current) = EvaluateCoverage(policy, today, window);
            switch (status)
            {
                case CoverageStatus.Active: counts.Active++; break;
                case CoverageStatus.ExpiringSoon: counts.ExpiringSoon++; break;
                case CoverageStatus.Lapsed: counts.Lapsed++; break;
                case CoverageStatus.Upcoming: counts.Upcoming++; break;
                case CoverageStatus.NoCoverage: counts.NoCoverage++; break;
                case CoverageStatus.Archived: counts.Archived++; break;
            }

            if (policy.Archived is not null)
            {
                continue;
            }

            liveCount++;
            var dtoType = policy.Type.Adapt<DtoInsurancePolicyType>();
            byType[dtoType] = byType.GetValueOrDefault(dtoType) + 1;

            if (current is not null)
            {
                premiumByCurrency[current.PremiumCurrencyCode] =
                    premiumByCurrency.GetValueOrDefault(current.PremiumCurrencyCode) + current.Premium;
                coverageByCurrency[current.CoverageCurrencyCode] =
                    coverageByCurrency.GetValueOrDefault(current.CoverageCurrencyCode) + current.CoverageAmount;
            }
        }

        var summary = new InsurancePortfolioSummary
        {
            TotalPolicies = liveCount,
            CountsByStatus = counts,
            CountsByType = byType
                .OrderBy(kv => kv.Key)
                .Select(kv => new InsuranceTypeCount { Type = kv.Key, Count = kv.Value })
                .ToList(),
            PremiumByCurrency = premiumByCurrency
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CurrencyAmount { CurrencyCode = kv.Key, Amount = kv.Value })
                .ToList(),
            CoverageByCurrency = coverageByCurrency
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CurrencyAmount { CurrencyCode = kv.Key, Amount = kv.Value })
                .ToList(),
            BaseCurrency = normalizedBase,
        };

        if (normalizedBase is not null)
        {
            var unconverted = new SortedSet<string>(StringComparer.Ordinal);
            summary.ConvertedTotalPremium = await ConvertSubtotals(premiumByCurrency, normalizedBase, unconverted, cancellationToken);
            summary.ConvertedTotalCoverage = await ConvertSubtotals(coverageByCurrency, normalizedBase, unconverted, cancellationToken);
            summary.UnconvertedCurrencies = unconverted.ToList();
        }

        return summary;
    }

    private async Task<decimal> ConvertSubtotals(
        IReadOnlyDictionary<string, decimal> subtotals, string baseCurrency, ISet<string> unconverted, CancellationToken cancellationToken = default)
    {
        var total = 0m;
        foreach (var (currency, amount) in subtotals)
        {
            var converted = await conversion.ConvertAsync(amount, currency, baseCurrency);
            if (converted is null)
            {
                unconverted.Add(currency);
            }
            else
            {
                total += converted.Value;
            }
        }

        return total;
    }

    // ── Coverage-status evaluation (deterministic, ordered — §5) ──────────────────

    // Archived is a terminal status that takes precedence over the derived coverage state (mirrors
    // Contracts) — an archived policy always reads as Archived. The current renewal is still surfaced
    // for context (premium/coverage/end-date).
    private static (CoverageStatus Status, PolicyRenewal? Current) EvaluateCoverage(
        InsurancePolicy policy, DateTime today, int windowDays)
    {
        var (status, current) = EvaluateCoverage(policy.Renewals, today, windowDays);
        return policy.Archived is not null ? (CoverageStatus.Archived, current) : (status, current);
    }

    private static (CoverageStatus Status, PolicyRenewal? Current) EvaluateCoverage(
        IEnumerable<PolicyRenewal> renewals, DateTime today, int windowDays)
    {
        var list = renewals as IReadOnlyList<PolicyRenewal> ?? renewals.ToList();
        if (list.Count == 0)
        {
            return (CoverageStatus.NoCoverage, null);
        }

        // Active: some renewal's [FromDate, ToDate] window contains today. The current renewal is the
        // matching one with the latest FromDate, then latest CreatedAtUtc (overlap tie-break).
        var current = list
            .Where(r => r.FromDate.Date <= today && today <= r.ToDate.Date)
            .OrderByDescending(r => r.FromDate)
            .ThenByDescending(r => r.CreatedAtUtc)
            .FirstOrDefault();

        if (current is not null)
        {
            var status = current.ToDate.Date <= today.AddDays(windowDays)
                ? CoverageStatus.ExpiringSoon
                : CoverageStatus.Active;
            return (status, current);
        }

        // No renewal contains today: Upcoming when the earliest start is in the future, else Lapsed.
        var earliestStart = list.Min(r => r.FromDate.Date);
        return earliestStart > today
            ? (CoverageStatus.Upcoming, null)
            : (CoverageStatus.Lapsed, null);
    }

    // ── Validation helpers ────────────────────────────────────────────────────────

    private async Task EnsureInsurerValid(Guid insurerId, CancellationToken cancellationToken = default)
    {
        // Contact moved to OdysseyContext, so validate via the cross-context lookup instead of a local
        // Contacts query. Require the contact to exist AND be non-archived.
        var refs = await contactLookup.ResolveRefsAsync([insurerId], cancellationToken);
        if (!refs.TryGetValue(insurerId, out var insurer) || insurer.Archived is not null)
        {
            throw new DomainValidationException(
                $"InsurerId {insurerId} does not reference an existing, non-archived contact.");
        }
    }

    private async Task EnsureInsuredAccountValid(Guid? insuredAccountId, CancellationToken cancellationToken = default)
    {
        if (insuredAccountId is not { } accountId)
        {
            return;
        }

        var exists = await context.Accounts.AnyAsync(a => a.AccountId == accountId && a.Archived == null, cancellationToken);
        if (!exists)
        {
            throw new DomainValidationException(
                $"InsuredAccountId {accountId} does not reference an existing, non-archived account.");
        }
    }

    private static void ValidateRenewalDates(DateTime fromDate, DateTime toDate)
    {
        if (toDate < fromDate)
        {
            throw new DomainValidationException("ToDate must be on or after FromDate.");
        }
    }

    private async Task ValidateRenewalCurrencies(string premiumCurrencyCode, string coverageCurrencyCode, CancellationToken cancellationToken = default)
    {
        await CurrencyValidationService.EnsureSupportedAndActive(
            context, premiumCurrencyCode, nameof(NewPolicyRenewal.PremiumCurrencyCode));
        await CurrencyValidationService.EnsureSupportedAndActive(
            context, coverageCurrencyCode, nameof(NewPolicyRenewal.CoverageCurrencyCode));
    }

    // ── Loading & mapping ─────────────────────────────────────────────────────────

    // Update is the only caller that writes to what it loads; every other one turns the row straight
    // into a DTO, so it reads through the untracked overload.
    private async Task<InsurancePolicy?> LoadWithDetails(Guid id, CancellationToken cancellationToken = default) =>
        await WithDetails(context.InsurancePolicies.AsNoTracking())
            .FirstOrDefaultAsync(p => p.InsurancePolicyId == id, cancellationToken);

    private async Task<InsurancePolicy?> LoadWithDetailsForUpdate(Guid id, CancellationToken cancellationToken = default) =>
        await WithDetails(context.InsurancePolicies)
            .FirstOrDefaultAsync(p => p.InsurancePolicyId == id, cancellationToken);

    private static IQueryable<InsurancePolicy> WithDetails(IQueryable<InsurancePolicy> policies) => policies
        .Include(p => p.InsuredAccount)
        .Include(p => p.Renewals)
            .ThenInclude(r => r.Files)
                .ThenInclude(f => f.FileMetadata)
        .Include(p => p.Files)
            .ThenInclude(f => f.FileMetadata);

    /// <summary>
    /// Fills in the accrued-premium figure: every period starting on or before the current period
    /// ends, converted into the current period's currency.
    ///
    /// <para>
    /// It is a separate async step rather than part of <c>ToDto</c> because the conversion needs
    /// exchange rates, and <c>ToDto</c> is a pure static projection. A period whose currency has no
    /// rate to the current one is <b>skipped</b>, not added at face value — the same posture the
    /// portfolio summary takes with <c>UnconvertedCurrencies</c> — and the period count reports what
    /// was actually summed, so the number and its caption cannot disagree.
    /// </para>
    /// </summary>
    private async Task<ExistingInsurancePolicy> WithAccruedPremium(
        ExistingInsurancePolicy dto, CancellationToken cancellationToken = default)
    {
        if (dto.CurrentRenewal is not { } current)
        {
            return dto;
        }

        var currency = current.PremiumCurrencyCode;
        var total = 0m;
        var counted = 0;
        foreach (var renewal in dto.Renewals.Where(r => r.FromDate.Date <= current.ToDate.Date))
        {
            var converted = string.Equals(renewal.PremiumCurrencyCode, currency, StringComparison.OrdinalIgnoreCase)
                ? renewal.Premium
                : await conversion.ConvertAsync(renewal.Premium, renewal.PremiumCurrencyCode, currency);
            if (converted is null)
            {
                continue;
            }

            total += converted.Value;
            counted++;
        }

        dto.AccruedPremium = total;
        dto.AccruedPremiumCurrencyCode = currency;
        dto.AccruedPremiumPeriods = counted;
        return dto;
    }

    private static ExistingInsurancePolicy ToDto(InsurancePolicy policy, InsurerReference insurer, DateTime today, int windowDays)
    {
        var (status, current) = EvaluateCoverage(policy, today, windowDays);

        return new ExistingInsurancePolicy
        {
            InsurancePolicyId = policy.InsurancePolicyId,
            Name = policy.Name,
            PolicyNumber = policy.PolicyNumber,
            Type = policy.Type.Adapt<DtoInsurancePolicyType>(),
            Insurer = insurer,
            InsuredAccount = policy.InsuredAccount is null ? null : ToInsuredAccountReference(policy.InsuredAccount),
            Notes = policy.Notes,
            CoverageStatus = status,
            CurrentRenewal = current is null ? null : ToRenewalDto(current),
            Renewals = policy.Renewals
                .OrderByDescending(r => r.FromDate)
                .ThenByDescending(r => r.CreatedAtUtc)
                .Select(ToRenewalDto)
                .ToList(),
            Files = policy.Files
                .Where(f => f.FileMetadata is not null)
                .OrderBy(f => f.AttachedAtUtc)
                .Select(ToPolicyFileDto)
                .ToList(),
            Archived = policy.Archived,
            CreatedAtUtc = policy.CreatedAtUtc,
        };
    }

    private static InsurerReference ToInsurerReference(ContactRef insurer) => new()
    {
        ContactId = insurer.ContactId,
        Name = insurer.Name,
        Type = insurer.Type,
    };

    // Builds the read reference from a batched-resolve result. InsurerId is required/non-null, but the
    // linked contact may have been deleted since; a missing ref degrades to a stable placeholder rather
    // than crashing or dropping the policy.
    private static InsurerReference BuildInsurerReference(Guid insurerId, IReadOnlyDictionary<Guid, ContactRef> refs) =>
        refs.TryGetValue(insurerId, out var insurer)
            ? ToInsurerReference(insurer)
            : new InsurerReference { ContactId = insurerId, Name = "(unknown)", Type = default };

    private async Task<InsurerReference> ResolveInsurerReference(Guid insurerId, CancellationToken cancellationToken)
    {
        var refs = await contactLookup.ResolveRefsAsync([insurerId], cancellationToken);
        return BuildInsurerReference(insurerId, refs);
    }

    private static InsuredAccountReference ToInsuredAccountReference(Account account) => new()
    {
        AccountId = account.AccountId,
        Name = account.Name,
        Type = account.AccountType.Adapt<DtoAccountType>(),
    };

    private static ExistingPolicyRenewal ToRenewalDto(PolicyRenewal renewal) => new()
    {
        PolicyRenewalId = renewal.PolicyRenewalId,
        InsurancePolicyId = renewal.InsurancePolicyId,
        FromDate = renewal.FromDate,
        ToDate = renewal.ToDate,
        Premium = renewal.Premium,
        PremiumCurrencyCode = renewal.PremiumCurrencyCode,
        CoverageAmount = renewal.CoverageAmount,
        CoverageCurrencyCode = renewal.CoverageCurrencyCode,
        Notes = renewal.Notes,
        CreatedAtUtc = renewal.CreatedAtUtc,
        Files = (renewal.Files ?? new List<PolicyRenewalFile>())
            .Where(f => f.FileMetadata is not null)
            .OrderBy(f => f.AttachedAtUtc)
            .Select(ToRenewalFileDto)
            .ToList(),
    };

    private static ExistingInsurancePolicyFile ToPolicyFileDto(InsurancePolicyFile file) => new()
    {
        Id = file.Id,
        InsurancePolicyId = file.InsurancePolicyId,
        FileMetadata = file.FileMetadata!.Adapt<ExistingFileMetadata>(),
        FileType = file.FileType.Adapt<DtoPolicyFileType>(),
        EffectiveDate = file.EffectiveDate,
        AttachedByUserId = file.AttachedByUserId,
        AttachedAtUtc = file.AttachedAtUtc,
    };

    private static ExistingPolicyRenewalFile ToRenewalFileDto(PolicyRenewalFile file) => new()
    {
        Id = file.Id,
        PolicyRenewalId = file.PolicyRenewalId,
        FileMetadata = file.FileMetadata!.Adapt<ExistingFileMetadata>(),
        FileType = file.FileType.Adapt<DtoPolicyFileType>(),
        EffectiveDate = file.EffectiveDate,
        AttachedByUserId = file.AttachedByUserId,
        AttachedAtUtc = file.AttachedAtUtc,
    };
}
