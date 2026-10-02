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
/// A contract's parties (issue #121): add, re-write and detach, with the one-of-three target rule, the
/// role matrix for the contract's type, the term anchor, the uniqueness pre-check and the per-contract
/// cap. Every write stages its event and writes its <see cref="ContractPartyAudit"/> line. Split out of
/// <see cref="ContractService"/> (issue #287 M1), which keeps the type-change check because that guards
/// a contract write, not a party write.
/// </summary>
public class ContractPartyService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;
    private readonly ISystemSettingsLookup systemSettingsLookup;
    private readonly ILogger<ContractPartyService> logger;

    public ContractPartyService(
        OdysseyContext context,
        IContactLookup contactLookup,
        TimeProvider timeProvider,
        ISystemSettingsLookup systemSettingsLookup,
        ILogger<ContractPartyService> logger)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
        this.systemSettingsLookup = systemSettingsLookup;
        this.logger = logger;
    }

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Links one account, contact or property to a contract, in a role, optionally for a term (issue #121 §5).
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

        EnsurePartyTargetXor(request);

        await EnsureTargetExists(request, cancellationToken);
        var requestedRole = EnsureRoleLegalForType(contract, request);
        var role = requestedRole.Adapt<ContextContractPartyRole>();
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
            PropertyId = request.PropertyId,
            Role = role,
            FromDate = fromDate,
            ToDate = toDate,
        };

        context.ContractParties.Add(party);

        // The event carries no link to the row being inserted (issue #138 Non-Goal 5) — only the role —
        // so there is nothing to wait for: it is staged in the same change tracker as the insert and
        // both go out in one save. No second round trip and no post-save write.
        var addedAt = UtcNow;
        ContractEventRecorder.Stage(
            context, contractId, ContractEventCatalogue.PartyAdded(role, addedAt), userId, addedAt);

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
    /// The body is a <b>full replacement</b>, not a patch: an omitted date clears it, and the role is
    /// required (issue #157 §8.1) so it is always restated. That is why every write is logged (§7.7).
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
        // meanwhile does not fail.
        if (!SameTarget(party, request))
        {
            await EnsureTargetExists(request, cancellationToken);
        }

        var requestedRole = EnsureRoleLegalForType(contract, request);
        var role = requestedRole.Adapt<ContextContractPartyRole>();
        var (fromDate, toDate) = NormalizePartyTerm(contract, request);
        await EnsureNotDuplicateParty(contractId, request, role, excludingPartyId: partyId, cancellationToken);

        var previousRole = party.Role;
        // Whether the link is being REPOINTED, judged before the row is overwritten. The row is updated
        // in place and stays one party (issue #121), but from the agreement's point of view one party
        // left and another joined — which is exactly the change a reader of the log needs to see, and
        // which would otherwise let a party vanish from the tiles with a silent log (issue #154 §8.2).
        var targetChanged = !SameTarget(party, request);

        party.AccountId = request.AccountId;
        party.ContactId = request.ContactId;
        party.PropertyId = request.PropertyId;
        party.Role = role;
        party.FromDate = fromDate;
        party.ToDate = toDate;

        if (targetChanged)
        {
            // Ordered removed-then-added so it reads in the order it happened. A write that changes only
            // the role and/or the dates records NOTHING here: that describes HOW an existing party is
            // described, not WHO is party to the agreement, and LogPartyWrite already covers it.
            var changedAt = UtcNow;
            ContractEventRecorder.Stage(
                context, contractId, ContractEventCatalogue.PartyRemoved(previousRole, changedAt), userId, changedAt);
            ContractEventRecorder.Stage(
                context, contractId, ContractEventCatalogue.PartyAdded(role, changedAt), userId, changedAt);
        }

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

        var removedAt = UtcNow;
        ContractEventRecorder.Stage(
            context, contractId, ContractEventCatalogue.PartyRemoved(party.Role, removedAt), userId, removedAt);

        await context.SaveChangesAsync(cancellationToken);

        // A detach has no role AFTER — the row is gone. Writing Unspecified there would make the line
        // byte-identical to a PUT that downgraded the role to Unspecified, which is precisely the event
        // this log exists to make visible; the two would then differ only by the action word, so a query
        // for the downgrade would match every detach as well.
        LogPartyWrite("detached", party, party.Role, userId, roleAfter: NoRole);
        return true;
    }

    /// <summary>What the "after" slot reads when there is no role after the write, i.e. on a detach.</summary>
    private const string NoRole = ContractPartyAudit.NoRole;

    /// <summary>
    /// One structured <c>Information</c> line per party write (issue #121 §7.7). <c>ContractParty</c>
    /// deliberately carries no <c>CreatedByUserId</c> column, so without this line an accidental role
    /// downgrade on the full-replacement <c>PUT</c> would leave no trace anywhere. A thin call to
    /// <see cref="ContractPartyAudit"/>, which <c>PropertyService.Delete</c> shares (issue #208), so the
    /// two sites cannot drift.
    /// </summary>
    private void LogPartyWrite(
        string action, ContractParty party, ContextContractPartyRole? previousRole, string? userId,
        string? roleAfter = null) =>
        ContractPartyAudit.Log(logger, action, party, previousRole, userId, roleAfter);

    /// <summary>Whether <paramref name="request"/> names the same target the stored row does.</summary>
    private static bool SameTarget(ContractParty party, ContractPartyRequest request) =>
        party.AccountId == request.AccountId &&
        party.ContactId == request.ContactId &&
        party.PropertyId == request.PropertyId;

    private async Task<ExistingContractParty> ProjectPartyAsync(Guid partyId, CancellationToken cancellationToken)
    {
        var loaded = await LoadPartyWithTargets(partyId, cancellationToken);
        IReadOnlyDictionary<Guid, ContactRef> contacts = loaded!.ContactId is { } contactId
            ? await contactLookup.ResolveRefsAsync([contactId], cancellationToken)
            : new Dictionary<Guid, ContactRef>();
        return ContractProjection.ToPartyDto(loaded, contacts);
    }

    /// <summary>
    /// The matrix check for a party write (issue #157 §3.2 step 3, §8.2). Returns the requested role
    /// once it is known legal on <paramref name="contract"/>'s type.
    /// </summary>
    /// <remarks>
    /// A service-layer rule rather than a data annotation, deliberately: legality depends on the
    /// <em>contract's</em> type, which model validation cannot see because the request body does not
    /// carry it. This is the derived-bound case CLAUDE.md distinguishes from a compile-time one, so a
    /// validator here is correct rather than decorative.
    ///
    /// <para>
    /// Raises <see cref="DomainUnprocessableException"/> — a <c>422</c>, not the <c>400</c> a
    /// <see cref="DomainValidationException"/> would give: the body is well-formed and every value in
    /// it is a real member, so what fails is the combination. The field key is <c>role</c>, so the
    /// message lands on the control the client rendered.
    /// </para>
    /// </remarks>
    private static DtoContractPartyRole EnsureRoleLegalForType(Contract contract, ContractPartyRequest request)
    {
        // [Required] already refused a null role on the HTTP path; a direct caller gets the same
        // rejection here rather than a NullReferenceException.
        if (request.Role is not { } role)
        {
            throw new DomainValidationException(
                "A party role is required.", code: null, field: nameof(ContractPartyRequest.Role));
        }

        var type = contract.Type.Adapt<DtoContractType>();
        if (ContractPartyRoleMatrix.IsLegal(type, role))
        {
            return role;
        }

        throw new DomainUnprocessableException(
            $"{role} is not a role a {type} contract can have. "
            + $"The roles it can have are: {DescribeLegalRoles(type)}.",
            nameof(ContractPartyRequest.Role));
    }

    /// <summary>
    /// The legal roles for <paramref name="type"/> as a reading list, suggested ones first — the same
    /// order the picker offers them in, so the message and the control agree.
    /// </summary>
    private static string DescribeLegalRoles(DtoContractType type) =>
        string.Join(", ", ContractPartyRoleMatrix.LegalFor(type));

    // One-of-three: exactly one target id must be set (issue #208 widened it from one-of-two).
    private static void EnsurePartyTargetXor(ContractPartyRequest request)
    {
        var setCount =
            (request.AccountId is not null ? 1 : 0) +
            (request.ContactId is not null ? 1 : 0) +
            (request.PropertyId is not null ? 1 : 0);
        if (setCount != 1)
        {
            // Keyed on the property field when it is one of several targets sent, so a client that
            // offered the property picker renders the message there (issue #208 §5.1).
            const string message = "Exactly one of accountId, contactId or propertyId must be set.";
            if (setCount > 1 && request.PropertyId is not null)
            {
                throw new DomainValidationException(message, code: null, field: nameof(ContractPartyRequest.PropertyId));
            }

            throw new DomainValidationException(message);
        }
    }

    /// <summary>
    /// The field key the inline-rendered party failures are attributed to: whichever of the three target
    /// ids the caller actually sent, since that is the control the client rendered.
    /// </summary>
    private static string PartyTargetField(ContractPartyRequest request) =>
        request.AccountId is not null ? nameof(ContractPartyRequest.AccountId)
        : request.ContactId is not null ? nameof(ContractPartyRequest.ContactId)
        : nameof(ContractPartyRequest.PropertyId);

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
    /// neither re-validates nor re-dates its parties: a renewal never
    /// re-dates a party.
    /// </remarks>
    private static (DateTime? FromDate, DateTime? ToDate) NormalizePartyTerm(
        Contract contract, ContractPartyRequest request)
    {
        var fromDate = DateTimeNormalization.NormalizeToUtc(request.FromDate);
        var toDate = DateTimeNormalization.NormalizeToUtc(request.ToDate);

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
        else if (request.PropertyId is { } propertyId)
        {
            // An archived or disposed property may still be linked: history must stay recordable
            // (issue #208 §8), exactly as an archived account can be.
            if (!await context.Properties.AnyAsync(p => p.PropertyId == propertyId, cancellationToken))
            {
                throw new DomainNotFoundException(
                    $"Property ID {propertyId} not found.", nameof(ContractPartyRequest.PropertyId));
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
             (request.ContactId != null && p.ContactId == request.ContactId) ||
             (request.PropertyId != null && p.PropertyId == request.PropertyId)), cancellationToken);
        if (duplicate)
        {
            throw new DomainConflictException(
                "That party is already linked to the contract in that role.",
                PartyTargetField(request));
        }
    }

    private async Task<ContractParty?> LoadPartyWithTargets(Guid partyId, CancellationToken cancellationToken = default)
    {
        return await context.ContractParties
            .Include(p => p.Account)
            .Include(p => p.Property)
            .FirstOrDefaultAsync(p => p.ContractPartyId == partyId, cancellationToken);
    }
}
