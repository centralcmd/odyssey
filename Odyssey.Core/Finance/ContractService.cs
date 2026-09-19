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
using Microsoft.Extensions.Logging;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using DtoContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.Core.Finance;

/// <summary>
/// CRUD for contracts plus party- and file-link management, derived-status computation and the summary
/// rollup (issue #174). Owns all business validation — the one-of-two (XOR) party invariant, the
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
    private readonly ILogger<ContractService> logger;

    public ContractService(
        OdysseyContext context,
        IContactLookup contactLookup,
        TimeProvider timeProvider,
        ISystemSettingsLookup systemSettingsLookup,
        ILogger<ContractService> logger)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
        this.systemSettingsLookup = systemSettingsLookup;
        this.logger = logger;
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

    /// <summary>
    /// Links one account or contact to a contract, in a role, optionally for a term (issue #121 §5).
    /// Returns <see langword="null"/> when the contract does not exist.
    /// </summary>
    public async Task<ExistingContractParty?> AddParty(
        Guid contractId, ContractPartyRequest request, string? userId, CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        EnsureNotArchived(contract, "adding parties");
        EnsurePartyTargetXor(request);

        await EnsureTargetExists(request, cancellationToken);
        var role = request.Role.Adapt<ContextContractPartyRole>();
        var (fromDate, toDate) = NormalizePartyTerm(contract, request);
        await EnsureNotDuplicateParty(contractId, request, role, excludingPartyId: null, cancellationToken);

        var caps = await systemSettingsLookup.GetRequestCapsAsync(cancellationToken);
        var count = await context.ContractParties.CountAsync(p => p.ContractId == contractId, cancellationToken);
        if (count >= caps.MaxPartiesPerContract)
        {
            throw new DomainUnprocessableException(
                $"Contract {contractId} already has the maximum of {caps.MaxPartiesPerContract} parties.",
                PartyTargetField(request));
        }

        var party = new ContractParty
        {
            ContractId = contractId,
            AccountId = request.AccountId,
            ContactId = request.ContactId,
            Role = role,
            FromDate = fromDate,
            ToDate = toDate,
        };

        context.ContractParties.Add(party);
        await context.SaveChangesAsync(cancellationToken);

        LogPartyWrite("added", party, previousRole: null, userId);
        return await ProjectPartyAsync(party.ContractPartyId, cancellationToken);
    }

    /// <summary>
    /// Re-writes one party: its role, its target, its dates, or any combination (issue #121 §5). The
    /// row is updated <b>in place</b>, so <c>ContractPartyId</c> is stable across a role or target
    /// change and the party stays one party. Returns <see langword="null"/> when the party is not on
    /// <i>this</i> contract; throws <see cref="DomainNotFoundException"/> when the contract itself is
    /// gone.
    /// </summary>
    /// <remarks>
    /// The body is a <b>full replacement</b>, not a patch: an omitted <c>role</c> resets the role to
    /// <c>Unspecified</c> and an omitted date clears it. That is why every write is logged (§7.7).
    /// The party cap is deliberately not re-checked — an in-place update is row-count-neutral, so it is
    /// never refused by a cap, including on a contract already at or above one a later edit lowered.
    /// </remarks>
    public async Task<ExistingContractParty?> UpdateParty(
        Guid contractId, Guid partyId, ContractPartyRequest request, string? userId,
        CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts.FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        if (contract is null)
        {
            // ContractNotFound and PartyNotOnContract are distinct failure classes (§9) that happen to
            // share a status: this one names only the contract, the null return below names both ids.
            // Neither carries a field key, which is what tells them apart from the inline target 404.
            throw new DomainNotFoundException($"Contract ID {contractId} not found.");
        }

        EnsureNotArchived(contract, "changing its parties");

        // Scoped by BOTH ids, exactly as DeleteParty is: a valid party id from another contract is a
        // 404, never a silent cross-contract edit (§7.5).
        var party = await context.ContractParties
            .FirstOrDefaultAsync(p => p.ContractPartyId == partyId && p.ContractId == contractId, cancellationToken);
        if (party is null)
        {
            return null;
        }

        EnsurePartyTargetXor(request);

        // Only a NEW target is validated for existence, so re-dating a party whose contact was deleted
        // meanwhile does not fail — the same rule the insurance party edit applies.
        if (party.AccountId != request.AccountId || party.ContactId != request.ContactId)
        {
            await EnsureTargetExists(request, cancellationToken);
        }

        var role = request.Role.Adapt<ContextContractPartyRole>();
        var (fromDate, toDate) = NormalizePartyTerm(contract, request);
        await EnsureNotDuplicateParty(contractId, request, role, excludingPartyId: partyId, cancellationToken);

        var previousRole = party.Role;
        party.AccountId = request.AccountId;
        party.ContactId = request.ContactId;
        party.Role = role;
        party.FromDate = fromDate;
        party.ToDate = toDate;

        await context.SaveChangesAsync(cancellationToken);

        LogPartyWrite("updated", party, previousRole, userId);
        return await ProjectPartyAsync(party.ContractPartyId, cancellationToken);
    }

    public async Task<bool> DeleteParty(
        Guid contractId, Guid partyId, string? userId, CancellationToken cancellationToken = default)
    {
        var party = await context.ContractParties
            .FirstOrDefaultAsync(p => p.ContractPartyId == partyId && p.ContractId == contractId, cancellationToken);
        if (party is null)
        {
            return false;
        }

        context.ContractParties.Remove(party);
        await context.SaveChangesAsync(cancellationToken);

        // A detach has no role AFTER — the row is gone. Writing Unspecified there would make the line
        // byte-identical to a PUT that downgraded the role to Unspecified, which is precisely the event
        // this log exists to make visible; the two would then differ only by the action word, so a query
        // for the downgrade would match every detach as well.
        LogPartyWrite("detached", party, party.Role, userId, roleAfter: NoRole);
        return true;
    }

    /// <summary>
    /// One structured <c>Information</c> line per party write (issue #121 §7.7). <c>ContractParty</c>
    /// deliberately carries no <c>CreatedByUserId</c> column — no v1 role confers or transfers an
    /// entitlement the way an insurance beneficiary designation does — but the <c>PUT</c> is a full
    /// replacement in which an omitted <c>role</c> silently resets to <c>Unspecified</c>, so without
    /// this line an accidental employment-relationship downgrade would leave no trace anywhere.
    /// </summary>
    /// <summary>What the "after" slot reads when there is no role after the write, i.e. on a detach.</summary>
    private const string NoRole = "(none)";

    private void LogPartyWrite(
        string action, ContractParty party, ContextContractPartyRole? previousRole, string? userId,
        string? roleAfter = null)
    {
        logger.LogInformation(
            "Contract party {Action}: contract {ContractId}, party {ContractPartyId}, target {TargetId}, " +
            "role {RoleBefore} -> {RoleAfter}, by user {UserId}.",
            action,
            party.ContractId,
            party.ContractPartyId,
            party.AccountId ?? party.ContactId,
            previousRole ?? ContextContractPartyRole.Unspecified,
            roleAfter ?? party.Role.ToString(),
            userId ?? "(unknown)");
    }

    private async Task<ExistingContractParty> ProjectPartyAsync(Guid partyId, CancellationToken cancellationToken)
    {
        var loaded = await LoadPartyWithTargets(partyId, cancellationToken);
        IReadOnlyDictionary<Guid, ContactRef> contacts = loaded!.ContactId is { } contactId
            ? await contactLookup.ResolveRefsAsync([contactId], cancellationToken)
            : new Dictionary<Guid, ContactRef>();
        return ToPartyDto(loaded, contacts);
    }

    private static void EnsureNotArchived(Contract contract, string what)
    {
        if (contract.Archived is not null)
        {
            throw new DomainUnprocessableException(
                $"Contract {contract.ContractId} is archived; unarchive it before {what}.");
        }
    }

    // One-of-two (XOR): exactly one target id must be set.
    private static void EnsurePartyTargetXor(ContractPartyRequest request)
    {
        var setCount =
            (request.AccountId is not null ? 1 : 0) +
            (request.ContactId is not null ? 1 : 0);
        if (setCount != 1)
        {
            throw new DomainValidationException(
                "Exactly one of accountId or contactId must be set.");
        }
    }

    /// <summary>
    /// The field key the inline-rendered party failures are attributed to: whichever of the two target
    /// ids the caller actually sent, since that is the control the client rendered.
    /// </summary>
    private static string PartyTargetField(ContractPartyRequest request) =>
        request.AccountId is not null
            ? nameof(ContractPartyRequest.AccountId)
            : nameof(ContractPartyRequest.ContactId);

    /// <summary>
    /// A party's term is the party's own fact, with one tie to the contract: it cannot begin before the
    /// contract did. Both dates are optional and null is the <b>default term</b> — the contract's own
    /// extent — not an unset value. Only the lower bound is tied, and only when the contract has a
    /// <c>StartDate</c>: an open-started term contract and a one-off (completion date only) have no
    /// anchor. <c>ToDate</c> is deliberately <b>not</b> bounded by the contract's <c>EndDate</c>, since
    /// a term contract's end moves when it is extended and bounding here would make an existing party's
    /// validity depend on the order two edits happened in.
    /// </summary>
    /// <remarks>
    /// The anchor is checked at party-write time only: editing the contract's <c>StartDate</c> later
    /// neither re-validates nor re-dates its parties, mirroring insurance, where a renewal never
    /// re-dates a party.
    /// </remarks>
    private static (DateTime? FromDate, DateTime? ToDate) NormalizePartyTerm(
        Contract contract, ContractPartyRequest request)
    {
        var fromDate = request.FromDate is { } from ? DateTimeNormalization.NormalizeToUtc(from) : (DateTime?)null;
        var toDate = request.ToDate is { } to ? DateTimeNormalization.NormalizeToUtc(to) : (DateTime?)null;

        if (fromDate is { } start && toDate is { } end && end.Date < start.Date)
        {
            throw new DomainValidationException(
                "ToDate must be on or after FromDate.",
                code: null,
                field: nameof(ContractPartyRequest.ToDate));
        }

        if (fromDate is { } began && contract.StartDate is { } contractStart && began.Date < contractStart.Date)
        {
            throw new DomainValidationException(
                $"This contract began {contractStart:yyyy-MM-dd} — a party cannot be in the role before that.",
                code: null,
                field: nameof(ContractPartyRequest.FromDate));
        }

        return (fromDate, toDate);
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

    // The two target 404s carry the field key of the id that was sent; the whole-request 404s (contract
    // gone, party not on this contract) deliberately carry none, which is how a client tells the three
    // apart without matching on message text (§9).
    private async Task EnsureTargetExists(ContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.AccountId is { } accountId)
        {
            if (!await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken))
            {
                throw new DomainNotFoundException(
                    $"Account ID {accountId} not found.", nameof(ContractPartyRequest.AccountId));
            }
        }
        else if (request.ContactId is { } contactId)
        {
            if (!(await contactLookup.ExistingIdsAsync([contactId], cancellationToken)).Contains(contactId))
            {
                throw new DomainNotFoundException(
                    $"Contact ID {contactId} not found.", nameof(ContractPartyRequest.ContactId));
            }
        }
    }

    /// <summary>
    /// The <i>(contract, target, role)</i> uniqueness pre-check (issue #121 §8 rule 6). Widened from
    /// <i>(contract, target)</i>: the same record may be named twice in two genuinely different
    /// capacities. <paramref name="excludingPartyId"/> takes the row being edited out of its own check,
    /// so a date-only edit is not a self-conflict.
    /// </summary>
    /// <remarks>
    /// This is a check-then-act with no transaction around it, so it is not what makes the rule
    /// <i>true</i> — the two unique indexes are, and a race surfaces through
    /// <c>GlobalExceptionHandler</c> as a generic 409. The pre-check is kept for the explaining message
    /// and because it is the only implementation the EF InMemory tiers see: that provider enforces no
    /// indexes at all.
    /// </remarks>
    private async Task EnsureNotDuplicateParty(
        Guid contractId, ContractPartyRequest request, ContextContractPartyRole role, Guid? excludingPartyId,
        CancellationToken cancellationToken = default)
    {
        var duplicate = await context.ContractParties.AnyAsync(p =>
            p.ContractId == contractId &&
            p.Role == role &&
            (excludingPartyId == null || p.ContractPartyId != excludingPartyId) &&
            ((request.AccountId != null && p.AccountId == request.AccountId) ||
             (request.ContactId != null && p.ContactId == request.ContactId)), cancellationToken);
        if (duplicate)
        {
            throw new DomainConflictException(
                "That party is already linked to the contract in that role.",
                PartyTargetField(request));
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
                Role = party.Role.Adapt<DtoContractPartyRole>(),
                FromDate = party.FromDate,
                ToDate = party.ToDate,
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
                // A top-level field on the party, so it survives an unresolved target reference.
                Role = party.Role.Adapt<DtoContractPartyRole>(),
                FromDate = party.FromDate,
                ToDate = party.ToDate,
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
