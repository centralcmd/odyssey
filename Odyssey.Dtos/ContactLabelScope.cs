namespace Odyssey.Dtos;

/// <summary>
/// Which contact-method labels are valid for which <see cref="ContactType"/> (issue #47 §6) — the one
/// place the per-type scope of <see cref="AddressLabel"/>, <see cref="EmailLabel"/> and
/// <see cref="PhoneLabel"/> is declared.
///
/// <para>
/// The server validates against it, the client <i>filters and validates</i> with it. A client-side
/// <i>copy</i> of a server rule is the defect CLAUDE.md forbids for caps, and this project has zero
/// project references precisely so both halves of the stack — the WASM client included — can name one
/// symbol.
/// </para>
///
/// <para><b>Each list is in DISPLAY order</b>, and the per-type default is always its first member, so
/// display order and default cannot drift apart. <c>Other</c> is valid for both types in all three
/// vocabularies, which is what makes it a legal clamp and remap target.</para>
///
/// <para><b>The scope-narrowing rule.</b> <i>Widening</i> a member's scope — adding a type to a list —
/// is free: every existing row stays valid. <i>Narrowing</i> one, or removing a member, breaks the
/// invariant "a contact method's label is valid for its contact's type" with no migration and no
/// guard, because the guard tests assert that a member is classified <i>somewhere</i>, not that its
/// classification is stable. A narrowing change therefore needs its own remap migration in the same
/// commit, written the same way as issue #47 §15.</para>
///
/// <para>Three typed members per operation, never a <c>kind</c> string parameter: <c>kind</c> is the
/// <i>client's</i> magic string, and threading it in here would make it an unchecked cross-assembly
/// contract.</para>
/// </summary>
public static class ContactLabelScope
{
    private static readonly AddressLabel[] PersonAddressLabels =
    [
        AddressLabel.Home,
        AddressLabel.Work,
        AddressLabel.Billing,
        AddressLabel.Postal,
        AddressLabel.Other,
    ];

    private static readonly AddressLabel[] OrganizationAddressLabels =
    [
        AddressLabel.Visiting,
        AddressLabel.Registered,
        AddressLabel.Branch,
        AddressLabel.Billing,
        AddressLabel.Postal,
        AddressLabel.Other,
    ];

    private static readonly EmailLabel[] PersonEmailLabels =
    [
        EmailLabel.Home,
        EmailLabel.Work,
        EmailLabel.Other,
    ];

    private static readonly EmailLabel[] OrganizationEmailLabels =
    [
        EmailLabel.General,
        EmailLabel.Support,
        EmailLabel.Sales,
        EmailLabel.Billing,
        EmailLabel.Claims,
        EmailLabel.Other,
    ];

    private static readonly PhoneLabel[] PersonPhoneLabels =
    [
        PhoneLabel.Home,
        PhoneLabel.Work,
        PhoneLabel.Mobile,
        PhoneLabel.Other,
    ];

    private static readonly PhoneLabel[] OrganizationPhoneLabels =
    [
        PhoneLabel.Switchboard,
        PhoneLabel.Support,
        PhoneLabel.Sales,
        PhoneLabel.Billing,
        PhoneLabel.Claims,
        PhoneLabel.Emergency,
        PhoneLabel.Direct,
        PhoneLabel.Mobile,
        PhoneLabel.Other,
    ];

    /// <summary>The <see cref="AddressLabel"/> members a contact of this type may use, in display order.</summary>
    public static IReadOnlyList<AddressLabel> AddressLabelsFor(ContactType type) =>
        type == ContactType.Person ? PersonAddressLabels : OrganizationAddressLabels;

    /// <summary>The <see cref="EmailLabel"/> members a contact of this type may use, in display order.</summary>
    public static IReadOnlyList<EmailLabel> EmailLabelsFor(ContactType type) =>
        type == ContactType.Person ? PersonEmailLabels : OrganizationEmailLabels;

    /// <summary>The <see cref="PhoneLabel"/> members a contact of this type may use, in display order.</summary>
    public static IReadOnlyList<PhoneLabel> PhoneLabelsFor(ContactType type) =>
        type == ContactType.Person ? PersonPhoneLabels : OrganizationPhoneLabels;

    /// <summary>The label a new address opens on — always the first offered member.</summary>
    public static AddressLabel DefaultAddressLabel(ContactType type) => AddressLabelsFor(type)[0];

    /// <summary>The label a new email address opens on — always the first offered member.</summary>
    public static EmailLabel DefaultEmailLabel(ContactType type) => EmailLabelsFor(type)[0];

    /// <summary>The label a new phone number opens on — always the first offered member.</summary>
    public static PhoneLabel DefaultPhoneLabel(ContactType type) => PhoneLabelsFor(type)[0];

    /// <summary>Whether <paramref name="label"/> may be written on an address of this contact type.</summary>
    public static bool IsValidFor(AddressLabel label, ContactType type) => AddressLabelsFor(type).Contains(label);

    /// <summary>Whether <paramref name="label"/> may be written on an email of this contact type.</summary>
    public static bool IsValidFor(EmailLabel label, ContactType type) => EmailLabelsFor(type).Contains(label);

    /// <summary>Whether <paramref name="label"/> may be written on a phone number of this contact type.</summary>
    public static bool IsValidFor(PhoneLabel label, ContactType type) => PhoneLabelsFor(type).Contains(label);

    /// <summary>
    /// Resolves <paramref name="label"/> to one valid for <paramref name="type"/> — itself when already
    /// valid, otherwise <see cref="AddressLabel.Other"/>. Used by vCard import and by the type-switch
    /// remap: clamp, never drop, and never guess a "closest" label.
    /// </summary>
    public static AddressLabel Clamp(AddressLabel label, ContactType type) =>
        IsValidFor(label, type) ? label : AddressLabel.Other;

    /// <inheritdoc cref="Clamp(AddressLabel, ContactType)"/>
    public static EmailLabel Clamp(EmailLabel label, ContactType type) =>
        IsValidFor(label, type) ? label : EmailLabel.Other;

    /// <inheritdoc cref="Clamp(AddressLabel, ContactType)"/>
    public static PhoneLabel Clamp(PhoneLabel label, ContactType type) =>
        IsValidFor(label, type) ? label : PhoneLabel.Other;
}
