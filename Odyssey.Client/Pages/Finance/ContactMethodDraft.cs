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
    public string Label { get; set; } = "Home";
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

    public Dictionary<string, string> Validate(string kind)
    {
        var errors = new Dictionary<string, string>();
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
