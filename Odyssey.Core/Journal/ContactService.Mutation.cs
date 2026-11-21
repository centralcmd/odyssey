using Odyssey.Core.Finance;
using System.Data;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using DetachedContactLinks = Odyssey.Dtos.Finance.DetachedContactLinks;
using ContactDeleteBlockerClass = Odyssey.Dtos.Finance.ContactDeleteBlockerClass;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Journal;

public partial class ContactService
{
    public async Task<ExistingContact> Create(NewContact newContact, CancellationToken cancellationToken = default)
    {
        ValidateShape(newContact);

        var externalUid = await ResolveExternalUid(newContact.ExternalUid, excludingContactId: null, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var contact = new Contact
        {
            NormalizedName = string.Empty,
            Type = newContact.Type,
            ExternalUid = externalUid,
            CreatedAt = now,
            UpdatedAt = now,
            Archived = newContact.Archived ? now : null,
        };

        ApplyBaseAndDetails(contact, newContact);

        context.Contacts.Add(contact);
        await context.SaveChangesAsync(cancellationToken);

        return (await Get(contact.ContactId, cancellationToken))!;
    }

    /// <summary>
    /// Replaces a contact. A <see cref="NewContact.Type"/> that differs from the stored type converts it,
    /// with the caller supplying the full target details (issue #47) — and is refused with
    /// <see cref="ContactTypeChangeBlockedException"/> while a type-restricted link exists (issue #335).
    ///
    /// <para>
    /// The whole unit of work — load, blocker check, mutation, save — runs inside one transaction whose
    /// execution strategy starts every attempt from a clean change tracker, so a retry re-reads the row
    /// instead of replaying against the mutated entity a failed attempt left behind (§3.2). The blocker
    /// check runs before any tracked entity is mutated, so a refusal leaves nothing dirty.
    /// </para>
    /// </summary>
    public async Task<ExistingContact?> Update(Guid id, NewContact putContact, CancellationToken cancellationToken = default)
    {
        var found = false;

        await ExecuteAtomicallyAsync(async () =>
        {
            var contact = await LoadForTypeChange(id, cancellationToken);
            found = contact is not null;
            if (contact is null)
            {
                return;
            }

            ValidateShape(putContact);

            // Resolved into a local, assigned after the blocker check: nothing on the tracked entity may
            // change before the check can still refuse the write (issue #335 §3.2 step 5).
            var externalUid = putContact.ExternalUid is null
                ? null
                : await ResolveExternalUid(putContact.ExternalUid, excludingContactId: id, cancellationToken);

            // Captured BEFORE the assignment below: the remap's whole trigger is the comparison, and
            // reading contact.Type afterwards would make it always equal (issue #47 §5).
            var previousType = contact.Type;
            if (previousType != putContact.Type)
            {
                await EnsureTypeChangeAllowedAsync(id, previousType, putContact.Type, cancellationToken);
            }

            if (externalUid is not null)
            {
                contact.ExternalUid = externalUid;
            }

            contact.Type = putContact.Type;
            ApplyBaseAndDetails(contact, putContact);
            ApplyArchiveTransition(contact, putContact.Archived);
            contact.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

            if (previousType != contact.Type)
            {
                await RemapChildLabelsAsync(contact, previousType, cancellationToken);
            }

            await context.SaveChangesAsync(cancellationToken);
        }, resetTrackerPerAttempt: true);

        return found ? await Get(id, cancellationToken) : null;
    }

    /// <summary>
    /// Looks up a contact by its <see cref="Contact.ExternalUid"/> (issue #338 §9) — backs
    /// vCard import's create-vs-update resolution (a vCard <c>UID</c> matching an existing row's
    /// <c>ExternalUid</c> means "update this row", otherwise "create new").
    /// </summary>
    public async Task<Guid?> FindIdByExternalUid(string externalUid, CancellationToken cancellationToken = default) =>
        await context.Contacts.AsNoTracking()
            .Where(c => c.ExternalUid == externalUid)
            .Select(c => (Guid?)c.ContactId)
            .FirstOrDefaultAsync(cancellationToken);

    // Generates a fresh urn:uuid ExternalUid when the caller didn't supply one; otherwise validates the
    // supplied value isn't already claimed by a *different* contact (issue #338 §6) — a 400 on the
    // direct API create/update path, a per-entry import skip when the caller is ContactVCardService.
    private async Task<string> ResolveExternalUid(string? requested, Guid? excludingContactId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return $"urn:uuid:{Guid.NewGuid()}";
        }

        var trimmed = requested.Trim();
        if (trimmed.Length > 255)
        {
            throw new DomainValidationException("External ID cannot exceed 255 characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new DomainValidationException("External ID cannot contain control characters.");
        }

        var owner = await context.Contacts.AsNoTracking()
            .Where(c => c.ExternalUid == trimmed)
            .Select(c => (Guid?)c.ContactId)
            .FirstOrDefaultAsync(cancellationToken);

        if (owner is { } existingOwnerId && existingOwnerId != excludingContactId)
        {
            throw new DomainValidationException("External ID is already in use by another contact.");
        }

        return trimmed;
    }

    /// <summary>
    /// Deletes a contact, applying the Finance-side on-delete behaviours first.
    ///
    /// <para>
    /// With <paramref name="detachBlockingLinks"/> the contact's <c>Beneficiary</c> contract-party
    /// rows are removed <b>in the same transaction</b> instead of blocking the delete: the supported
    /// release valve for an erasure request (issue #157 §5.4). Without it a contact named as a
    /// contract beneficiary is refused, which is the deliberate default: a beneficiary designation
    /// vanishing silently on contact deletion would lose it without trace.
    /// </para>
    /// </summary>
    /// <param name="permittedDetachClasses">
    /// Which classes of blocking link this caller has proved it may destroy (issue #157 §7.3).
    /// <see langword="null"/> means "no claim check", which is what a non-HTTP caller with no
    /// <c>ClaimsPrincipal</c> passes; the HTTP path always supplies a set. The check runs against the
    /// <b>one snapshot</b> read inside the transaction, never a re-query, so a link inserted between
    /// the determination and the destruction cannot be destroyed by a caller never asked to prove the
    /// claim for it.
    /// </param>
    /// <returns>What was detached, or null when nothing was (including when the contact did not exist).</returns>
    /// <exception cref="DomainForbiddenException">
    /// The detach was requested but <paramref name="permittedDetachClasses"/> does not cover a class
    /// present in the snapshot. Nothing is written — never a silent downgrade to the refused delete.
    /// </exception>
    public async Task<DetachedContactLinks?> Delete(
        Guid id, bool detachBlockingLinks = false,
        IReadOnlySet<ContactDeleteBlockerClass>? permittedDetachClasses = null,
        CancellationToken cancellationToken = default)
    {
        var contact = await context.Contacts
            .FirstOrDefaultAsync(value => value.ContactId == id, cancellationToken);
        if (contact is null)
        {
            return null;
        }

        // The finance references to a contact are real FKs again now that both halves share one context,
        // so the database would apply the SET NULL / CASCADE behaviours on its own. The
        // application-level guard below is kept in front of them for two reasons: it turns a constraint
        // violation into a 409 with an explanation rather than a raw FK error surfacing as a 500, and
        // it is the only implementation of those behaviours under the EF InMemory provider the fast test
        // tiers run on.
        //
        // There is no advisory lock any more (issue #27 §5): it was a mutex with nothing to contend
        // with, holding a pinned connection and a 10-second timeout per delete.
        DetachedContactLinks? detached = null;

        try
        {
            await ExecuteAtomicallyAsync(async () =>
            {
                await DeleteInTransactionAsync();
            });
        }
        catch (DbUpdateException exception) when (DbErrors.ReferencedRowFrom(exception, "Books") || DbErrors.ReferencedRowFrom(exception, "BookContributors"))
        {
            // A book credit added between the in-transaction check and the delete: a Books/BookContributors RESTRICT
            // key refused it (issue #527 §7.10). Caught OUTSIDE ExecuteAtomicallyAsync, so the transaction —
            // ClearAndCascadeReferencesAsync's relational statements included — has rolled back first.
            throw new DomainConflictException(BookCreditMessage);
        }

        return detached;

        async Task DeleteInTransactionAsync()
        {
            // Book credits are never detachable (a required main author has nothing to detach to), so they refuse
            // the delete on both paths, before anything is staged (issue #527 §7.10).
            if (bookCredits is not null && await bookCredits.HasCreditsAsync(id, cancellationToken))
            {
                throw new DomainConflictException(BookCreditMessage);
            }

            if (detachBlockingLinks)
            {
                // ONE snapshot, read inside this transaction, drives both decisions below (issue #157
                // §7.3). Reading it here rather than pre-flighting in the controller is the whole
                // point: the claims this valve demands are derived from which classes are present, so
                // two snapshots would let a row inserted between them be destroyed by a caller never
                // asked to prove the claim for it (CWE-367).
                var plan = await referenceGuard.ReadLinkDetachPlanAsync(id, cancellationToken);

                EnsureDetachPermitted(plan, permittedDetachClasses);

                // Staged onto this context, not saved: the detach and the delete must commit together,
                // or an interruption leaves the links gone and the contact still present. Tracked
                // RemoveRange, never ExecuteDelete — that throws on the InMemory provider.
                detached = referenceGuard.StageLinkDetach(plan);
            }
            else if (await referenceGuard.IsReferencedByRestrictedLinkAsync(id, cancellationToken))
            {
                // A contact named as a Beneficiary party on a contract blocks the delete. The
                // controller re-checks first and shapes a claim-conditional payload; this stays as
                // defence-in-depth for direct (non-HTTP) callers, and it is the ONLY enforcement,
                // since that FK stays CASCADE (issue #157 §5.5).
                throw new DomainConflictException(
                    "This contact is named as a beneficiary on one or more contracts and cannot be "
                    + "deleted. Detach those links, or remove it from those records first.");
            }

            // Clear/cascade the Finance-side references (SetNull + contract-party Cascade), then delete
            // the contact (its Person/Org/address/email/phone children and the journal/photo link rows
            // cascade in-context). All of it inside the transaction: widening the guard from one probe
            // to three widened the window this used to leave open, and the six ExecuteUpdate/
            // ExecuteDelete statements in the cleanup were the genuinely non-atomic part all along.
            await referenceGuard.ClearAndCascadeReferencesAsync(id, cancellationToken);

            // The contact's image dies with it (issue #86 §6). This direction is NOT expressible as a
            // foreign key — the FK runs the other way, Contact → FileMetadata with SET NULL — so it is
            // application code here, inside the same transaction, applying the shared release rule: a
            // file whose content type is not avatar-legal is detached rather than destroyed.
            await ContactAvatarRelease.StageAsync(context, objectStore, contact, logger, "contact-delete", cancellationToken);

            context.Contacts.Remove(contact);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The refusal when a contact is credited on any book (issue #527 §9 <c>ContactCreditedOnBooks</c>).</summary>
    public const string BookCreditMessage =
        "This contact is credited on one or more books and cannot be deleted. Remove it from those books first.";

    /// <summary>
    /// Refuses the detach when the caller has not proved the claim for a blocker class the snapshot
    /// actually contains (issue #157 §7.3). Per class present, never unconditionally: demanding
    /// <c>contracts.update</c> of a caller whose contact has no contract links would de-authorize a
    /// request that is legitimate today.
    /// </summary>
    /// <remarks>
    /// Raised as a domain error rather than decided in the controller because the snapshot it reads
    /// only exists inside the transaction. <see langword="null"/> is the non-HTTP caller, which has no
    /// claims to check — the same posture every other service-layer rule takes toward a direct caller.
    /// </remarks>
    private static void EnsureDetachPermitted(
        ContactLinkDetachPlan plan, IReadOnlySet<ContactDeleteBlockerClass>? permitted)
    {
        if (permitted is null)
        {
            return;
        }

        var missing = plan.Classes.Where(blocker => !permitted.Contains(blocker)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        // Names the capability, not the rows: what the caller has to go and obtain.
        var capabilities = missing.Select(blocker => blocker switch
        {
            ContactDeleteBlockerClass.ContractBeneficiary => "update contracts",
            _ => blocker.ToString(),
        });

        throw new DomainForbiddenException(
            $"Detaching this contact's links requires permission to {string.Join(" and ", capabilities)}.");
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction. Wrapped in the context's execution
    /// strategy because <c>AddDatabases</c> enables retry-on-failure, and a retrying strategy refuses an
    /// ambient transaction it did not open itself (a bare <c>BeginTransactionAsync</c> throws).
    /// Follows <c>UserAdministrationService.ExecuteAtomicallyAsync</c>.
    ///
    /// <para>
    /// The EF InMemory provider honours neither transactions nor the execution strategy, so it runs the
    /// work directly — which is correct rather than a compromise: there is nothing there to be atomic
    /// against, and <c>BeginTransactionAsync</c> would warn. The real coverage lives in
    /// <c>Odyssey.IntegrationTests</c>.
    /// </para>
    /// </summary>
    private async Task ExecuteAtomicallyAsync(Func<Task> work, bool resetTrackerPerAttempt = false)
    {
        if (!context.Database.IsRelational())
        {
            await work();
            return;
        }

        // Joins an ambient transaction rather than nesting a second BEGIN, which MariaDB does not support
        // and the execution strategy rejects. vCard import opens one around Update (issue #335 §3.2); its
        // owner owns the retry, so the tracker is not cleared here — that would discard the owner's work.
        if (context.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // The scoped context survives a retry: without a reset, the attempt's first read would return
            // the already-tracked, already-mutated entity instead of re-reading the row (issue #335 N1).
            // The reset also discards anything a caller staged on the context and did not save — no
            // caller does, and one that needs to must open the transaction itself so this joins it.
            if (resetTrackerPerAttempt)
            {
                context.ChangeTracker.Clear();
            }

            await using var transaction = await context.Database.BeginTransactionAsync();
            await work();
            await transaction.CommitAsync();
        });
    }

    // ── Base + details mapping ────────────────────────────────────────────────

    // Cross-field shape check (§9), mirroring NewContact.Validate for direct (non-HTTP) callers
    // that bypass [ApiController] model validation.
    private static void ValidateShape(NewContact source)
    {
        var personExpected = source.Type == ContactType.Person;
        if (personExpected && (source.PersonDetails is null || source.OrganizationDetails is not null))
        {
            throw new DomainValidationException("A Person contact requires person details and no organization details.");
        }

        if (!personExpected && (source.OrganizationDetails is null || source.PersonDetails is not null))
        {
            throw new DomainValidationException("An Organization contact requires organization details and no person details.");
        }
    }

    private void ApplyBaseAndDetails(Contact contact, NewContact source)
    {
        contact.DisplayName = CleanOptional(source.DisplayName, 128, nameof(source.DisplayName));
        contact.Notes = CleanOptional(source.Notes, 1024, nameof(source.Notes));

        if (source.Type == ContactType.Person)
        {
            // Drop an organization sub-record left over from a type change.
            if (contact.OrganizationDetails is not null)
            {
                context.Remove(contact.OrganizationDetails);
                contact.OrganizationDetails = null;
            }

            var details = source.PersonDetails!;
            var (dob, dod) = ValidatePersonDates(details.DateOfBirth, details.DateOfDeath);
            contact.PersonDetails ??= new PersonDetails { FirstName = string.Empty, LastName = string.Empty };
            contact.PersonDetails.FirstName = CleanRequired(details.FirstName, 128, "First name");
            contact.PersonDetails.LastName = CleanRequired(details.LastName, 128, "Last name");
            contact.PersonDetails.MiddleName = CleanOptional(details.MiddleName, 128, "Middle name");
            contact.PersonDetails.DateOfBirth = dob;
            contact.PersonDetails.DateOfDeath = dod;
            contact.PersonDetails.Sex = details.Sex;
            contact.PersonDetails.Title = CleanOptional(details.Title, 128, "Title");
            contact.PersonDetails.Company = CleanOptional(details.Company, 256, "Company");
        }
        else
        {
            if (contact.PersonDetails is not null)
            {
                context.Remove(contact.PersonDetails);
                contact.PersonDetails = null;
            }

            var details = source.OrganizationDetails!;
            var (established, dissolved) = ValidateOrganizationDates(details.EstablishedDate, details.DissolvedDate);
            contact.OrganizationDetails ??= new OrganizationDetails { LegalName = string.Empty };
            contact.OrganizationDetails.LegalName = CleanRequired(details.LegalName, 256, "Legal name");
            contact.OrganizationDetails.OrganizationNumber = CleanOptional(details.OrganizationNumber, 64, "Organization number");
            contact.OrganizationDetails.Website = ValidateWebsite(details.Website);
            contact.OrganizationDetails.EstablishedDate = established;
            contact.OrganizationDetails.DissolvedDate = dissolved;
        }

        contact.NormalizedName = ContactNaming.Normalize(ContactNaming.Resolve(contact));
    }

    /// <summary>
    /// The birth/death pair (issue #48 §9). Checked <b>from both sides</b>: editing a date of birth
    /// past an existing date of death is rejected on <c>dateOfBirth</c>, and the reverse on
    /// <c>dateOfDeath</c>, so the invariant cannot be broken by touching either field. That is also
    /// why the client's birth-date control is an <c>OdsDateField</c> rather than a bare picker — it
    /// needs an error channel of its own.
    /// </summary>
    private (DateOnly? DateOfBirth, DateOnly? DateOfDeath) ValidatePersonDates(DateTime? dateOfBirth, DateTime? dateOfDeath)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        DateOnly? birth = dateOfBirth is { } b ? DateOnly.FromDateTime(b) : null;
        DateOnly? death = dateOfDeath is { } d ? DateOnly.FromDateTime(d) : null;

        if (birth > today)
        {
            throw new DomainValidationException("Date of birth cannot be in the future.", code: null, field: "dateOfBirth");
        }

        if (death > today)
        {
            throw new DomainValidationException("Date of death cannot be in the future.", code: null, field: "dateOfDeath");
        }

        // The pair error is attributed to whichever field the caller is most likely to have just
        // changed. A write carries both, so there is no way to tell here — and the death date is the
        // later, more specific one, so it is where the message renders.
        if (birth is { } bv && death is { } dv && dv < bv)
        {
            throw new DomainValidationException("Date of death cannot be before the date of birth.", code: null, field: "dateOfDeath");
        }

        return (birth, death);
    }

    /// <summary>
    /// The establishment/dissolution pair (issue #48 §9). Either may be set without the other — the
    /// founding date of an old institution is frequently unknown — but neither may be in the future
    /// and a dissolution cannot precede an establishment.
    /// </summary>
    private (DateOnly? Established, DateOnly? Dissolved) ValidateOrganizationDates(DateTime? established, DateTime? dissolved)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        DateOnly? from = established is { } e ? DateOnly.FromDateTime(e) : null;
        DateOnly? to = dissolved is { } d ? DateOnly.FromDateTime(d) : null;

        if (from > today)
        {
            throw new DomainValidationException("Established date cannot be in the future.", code: null, field: "establishedDate");
        }

        if (to > today)
        {
            throw new DomainValidationException("Dissolved date cannot be in the future.", code: null, field: "dissolvedDate");
        }

        if (from is { } fv && to is { } tv && tv < fv)
        {
            throw new DomainValidationException("Dissolved date cannot be before the established date.", code: null, field: "dissolvedDate");
        }

        return (from, to);
    }

    private static string? ValidateWebsite(string? website)
    {
        var cleaned = CleanOptional(website, 2048, "Website");
        if (cleaned is null)
        {
            return null;
        }

        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new DomainValidationException("Website must be an absolute http:// or https:// URL.");
        }

        return cleaned;
    }

    private static string CleanRequired(string value, int maxLength, string label)
    {
        var cleaned = MultiWhitespaceRegex.Replace((value ?? string.Empty).Trim(), " ");
        if (cleaned.Length is < 1)
        {
            throw new DomainValidationException($"{label} is required.");
        }

        if (cleaned.Length > maxLength)
        {
            throw new DomainValidationException($"{label} cannot exceed {maxLength} characters.");
        }

        if (cleaned.Any(char.IsControl))
        {
            throw new DomainValidationException($"{label} cannot contain control characters.");
        }

        return cleaned;
    }

    private static string? CleanOptional(string? value, int maxLength, string label)
    {
        if (value is null)
        {
            return null;
        }

        var cleaned = value.Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (cleaned.Length > maxLength)
        {
            throw new DomainValidationException($"{label} cannot exceed {maxLength} characters.");
        }

        if (cleaned.Any(char.IsControl))
        {
            throw new DomainValidationException($"{label} cannot contain control characters.");
        }

        return cleaned;
    }

    private void ApplyArchiveTransition(Contact contact, bool requestedArchived)
    {
        var currentArchived = contact.Archived is not null;
        if (!currentArchived && requestedArchived)
        {
            contact.Archived = timeProvider.GetUtcNow().UtcDateTime;
        }
        else if (currentArchived && !requestedArchived)
        {
            contact.Archived = null;
        }
    }
}
