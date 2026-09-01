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

    public InsuranceService(
        OdysseyContext context,
        CurrencyConversionService conversion,
        IContactLookup contactLookup,
        TimeProvider timeProvider,
        ISystemSettingsLookup systemSettingsLookup)
    {
        this.context = context;
        this.conversion = conversion;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
        this.systemSettingsLookup = systemSettingsLookup;
        // No advisory lock on this path (issue #27 §5). The three contact link tables carry real
        // Restrict FKs, so the DATABASE arbitrates the validate-then-persist race against a concurrent
        // contact delete, and its violation maps to a 409 rather than surfacing as a 500. The lock that
        // used to stand here pinned a connection for a blocking ten-second acquire on every write, and
        // was a documented no-op on non-relational providers — so it never protected the fast tiers
        // either.
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
            .Include(p => p.Insurers)
            .AsSplitQuery()
            .AsQueryable();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            // Pre-resolve the matching insurer ids via the lookup and filter by insurer-link membership
            // — OR-combined with the policy-field match, so the term matches on policy name OR insurer
            // name, exactly as before. Insured contacts and beneficiaries are deliberately NOT searched
            // (§10 #6): an insurance.read holder can already list every policy, so it adds no capability
            // — but it would make the contacts surface's names queryable through a second door.
            var insurerMatchIds = (await contactLookup.SearchIdsByNameAsync(term, cancellationToken)).ToHashSet();
            q = q.Where(p =>
                EF.Functions.Like(p.Name, pattern) ||
                p.Insurers.Any(i => insurerMatchIds.Contains(i.ContactId)));
        }

        // API-only in v1 (Non-Goal 7): matches a policy naming the contact in ANY of the three contact
        // collections. The row does not say WHICH — a future filter UI that needs to explain the match
        // will need a matched-kind projection.
        var contactFilter = (query.ContactIds ?? []).Distinct().ToList();
        if (contactFilter.Count > 0)
        {
            q = q.Where(p =>
                p.Insurers.Any(i => contactFilter.Contains(i.ContactId)) ||
                p.InsuredContacts.Any(i => contactFilter.Contains(i.ContactId)) ||
                p.Beneficiaries.Any(b => contactFilter.Contains(b.ContactId)));
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
        // A document lives on a period, so the policy's count is the sum across its periods
        // (issue #26). Still one grouped query over indexed columns for the whole filtered set —
        // the same shape and cost class as the policy-level query it replaces, and no N+1.
        var fileCounts = await context.PolicyRenewalFiles
            .Where(f => policyIds.Contains(f.PolicyRenewal!.InsurancePolicyId))
            .GroupBy(f => f.PolicyRenewal!.InsurancePolicyId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        // The three non-insurer collections are COUNTED on the list row, not named — one grouped query
        // each over the (InsurancePolicyId, TargetId) unique index, so the round-trip count is fixed and
        // independent of how many links each policy holds. Counts are of link ROWS, never of resolved
        // names: a count aligned onto resolved names would make a contact whose links are all unnamed
        // look erasable when it is not (§9).
        var insuredAccountCounts = await CountLinksAsync(
            context.InsurancePolicyInsuredAccounts.Where(l => policyIds.Contains(l.InsurancePolicyId)),
            l => l.InsurancePolicyId, cancellationToken);
        var insuredContactCounts = await CountLinksAsync(
            context.InsurancePolicyInsuredContacts.Where(l => policyIds.Contains(l.InsurancePolicyId)),
            l => l.InsurancePolicyId, cancellationToken);
        var beneficiaryCounts = await CountLinksAsync(
            context.InsurancePolicyBeneficiaries.Where(l => policyIds.Contains(l.InsurancePolicyId)),
            l => l.InsurancePolicyId, cancellationToken);

        // Resolve every insurer contact across the whole page in ONE batched lookup — the insurers are
        // the one collection the row names rather than counts, because they are on its meta line.
        var insurerIds = policies.SelectMany(p => p.Insurers.Select(i => i.ContactId)).Distinct().ToList();
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
                Insurers = BuildContactReferences(p.Insurers.Select(i => new LinkTerm(i.ContactId, i.FromDate, i.ToDate)), insurerRefs),
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
                InsuredAccountCount = insuredAccountCounts.GetValueOrDefault(p.InsurancePolicyId),
                InsuredContactCount = insuredContactCounts.GetValueOrDefault(p.InsurancePolicyId),
                BeneficiaryCount = beneficiaryCounts.GetValueOrDefault(p.InsurancePolicyId),
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

        return await ProjectAsync(policy, cancellationToken);
    }

    public async Task<ExistingInsurancePolicy> Create(
        NewInsurancePolicy request, string? userId = null, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var policy = new InsurancePolicy
        {
            Name = request.Name,
            PolicyNumber = request.PolicyNumber,
            Type = request.Type.Adapt<ContextInsurancePolicyType>(),
            Notes = request.Notes,
            Archived = null,
            CreatedAtUtc = now,
        };

        // On create there are no stored links, so "null" and "[]" mean the same thing and every
        // submitted id is an ADDED id — validated for existence and non-archived state, deduped, and
        // checked against the live effective cap.
        var links = await ResolveLinkWriteAsync(
            storedInsurers: [],
            storedInsuredAccounts: [],
            storedInsuredContacts: [],
            storedBeneficiaries: [],
            request.InsurerIds,
            request.InsuredAccountIds,
            request.InsuredContactIds,
            request.BeneficiaryIds,
            cancellationToken);

        foreach (var contactId in links.Insurers.Added)
            policy.Insurers.Add(new InsurancePolicyInsurer { ContactId = contactId });
        foreach (var accountId in links.InsuredAccounts.Added)
            policy.InsuredAccounts.Add(new InsurancePolicyInsuredAccount { AccountId = accountId });
        foreach (var contactId in links.InsuredContacts.Added)
            policy.InsuredContacts.Add(new InsurancePolicyInsuredContact { ContactId = contactId });
        foreach (var contactId in links.Beneficiaries.Added)
            policy.Beneficiaries.Add(new InsurancePolicyBeneficiary
            {
                ContactId = contactId,
                CreatedByUserId = userId,
                CreatedAtUtc = now,
            });

        context.InsurancePolicies.Add(policy);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await LoadWithDetails(policy.InsurancePolicyId, cancellationToken);
        return await ProjectAsync(loaded!, cancellationToken);
    }

    public async Task<ExistingInsurancePolicy?> Update(
        Guid id, UpdateInsurancePolicy request, string? userId = null, CancellationToken cancellationToken = default)
    {
        var policy = await LoadWithDetailsForUpdate(id, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        var links = await ResolveLinkWriteAsync(
            policy.Insurers.Select(l => l.ContactId).ToList(),
            policy.InsuredAccounts.Select(l => l.AccountId).ToList(),
            policy.InsuredContacts.Select(l => l.ContactId).ToList(),
            policy.Beneficiaries.Select(l => l.ContactId).ToList(),
            request.InsurerIds,
            request.InsuredAccountIds,
            request.InsuredContactIds,
            request.BeneficiaryIds,
            cancellationToken);

        policy.Name = request.Name;
        policy.PolicyNumber = request.PolicyNumber;
        policy.Type = request.Type.Adapt<ContextInsurancePolicyType>();
        policy.Notes = request.Notes;
        // Archive (preserving the original archive stamp) or unarchive per the request.
        policy.Archived = request.Archived
            ? policy.Archived ?? timeProvider.GetUtcNow().UtcDateTime
            : null;

        var now = timeProvider.GetUtcNow().UtcDateTime;

        ApplyDiff(policy.Insurers, links.Insurers, l => l.ContactId,
            contactId => new InsurancePolicyInsurer { InsurancePolicyId = id, ContactId = contactId });
        ApplyDiff(policy.InsuredAccounts, links.InsuredAccounts, l => l.AccountId,
            accountId => new InsurancePolicyInsuredAccount { InsurancePolicyId = id, AccountId = accountId });
        ApplyDiff(policy.InsuredContacts, links.InsuredContacts, l => l.ContactId,
            contactId => new InsurancePolicyInsuredContact { InsurancePolicyId = id, ContactId = contactId });
        // An existing beneficiary row keeps its original author: re-saving a policy never rewrites who
        // named a beneficiary, so only the newly-inserted rows take the calling user's id.
        ApplyDiff(policy.Beneficiaries, links.Beneficiaries, l => l.ContactId,
            contactId => new InsurancePolicyBeneficiary
            {
                InsurancePolicyId = id,
                ContactId = contactId,
                CreatedByUserId = userId,
                CreatedAtUtc = now,
            });

        // The policy scalars and all four link diffs commit in ONE SaveChangesAsync: a partial write is
        // impossible.
        await context.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadWithDetails(id, cancellationToken);
        return await ProjectAsync(reloaded!, cancellationToken);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        // Hard delete: removes the policy and cascades its renewals, file-join rows and the four link
        // collections. The underlying FileMetadata/blobs are owned by the files API and are left intact
        // (same as detach). Children are loaded so the cascade also applies under the EF InMemory
        // provider (used by tests), which enforces no foreign keys and so applies no database cascade.
        // Tracked removal, never ExecuteDelete — that lives in EntityFrameworkCore.Relational and
        // throws on InMemory, which is the very tier this Include chain exists to serve.
        var policy = await context.InsurancePolicies
            .Include(p => p.Renewals)
                .ThenInclude(r => r.Files)
            .Include(p => p.Insurers)
            .Include(p => p.InsuredAccounts)
            .Include(p => p.InsuredContacts)
            .Include(p => p.Beneficiaries)
            .AsSplitQuery()
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

    // ── Parties: one link at a time (design system, AddPolicyPartyModal) ──────────

    /// <summary>
    /// Links ONE contact or account to the policy in one of its four roles, with an optional term.
    /// The full-set <see cref="Update"/> path is untouched and still works; this is the write the
    /// New party dialog makes, and the only one that can carry a term.
    /// </summary>
    public async Task<ExistingInsurancePolicy?> AddParty(
        Guid policyId, InsurancePolicyPartyRequest request, string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var policy = await LoadWithDetailsForUpdate(policyId, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        await WritePartyAsync(policy, request, existing: null, userId, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadWithDetails(policyId, cancellationToken);
        return await ProjectAsync(reloaded!, cancellationToken);
    }

    /// <summary>
    /// Re-writes ONE existing party — its role, its target, its dates, or any combination. The route
    /// names the link as it stands and the body names what it should become, so a party moved between
    /// roles stays ONE party: the old row is dropped and the new one written in the same
    /// <c>SaveChangesAsync</c>, never left as two.
    /// </summary>
    public async Task<ExistingInsurancePolicy?> UpdateParty(
        Guid policyId, InsurancePartyRole role, Guid targetId, InsurancePolicyPartyRequest request,
        string? userId = null, CancellationToken cancellationToken = default)
    {
        var policy = await LoadWithDetailsForUpdate(policyId, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        var existing = FindParty(policy, role, targetId);
        if (existing is null)
        {
            throw new DomainNotFoundException(
                $"Policy {policyId} has no {PartyNoun(role)} party for {targetId}.");
        }

        await WritePartyAsync(policy, request, existing, userId, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadWithDetails(policyId, cancellationToken);
        return await ProjectAsync(reloaded!, cancellationToken);
    }

    /// <summary>
    /// Detaches ONE party. The linked contact or account is never touched — a party is a link, so this
    /// removes the link row and nothing else.
    /// </summary>
    /// <remarks>
    /// Unlike an omission from the full-set <see cref="Update"/>, this is allowed even when the target
    /// is archived or unresolvable. §9's 422 exists because an omission cannot be told apart from a
    /// caller that never saw the member; a DELETE that names the link says exactly what it means. The
    /// UI still withholds the affordance on an unnamed member, because its record is not in the picker
    /// and the edit dialog could not round-trip it.
    /// </remarks>
    public async Task<bool> RemoveParty(
        Guid policyId, InsurancePartyRole role, Guid targetId, CancellationToken cancellationToken = default)
    {
        var policy = await LoadWithDetailsForUpdate(policyId, cancellationToken);
        var existing = policy is null ? null : FindParty(policy, role, targetId);
        if (existing is null)
        {
            return false;
        }

        RemoveParty(policy!, role, existing);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The shared body of the add and the edit: validate the desired link, drop the row being replaced
    /// (if any), and insert the new one. Nothing is saved here — the caller commits, so a role move is
    /// one transaction.
    /// </summary>
    private async Task WritePartyAsync(
        InsurancePolicy policy, InsurancePolicyPartyRequest request, object? existing, string? userId,
        CancellationToken cancellationToken)
    {
        var role = request.Role;
        ValidatePartyTerm(policy, request);

        // A target already in the requested role is a duplicate — unless it IS the row being edited,
        // which is a date-only change.
        var duplicate = FindParty(policy, role, request.TargetId);
        if (duplicate is not null && !ReferenceEquals(duplicate, existing))
        {
            throw new DomainConflictException(
                $"That {PartyNoun(role)} is already linked to this policy.");
        }

        // Only a NEW target is validated for existence and archived state — re-dating a party whose
        // contact was archived meanwhile must not fail, the same rule the full-set diff applies.
        var alreadyLinked = existing is not null && PartyTargetId(existing) == request.TargetId;
        if (!alreadyLinked)
        {
            await EnsurePartyTargetAvailable(role, request.TargetId, cancellationToken);
        }

        var field = PartyField(role);
        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        // The resulting ROW count: the collection's current size, plus one unless this write is
        // re-using a row already in it.
        var resulting = PartyCount(policy, role) + (duplicate is not null ? 0 : 1);
        if (resulting > caps.MaxLinksPerPolicy)
        {
            throw new DomainUnprocessableException(
                $"A policy takes at most {caps.MaxLinksPerPolicy} {field.Noun}; this would leave {resulting}.",
                field.Property);
        }

        // The old row goes first, so a party moved between roles is one party rather than two.
        string? beneficiaryAuthor = null;
        DateTime? beneficiaryStamp = null;
        if (existing is not null)
        {
            if (existing is InsurancePolicyBeneficiary previous)
            {
                // A beneficiary that stays a beneficiary keeps its original author: re-dating a
                // designation never rewrites who named it.
                beneficiaryAuthor = previous.CreatedByUserId;
                beneficiaryStamp = previous.CreatedAtUtc;
            }

            RemoveParty(policy, PartyRoleOf(existing), existing);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var policyId = policy.InsurancePolicyId;
        switch (role)
        {
            case InsurancePartyRole.Insurer:
                policy.Insurers.Add(new InsurancePolicyInsurer
                {
                    InsurancePolicyId = policyId,
                    ContactId = request.TargetId,
                    FromDate = request.FromDate,
                    ToDate = request.ToDate,
                });
                break;
            case InsurancePartyRole.InsuredAccount:
                policy.InsuredAccounts.Add(new InsurancePolicyInsuredAccount
                {
                    InsurancePolicyId = policyId,
                    AccountId = request.TargetId,
                    FromDate = request.FromDate,
                    ToDate = request.ToDate,
                });
                break;
            case InsurancePartyRole.InsuredContact:
                policy.InsuredContacts.Add(new InsurancePolicyInsuredContact
                {
                    InsurancePolicyId = policyId,
                    ContactId = request.TargetId,
                    FromDate = request.FromDate,
                    ToDate = request.ToDate,
                });
                break;
            default:
                policy.Beneficiaries.Add(new InsurancePolicyBeneficiary
                {
                    InsurancePolicyId = policyId,
                    ContactId = request.TargetId,
                    CreatedByUserId = beneficiaryAuthor ?? userId,
                    CreatedAtUtc = beneficiaryStamp ?? now,
                    FromDate = request.FromDate,
                    ToDate = request.ToDate,
                });
                break;
        }
    }

    /// <summary>
    /// A party's term is its own fact, with one tie to the policy: it cannot begin before cover ever
    /// did. Both dates are optional, and null is the default term — the policy's own extent — not an
    /// unset value.
    /// </summary>
    private static void ValidatePartyTerm(InsurancePolicy policy, InsurancePolicyPartyRequest request)
    {
        if (request.FromDate is { } from && request.ToDate is { } to && to.Date < from.Date)
        {
            throw new DomainValidationException(
                "ToDate must be on or after FromDate.",
                code: null,
                field: nameof(InsurancePolicyPartyRequest.ToDate));
        }

        if (request.FromDate is { } start && policy.Renewals.Count > 0)
        {
            var coverBegan = policy.Renewals.Min(r => r.FromDate).Date;
            if (start.Date < coverBegan)
            {
                throw new DomainValidationException(
                    $"Cover began {coverBegan:yyyy-MM-dd} — a party cannot be in the role before that.",
                    code: null,
                    field: nameof(InsurancePolicyPartyRequest.FromDate));
            }
        }
    }

    private async Task EnsurePartyTargetAvailable(
        InsurancePartyRole role, Guid targetId, CancellationToken cancellationToken)
    {
        var field = PartyField(role);
        if (role == InsurancePartyRole.InsuredAccount)
        {
            var live = await context.Accounts
                .AnyAsync(a => a.AccountId == targetId && a.Archived == null, cancellationToken);
            if (!live)
            {
                throw new DomainValidationException(
                    $"{field.Property} contains {targetId}, which does not reference an existing, non-archived record.",
                    code: null,
                    field: nameof(InsurancePolicyPartyRequest.TargetId));
            }

            return;
        }

        var refs = await contactLookup.ResolveRefsAsync([targetId], cancellationToken);
        if (!refs.TryGetValue(targetId, out var contact) || contact.Archived is not null)
        {
            throw new DomainValidationException(
                $"{field.Property} contains {targetId}, which does not reference an existing, non-archived record.",
                code: null,
                field: nameof(InsurancePolicyPartyRequest.TargetId));
        }
    }

    private static object? FindParty(InsurancePolicy policy, InsurancePartyRole role, Guid targetId) => role switch
    {
        InsurancePartyRole.Insurer => policy.Insurers.FirstOrDefault(l => l.ContactId == targetId),
        InsurancePartyRole.InsuredAccount => policy.InsuredAccounts.FirstOrDefault(l => l.AccountId == targetId),
        InsurancePartyRole.InsuredContact => policy.InsuredContacts.FirstOrDefault(l => l.ContactId == targetId),
        _ => policy.Beneficiaries.FirstOrDefault(l => l.ContactId == targetId),
    };

    private void RemoveParty(InsurancePolicy policy, InsurancePartyRole role, object link)
    {
        switch (role)
        {
            case InsurancePartyRole.Insurer:
                policy.Insurers.Remove((InsurancePolicyInsurer)link);
                break;
            case InsurancePartyRole.InsuredAccount:
                policy.InsuredAccounts.Remove((InsurancePolicyInsuredAccount)link);
                break;
            case InsurancePartyRole.InsuredContact:
                policy.InsuredContacts.Remove((InsurancePolicyInsuredContact)link);
                break;
            default:
                policy.Beneficiaries.Remove((InsurancePolicyBeneficiary)link);
                break;
        }

        context.Remove(link);
    }

    private static InsurancePartyRole PartyRoleOf(object link) => link switch
    {
        InsurancePolicyInsurer => InsurancePartyRole.Insurer,
        InsurancePolicyInsuredAccount => InsurancePartyRole.InsuredAccount,
        InsurancePolicyInsuredContact => InsurancePartyRole.InsuredContact,
        _ => InsurancePartyRole.Beneficiary,
    };

    private static Guid PartyTargetId(object link) => link switch
    {
        InsurancePolicyInsurer insurer => insurer.ContactId,
        InsurancePolicyInsuredAccount account => account.AccountId,
        InsurancePolicyInsuredContact insured => insured.ContactId,
        _ => ((InsurancePolicyBeneficiary)link).ContactId,
    };

    private static int PartyCount(InsurancePolicy policy, InsurancePartyRole role) => role switch
    {
        InsurancePartyRole.Insurer => policy.Insurers.Count,
        InsurancePartyRole.InsuredAccount => policy.InsuredAccounts.Count,
        InsurancePartyRole.InsuredContact => policy.InsuredContacts.Count,
        _ => policy.Beneficiaries.Count,
    };

    private static LinkField PartyField(InsurancePartyRole role) => role switch
    {
        InsurancePartyRole.Insurer => InsurersField,
        InsurancePartyRole.InsuredAccount => InsuredAccountsField,
        InsurancePartyRole.InsuredContact => InsuredContactsField,
        _ => BeneficiariesField,
    };

    private static string PartyNoun(InsurancePartyRole role) => role switch
    {
        InsurancePartyRole.Insurer => "insurer",
        InsurancePartyRole.InsuredAccount => "insured account",
        InsurancePartyRole.InsuredContact => "insured contact",
        _ => "beneficiary",
    };

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

    // ── Link collections: validation, diff and cap enforcement (issue #27 §9) ─────

    /// <summary>The resolved write plan for one collection: what to add, and what to remove.</summary>
    private sealed record LinkDiff(IReadOnlyList<Guid> Added, IReadOnlySet<Guid> Removed)
    {
        public static readonly LinkDiff Unchanged = new([], new HashSet<Guid>());
    }

    private sealed record LinkWrite(
        LinkDiff Insurers,
        LinkDiff InsuredAccounts,
        LinkDiff InsuredContacts,
        LinkDiff Beneficiaries);

    /// <summary>
    /// One collection's write field: the problem-details errors key, the noun for a cap message, and
    /// the routes that actually remove an unnamed member.
    /// </summary>
    /// <remarks>
    /// <see cref="RemovalAdvice"/> is per-collection rather than one shared sentence because the two
    /// target kinds do not have the same routes. A contact link can be detached wholesale
    /// (<c>DELETE /api/contacts/{id}?detachInsuranceLinks=true</c>); an <b>account</b> link has no
    /// such endpoint, so telling that caller to "detach the contact's insurance links" names an
    /// operation that does not exist for the field they were writing.
    /// </remarks>
    private sealed record LinkField(string Property, string Noun, string RemovalAdvice);

    private const string ContactRemovalAdvice =
        "Detach the contact's insurance links, or unarchive the contact and then remove it.";

    private const string AccountRemovalAdvice =
        "Unarchive the account and then remove it.";

    private static readonly LinkField InsurersField =
        new(nameof(UpdateInsurancePolicy.InsurerIds), "insurers", ContactRemovalAdvice);
    private static readonly LinkField InsuredAccountsField =
        new(nameof(UpdateInsurancePolicy.InsuredAccountIds), "insured accounts", AccountRemovalAdvice);
    private static readonly LinkField InsuredContactsField =
        new(nameof(UpdateInsurancePolicy.InsuredContactIds), "insured contacts", ContactRemovalAdvice);
    private static readonly LinkField BeneficiariesField =
        new(nameof(UpdateInsurancePolicy.BeneficiaryIds), "beneficiaries", ContactRemovalAdvice);

    /// <summary>
    /// Turns the four submitted arrays into four diffs, in a fixed number of round trips: one batched
    /// contact resolve across the union of all three contact collections, and one account existence
    /// query.
    ///
    /// <para>
    /// The resolved contact set is <b>added ∪ stored</b>, not added alone — the same single round trip,
    /// but the diff needs each <i>stored</i> link's current availability and has no other source for it.
    /// </para>
    /// </summary>
    private async Task<LinkWrite> ResolveLinkWriteAsync(
        IReadOnlyCollection<Guid> storedInsurers,
        IReadOnlyCollection<Guid> storedInsuredAccounts,
        IReadOnlyCollection<Guid> storedInsuredContacts,
        IReadOnlyCollection<Guid> storedBeneficiaries,
        List<Guid>? requestedInsurers,
        List<Guid>? requestedInsuredAccounts,
        List<Guid>? requestedInsuredContacts,
        List<Guid>? requestedBeneficiaries,
        CancellationToken cancellationToken)
    {
        // Duplicates are de-duplicated rather than rejected, following PhotoService: a set-valued field
        // is naturally idempotent.
        var insurers = requestedInsurers?.Distinct().ToList();
        var insuredAccounts = requestedInsuredAccounts?.Distinct().ToList();
        var insuredContacts = requestedInsuredContacts?.Distinct().ToList();
        var beneficiaries = requestedBeneficiaries?.Distinct().ToList();

        var contactIds = (insurers ?? []).Concat(insuredContacts ?? []).Concat(beneficiaries ?? [])
            .Concat(storedInsurers).Concat(storedInsuredContacts).Concat(storedBeneficiaries)
            .Distinct()
            .ToList();
        var contactRefs = contactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : (IReadOnlyDictionary<Guid, ContactRef>)await contactLookup.ResolveRefsAsync(contactIds, cancellationToken);

        var accountIds = (insuredAccounts ?? []).Concat(storedInsuredAccounts).Distinct().ToList();
        var liveAccountIds = accountIds.Count == 0
            ? []
            : (await context.Accounts
                .Where(a => accountIds.Contains(a.AccountId) && a.Archived == null)
                .Select(a => a.AccountId)
                .ToListAsync(cancellationToken)).ToHashSet();

        bool ContactAvailable(Guid id) => contactRefs.TryGetValue(id, out var c) && c.Archived is null;

        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);

        return new LinkWrite(
            Diff(storedInsurers, insurers, ContactAvailable, InsurersField, caps.MaxLinksPerPolicy),
            Diff(storedInsuredAccounts, insuredAccounts, liveAccountIds.Contains, InsuredAccountsField, caps.MaxLinksPerPolicy),
            Diff(storedInsuredContacts, insuredContacts, ContactAvailable, InsuredContactsField, caps.MaxLinksPerPolicy),
            Diff(storedBeneficiaries, beneficiaries, ContactAvailable, BeneficiariesField, caps.MaxLinksPerPolicy));
    }

    /// <summary>
    /// One collection's diff, with every rule §9 states applied in the order they have to be:
    /// <list type="number">
    /// <item><c>null</c> leaves the collection untouched; <c>[]</c> clears it.</item>
    /// <item>Only <b>added</b> ids are validated for existence and archived state, so an unrelated edit
    /// does not 400 because a still-linked target was archived meanwhile.</item>
    /// <item>A stored link whose target is not <c>Available</c> at write time is <b>retained</b>, and
    /// omitting it is <b>refused</b> with a 422 rather than silently ignored or silently honoured.</item>
    /// <item>The cap is checked against <c>submitted ∪ retained</c> — the resulting ROW count — so a
    /// collection cannot exceed a limit it would then be unable to round-trip.</item>
    /// </list>
    /// </summary>
    private static LinkDiff Diff(
        IReadOnlyCollection<Guid> stored,
        List<Guid>? requested,
        Func<Guid, bool> isAvailable,
        LinkField field,
        int effectiveCap)
    {
        if (requested is null)
        {
            return LinkDiff.Unchanged;
        }

        var storedSet = stored.ToHashSet();
        var requestedSet = requested.ToHashSet();

        foreach (var id in requested.Where(id => !storedSet.Contains(id) && !isAvailable(id)))
        {
            throw new DomainValidationException(
                $"{field.Property} contains {id}, which does not reference an existing, non-archived record.",
                code: null,
                field: field.Property);
        }

        // Retained: a stored link the write cannot remove, because its target is no longer Available
        // and so was never offered as a removable chip. Stated in terms a stateless PUT can evaluate —
        // there is no ETag, version token or server-side record of the caller's prior GET.
        var retained = storedSet.Where(id => !isAvailable(id)).ToHashSet();

        var omittedUnnamed = retained.Where(id => !requestedSet.Contains(id)).ToList();
        if (omittedUnnamed.Count > 0)
        {
            // Refused loudly rather than silently retained: a 200 whose body did not match what was
            // asked for would misdescribe the write, and it would also swallow a genuinely deliberate
            // removal in the race where the target was Available at load and archived before the save
            // landed. The detach path is named FIRST — unarchiving is globally visible and momentarily
            // re-discloses the name on every policy linking the contact (§10 #5).
            var ids = string.Join(", ", omittedUnnamed.OrderBy(id => id));
            throw new DomainUnprocessableException(
                $"{field.Property} omits {ids}, which cannot be removed here: the linked record is "
                + "archived or no longer resolves, so it has no name to show and no chip to remove. "
                + field.RemovalAdvice,
                field.Property);
        }

        var resulting = requestedSet.Union(retained).ToHashSet();
        if (resulting.Count > effectiveCap)
        {
            throw new DomainUnprocessableException(
                $"A policy takes at most {effectiveCap} {field.Noun}; this would leave {resulting.Count}.",
                field.Property);
        }

        return new LinkDiff(
            [.. requested.Where(id => !storedSet.Contains(id))],
            storedSet.Where(id => !resulting.Contains(id)).ToHashSet());
    }

    /// <summary>Applies one resolved diff to a tracked link collection.</summary>
    private void ApplyDiff<TLink>(
        ICollection<TLink> links,
        LinkDiff diff,
        Func<TLink, Guid> targetId,
        Func<Guid, TLink> create)
        where TLink : class
    {
        if (diff.Removed.Count > 0)
        {
            foreach (var link in links.Where(l => diff.Removed.Contains(targetId(l))).ToList())
            {
                links.Remove(link);
                context.Remove(link);
            }
        }

        foreach (var id in diff.Added)
        {
            links.Add(create(id));
        }
    }

    private static async Task<Dictionary<Guid, int>> CountLinksAsync<TLink>(
        IQueryable<TLink> links, System.Linq.Expressions.Expression<Func<TLink, Guid>> policyId,
        CancellationToken cancellationToken) =>
        await links
            .GroupBy(policyId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

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

    // AsSplitQuery: five collection Includes on one query produce a cartesian product. This is what
    // Draft v1's "single-query shape" claim overlooked — the detail load has always materialised more
    // than one collection.
    private static IQueryable<InsurancePolicy> WithDetails(IQueryable<InsurancePolicy> policies) => policies
        .Include(p => p.Renewals)
            .ThenInclude(r => r.Files)
                .ThenInclude(f => f.FileMetadata)
        .Include(p => p.Insurers)
        .Include(p => p.InsuredAccounts)
        .Include(p => p.InsuredContacts)
        .Include(p => p.Beneficiaries)
        .AsSplitQuery();

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
        var accrued = dto.Renewals.Where(r => r.FromDate.Date <= current.ToDate.Date).ToList();

        // One rate lookup for every currency in the set, not one per period: ConvertAsync would issue
        // a query per renewal, which is the shape of problem the rest of this service avoids.
        var rates = await conversion.GetLatestRatesToAsync(
            currency, accrued.Select(r => r.PremiumCurrencyCode), cancellationToken);

        var total = 0m;
        var counted = 0;
        foreach (var renewal in accrued)
        {
            decimal? converted =
                string.Equals(renewal.PremiumCurrencyCode, currency, StringComparison.OrdinalIgnoreCase)
                    ? renewal.Premium
                    : rates.TryGetValue(CurrencyValidationService.Normalize(renewal.PremiumCurrencyCode), out var rate)
                        ? renewal.Premium * rate
                        : null;
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

    /// <summary>
    /// The detail projection: one batched contact resolve for the union of the three contact
    /// collections, one batched account query for the fourth, then the accrued-premium pass.
    /// </summary>
    private async Task<ExistingInsurancePolicy> ProjectAsync(InsurancePolicy policy, CancellationToken cancellationToken)
    {
        var contactIds = policy.Insurers.Select(l => l.ContactId)
            .Concat(policy.InsuredContacts.Select(l => l.ContactId))
            .Concat(policy.Beneficiaries.Select(l => l.ContactId))
            .Distinct()
            .ToList();
        var contactRefs = contactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : (IReadOnlyDictionary<Guid, ContactRef>)await contactLookup.ResolveRefsAsync(contactIds, cancellationToken);

        var accountIds = policy.InsuredAccounts.Select(l => l.AccountId).Distinct().ToList();
        var accounts = accountIds.Count == 0
            ? new Dictionary<Guid, Account>()
            : await context.Accounts
                .AsNoTracking()
                .Where(a => accountIds.Contains(a.AccountId))
                .ToDictionaryAsync(a => a.AccountId, cancellationToken);

        var window = (await systemSettingsLookup.GetInsurancePolicySettingsAsync(cancellationToken)).ExpiringSoonWindowDays;
        var dto = ToDto(policy, Today, window);

        dto.Insurers = BuildContactReferences(
            policy.Insurers.Select(l => new LinkTerm(l.ContactId, l.FromDate, l.ToDate)), contactRefs);
        dto.InsuredContacts = BuildContactReferences(
            policy.InsuredContacts.Select(l => new LinkTerm(l.ContactId, l.FromDate, l.ToDate)), contactRefs);
        dto.Beneficiaries = BuildContactReferences(
            policy.Beneficiaries.Select(l => new LinkTerm(l.ContactId, l.FromDate, l.ToDate)), contactRefs);
        dto.InsuredAccounts = BuildAccountReferences(
            policy.InsuredAccounts.Select(l => new LinkTerm(l.AccountId, l.FromDate, l.ToDate)), accounts);

        return await WithAccruedPremium(dto, cancellationToken);
    }

    private static ExistingInsurancePolicy ToDto(InsurancePolicy policy, DateTime today, int windowDays)
    {
        var (status, current) = EvaluateCoverage(policy, today, windowDays);

        return new ExistingInsurancePolicy
        {
            InsurancePolicyId = policy.InsurancePolicyId,
            Name = policy.Name,
            PolicyNumber = policy.PolicyNumber,
            Type = policy.Type.Adapt<DtoInsurancePolicyType>(),
            Notes = policy.Notes,
            CoverageStatus = status,
            CurrentRenewal = current is null ? null : ToRenewalDto(current),
            Renewals = policy.Renewals
                .OrderByDescending(r => r.FromDate)
                .ThenByDescending(r => r.CreatedAtUtc)
                .Select(ToRenewalDto)
                .ToList(),
            Archived = policy.Archived,
            CreatedAtUtc = policy.CreatedAtUtc,
        };
    }

    /// <summary>
    /// Builds one collection's read references from a batched-resolve result, ordered by resolved
    /// display name ascending.
    ///
    /// <para>
    /// <b>An archived or unresolvable link keeps its row and loses its name.</b> Neither the former
    /// <c>"(unknown)"</c> placeholder plus raw GUID nor an outright drop is correct: the first leaks a
    /// GUID into the UI and keeps disclosing archived names, and the second silently deletes link rows
    /// — a full-set diff would compute the invisible member as removed and delete it. The name is the
    /// personal data; the id is what keeps the read-modify-write round trip honest.
    /// </para>
    /// <para>
    /// An unnamed member has no name to sort on, so it sorts last, by id — a stable order rather than
    /// an arbitrary one.
    /// </para>
    /// </summary>
    private static List<PolicyContactReference> BuildContactReferences(
        IEnumerable<LinkTerm> links, IReadOnlyDictionary<Guid, ContactRef> refs) =>
        [.. links
            .Select(link =>
            {
                if (!refs.TryGetValue(link.TargetId, out var contact))
                {
                    // No contact row at all: no name AND no type — ContactType has no zero member
                    // (Person = 1, Organization = 2), so a non-nullable field would serialize as 0 and
                    // map to no member.
                    return new PolicyContactReference
                    {
                        ContactId = link.TargetId,
                        Name = null,
                        Type = null,
                        Availability = LinkAvailability.Unresolvable,
                        FromDate = link.FromDate,
                        ToDate = link.ToDate,
                    };
                }

                var archived = contact.Archived is not null;
                return new PolicyContactReference
                {
                    ContactId = link.TargetId,
                    Name = archived ? null : contact.Name,
                    Type = contact.Type,
                    Availability = archived ? LinkAvailability.Archived : LinkAvailability.Available,
                    // The TERM survives an unnamed link: it is the link row's own fact, not the
                    // contact's, so withholding it would lose data the name rule never covered.
                    FromDate = link.FromDate,
                    ToDate = link.ToDate,
                };
            })
            .OrderBy(r => r.Name is null)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.ContactId)];

    private static List<InsuredAccountReference> BuildAccountReferences(
        IEnumerable<LinkTerm> links, IReadOnlyDictionary<Guid, Account> accounts) =>
        [.. links
            .Where(link => accounts.ContainsKey(link.TargetId))
            .Select(link => ToInsuredAccountReference(accounts[link.TargetId], link))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.AccountId)];

    private static InsuredAccountReference ToInsuredAccountReference(Account account, LinkTerm link) => new()
    {
        AccountId = account.AccountId,
        Name = account.Name,
        Type = account.AccountType.Adapt<DtoAccountType>(),
        FromDate = link.FromDate,
        ToDate = link.ToDate,
    };

    /// <summary>
    /// One link row reduced to what the read path needs: its target and the term it carries. Lets the
    /// two reference builders take the term without either of them knowing which of the four link
    /// entities produced it.
    /// </summary>
    private readonly record struct LinkTerm(Guid TargetId, DateTime? FromDate, DateTime? ToDate);

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
