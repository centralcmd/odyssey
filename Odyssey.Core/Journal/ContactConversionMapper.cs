using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Fields = Odyssey.Dtos.Journal.ContactConversionFields;

namespace Odyssey.Core.Journal;

/// <summary>
/// The field mapping of a Person ⇄ Organization conversion (issue #335 §8). A pure function of the
/// stored contact, so the preview and the conversion derive from one rule set and cannot disagree.
/// </summary>
public static class ContactConversionMapper
{
    private const int MaxNamePart = 128;
    private const int MaxLegalName = 256;

    /// <summary>
    /// Derives the target details. <paramref name="contact"/> must have its current type's details row
    /// loaded. Overrides are applied as given; the caller sanitises them first.
    /// </summary>
    public static ContactConversionDerivation Derive(
        Contact contact, ContactType targetType, bool carryLifecycleDates, DateOnly today,
        string? firstNameOverride = null, string? lastNameOverride = null, string? legalNameOverride = null)
    {
        return targetType == ContactType.Organization
            ? ToOrganization(contact, carryLifecycleDates, today, legalNameOverride)
            : ToPerson(contact, carryLifecycleDates, today, firstNameOverride, lastNameOverride);
    }

    private static ContactConversionDerivation ToOrganization(
        Contact contact, bool carryDates, DateOnly today, string? legalNameOverride)
    {
        var p = contact.PersonDetails
            ?? throw new InvalidOperationException("A Person contact must have its person details loaded.");
        var result = new ContactConversionDerivation();

        var parts = new[] { p.FirstName, p.MiddleName, p.LastName }.Where(HasValue).Select(v => v!.Trim());
        var fromParts = string.Join(' ', parts);
        var displayName = contact.DisplayName?.Trim();

        // A display name is the user's own statement of how the record reads, and for a mis-typed
        // organization it usually already IS the organization's name, so it wins over the parts (§8.1).
        string legalName;
        var partsUsed = false;
        if (legalNameOverride is not null)
        {
            legalName = legalNameOverride;
        }
        else if (!string.IsNullOrEmpty(displayName))
        {
            legalName = displayName;
        }
        else
        {
            legalName = fromParts;
            partsUsed = true;
        }

        if (legalNameOverride is null && legalName.Length > MaxLegalName)
        {
            result.Issues.Add(Issue(Fields.LegalName,
                $"The derived legal name is longer than {MaxLegalName} characters. Enter a shorter one."));
        }

        foreach (var (field, value) in new[] { (Fields.FirstName, p.FirstName), (Fields.MiddleName, p.MiddleName), (Fields.LastName, p.LastName) })
        {
            if (!HasValue(value))
                continue;
            if (partsUsed)
                result.Converted.Add(new ContactConvertedField { From = field, To = Fields.LegalName });
            else
                result.Lost.Add(field);
        }

        var (established, dissolved) = CarryDates(
            result, carryDates, today,
            (Fields.DateOfBirth, p.DateOfBirth, Fields.EstablishedDate),
            (Fields.DateOfDeath, p.DateOfDeath, Fields.DissolvedDate));

        AddLostIfSet(result, (Fields.Sex, p.Sex?.ToString()), (Fields.Title, p.Title), (Fields.Company, p.Company));

        result.OrganizationDetails = new OrganizationDetailsDto
        {
            LegalName = legalName,
            OrganizationNumber = null,
            Website = null,
            EstablishedDate = ToDateTime(established),
            DissolvedDate = ToDateTime(dissolved),
        };
        return result;
    }

