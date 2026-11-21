using System.Data;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Odyssey.Core.Journal;

public partial class ContactService
{
    // ── Type conversion (issue #335) ──────────────────────────────────────────

    /// <summary>
    /// The read-only dry run of a conversion: the derived target details, what will be lost or carried
    /// over, the label remap and the blockers. Writes nothing. <see langword="null"/> for an unknown id.
    /// </summary>
    /// <exception cref="DomainUnprocessableException">The target equals the current type.</exception>
    public async Task<ContactConversionPreview?> PreviewConversionAsync(
        Guid id, ContactType targetType, bool carryLifecycleDates, CancellationToken cancellationToken = default)
    {
        var contact = await context.Contacts
            .AsNoTracking()
            .Include(c => c.PersonDetails)
            .Include(c => c.OrganizationDetails)
            .Include(c => c.Addresses)
            .Include(c => c.EmailAddresses)
            .Include(c => c.PhoneNumbers)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.ContactId == id, cancellationToken);
        if (contact is null)
        {
            return null;
        }

        RequireDifferentType(contact.Type, targetType);

        var derivation = ContactConversionMapper.Derive(contact, targetType, carryLifecycleDates, Today());
        var blockers = await ReadTypeChangeBlockersAsync(id, contact.Type, targetType, cancellationToken);

