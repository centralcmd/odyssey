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
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.Extensions.Logging;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextTermDirection = Odyssey.Context.TermDirection;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using DtoContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.Core.Finance;

/// <summary>
/// CRUD for contracts: the list and detail reads, create, update and delete, the lifecycle stamps
/// (ready, signed, paused, archived) and the type-change guard over existing parties (issue #174).
/// Parties, files and the summary rollup live in <see cref="ContractPartyService"/>,
/// <see cref="ContractFileService"/> and <see cref="ContractSummaryService"/> (issue #287 M1); the
/// derived status they all report is <see cref="ContractStatusRules"/>. The controllers own claim
/// authorization and the file content-type allow-list. Note there is no archive guard on the write paths: archival
/// hides a contract from the default list, it does not lock it, and only <see cref="EnsureArchivable"/>
/// (the transition INTO archived) still refuses anything on that account.
///
/// All time-relative computation uses a single UTC "today" captured once per request from the injected
/// <see cref="TimeProvider"/>, so a contract cannot evaluate to different statuses within one request.
/// </summary>
public class ContractService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ContractService> logger;

    public ContractService(
        OdysseyContext context,
        IContactLookup contactLookup,
        TimeProvider timeProvider,
        ILogger<ContractService> logger)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private DateTime Today => timeProvider.GetUtcNow().UtcDateTime.Date;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

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
            // Contacts are resolved through IContactLookup rather than a navigation join, so
            // pre-resolve matching contact ids and filter parties by membership.
            var contactMatchIds = (await contactLookup.SearchIdsByNameAsync(term, cancellationToken)).ToHashSet();
            q = q.Where(c =>
                EF.Functions.Like(c.Name, pattern) ||
                (c.Description != null && EF.Functions.Like(c.Description, pattern)) ||
                (c.ReferenceNumber != null && EF.Functions.Like(c.ReferenceNumber, pattern)) ||
                c.Parties.Any(p => p.ContactId != null && contactMatchIds.Contains(p.ContactId.Value)));
        }

        var projected = await q
            .Select(c => new
            {
                Contract = c,
                PartyCount = c.Parties.Count,
                FileCount = c.Files.Count,
                // A correlated subquery in the one list query, exactly like the two counts above —
                // never a second grouped read, so a page of 50 costs the same as a page of 1.
                TermCount = c.Terms.Count,
                // The event log's size, on the same terms: a correlated subquery in the one list
                // query. The log itself is unbounded and has its own paged endpoint — nothing on this
                // path loads its rows.
                EventCount = c.Events.Count,
                // The contract's saved tag filter, counted on the same terms (issue #166 §3.4): the
                // fifth correlated subquery in the one list query, never a second grouped read.
                SmartTagCount = c.SmartTags.Count,
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

        var incomingIds = await LoadContractsWithIncomingTermAsync(
            [.. projected.Select(x => x.Contract.ContractId)], today, cancellationToken);

        var items = projected.Select(x => new ContractListItem
        {
            ContractId = x.Contract.ContractId,
            Name = x.Contract.Name,
            Type = x.Contract.Type.Adapt<DtoContractType>(),
            Description = x.Contract.Description,
            ReferenceNumber = x.Contract.ReferenceNumber,
            StartDate = x.Contract.StartDate,
            EndDate = x.Contract.EndDate,
            CompletionDate = x.Contract.CompletionDate,
            Status = ContractStatusRules.DeriveStatus(x.Contract, today),
            InstitutionName = x.InstitutionContactId is { } cid && institutionRefs.TryGetValue(cid, out var institution)
                ? institution.Name
                : null,
            PartyCount = x.PartyCount,
            FileCount = x.FileCount,
            TermCount = x.TermCount,
            EventCount = x.EventCount,
            SmartTagCount = x.SmartTagCount,
            Archived = x.Contract.Archived,
            Paused = x.Contract.Paused,
            Ready = x.Contract.Ready,
            Signed = x.Contract.Signed,
            HasIncomingTerm = incomingIds.Contains(x.Contract.ContractId),
        });

        if (statusFilter.Length > 0)
        {
            items = items.Where(i => statusFilter.Contains(i.Status));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is null or ContractSortBy.Name or ContractSortBy.Type or ContractSortBy.Status or ContractSortBy.ReferenceNumber);
        IOrderedEnumerable<ContractListItem> sorted = query.SortBy switch
        {
            ContractSortBy.StartDate => ascending
                ? items.OrderBy(i => i.StartDate is null).ThenBy(i => i.StartDate)
                : items.OrderBy(i => i.StartDate is null).ThenByDescending(i => i.StartDate),
            ContractSortBy.EndDate => ascending
                ? items.OrderBy(i => i.EndDate is null).ThenBy(i => i.EndDate)
                : items.OrderBy(i => i.EndDate is null).ThenByDescending(i => i.EndDate),
            ContractSortBy.ReferenceNumber => ascending
                ? items.OrderBy(i => i.ReferenceNumber is null).ThenBy(i => i.ReferenceNumber)
                : items.OrderBy(i => i.ReferenceNumber is null).ThenByDescending(i => i.ReferenceNumber),
            ContractSortBy.Type => ascending ? items.OrderBy(i => i.Type) : items.OrderByDescending(i => i.Type),
            // The shared LIFECYCLE rank, not the enum ordinal (issue #145 §8): Draft = 5 and
            // Ready = 6 are APPENDED members — an ordinal is a wire and persistence contract and is
            // never renumbered — so ordering on it would sort the two EARLIEST lifecycle states last,
            // behind Archived. Only the reading order changes, and it lives in one place the client
            // reads too.
            ContractSortBy.Status => ascending
                ? items.OrderBy(i => ContractStatusOrder.Rank(i.Status))
                : items.OrderByDescending(i => ContractStatusOrder.Rank(i.Status)),
            _ => ascending ? items.OrderBy(i => i.Name) : items.OrderByDescending(i => i.Name),
        };
        var ordered = sorted.ThenBy(i => i.ContractId).ToList();
        return ListQuery.ToPagedResult(ordered, query.Offset, query.Limit);
    }

    /// <summary>
    /// The contracts on this PAGE whose in-force terms include an <c>Incoming</c> one (issue #159),
    /// for the collapsed row's "Money in" marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE query for the whole page, not one per row: the page's ids and <c>EffectiveFrom &lt;= today</c>
    /// narrow it in SQL, then <see cref="TermSeries"/> collapses each contract's candidates in memory —
    /// the same winner rule the record card, the <c>…/terms/current</c> endpoint and the run rate use,
    /// so "in force" still cannot mean different things on the row and inside it. A correlated
    /// <c>Any()</c> in the list projection could not do this: it would mark a contract whose incoming
    /// term has been superseded by an outgoing one, which is precisely the row that must not be marked.
    /// </para>
    /// <para>
    /// Direction is not part of the series key and is not a predicate before the collapse, so the
    /// winner is chosen exactly as it is everywhere else and only then read for its direction.
    /// </para>
    /// </remarks>
    private async Task<HashSet<Guid>> LoadContractsWithIncomingTermAsync(
        List<Guid> contractIds, DateTime today, CancellationToken cancellationToken)
    {
        if (contractIds.Count == 0)
        {
            return [];
        }

        var candidates = await context.Terms
            .AsNoTracking()
            .Where(t => contractIds.Contains(t.ContractId) && t.EffectiveFrom <= today)
            .ToListAsync(cancellationToken);

        return [.. candidates
            .GroupBy(t => t.ContractId)
            .Where(group => TermSeries.Current(group).Any(t => t.Direction == ContextTermDirection.Incoming))
            .Select(group => group.Key)];
    }

    /// <summary>
    /// The raw id of the user who added the contract, or null when none is recorded. Kept off
    /// <see cref="ExistingContract"/> on purpose: the API resolves it to a display label with the
    /// caller's claims, and the response carries the label only.
    /// </summary>
    public Task<string?> CreatedByUserIdOf(Guid id, CancellationToken cancellationToken = default) =>
        context.Contracts.AsNoTracking()
            .Where(c => c.ContractId == id)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<ExistingContract?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var contract = await LoadWithDetails(id, cancellationToken);
        return contract is null ? null : await ToDto(contract, Today, cancellationToken);
    }

    /// <summary>
    /// Creates a contract. <paramref name="userId"/> is the acting user, for the signature-transition
    /// log line (issue #145 §7.7) — the same position and nullability the party writes already use. A
    /// <c>Signed</c> transition can happen on <c>POST</c> as well as <c>PUT</c>, so both call chains
    /// carry it; plumbing one and not the other would leave contracts entered already-signed with an
    /// unattributed line.
    /// </summary>
    public async Task<ExistingContract> Create(
        NewContract request, string? userId, CancellationToken cancellationToken = default)
    {
        var (startDate, endDate, completionDate) = NormalizeDates(request.StartDate, request.EndDate, request.CompletionDate);
        // The same helper PUT runs, so a rule enforced on one write path and not the other cannot
        // happen — the defect class this codebase keeps closing.
        var (ready, signed) = NormalizeSignature(request.Ready, request.Signed);

        var contract = new Contract
        {
            Name = request.Name,
            Type = request.Type.Adapt<ContextContractType>(),
            Description = request.Description,
            ReferenceNumber = NormalizeReferenceNumber(request.ReferenceNumber),
            StartDate = startDate,
            EndDate = endDate,
            CompletionDate = completionDate,
            Archived = null,
            Paused = null,
            // Both omitted — the normal path — creates the contract in Draft.
            Ready = ready,
            Signed = signed,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            CreatedByUserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
        };

        context.Contracts.Add(contract);

        // The same detector PUT runs, fired against an all-null "before" (issue #154 §8.1). That is why
        // Non-Goal 6 is scoped to the ACT of creating: a contract entered already-signed records its
        // Ready and Signed TRANSITIONS here, while nothing records the creation itself. Staged before
        // the save, so the contract and its events go out together and the log can neither miss a
        // transition that happened nor claim one that did not (§8.6).
        var nowUtc = UtcNow;
        var stamps = new ContractStamps(contract.Paused, contract.Archived, ready, signed);
        ContractEventRecorder.StageAll(
            context,
            contract.ContractId,
            DetectStampTransitions(ContractStamps.None, stamps, nowUtc),
            userId,
            nowUtc);

        await context.SaveChangesAsync(cancellationToken);

        // After the save: a log line describes a committed fact (§8.6).
        LogStampWrites(contract.ContractId, ContractStamps.None, stamps, userId);

        var loaded = await LoadWithDetails(contract.ContractId, cancellationToken);
        return await ToDto(loaded!, Today, cancellationToken);
    }

    /// <summary>
    /// Full-replacement update. <paramref name="userId"/> is the acting user, for the
    /// signature-transition log line (issue #145 §7.7).
    /// </summary>
    public async Task<ExistingContract?> Update(
        Guid id, UpdateContract request, string? userId, CancellationToken cancellationToken = default)
    {
        var contract = await LoadWithDetails(id, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        // Before anything is written: a type change that would leave an existing party holding a role
        // the INCOMING type rejects is refused outright (issue #157 §3.3). The controller pre-checks
        // and shapes the structured body; this stays unconditional for direct (non-HTTP) callers, and
        // running it here — ahead of every other guard — is what makes "nothing is written" true.
        await EnsureTypeChangeKeepsPartiesLegalAsync(contract, request.Type, cancellationToken);

        var (startDate, endDate, completionDate) = NormalizeDates(request.StartDate, request.EndDate, request.CompletionDate);
        // PUT is a full replacement, so a present value SETS each stamp and an omitted one CLEARS it.
        // Guarded before anything is written back, so "stored" below still means "as of before this
        // call".
        var (ready, signed) = NormalizeSignature(request.Ready, request.Signed);
        // All four stamps as they stand, captured before anything is written back — the detector
        // compares this against what the request leaves behind (issue #154 §8.1). The two that were
        // already captured for the log line are simply the two the pattern started with.
        var previousStamps = new ContractStamps(contract.Paused, contract.Archived, contract.Ready, contract.Signed);

        contract.Name = request.Name;
        contract.Type = request.Type.Adapt<ContextContractType>();
        contract.Description = request.Description;
        contract.ReferenceNumber = NormalizeReferenceNumber(request.ReferenceNumber);
        contract.StartDate = startDate;
        contract.EndDate = endDate;
        contract.CompletionDate = completionDate;
        // The lifecycle is ORDERED, not orthogonal: archiving retires a contract that is already
        // over, so only an ended one can be archived. Validated against the request's dates, not the
        // stored ones, so a single PUT may end and archive in one go.
        // Widened by issue #145: an UNSIGNED contract is archivable whatever its dates, because
        // abandoning a negotiation is the single likeliest reason to archive a draft and a draft
        // typically has no end date at all — the un-widened rule would strand it forever.
        EnsureArchivable(contract, request.IsArchived, endDate, completionDate, signed);
        // Then the pause guard, against the same request dates plus the STORED archive stamp — so a
        // body asserting both on a contract that has ended is refused here, and one on a contract that
        // has not is refused above — and against the REQUEST'S signature stamps (issue #145 §8), so a
        // single PUT that signs a Draft contract and pauses it in the same body succeeds.
        EnsurePausable(contract, request.IsPaused, startDate, endDate, completionDate, ready, signed);

        // Archive (preserving the original archive stamp) or unarchive per the request.
        contract.Archived = request.IsArchived
            ? contract.Archived ?? timeProvider.GetUtcNow().UtcDateTime
            : null;

        // Pause or resume, same idempotence rule: a repeated or replayed PUT keeps the ORIGINAL stamp,
        // so "paused since" never resets. Neither stamp is auto-cleared by the other — archiving a
        // paused contract retains the pause, losslessly, and the derivation simply reports Archived.
        contract.Paused = request.IsPaused
            ? contract.Paused ?? timeProvider.GetUtcNow().UtcDateTime
            : null;

        // Full replacement, unlike the two stamps above: these carry a caller-supplied MOMENT, not a
        // boolean intent, so there is no original value to preserve and no idempotence rule to apply.
        contract.Ready = ready;
        contract.Signed = signed;

        var nowUtc = UtcNow;
        var stamps = new ContractStamps(contract.Paused, contract.Archived, contract.Ready, contract.Signed);
        // At most four extra INSERTs in the existing single save, and zero extra queries: the before
        // values were read off the already-loaded tracked entity.
        ContractEventRecorder.StageAll(
            context, id, DetectStampTransitions(previousStamps, stamps, nowUtc), userId, nowUtc);

        await context.SaveChangesAsync(cancellationToken);

        LogStampWrites(id, previousStamps, stamps, userId);

        var reloaded = await LoadWithDetails(id, cancellationToken);
        return await ToDto(reloaded!, Today, cancellationToken);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        // Hard delete: removes the contract and cascades its party + file link rows, its term history
        // (issue #135), its event log (issue #138) and its smart-tag links (issue #166). The underlying
        // accounts/contacts/policies, FileMetadata/blobs and TransactionTags are left intact. Children
        // are loaded so the cascade also applies under the EF InMemory provider (used by tests), which
        // does not enforce database-level cascade — without the Terms/Events/SmartTags includes a
        // contract delete would orphan every such row on exactly the tier meant to catch it.
        var contract = await context.Contracts
            .Include(c => c.Parties)
            .Include(c => c.Files)
            .Include(c => c.Terms)
            .Include(c => c.Events)
            .Include(c => c.SmartTags)
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

    /// <summary>
    /// Every contract naming <paramref name="accountId"/> as a party, one row per contract with each
    /// role the account holds there, for the account record's Contracts section. Returns
    /// <see langword="null"/> when the account does not exist, so the route can tell a missing account
    /// from one that is simply party to nothing.
    /// </summary>
    /// <remarks>
    /// Archived contracts are included, as they are on the contracts list by default: the link is still
    /// on record and the tile states the status. Ordered by name, then id, so the order is stable.
    /// </remarks>
    public async Task<List<AccountContractLink>?> ListForAccountAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken))
            return null;

        var today = Today;
        var rows = await context.Contracts
            .AsNoTracking()
            .Where(c => c.Parties.Any(p => p.AccountId == accountId))
            .Select(c => new
            {
                Contract = c,
                Roles = c.Parties
                    .Where(p => p.AccountId == accountId)
                    .OrderBy(p => p.ContractPartyId)
                    .Select(p => p.Role)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .Select(x => new AccountContractLink
                {
                    ContractId = x.Contract.ContractId,
                    Name = x.Contract.Name,
                    Type = x.Contract.Type.Adapt<DtoContractType>(),
                    Status = ContractStatusRules.DeriveStatus(x.Contract, today),
                    Roles = [.. x.Roles.Select(r => r.Adapt<DtoContractPartyRole>())],
                })
                .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(l => l.ContractId),
        ];
    }

    /// <summary>
    /// Every contract naming <paramref name="propertyId"/> as a party, one row per contract with each
    /// role the property holds there, for the property record's Contracts section (issue #208 §5.5).
    /// <see cref="ListForAccountAsync"/> line for line: <see langword="null"/> when the property does
    /// not exist, archived contracts included, ordered by name then id.
    /// </summary>
    public async Task<List<PropertyContractLink>?> ListForPropertyAsync(
        Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (!await context.Properties.AnyAsync(p => p.PropertyId == propertyId, cancellationToken))
            return null;

        var today = Today;
        var rows = await context.Contracts
            .AsNoTracking()
            .Where(c => c.Parties.Any(p => p.PropertyId == propertyId))
            .Select(c => new
            {
                Contract = c,
                Roles = c.Parties
                    .Where(p => p.PropertyId == propertyId)
                    .OrderBy(p => p.ContractPartyId)
                    .Select(p => p.Role)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .Select(x => new PropertyContractLink
                {
                    ContractId = x.Contract.ContractId,
                    Name = x.Contract.Name,
                    Type = x.Contract.Type.Adapt<DtoContractType>(),
                    Status = ContractStatusRules.DeriveStatus(x.Contract, today),
                    Roles = [.. x.Roles.Select(r => r.Adapt<DtoContractPartyRole>())],
                })
                .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(l => l.ContractId),
        ];
    }

    /// <summary>
    /// Refuses a contract type change that would orphan an existing party (issue #157 §3.3). No-op
    /// when the type is unchanged, so an ordinary edit costs nothing.
    /// </summary>
    private async Task EnsureTypeChangeKeepsPartiesLegalAsync(
        Contract contract, DtoContractType requestedType, CancellationToken cancellationToken)
    {
        var offending = await FindPartiesRejectedByTypeAsync(contract, requestedType, cancellationToken);
        if (offending.Count == 0)
        {
            return;
        }

        // The structured list is the controller's to shape; this message is what a non-HTTP caller
        // gets, and it names the same two routes out.
        throw new DomainUnprocessableException(
            $"{offending.Count} part{(offending.Count == 1 ? "y" : "ies")} on this contract "
            + $"hold{(offending.Count == 1 ? "s" : "")} a role a {requestedType} contract cannot have. "
            + "Re-role or detach them first.",
            nameof(UpdateContract.Type));
    }

    /// <summary>
    /// The parties on <paramref name="contractId"/> whose role <paramref name="requestedType"/> would
    /// reject, projected to what the <c>422</c> body names them by. Empty when the change is safe, and
    /// when the contract does not exist.
    /// </summary>
    /// <remarks>
    /// Public because <c>ContractsController</c> pre-checks with it to build the claim-free structured
    /// body, exactly as the contact-delete <c>409</c> pre-checks with <c>IContactReferenceGuard</c>:
    /// a <c>DomainException</c> cannot carry a list of objects. Both callers therefore have to agree
    /// on <em>when</em> the rule applies, which is why the "only on a type CHANGE" condition lives
    /// here rather than at either call site.
    /// </remarks>
    public async Task<IReadOnlyList<BlockingContractParty>> FindPartiesRejectedByTypeAsync(
        Guid contractId, DtoContractType requestedType, CancellationToken cancellationToken = default)
    {
        var contract = await context.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ContractId == contractId, cancellationToken);
        return contract is null
            ? []
            : await FindPartiesRejectedByTypeAsync(contract, requestedType, cancellationToken);
    }

    private async Task<IReadOnlyList<BlockingContractParty>> FindPartiesRejectedByTypeAsync(
        Contract contract, DtoContractType requestedType, CancellationToken cancellationToken)
    {
        // Only a type CHANGE is checked. A contract keeping its type is never refused, however
        // illegal an existing party's role is: such a row is a legacy one the migration could not
        // reach or one stranded by an earlier change, and freezing every other field on its contract
        // would not help — the party endpoint is where it gets fixed. It also means an ordinary edit
        // costs no query at all.
        if (contract.Type.Adapt<DtoContractType>() == requestedType)
        {
            return [];
        }

        // One projection of this contract's own party rows — bounded by the parties on a single
        // contract, so single digits in practice (§10).
        var parties = await context.ContractParties
            .AsNoTracking()
            .Where(p => p.ContractId == contract.ContractId)
            .Select(p => new
            {
                p.ContractPartyId,
                p.Role,
                p.ContactId,
                AccountName = p.Account != null ? p.Account.Name : null,
                PropertyName = p.Property != null ? p.Property.Name : null,
            })
            .OrderBy(p => p.ContractPartyId)
            .ToListAsync(cancellationToken);

        var rejected = parties
            .Where(p => !ContractPartyRoleMatrix.IsLegal(requestedType, p.Role.Adapt<DtoContractPartyRole>()))
            .ToList();
        if (rejected.Count == 0)
        {
            return [];
        }

        var contactIds = rejected.Where(p => p.ContactId is not null).Select(p => p.ContactId!.Value).Distinct().ToList();
        IReadOnlyDictionary<Guid, ContactRef> contacts = contactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(contactIds, cancellationToken);

        return
        [
            .. rejected.Select(p => new BlockingContractParty
            {
                ContractPartyId = p.ContractPartyId,
                Role = p.Role.Adapt<DtoContractPartyRole>(),
                // An unresolvable target keeps its row and loses its name — the rule the link
                // link collections already follow.
                // Resolved contact → account → property (issue #208 §8).
                DisplayName = p.ContactId is { } contactId
                    ? contacts.GetValueOrDefault(contactId)?.Name
                    : p.AccountName ?? p.PropertyName,
            }),
        ];
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
        Contract contract, bool isArchived, DateTime? endDate, DateTime? completionDate, DateTime? signed)
    {
        if (!isArchived || contract.Archived is not null)
        {
            return;
        }

        // Widened by issue #145: archiving is permitted when the contract has ended OR when the
        // request leaves it UNSIGNED. Abandoning a negotiation is the likeliest reason to archive a
        // draft, and a draft typically has no end date at all — without this branch the un-widened
        // rule would leave an abandoned draft un-archivable forever, with no step the reader could
        // take to satisfy it.
        if (signed is null)
        {
            return;
        }

        if (!ContractLifecycle.HasEnded(endDate, completionDate, Today))
        {
            throw new DomainValidationException(
                "A contract can only be archived once it has ended. Set an EndDate before today, or a CompletionDate on or before today, first.");
        }
    }

    /// <summary>
    /// The mirror of <see cref="EnsureArchivable"/> for the pause stamp (issue #140 §8): a
    /// <b>transition into</b> paused is permitted only from <c>Active</c>.
    ///
    /// <para>
    /// Evaluated against the <b>request's</b> dates and the <b>stored</b> archive stamp, exactly as the
    /// archive guard is, so one PUT may move a start date into the past and pause in the same write.
    /// </para>
    ///
    /// <para>
    /// Only the transition is checked. A contract already paused is never re-validated, so one that
    /// later expires or is archived is never stranded in a state it cannot be written out of — and
    /// <b>clearing a pause is never refused</b>, on any contract in any state. A guard on the way out
    /// is how a row gets stranded.
    /// </para>
    /// </summary>
    private void EnsurePausable(
        Contract contract, bool isPaused, DateTime? startDate, DateTime? endDate, DateTime? completionDate,
        DateTime? ready, DateTime? signed)
    {
        if (!isPaused || contract.Paused is not null)
        {
            return;
        }

        // The base status, not the full one: the contract is not paused yet, so there is nothing for
        // the Paused member to replace, and asking for it back would be circular. It is now
        // SIGNATURE-AWARE, so pausing a Draft or Ready contract refuses under the existing code with a
        // message naming the actual state — a pause is a stamp with nothing to suspend on a contract
        // nobody has signed, and it would put the row into a state the derivation never reports.
        //
        // It reads the REQUEST'S signature stamps, not the stored ones (issue #145 §8), and that is a
        // deliberate asymmetry with the STORED archive stamp beside it. EnsureArchivable has already
        // adjudicated the archive transition one line above, so re-reading the request's archive
        // intent here would double-judge it; no such prior guard exists for the signature stamps, so
        // the same stale read would be a defect rather than a mirror of one — a single PUT that signs
        // a Draft contract AND pauses it would be judged against the still-null stored Signed and
        // refused, for a contract the very same body makes Active.
        var status = ContractStatusRules.DeriveBaseStatus(startDate, endDate, completionDate, contract.Archived, ready, signed, Today);
        if (status != ContractStatus.Active)
        {
            throw new DomainValidationException(
                $"Only an active contract can be paused. This contract is {status} — clear its archive, or move its dates so it is running today, first.",
                "contract_pause_requires_active",
                nameof(UpdateContract.IsPaused));
        }
    }

    // ── Validation helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Trim, then blank to null (issue #181). Called from <c>Create</c> and <c>Update</c> alike, so
    /// "no reference number" has one stored representation on both write paths.
    /// </summary>
    private static string? NormalizeReferenceNumber(string? referenceNumber) =>
        ContractReferenceNumber.Normalize(referenceNumber);

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

    /// <summary>
    /// Normalizes both signature stamps to UTC and runs the three signature guards (issue #145 §8).
    /// Shared verbatim by <c>Create</c> and <c>Update</c>: a rule enforced on one write path and not
    /// the other is the defect class this codebase keeps closing.
    ///
    /// <para>
    /// <b>Clearing is never refused.</b> Both null, or a cleared <c>Signed</c> on a signed contract in
    /// any state, passes every guard — a guard on the way out is how a row gets stranded, which is the
    /// rule <c>EnsurePausable</c> already states in so many words.
    /// </para>
    ///
    /// <para>
    /// The two future checks run first so the field a client highlights matches the field the server
    /// names for the same body: the shared client-side helper (<c>conSignatureError</c> in the design
    /// system) tests them in this order.
    /// </para>
    /// </summary>
    private (DateTime? Ready, DateTime? Signed) NormalizeSignature(DateTime? ready, DateTime? signed)
    {
        // Through the same funnel every client-supplied date on this surface already passes, so a
        // Local or Unspecified kind cannot store a value off by a timezone offset.
        var readyUtc = DateTimeNormalization.NormalizeToUtc(ready);
        var signedUtc = DateTimeNormalization.NormalizeToUtc(signed);

        var today = Today;

        // G3 — DATE granularity, not instant: a client clock a few minutes ahead of the server must
        // not turn an ordinary "signed just now" into a 400, while a value dated tomorrow or later is
        // still refused. Both stamps record something that has HAPPENED, which is what makes Signed a
        // fact rather than a schedule.
        if (readyUtc is { } readyValue && readyValue.Date > today)
        {
            throw new DomainValidationException(
                "A ready date records something that has happened — it cannot be in the future.",
                "contract_signature_date_in_future",
                nameof(UpdateContract.Ready));
        }

        if (signedUtc is { } signedValue && signedValue.Date > today)
        {
            throw new DomainValidationException(
                "A signed date records something that has happened — it cannot be in the future.",
                "contract_signature_date_in_future",
                nameof(UpdateContract.Signed));
        }

        // G1 — ONE rule, not two. Under the full-replacement PUT, "clearing Ready on a signed
        // contract" and "signing a contract that was never marked ready" are the same request shape
        // (signed present, ready absent), so they take one guard and one code.
        if (signedUtc is not null && readyUtc is null)
        {
            throw new DomainValidationException(
                "A signed contract needs a ready date too. Set when it was ready for signature, or clear the signed date.",
                "contract_signed_requires_ready",
                nameof(UpdateContract.Signed));
        }

        // G2 — INSTANT granularity, unlike G3. Both values come from the same request body, so there
        // is no clock to be skewed against: a caller that sends a signed one second before its own
        // ready has contradicted itself, and rounding that away to date granularity would silently
        // accept it.
        if (signedUtc is { } s2 && readyUtc is { } r2 && s2 < r2)
        {
            throw new DomainValidationException(
                "A contract cannot be signed before it was ready for signature.",
                "contract_signed_before_ready",
                nameof(UpdateContract.Signed));
        }

        return (readyUtc, signedUtc);
    }

    // ── Stamp transitions: detection, recording and the log safety net (issue #154) ───

    /// <summary>
    /// A contract's four lifecycle stamps as one value, so "before" and "after" are one thing each
    /// rather than eight loose locals threaded through three methods.
    /// </summary>
    private sealed record ContractStamps(DateTime? Paused, DateTime? Archived, DateTime? Ready, DateTime? Signed)
    {
        /// <summary>
        /// The all-null "before" a <c>POST</c> is judged against — a contract that did not exist held
        /// none of the four stamps.
        /// </summary>
        public static readonly ContractStamps None = new(null, null, null, null);
    }

    /// <summary>
    /// Compares the four stamps as they stood against the four as the request leaves them and emits one
    /// descriptor per changed stamp — at most four (issue #154 §8.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It fires on the CHANGE, never on the value</b>, and three failure modes follow from that.
    /// A replayed or idempotent <c>PUT</c> writes nothing, because <c>Paused</c> and <c>Archived</c>
    /// preserve their original stamp on a repeated write and so compare equal. A <b>re-date</b> —
    /// non-null to a <em>different</em> non-null, which only the two caller-supplied stamps can
    /// express — is a correction to the record rather than a signing, and writes nothing either.
    /// And several transitions in one request each write their own event.
    /// </para>
    /// <para>
    /// <b>The moment is resolved per transition</b> (§8.4). A server-generated stamp
    /// (<c>Paused</c>, <c>Archived</c>) carries its own value, which <em>is</em> the server clock at
    /// that write. A caller-supplied one (<c>Ready</c>, <c>Signed</c>) is clamped to
    /// <c>min(stamp, now)</c>: the stamp is validated at <b>date</b> granularity, so a contract signed
    /// "today" may carry an instant hours ahead of the server clock, and writing it raw would produce
    /// an event that <c>ContractEventService</c>'s own instant-plus-tolerance bound would reject.
    /// Taking <c>now</c> unconditionally is not the fix — a contract signed last March must date its
    /// event last March. Every <b>cleared</b> stamp takes the server clock; there is no stamp left to
    /// read.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<TransitionDescriptor> DetectStampTransitions(
        ContractStamps before, ContractStamps after, DateTime nowUtc)
    {
        var descriptors = new List<TransitionDescriptor>(4);

        Detect(ContractStamp.Paused, before.Paused, after.Paused);
        Detect(ContractStamp.Archived, before.Archived, after.Archived);
        Detect(ContractStamp.Ready, before.Ready, after.Ready);
        Detect(ContractStamp.Signed, before.Signed, after.Signed);

        return descriptors;

        void Detect(ContractStamp stamp, DateTime? was, DateTime? now)
        {
            if (was is null && now is { } set)
            {
                descriptors.Add(ContractEventCatalogue.Stamp(stamp, set: true, OccurredAtForSet(stamp, set, nowUtc)));
            }
            else if (was is not null && now is null)
            {
                descriptors.Add(ContractEventCatalogue.Stamp(stamp, set: false, nowUtc));
            }
        }
    }

    private static DateTime OccurredAtForSet(ContractStamp stamp, DateTime stampValue, DateTime nowUtc) =>
        stamp switch
        {
            ContractStamp.Paused or ContractStamp.Archived => stampValue,
            _ => stampValue < nowUtc ? stampValue : nowUtc,
        };

    /// <summary>
    /// One structured <c>Information</c> line per stamp transition (issue #145 §7.7, widened to all
    /// four stamps by issue #154 §8.8). A write that changes no stamp emits nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the safety net under an editable log.</b> Every <c>ContractEvent</c> issue #154
    /// records is editable and deletable by any <c>contracts.update</c> holder, so a row's presence is
    /// evidence that something happened and its absence is evidence of nothing. These lines go to the
    /// application log rather than the database, which no endpoint can edit or delete — so pausing a
    /// contract and then deleting the event that recorded it still leaves a trace. Before #154 this
    /// covered the two <em>signature</em> stamps only, which would have left exactly that hole under
    /// <c>Paused</c> and <c>Archived</c>.
    /// </para>
    /// <para>
    /// Neither signature field carries a <c>MarkedReadyByUserId</c> / <c>SignedRecordedByUserId</c>
    /// attribution COLUMN, deliberately: no contract field carries attribution today, and any holder of
    /// <c>contracts.update</c> can already rewrite the name, counterparty, dates and price history
    /// unattributed. The trigger to revisit is stated in issue #145 §7.7 — if contract mutations
    /// become audited, or if <c>Signed</c> is ever treated as evidence of a legal fact rather than a
    /// record-keeping convenience, BOTH fields take <c>SET NULL</c> FK columns in the same change.
    /// </para>
    /// <para>
    /// Every value is an opaque id, a fixed literal or a timestamp — never a contract name, a party
    /// name or any free text — so the line names rows a reader would still need <c>contracts.read</c>
    /// to resolve, exactly as the party line's does, and carries nothing a forged log line could use.
    /// </para>
    /// </remarks>
    private void LogStampWrites(Guid contractId, ContractStamps before, ContractStamps after, string? userId)
    {
        LogStampWrite(contractId, ContractStamp.Paused, before.Paused, after.Paused, userId);
        LogStampWrite(contractId, ContractStamp.Archived, before.Archived, after.Archived, userId);
        LogStampWrite(contractId, ContractStamp.Ready, before.Ready, after.Ready, userId);
        LogStampWrite(contractId, ContractStamp.Signed, before.Signed, after.Signed, userId);
    }

    private void LogStampWrite(
        Guid contractId, ContractStamp stamp, DateTime? before, DateTime? after, string? userId)
    {
        if (Nullable.Equals(before, after))
        {
            return;
        }

        logger.LogInformation(
            "Contract stamp {Stamp} {Action}: contract {ContractId}, {Before} -> {After}, by user {UserId}.",
            stamp,
            after is null ? "cleared" : "set",
            contractId,
            before?.ToString("O") ?? NoValue,
            after?.ToString("O") ?? NoValue,
            userId ?? "(unknown)");
    }

    /// <summary>What a log slot reads when the stamp is absent on that side of the write.</summary>
    private const string NoValue = "(none)";

    // ── Loading & mapping ───────────────────────────────────────────────────────────

    private async Task<Contract?> LoadWithDetails(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Contracts
            .Include(c => c.Parties).ThenInclude(p => p.Account)
            .Include(c => c.Parties).ThenInclude(p => p.Property)
            .Include(c => c.Files).ThenInclude(f => f.FileMetadata)
            .FirstOrDefaultAsync(c => c.ContractId == id, cancellationToken);
    }

    /// <summary>
    /// The in-force entry of each of the contract's term series (issue #135) — <b>one</b> additional
    /// indexed read on the detail path, filtered on the contract and the cutoff in SQL and collapsed
    /// per series in memory by the shared <see cref="TermSeries.Current"/> rule. Deliberately not an
    /// <c>Include</c> on <see cref="LoadWithDetails"/>: that would materialise the whole history
    /// (bounded only by the per-contract cap) to return at most one row per series.
    /// </summary>
    private async Task<List<AccountCurrentTerm>> LoadCurrentTermsAsync(
        Guid contractId, DateTime asOf, CancellationToken cancellationToken)
    {
        var candidates = await context.Terms
            .AsNoTracking()
            .Where(t => t.ContractId == contractId && t.EffectiveFrom <= asOf)
            .ToListAsync(cancellationToken);

        return TermSeries.Current(candidates).Adapt<List<AccountCurrentTerm>>();
    }

    private async Task<ExistingContract> ToDto(Contract contract, DateTime today, CancellationToken cancellationToken)
    {
        // Batch-resolve the distinct, non-null party contact ids in one IContactLookup call rather
        // than a navigation include.
        var contactIds = contract.Parties
            .Where(p => p.ContactId is not null)
            .Select(p => p.ContactId!.Value)
            .Distinct()
            .ToList();
        IReadOnlyDictionary<Guid, ContactRef> contacts = contactIds.Count == 0
            ? new Dictionary<Guid, ContactRef>()
            : await contactLookup.ResolveRefsAsync(contactIds, cancellationToken);

        // Resolved against the same "today" the derived status uses, so one request cannot report a
        // contract as expired while pricing it as in force.
        var currentTerms = await LoadCurrentTermsAsync(contract.ContractId, today, cancellationToken);

        return new ExistingContract
        {
            ContractId = contract.ContractId,
            Name = contract.Name,
            Type = contract.Type.Adapt<DtoContractType>(),
            Description = contract.Description,
            ReferenceNumber = contract.ReferenceNumber,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
            CompletionDate = contract.CompletionDate,
            Status = ContractStatusRules.DeriveStatus(contract, today),
            Parties = contract.Parties
                .OrderBy(p => p.ContractPartyId)
                .Select(p => ContractProjection.ToPartyDto(p, contacts))
                .ToList(),
            Files = contract.Files
                .Where(f => f.FileMetadata is not null)
                .OrderBy(f => f.AttachedAtUtc)
                .Select(ContractProjection.ToFileDto)
                .ToList(),
            CurrentTerms = currentTerms,
            Archived = contract.Archived,
            Paused = contract.Paused,
            Ready = contract.Ready,
            Signed = contract.Signed,
            CreatedAtUtc = contract.CreatedAtUtc,
        };
    }
}
