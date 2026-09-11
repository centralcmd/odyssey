using Odyssey.Dtos;
using System.Text.RegularExpressions;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Mutable form model for a single contact contact record (address / email / phone, issue #325).
/// One shape covers all three kinds; only the relevant fields are used per <c>kind</c>.
/// </summary>
public sealed class ContactMethodDraft
{
    public Guid? Id { get; set; }

    /// <summary>
    /// The selected label's enum member name. <b>No default</b> (issue #47 §3): the valid set depends
    /// on the parent contact's type, which this shape cannot see, and a hard-coded <c>"Home"</c> would
    /// open "Add phone number" on an organization with a label the server rejects. The host applies
    /// <c>ContactLabelScope.Default…Label(contact.Type)</c> when it opens a new method; a call site
    /// that forgets fails loudly and client-side under <see cref="Validate"/>'s set-membership rule.
    /// </summary>
    public string Label { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }

    // email / phone
    public string Value { get; set; } = string.Empty;

    // address
    public string Line1 { get; set; } = string.Empty;
    public string Line2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;

    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(@"^[+\d][\d\s()\-]{5,}$", RegexOptions.Compiled);
    private static readonly Regex CountryRegex = new("^[A-Za-z]{2}$", RegexOptions.Compiled);

    /// <summary>
    /// Client-side pre-check. The label rule is <b>set membership</b>, not non-emptiness (issue #47
    /// §3): <c>OdsTypeSelect</c> renders its placeholder for a value absent from the offered list
    /// while the bound <c>Label</c> still holds the old string, so a merely-non-empty rule would let a
    /// stale <c>"Home"</c> through to a server 422 on a control that looked empty. Required-style
    /// wording is deliberate for the same reason — the stale value is invisible, so naming it would
    /// point at nothing on screen.
    /// </summary>
    public Dictionary<string, string> Validate(string kind, ContactType contactType)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!IsLabelOffered(kind, contactType))
            errors["label"] = "Label is required.";

        switch (kind)
        {
            case "address":
                if (string.IsNullOrWhiteSpace(Line1)) errors["line1"] = "Line 1 is required.";
                if (string.IsNullOrWhiteSpace(City)) errors["city"] = "City is required.";
                if (!CountryRegex.IsMatch(CountryCode.Trim())) errors["countryCode"] = "Two-letter country code.";
                break;
            case "email":
                if (!EmailRegex.IsMatch(Value.Trim())) errors["value"] = "Enter a valid email address.";
                break;
            default:
                if (!PhoneRegex.IsMatch(Value.Trim())) errors["value"] = "Enter a valid phone number.";
                break;
        }
        return errors;
    }

    private bool IsLabelOffered(string kind, ContactType contactType) => kind switch
    {
        "email" => Enum.TryParse<EmailLabel>(Label, out var e) && ContactLabelScope.IsValidFor(e, contactType),
        "phone" => Enum.TryParse<PhoneLabel>(Label, out var p) && ContactLabelScope.IsValidFor(p, contactType),
        _ => Enum.TryParse<AddressLabel>(Label, out var a) && ContactLabelScope.IsValidFor(a, contactType),
    };

    public NewAddress ToNewAddress() => new()
    {
        Label = Enum.TryParse<AddressLabel>(Label, out var l) ? l : AddressLabel.Other,
        IsPrimary = IsPrimary,
        Line1 = Line1.Trim(),
        Line2 = string.IsNullOrWhiteSpace(Line2) ? null : Line2.Trim(),
        City = City.Trim(),
        PostalCode = string.IsNullOrWhiteSpace(PostalCode) ? null : PostalCode.Trim(),
        Region = string.IsNullOrWhiteSpace(Region) ? null : Region.Trim(),
        CountryCode = CountryCode.Trim().ToUpperInvariant(),
    };

    public NewEmailAddress ToNewEmail() => new()
    {
        Label = Enum.TryParse<EmailLabel>(Label, out var l) ? l : EmailLabel.Other,
        IsPrimary = IsPrimary,
        Value = Value.Trim(),
    };

    public NewPhoneNumber ToNewPhone() => new()
    {
        Label = Enum.TryParse<PhoneLabel>(Label, out var l) ? l : PhoneLabel.Other,
        IsPrimary = IsPrimary,
        Value = Value.Trim(),
    };

    public static ContactMethodDraft FromAddress(ExistingAddress a) => new()
    {
        Id = a.Id, Label = a.Label.ToString(), IsPrimary = a.IsPrimary,
        Line1 = a.Line1, Line2 = a.Line2 ?? string.Empty, City = a.City,
        PostalCode = a.PostalCode ?? string.Empty, Region = a.Region ?? string.Empty, CountryCode = a.CountryCode,
    };

    public static ContactMethodDraft FromEmail(ExistingEmailAddress e) => new()
    {
        Id = e.Id, Label = e.Label.ToString(), IsPrimary = e.IsPrimary, Value = e.Value,
    };

    public static ContactMethodDraft FromPhone(ExistingPhoneNumber p) => new()
    {
        Id = p.Id, Label = p.Label.ToString(), IsPrimary = p.IsPrimary, Value = p.Value,
    };
}