        return new ContactConversionPreview
        {
            CurrentType = contact.Type,
            TargetType = targetType,
            UpdatedAt = contact.UpdatedAt,
            CanConvert = !blockers.HasAny() && derivation.Issues.Count == 0,
            PersonDetails = derivation.PersonDetails,
            OrganizationDetails = derivation.OrganizationDetails,
            LostFields = derivation.Lost,
            ConvertedFields = derivation.Converted,
            DerivationIssues = derivation.Issues,
            Notes = derivation.Notes,
            RemappedLabels = new ContactRemappedLabels
            {
                Addresses = contact.Addresses.Count(a => ContactLabelScope.Clamp(a.Label, targetType) != a.Label),
                Emails = contact.EmailAddresses.Count(e => ContactLabelScope.Clamp(e.Label, targetType) != e.Label),
                Phones = contact.PhoneNumbers.Count(p => ContactLabelScope.Clamp(p.Label, targetType) != p.Label),
            },
            Blockers = blockers,
        };
    }

    /// <summary>
    /// Converts a contact to <see cref="ContactConversionRequest.TargetType"/> in one transaction, deriving
    /// the target details server-side (§8). Its id and every link that is not type-restricted are kept.
    /// <see langword="null"/> for an unknown id.
    /// </summary>
    /// <exception cref="ContactConversionStaleException">The contact changed since <c>expectedUpdatedAt</c>.</exception>
    /// <exception cref="ContactTypeChangeBlockedException">A type-restricted link exists.</exception>
    /// <exception cref="DomainUnprocessableException">Same type, or a derivation issue no override resolves.</exception>
    /// <exception cref="DomainValidationException">An override fails the create path's sanitiser.</exception>
    public async Task<ContactConversionResult?> ConvertAsync(
        Guid id, ContactConversionRequest request, CancellationToken cancellationToken = default)
    {
        var targetType = request.TargetType
            ?? throw new DomainValidationException("A target type is required.", code: null, field: "targetType");
        var expectedUpdatedAt = request.ExpectedUpdatedAt
            ?? throw new DomainValidationException("The expected update time is required.", code: null, field: "expectedUpdatedAt");

        // Sanitised exactly as on create (trim, collapse whitespace, length, no control characters), but
        // keyed on the field so the dialog can mark it: an override the caller typed is a 400.
        var firstName = CleanOverride(request.FirstName, 128, ContactConversionFields.FirstName, "First name");
        var lastName = CleanOverride(request.LastName, 128, ContactConversionFields.LastName, "Last name");
        var legalName = CleanOverride(request.LegalName, 256, ContactConversionFields.LegalName, "Legal name");

        (ContactType PreviousType, int Lost, int Converted)? outcome = null;

        await ExecuteAtomicallyAsync(async () =>
        {
            outcome = null;
            var contact = await LoadForTypeChange(id, cancellationToken);
            if (contact is null)
            {
                return;
            }

            // The conversion is irreversible and the user decided from a preview, so the record must be
            // the one they saw. Compared exactly: the value is always one this server returned.
            if (contact.UpdatedAt != expectedUpdatedAt)
            {
                throw new ContactConversionStaleException(
                    "The contact changed since it was loaded. Review the conversion again.");
            }

            var previousType = contact.Type;
            RequireDifferentType(previousType, targetType);

            var derivation = ContactConversionMapper.Derive(
                contact, targetType, request.CarryLifecycleDates, Today(), firstName, lastName, legalName);
            if (derivation.Issues.Count > 0)
            {
                var issue = derivation.Issues[0];
                throw new DomainUnprocessableException(issue.Message, issue.Field);
            }

            await EnsureTypeChangeAllowedAsync(id, previousType, targetType, cancellationToken);

            // Applied through the same path a type-changing PUT takes, so ValidateShape, the sanitisers
            // and the date validators all run on the derived details.
            var target = new NewContact
            {
                Type = targetType,
                DisplayName = contact.DisplayName,
                Notes = contact.Notes,
                Archived = contact.Archived is not null,
                PersonDetails = derivation.PersonDetails,
                OrganizationDetails = derivation.OrganizationDetails,
            };
            ValidateShape(target);

            contact.Type = targetType;
            ApplyBaseAndDetails(contact, target);
            contact.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await RemapChildLabelsAsync(contact, previousType, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            outcome = (previousType, derivation.Lost.Count, derivation.Converted.Count);
        }, resetTrackerPerAttempt: true);

        if (outcome is not { } done)
        {
            return null;
        }

        // PII-free: ids, types and counts only (issue #335 §7.5).
        logger?.LogInformation(
            "Contact {ContactId} converted {PreviousType} -> {NewType}: {LostFieldCount} field(s) dropped, "
            + "{ConvertedFieldCount} carried over.",
            id, done.PreviousType, targetType, done.Lost, done.Converted);

        return new ContactConversionResult((await Get(id, cancellationToken))!, done.PreviousType);
    }

    /// <summary>
    /// Counts the two type-restricted links a change from <paramref name="from"/> to <paramref name="to"/>
    /// would orphan (issue #335 §8.3), with up to <see cref="ContactConversionLimits.MaxBlockerItems"/>
    /// ids each, ordered by id. Archived photos and properties count: they are still stored and
    /// restorable, and restoring one would bring the invalid link back.
    /// </summary>
    private async Task<ContactTypeChangeBlockers> ReadTypeChangeBlockersAsync(
        Guid id, ContactType from, ContactType to, CancellationToken cancellationToken)
    {
        var blockers = new ContactTypeChangeBlockers();
        if (from == to)
        {
            return blockers;
        }

        if (to == ContactType.Organization)
        {
            var photos = context.PhotoPeople.Where(pp => pp.ContactId == id);
            var count = await photos.CountAsync(cancellationToken);
            if (count > 0)
            {
                blockers.PhotoPersonTags = new ContactPhotoTagBlocker
                {
                    Count = count,
                    PhotoIds = await photos.Select(pp => pp.PhotoId).Distinct().OrderBy(photoId => photoId)
                        .Take(ContactConversionLimits.MaxBlockerItems).ToListAsync(cancellationToken),
                    Truncated = count > ContactConversionLimits.MaxBlockerItems,
                };
            }
        }
        else
        {
            var properties =
                from details in context.RealEstateDetails
                where details.HomeownerAssociationId == id
                join property in context.Properties on details.PropertyId equals property.PropertyId
                select property;
            var count = await properties.CountAsync(cancellationToken);
            if (count > 0)
            {
                blockers.HomeownerAssociations = new ContactHomeownerAssociationBlocker
                {
                    Count = count,
                    Properties = await properties
                        .OrderBy(property => property.PropertyId)
                        .Take(ContactConversionLimits.MaxBlockerItems)
                        .Select(property => new ContactBlockingProperty { Id = property.PropertyId, Name = property.Name })
                        .ToListAsync(cancellationToken),
                    Truncated = count > ContactConversionLimits.MaxBlockerItems,
                };
            }
        }

        return blockers;
    }

    /// <summary>
    /// Refuses a type change while a type-restricted link exists. Every type-change path — the
    /// conversion endpoint, a type-changing PUT and vCard import's update-by-UID — reaches it, inside its
    /// own transaction. The message names the blocker class only (§7.2).
    /// </summary>
    private async Task EnsureTypeChangeAllowedAsync(
        Guid id, ContactType from, ContactType to, CancellationToken cancellationToken)
    {
        var blockers = await ReadTypeChangeBlockersAsync(id, from, to, cancellationToken);
        if (!blockers.HasAny())
        {
            return;
        }

        var message = blockers.PhotoPersonTags.Count > 0
            ? "This contact is tagged as a person on one or more photos and cannot be converted to an "
              + "organization. Remove those tags first."
            : "This contact is the homeowner association of one or more properties and cannot be converted "
              + "to a person. Change the association on those properties first.";
        throw new ContactTypeChangeBlockedException(message, blockers);
    }

    private static void RequireDifferentType(ContactType current, ContactType target)
    {
        if (current == target)
        {
            throw new DomainUnprocessableException(
                $"The contact is already {(target == ContactType.Organization ? "an organization" : "a person")}.",
                "targetType");
        }
    }

    private static string? CleanOverride(string? value, int maxLength, string field, string label)
    {
        if (value is null)
        {
            return null;
        }

        // Checked BEFORE CleanRequired, which collapses CR/LF/tab into a space: an override is a single
        // name the caller typed, so a line break in it is refused rather than silently reshaped (§8.4).
        if (value.Any(char.IsControl))
        {
            throw new DomainValidationException($"{label} cannot contain control characters.", code: null, field: field);
        }

        try
        {
            return CleanRequired(value, maxLength, label);
        }
        catch (DomainValidationException ex)
        {
            throw new DomainValidationException(ex.Message, code: null, field: field);
        }
    }

    private Task<Contact?> LoadForTypeChange(Guid id, CancellationToken cancellationToken) =>
        context.Contacts
            .Include(c => c.PersonDetails)
            .Include(c => c.OrganizationDetails)
            .FirstOrDefaultAsync(value => value.ContactId == id, cancellationToken);

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
}