    private static ContactConversionDerivation ToPerson(
        Contact contact, bool carryDates, DateOnly today, string? firstNameOverride, string? lastNameOverride)
    {
        var o = contact.OrganizationDetails
            ?? throw new InvalidOperationException("An Organization contact must have its organization details loaded.");
        var result = new ContactConversionDerivation();

        // lastName = the last whitespace-separated token, firstName = everything before it (§8.2).
        var tokens = (o.LegalName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var derivedLast = tokens.Length > 1 ? tokens[^1] : string.Empty;
        var derivedFirst = tokens.Length > 1 ? string.Join(' ', tokens[..^1]) : string.Join(' ', tokens);

        var firstName = firstNameOverride ?? derivedFirst;
        var lastName = lastNameOverride ?? derivedLast;

        if (lastNameOverride is null && tokens.Length < 2)
        {
            result.Issues.Add(Issue(Fields.LastName,
                "The legal name is a single word, so there is no last name to take from it. Enter one."));
        }

        if (firstNameOverride is null && firstName.Length > MaxNamePart)
        {
            result.Issues.Add(Issue(Fields.FirstName,
                $"The derived first name is longer than {MaxNamePart} characters. Enter a shorter one."));
        }

        if (firstNameOverride is null && firstName.Length == 0)
        {
            result.Issues.Add(Issue(Fields.FirstName, "There is no first name to take from the legal name. Enter one."));
        }

        if (lastNameOverride is null && lastName.Length > MaxNamePart)
        {
            result.Issues.Add(Issue(Fields.LastName,
                $"The derived last name is longer than {MaxNamePart} characters. Enter a shorter one."));
        }

        if (tokens.Length > 2 && (firstNameOverride is null || lastNameOverride is null))
        {
            result.Notes.Add(new ContactConversionNote
            {
                Code = ContactConversionCodes.NameSplitLowConfidence,
                Field = Fields.FirstName,
            });
        }

        if (HasValue(o.LegalName))
        {
            // Both parts overridden means nothing of the legal name survives.
            if (firstNameOverride is not null && lastNameOverride is not null)
            {
                result.Lost.Add(Fields.LegalName);
            }
            else
            {
                if (firstNameOverride is null)
                    result.Converted.Add(new ContactConvertedField { From = Fields.LegalName, To = Fields.FirstName });
                if (lastNameOverride is null && tokens.Length > 1)
                    result.Converted.Add(new ContactConvertedField { From = Fields.LegalName, To = Fields.LastName });
            }
        }

        var (birth, death) = CarryDates(
            result, carryDates, today,
            (Fields.EstablishedDate, o.EstablishedDate, Fields.DateOfBirth),
            (Fields.DissolvedDate, o.DissolvedDate, Fields.DateOfDeath));

        AddLostIfSet(result, (Fields.OrganizationNumber, o.OrganizationNumber), (Fields.Website, o.Website));

        result.PersonDetails = new PersonDetailsDto
        {
            FirstName = firstName,
            LastName = lastName,
            MiddleName = null,
            DateOfBirth = ToDateTime(birth),
            DateOfDeath = ToDateTime(death),
            Sex = null,
            Title = null,
            Company = null,
        };
        return result;
    }

    /// <summary>
    /// Carries the lifecycle pair, or drops it into <see cref="ContactConversionDerivation.Lost"/>. The two
    /// pairs share their rules (not in the future, start ≤ end), so a carried pair always passes the target's
    /// validators; a stored legacy row that somehow does not is a derivation issue on the TARGET field —
    /// a <c>422</c>, never a <c>400</c>, since the caller did not send the value (§8.4).
    /// </summary>
    private static (DateOnly? Start, DateOnly? End) CarryDates(
        ContactConversionDerivation result, bool carry, DateOnly today,
        (string From, DateOnly? Value, string To) start,
        (string From, DateOnly? Value, string To) end)
    {
        foreach (var (from, value, to) in new[] { start, end })
        {
            if (value is null)
                continue;
            if (carry)
                result.Converted.Add(new ContactConvertedField { From = from, To = to });
            else
                result.Lost.Add(from);
        }

        if (!carry)
            return (null, null);

        foreach (var (_, value, to) in new[] { start, end })
        {
            if (value > today)
                result.Issues.Add(Issue(to, "This stored date is in the future, so it cannot be carried over. Convert without the dates."));
        }

        if (start.Value is { } s && end.Value is { } e && e < s)
            result.Issues.Add(Issue(end.To, "The stored dates are out of order, so they cannot be carried over. Convert without the dates."));

        return (start.Value, end.Value);
    }

    private static void AddLostIfSet(ContactConversionDerivation result, params (string Field, string? Value)[] fields)
    {
        foreach (var (field, value) in fields)
        {
            if (HasValue(value))
                result.Lost.Add(field);
        }
    }

    private static ContactConversionIssue Issue(string field, string message) => new() { Field = field, Message = message };

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static DateTime? ToDateTime(DateOnly? value) => value?.ToDateTime(TimeOnly.MinValue);
}

/// <summary>The result of <see cref="ContactConversionMapper.Derive"/>.</summary>
public sealed class ContactConversionDerivation
{
    public PersonDetailsDto? PersonDetails { get; set; }

    public OrganizationDetailsDto? OrganizationDetails { get; set; }

    public List<string> Lost { get; } = [];

    public List<ContactConvertedField> Converted { get; } = [];

    public List<ContactConversionIssue> Issues { get; } = [];

    public List<ContactConversionNote> Notes { get; } = [];
}
