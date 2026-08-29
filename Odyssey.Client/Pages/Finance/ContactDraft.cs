using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Mutable form model backing the contact create/edit surfaces (issue #325). Holds both the
/// Person and Organization field sets; only the set matching <see cref="Type"/> is submitted. Switching
/// <see cref="Type"/> in the create dialog discards the previously-entered set (§3).
/// </summary>
public sealed class ContactDraft
{
    public ContactType Type { get; set; } = ContactType.Person;
    public string DisplayName { get; set; } = string.Empty;

    // Base — carried through the full-replace PUT even though the DS form doesn't surface them,
    // so an inline edit never silently wipes a persisted value (Notes) or the relationship.
    public string Notes { get; set; } = string.Empty;
    public RelationshipType? RelationshipType { get; set; }

    // Person
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public string Sex { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;

    // Organization
    public string LegalName { get; set; } = string.Empty;
    public string OrganizationNumber { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;

    /// <summary>Client-side field validation mirroring the server rules (§9). Empty ⇒ valid.</summary>
    public Dictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>();
        if (Type == ContactType.Person)
        {
            if (string.IsNullOrWhiteSpace(FirstName)) errors["firstName"] = "First name is required.";
            if (string.IsNullOrWhiteSpace(LastName)) errors["lastName"] = "Last name is required.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(LegalName)) errors["legalName"] = "Legal name is required for an organization.";
            if (!string.IsNullOrWhiteSpace(Website)
                && !Website.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !Website.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                errors["website"] = "Must start with http:// or https://";
        }
        return errors;
    }

    public NewContact ToNew(bool archived)
    {
        var displayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim();
        var newContact = new NewContact
        {
            Type = Type,
            DisplayName = displayName,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            Archived = archived,
        };

        if (Type == ContactType.Person)
        {
            newContact.PersonDetails = new PersonDetailsDto
            {
                FirstName = FirstName.Trim(),
                LastName = LastName.Trim(),
                DateOfBirth = DateOfBirth,
                RelationshipType = RelationshipType,
                Sex = Enum.TryParse<Sex>(Sex, out var sex) ? sex : null,
                Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
                Company = string.IsNullOrWhiteSpace(Company) ? null : Company.Trim(),
            };
        }
        else
        {
            newContact.OrganizationDetails = new OrganizationDetailsDto
            {
                LegalName = LegalName.Trim(),
                OrganizationNumber = string.IsNullOrWhiteSpace(OrganizationNumber) ? null : OrganizationNumber.Trim(),
                Website = string.IsNullOrWhiteSpace(Website) ? null : Website.Trim(),
            };
        }

        return newContact;
    }

    public static ContactDraft From(ExistingContact contact)
    {
        var draft = new ContactDraft
        {
            Type = contact.Type,
            DisplayName = contact.DisplayName ?? string.Empty,
            Notes = contact.Notes ?? string.Empty,
        };

        if (contact.PersonDetails is { } person)
        {
            draft.FirstName = person.FirstName;
            draft.LastName = person.LastName;
            draft.DateOfBirth = person.DateOfBirth;
            draft.RelationshipType = person.RelationshipType;
            draft.Sex = person.Sex?.ToString() ?? string.Empty;
            draft.Title = person.Title ?? string.Empty;
            draft.Company = person.Company ?? string.Empty;
        }

        if (contact.OrganizationDetails is { } org)
        {
            draft.LegalName = org.LegalName;
            draft.OrganizationNumber = org.OrganizationNumber ?? string.Empty;
            draft.Website = org.Website ?? string.Empty;
        }

        return draft;
    }
}
