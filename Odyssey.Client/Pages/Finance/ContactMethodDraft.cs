using Odyssey.Client.Components;
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
                // The fields are the address block's (OdsAddressBlock / AddressBlockModel.Validate): a picked
                // place needs none of them.
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

    /// <summary>
    /// The address body for what the address block decided (issue #383 §5.1): a place by id, or the fields —
    /// with <c>applyTo</c> and <c>expectedOtherUses</c> when a stored address was changed.
    /// </summary>
    public NewAddress ToNewAddress(AddressWrite write) => new()
    {
        Label = Enum.TryParse<AddressLabel>(Label, out var l) ? l : AddressLabel.Other,
        IsPrimary = IsPrimary,
        LocationId = write.LocationId,
        Line1 = write.Fields?.Line1,
        Line2 = write.Fields?.Line2,
        City = write.Fields?.City,
        PostalCode = write.Fields?.PostalCode,
        Region = write.Fields?.Region,
        CountryCode = write.Fields?.CountryCode,
        ApplyTo = write.ApplyTo,
        ExpectedOtherUses = write.ExpectedOtherUses,
    };

    /// <summary>
    /// "Set as primary": the stored place re-sent BY ID and nothing else. Re-sending the fields would take
    /// the by-fields path, which needs <c>locations.create</c> (#380 §5.3).
    /// </summary>
    public static NewAddress PrimaryAddressBody(ExistingAddress a) => new()
    {
        Label = a.Label,
        IsPrimary = true,
        LocationId = a.LocationId,
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
        Stored = a,
    };

    /// <summary>The address being edited, or null for a new one (issue #383).</summary>
    public ExistingAddress? Stored { get; set; }

    public static ContactMethodDraft FromEmail(ExistingEmailAddress e) => new()
    {
        Id = e.Id, Label = e.Label.ToString(), IsPrimary = e.IsPrimary, Value = e.Value,
    };

    public static ContactMethodDraft FromPhone(ExistingPhoneNumber p) => new()
    {
        Id = p.Id, Label = p.Label.ToString(), IsPrimary = p.IsPrimary, Value = p.Value,
    };
}
