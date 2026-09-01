using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using ContextContractType = Odyssey.Context.ContractType;
using ContextContractFileType = Odyssey.Context.ContractFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using DtoContractFileType = Odyssey.Dtos.Finance.ContractFileType;
using DtoInsurancePolicyType = Odyssey.Dtos.Finance.InsurancePolicyType;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;

namespace Odyssey.Core.Finance;

/// <summary>
/// CRUD for contracts plus party- and file-link management, derived-status computation and the summary
/// rollup (issue #174). Owns all business validation — the one-of-three (XOR) party invariant, the
/// archive guard, defensive caps and the data-minimised read projections; the controller owns claim
/// authorization and the file content-type allow-list.
///
/// All time-relative computation uses a single UTC "today" captured once per request from the injected
/// <see cref="TimeProvider"/>, so a contract cannot evaluate to different statuses within one request.
/// </summary>
public class ContractService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;
    private readonly ISystemSettingsLookup systemSettingsLookup;

    public ContractService(
        OdysseyContext context,
        IContactLookup contactLookup,
        TimeProvider timeProvider,
        ISystemSettingsLookup systemSettingsLookup)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
        this.systemSettingsLookup = systemSettingsLookup;
    }

    private DateTime Today => timeProvider.GetUtcNow().UtcDateTime.Date;

    // ── Contracts ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Server-side paged list (issue #277): SQL search + multi-type filter, then derive status, filter
    /// (multi-select) and sort in memory (status is a derived value that cannot be expressed in SQL),
    /// then slice. Archived contracts are shown by default (matching the design system) and only
    /// excluded when an explicit status filter omits the Archived status.
    /// </summary>
    public async Task<PagedResult<ContractListItem>> ListAsync(
        ContractsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var today = Today;
        var q = context.Contracts.AsNoTracking().AsQueryable();

        // Archived contracts are shown by default and only excluded when an explicit status filter
        // omits Archived (applied post-projection below) — matching the design system, which no
        // longer hides archived rows.
        var statusFilter = query.Statuses ?? [];

        var typeFilter = (query.Types ?? [])
            .Select(t => t.Adapt<ContextContractType>())
            .ToList();
        if (typeFilter.Count > 0)
        {
            q = q.Where(c => typeFilter.Contains(c.Type));
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            // Contact now lives in OdysseyContext — a SQL JOIN to the Contacts table is impossible across
            // the context boundary, so pre-resolve matching contact ids and filter parties by membership.
            var contactMatchIds = (await contactLookup.SearchIdsByNameAsync(term, cancellationToken)).ToHashSet();
            q = q.Where(c =>
                EF.Functions.Like(c.Name, pattern) ||
                (c.Description != null && EF.Functions.Like(c.Description, pattern)) ||
                c.Parties.Any(p => p.ContactId != null && contactMatchIds.Contains(p.ContactId.Value)));
        }

        var projected = await q
            .Select(c => new
            {
                Contract = c,
                PartyCount = c.Parties.Count,
                FileCount = c.Files.Count,
                // Contact id of the first institution party (issue #325); its display name is resolved
                // after materialisation via the contact lookup (Contact now lives in OdysseyContext).
                InstitutionContactId = c.Parties
                    .Where(p => p.ContactId != null)
                    .OrderBy(p => p.ContractPartyId)
                    .Select(p => p.ContactId)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var institutionContactIds = projected
            .Where(x => x.InstitutionContactId != null)
            .Select(x => x.InstitutionContactId!.Value)
            .Distinct()
            .ToList();
        var institutionRefs = institutionContactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(institutionContactIds, cancellationToken);

        var items = projected.Select(x => new ContractListItem
        {
            ContractId = x.Contract.ContractId,
            Name = x.Contract.Name,
            Type = x.Contract.Type.Adapt<DtoContractType>(),
            Description = x.Contract.Description,
            StartDate = x.Contract.StartDate,
            EndDate = x.Contract.EndDate,
            CompletionDate = x.Contract.CompletionDate,
            Status = DeriveStatus(x.Contract, today),
            InstitutionName = x.InstitutionContactId is { } cid && institutionRefs.TryGetValue(cid, out var institution)
                ? institution.Name
                : null,
            PartyCount = x.PartyCount,
            FileCount = x.FileCount,
            Archived = x.Contract.Archived,
        });

        if (statusFilter.Length > 0)
        {
            items = items.Where(i => statusFilter.Contains(i.Status));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or ContractSortBy.Name or ContractSortBy.Type or ContractSortBy.Status);
        IOrderedEnumerable<ContractListItem> sorted = query.SortBy switch
        {
            ContractSortBy.StartDate => ascending
                ? items.OrderBy(i => i.StartDate is null).ThenBy(i => i.StartDate)
                : items.OrderBy(i => i.StartDate is null).ThenByDescending(i => i.StartDate),
            ContractSortBy.EndDate => ascending
                ? items.OrderBy(i => i.EndDate is null).ThenBy(i => i.EndDate)
                : items.OrderBy(i => i.EndDate is null).ThenByDescending(i => i.EndDate),
            ContractSortBy.Type => ascending ? items.OrderBy(i => i.Type) : items.OrderByDescending(i => i.Type),
            ContractSortBy.Status => ascending ? items.OrderBy(i => i.Status) : items.OrderByDescending(i => i.Status),
            _ => ascending ? items.OrderBy(i => i.Name) : items.OrderByDescending(i => i.Name),
        };
        var ordered = sorted.ThenBy(i => i.ContractId).ToList();
        return ListQuery.ToPagedResult(ordered, query.Offset, query.Limit);
    }

    public async Task<ContractSummary> GetSummary(CancellationToken cancellationToken = default)
    {
        var today = Today;
        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);

        var contracts = await context.Contracts
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(caps.MaxSummaryContracts)
            .Select(c => new { c.Type, c.StartDate, c.EndDate, c.CompletionDate, c.Archived })
            .ToListAsync(cancellationToken);

        var counts = new ContractStatusCounts();
        var byType = new Dictionary<DtoContractType, int>();

        foreach (var c in contracts)
        {
            var status = DeriveStatus(c.StartDate, c.EndDate, c.CompletionDate, c.Archived, today);
            switch (status)
            {
                case ContractStatus.Active: counts.Active++; break;
                case ContractStatus.Upcoming: counts.Upcoming++; break;
                case ContractStatus.Expired: counts.Expired++; break;
                case ContractStatus.Archived: counts.Archived++; break;
            }

            // The by-type breakdown covers only the active (non-archived) set — archived contracts are
            // counted in the status pills but excluded from "By type" (matches the design's summary).
            if (c.Archived is null)
            {
                var dtoType = c.Type.Adapt<DtoContractType>();
                byType[dtoType] = byType.GetValueOrDefault(dtoType) + 1;
            }
        }

        return new ContractSummary
        {
            TotalContracts = contracts.Count,
            CountsByStatus = counts,
            CountsByType = byType
                .OrderBy(kv => kv.Key)
                .Select(kv => new ContractTypeCount { Type = kv.Key, Count = kv.Value })
                .ToList(),
        };
    }

    public async Task<ExistingContract?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var contract = await LoadWithDetails(id, cancellationToken);
        return contract is null ? null : await ToDto(contract, Today, cancellationToken);
    }

    public async Task<ExistingContract> Create(NewContract request, CancellationToken cancellationToken = default)
    {
        var (startDate, endDate, completionDate) = NormalizeDates(request.StartDate, request.EndDate, request.CompletionDate);

        var contract = new Contract
        {
            Name = request.Name,
            Type = request.Type.Adapt<ContextContractType>(),
            Description = request.Description,
            StartDate = startDate,
            EndDate = endDate,
            CompletionDate = completionDate,
            Archived = null,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.Contracts.Add(contract);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await LoadWithDetails(contract.ContractId, cancellationToken);
        return await ToDto(loaded!, Today, cancellationToken);
    }

    public async Task<ExistingContract?> Update(Guid id, UpdateContract request, CancellationToken cancellationToken = default)
    {
        var contract = await LoadWithDetails(id, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        var (startDate, endDate, completionDate) = NormalizeDates(request.StartDate, request.EndDate, request.CompletionDate);

        contract.Name = request.Name;
        contract.Type = request.Type.Adapt<ContextContractType>();
        contract.Description = request.Description;
        contract.StartDate = startDate;
        contract.EndDate = endDate;
        contract.CompletionDate = completionDate;
        // The lifecycle is ORDERED, not orthogonal: archiving retires a contract that is already
        // over, so only an ended one can be archived. Validated against the request's dates, not the
        // stored ones, so a single PUT may end and archive in one go.
        EnsureArchivable(contract, request.IsArchived, endDate, completionDate);

        // Archive (preserving the original archive stamp) or unarchive per the request.
        contract.Archived = request.IsArchived
            ? contract.Archived ?? timeProvider.GetUtcNow().UtcDateTime
            : null;

        await context.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadWithDetails(id, cancellationToken);
        return await ToDto(reloaded!, Today, cancellationToken);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        // Hard delete: removes the contract and cascades its party + file link rows. The underlying
        // accounts/contacts/policies and FileMetadata/blobs are left intact. Children are loaded
        // so the cascade also applies under the EF InMemory provider (used by tests), which does not
        // enforce database-level cascade.
        var contract = await context.Contracts
            .Include(c => c.Parties)
            .Include(c => c.Files)
            .FirstOrDefaultAsync(c => c.ContractId == id, cancellationToken);
        if (contract is null)
        {
            return false;
        }

        context.Contracts.Remove(contract);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> Exists(Guid id, CancellationToken cancellationToken = default) =>
        await context.Contracts.AnyAsync(c => c.ContractId == id, cancellationToken);

    // ── Parties ──────────────────────────────────────────────────────────────────

    public async Task<ExistingContractParty?> AddParty(Guid contractId, AddContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        if (contract.Archived is not null)
        {
            throw new DomainValidationException(
                $"Contract {contractId} is archived; unarchive it before adding parties.");
        }

        // One-of-two (XOR): exactly one target id must be set.
        var setCount =
            (request.AccountId is not null ? 1 : 0) +
            (request.ContactId is not null ? 1 : 0);
        if (setCount != 1)
        {
            throw new DomainValidationException(
                "Exactly one of accountId or contactId must be set.");
        }

        await EnsureTargetExists(request, cancellationToken);
        await EnsureNotDuplicateParty(contractId, request, cancellationToken);

        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        var count = await context.ContractParties.CountAsync(p => p.ContractId == contractId, cancellationToken);
        if (count >= caps.MaxPartiesPerContract)
        {
            throw new DomainUnprocessableException(
                $"Contract {contractId} already has the maximum of {caps.MaxPartiesPerContract} parties.");
        }

        var party = new ContractParty
        {
            ContractId = contractId,
            AccountId = request.AccountId,
            ContactId = request.ContactId,
        };

        context.ContractParties.Add(party);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await LoadPartyWithTargets(party.ContractPartyId, cancellationToken);
        IReadOnlyDictionary<Guid, ContactRef> contacts = loaded!.ContactId is { } contactId
            ? await contactLookup.ResolveRefsAsync([contactId], cancellationToken)
            : new Dictionary<Guid, ContactRef>();
        return ToPartyDto(loaded, contacts);
    }

    public async Task<bool> DeleteParty(Guid contractId, Guid partyId, CancellationToken cancellationToken = default)
    {
        var party = await context.ContractParties
            .FirstOrDefaultAsync(p => p.ContractPartyId == partyId && p.ContractId == contractId, cancellationToken);
        if (party is null)
        {
            return false;
        }

        context.ContractParties.Remove(party);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Files ────────────────────────────────────────────────────────────────────

    public async Task<ExistingContractFile?> AttachFile(
        Guid contractId, Guid fileMetadataId, string userId, DtoContractFileType fileType, CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        if (contract.Archived is not null)
        {
            throw new DomainValidationException(
                $"Contract {contractId} is archived; unarchive it before attaching files.");
        }

        var duplicate = await context.ContractFiles
            .AnyAsync(f => f.ContractId == contractId && f.FileMetadataId == fileMetadataId, cancellationToken);
        if (duplicate)
        {
            throw new DomainConflictException(
                $"File {fileMetadataId} is already attached to contract {contractId}.");
        }

        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        var count = await context.ContractFiles.CountAsync(f => f.ContractId == contractId, cancellationToken);
        if (count >= caps.MaxFilesPerContract)
        {
            throw new DomainUnprocessableException(
                $"Contract {contractId} already has the maximum of {caps.MaxFilesPerContract} attached files.");
        }

        var link = new ContractFile
        {
            ContractId = contractId,
            FileMetadataId = fileMetadataId,
            FileType = fileType.Adapt<ContextContractFileType>(),
            AttachedByUserId = userId,
            AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        context.ContractFiles.Add(link);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await context.ContractFiles
            .Include(f => f.FileMetadata)
            .FirstAsync(f => f.ContractFileId == link.ContractFileId);
        return ToFileDto(loaded);
    }

    public async Task<bool> IsFileAttachedToContract(Guid contractId, Guid fileMetadataId, CancellationToken cancellationToken = default) =>
        await context.ContractFiles.AnyAsync(f => f.ContractId == contractId && f.FileMetadataId == fileMetadataId, cancellationToken);

    public async Task<bool> DetachFile(Guid contractId, Guid fileMetadataId, CancellationToken cancellationToken = default)
    {
        var link = await context.ContractFiles
            .FirstOrDefaultAsync(f => f.ContractId == contractId && f.FileMetadataId == fileMetadataId, cancellationToken);
        if (link is null)
        {
            return false;
        }

        context.ContractFiles.Remove(link);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Archiving requires a contract that is over — the lifecycle is ordered, so Archived implies
    /// ended and the status chip renders one state rather than a stack of flags.
    ///
    /// <para>
    /// "Over" is not the same as the derived <see cref="ContractStatus.Expired"/>: a one-off whose
    /// completion date has passed stays <c>Active</c> in the status derivation (it is a settled
    /// record, not a lapsed term), and it is archivable. Hence the two-branch check rather than a
    /// status comparison.
    /// </para>
    ///
    /// <para>
    /// Only the <b>transition</b> into archived is checked. A row archived before this rule existed
    /// stays editable and restorable: re-validating it on every save would strand it, since the only
    /// way out is a PUT that carries <c>IsArchived = true</c> right up until the one that clears it.
    /// Restoring is always allowed.
    /// </para>
    /// </summary>
    private void EnsureArchivable(
        Contract contract, bool isArchived, DateTime? endDate, DateTime? completionDate)
    {
        if (!isArchived || contract.Archived is not null)
        {
            return;
        }

        if (!ContractLifecycle.HasEnded(endDate, completionDate, Today))
        {
            throw new DomainValidationException(
                "A contract can only be archived once it has ended. Set an EndDate before today, or a CompletionDate on or before today, first.");
        }
    }

    // ── Derived status (deterministic, ordered — §6) ──────────────────────────────

    private static ContractStatus DeriveStatus(Contract contract, DateTime today) =>
        DeriveStatus(contract.StartDate, contract.EndDate, contract.CompletionDate, contract.Archived, today);

    private static ContractStatus DeriveStatus(
        DateTime? startDate, DateTime? endDate, DateTime? completionDate, DateTime? archived, DateTime today)
    {
        if (archived is not null)
        {
            return ContractStatus.Archived;
        }
        // One-off: a point-in-time agreement — Upcoming until its completion date, a settled record after.
        if (completionDate is { } completion)
        {
            return completion.Date > today ? ContractStatus.Upcoming : ContractStatus.Active;
        }
        if (startDate is { } start && start.Date > today)
        {
            return ContractStatus.Upcoming;
        }
        if (endDate is { } end && end.Date < today)
        {
            return ContractStatus.Expired;
        }
        return ContractStatus.Active;
    }

    // ── Validation helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the term/one-off dates and returns the normalized triple: a one-off (completion set)
    /// clears the term dates; a term validates <c>end ≥ start</c> when both are present.
    /// </summary>
    private static (DateTime? StartDate, DateTime? EndDate, DateTime? CompletionDate) NormalizeDates(
        DateTime? startDate, DateTime? endDate, DateTime? completionDate)
    {
        if (completionDate is not null)
        {
            return (null, null, completionDate);
        }
        if (endDate is { } end && startDate is { } start && end.Date < start.Date)
        {
            throw new DomainValidationException("EndDate must be on or after StartDate.");
        }
        return (startDate, endDate, null);
    }

    private async Task EnsureTargetExists(AddContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.AccountId is { } accountId)
        {
            if (!await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken))
            {
                throw new DomainNotFoundException($"Account ID {accountId} not found.");
            }
        }
        else if (request.ContactId is { } contactId)
        {
            if (!(await contactLookup.ExistingIdsAsync([contactId], cancellationToken)).Contains(contactId))
            {
                throw new DomainNotFoundException($"Contact ID {contactId} not found.");
            }
        }
    }

    private async Task EnsureNotDuplicateParty(Guid contractId, AddContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        var duplicate = await context.ContractParties.AnyAsync(p =>
            p.ContractId == contractId &&
            ((request.AccountId != null && p.AccountId == request.AccountId) ||
             (request.ContactId != null && p.ContactId == request.ContactId)), cancellationToken);
        if (duplicate)
        {
            throw new DomainConflictException(
                "That party is already linked to the contract.");
        }
    }

    // ── Loading & mapping ───────────────────────────────────────────────────────────

    private async Task<Contract?> LoadWithDetails(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Contracts
            .Include(c => c.Parties).ThenInclude(p => p.Account)
            .Include(c => c.Files).ThenInclude(f => f.FileMetadata)
            .FirstOrDefaultAsync(c => c.ContractId == id, cancellationToken);
    }

    private async Task<ContractParty?> LoadPartyWithTargets(Guid partyId, CancellationToken cancellationToken = default)
    {
        return await context.ContractParties
            .Include(p => p.Account)
            .FirstOrDefaultAsync(p => p.ContractPartyId == partyId, cancellationToken);
    }

    private async Task<ExistingContract> ToDto(Contract contract, DateTime today, CancellationToken cancellationToken)
    {
        // Batch-resolve the distinct, non-null party contact ids in one call (Contact now lives in
        // OdysseyContext — no cross-context navigation include).
        var contactIds = contract.Parties
            .Where(p => p.ContactId is not null)
            .Select(p => p.ContactId!.Value)
            .Distinct()
            .ToList();
        IReadOnlyDictionary<Guid, ContactRef> contacts = contactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(contactIds, cancellationToken);

        return new ExistingContract
        {
            ContractId = contract.ContractId,
            Name = contract.Name,
            Type = contract.Type.Adapt<DtoContractType>(),
            Description = contract.Description,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
            CompletionDate = contract.CompletionDate,
            Status = DeriveStatus(contract, today),
            Parties = contract.Parties
                .OrderBy(p => p.ContractPartyId)
                .Select(p => ToPartyDto(p, contacts))
                .ToList(),
            Files = contract.Files
                .Where(f => f.FileMetadata is not null)
                .OrderBy(f => f.AttachedAtUtc)
                .Select(ToFileDto)
                .ToList(),
            Archived = contract.Archived,
            CreatedAtUtc = contract.CreatedAtUtc,
        };
    }

    // Explicit member mapping (never a permissive Adapt) so a future field added to Account or
    // Contact cannot silently re-leak into this cross-claim projection (§9/§10 #2).
    private static ExistingContractParty ToPartyDto(ContractParty party, IReadOnlyDictionary<Guid, ContactRef> contacts)
    {
        if (party.AccountId is not null)
        {
            return new ExistingContractParty
            {
                ContractPartyId = party.ContractPartyId,
                ContractId = party.ContractId,
                Kind = ContractPartyKind.Account,
                Account = party.Account is null ? null : new ContractAccountReference
                {
                    AccountId = party.Account.AccountId,
                    Name = party.Account.Name,
                    Type = party.Account.AccountType.Adapt<DtoAccountType>(),
                },
            };
        }

        {
            // Resolve via the batched lookup (Contact lives in OdysseyContext). An unresolved link
            // (contact deleted across the context boundary) nulls the reference, as the read path
            // does for any missing link.
            var contact = party.ContactId is { } contactId ? contacts.GetValueOrDefault(contactId) : null;
            return new ExistingContractParty
            {
                ContractPartyId = party.ContractPartyId,
                ContractId = party.ContractId,
                Kind = ContractPartyKind.Institution,
                Institution = contact is null ? null : new ContractContactReference
                {
                    ContactId = contact.ContactId,
                    Name = contact.Name,
                    // No .Adapt here (unlike Account): ContactRef already declares Type as the Dtos
                    // ContactType, so this is a same-type assignment.
                    Type = contact.Type,
                },
            };
        }
    }

    private static ExistingContractFile ToFileDto(ContractFile file) => new()
    {
        ContractFileId = file.ContractFileId,
        ContractId = file.ContractId,
        FileMetadata = file.FileMetadata!.Adapt<ExistingFileMetadata>(),
        FileType = file.FileType.Adapt<DtoContractFileType>(),
        AttachedByUserId = file.AttachedByUserId,
        AttachedAtUtc = file.AttachedAtUtc,
    };
}
