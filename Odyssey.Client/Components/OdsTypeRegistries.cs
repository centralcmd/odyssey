using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  Domain type registries (Odyssey Design System · components/ContactType*,
//  AccountFileType*, TransactionFileType*). The canonical name · glyph · category
//  color for each domain enum member, mirroring the DS CONTACT_TYPES /
//  ACCOUNT_FILE_TYPES / TRANSACTION_FILE_TYPES registries and the matching C#
//  enums in Odyssey.Dtos.Finance. Keep all three in lockstep.
//
//  This is a first-class source of truth: a wrong mapping never throws and never
//  fails a build, it is just silently wrong on screen — which is why it lives in
//  its own file and is covered by Odyssey.Client.Tests/OdsTypeRegistriesTests.
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
//  Domain type registries (Odyssey Design System · components/ContactType*,
//  AccountFileType*, TransactionFileType*). The canonical name · glyph · category
//  color for each domain enum member, mirroring the DS CONTACT_TYPES /
//  ACCOUNT_FILE_TYPES / TRANSACTION_FILE_TYPES registries and the matching C#
//  enums in Odyssey.Dtos.Finance. Keep all three in lockstep.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A domain enum member rendered as a pickable type — its key, label, Material glyph,
/// category color and soft tint. Backs the ContactType / *FileType pickers.
/// </summary>
public sealed record OdsTypeOption
{
    /// <summary>Enum key — the value bound by the picker (e.g. "Merchant", "Statement").</summary>
    public required string Key { get; init; }
    /// <summary>Visible label.</summary>
    public required string Label { get; init; }
    /// <summary>Material Icons ligature name.</summary>
    public required string Icon { get; init; }
    /// <summary>Icon foreground color (any CSS color).</summary>
    public required string Color { get; init; }
    /// <summary>Icon background tint (any CSS color).</summary>
    public required string Soft { get; init; }

    /// <summary>
    /// This member names the THING the record is about rather than a party to it — the DS registries'
    /// <c>object: true</c> flag. Set only by <see cref="OdsTypeRegistries.ContractPartyRoles"/>
    /// today, where it marks <c>Object</c>, <c>Property</c> and <c>Collateral</c> (issue #169).
    /// </summary>
    /// <remarks>
    /// It rides on the registry ROW, exactly as the design system declares it, rather than living in
    /// a second list beside the registry: a role added later declares what kind of member it is next
    /// to its own glyph and colour, where the omission is visible, instead of in a list a reader has
    /// no reason to open. Every other registry leaves it <see langword="false"/>.
    /// </remarks>
    public bool IsObject { get; init; }
}

/// <summary>A labelled section of <see cref="OdsTypeOption"/>s for a grouped
/// <c>OdsTypeSelect</c> (e.g. Assets / Liabilities).</summary>
public sealed record OdsTypeSelectGroup(string Label, IReadOnlyList<OdsTypeOption> Items);

/// <summary>
/// The one projection of a contact record to a picker option (Odyssey Design System ·
/// <c>ContactSelect</c>): the id as the value, the resolved display name as the label, and the leading
/// glyph + colour read off <see cref="OdsTypeRegistries.ContactTypes"/> — never re-hardcoded per
/// surface, which is how a contact list ends up reading as merchants only.
/// </summary>
public static class OdsContactOptions
{
    public static OdsOption From(Odyssey.Dtos.Journal.ExistingContact contact)
    {
        var meta = OdsTypeRegistries.ContactTypeOf(contact.Type.ToString());
        return new OdsOption(contact.ContactId.ToString(), Labelled(contact))
        {
            Icon = meta.Icon,
            IconColor = meta.Color,
        };
    }

    /// <summary>
    /// The option's label, suffixed when the contact carries a date of death (Person) or a
    /// dissolution date (Organization): <c>Kari Nordmann · Deceased</c> (issue #48 §3 state 12).
    ///
    /// <para>
    /// The contact is <b>still fully selectable</b> — recording either date removes no capability —
    /// and the suffix goes into the <b>label</b> rather than a sub-line for two reasons: it lands in
    /// the accessible name by construction, and <c>OdsCombobox</c> renders no
    /// <c>OdsOption.Sub</c> at all (only OdsTagMultiSelect does), so a sub-line would be silently
    /// invisible. It is deliberately not an OdsContactChip either: that component is not on the
    /// picker path, and wiring it would re-widen the cross-claim projection issue #48 narrowed.
    /// </para>
    /// </summary>
    private static string Labelled(Odyssey.Dtos.Journal.ExistingContact contact)
    {
        var state = contact.Type == Odyssey.Dtos.ContactType.Person
            ? contact.PersonDetails?.DateOfDeath is not null ? "Deceased" : null
            : contact.OrganizationDetails?.DissolvedDate is not null ? "Dissolved" : null;

        return state is null ? contact.ResolvedDisplayName : $"{contact.ResolvedDisplayName} · {state}";
    }

    /// <summary>
    /// An option for a contact the caller only holds as the narrow finance embed (issue #48 §10.2):
    /// <see cref="Odyssey.Dtos.Journal.ContactEmbed"/> carries an id and a resolved name and nothing
    /// else, deliberately — including no <c>Type</c>, so this option cannot carry a type glyph.
    ///
    /// <para>
    /// Its one call site is the transaction dialog's edit-mode fallback, where the linked contact has
    /// since been archived and so is absent from the picker's own option list. The picker needs
    /// <i>something</i> to render the trigger's name against; an unresolvable-type option is the
    /// right shape for that, and the alternative — widening the embed to carry a type — is the leak
    /// the narrowing closed.
    /// </para>
    /// </summary>
    public static OdsOption From(Odyssey.Dtos.Journal.ContactEmbed contact)
    {
        var meta = OdsTypeRegistries.ContactTypeOf(null);
        return new OdsOption(contact.ContactId.ToString(), contact.ResolvedDisplayName)
        {
            Icon = meta.Icon,
            IconColor = meta.Color,
        };
    }

    /// <summary>The ACTIVE contacts as options, name-ordered — an archived contact must not be
    /// linkable, so it never reaches a picker's list.</summary>
    public static IReadOnlyList<OdsOption> Active(IEnumerable<Odyssey.Dtos.Journal.ExistingContact> contacts) =>
        [.. contacts.Where(c => c.Archived is null)
            .OrderBy(c => c.ResolvedDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(From)];
}

/// <summary>The canonical domain type registries and their <see cref="OdsOption"/> projections.</summary>
public static class OdsTypeRegistries
{
    /// <summary>ContactType — Person · Organization (issue #325, trimmed from the earlier six values).
    /// Mirrors the DS CONTACT_TYPES registry.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> ContactTypes =
    [
        new() { Key = "Person",       Label = "Person",       Icon = "person",          Color = "oklch(0.80 0.15 150)",  Soft = "oklch(0.80 0.15 150 / 0.16)" },
        new() { Key = "Organization", Label = "Organization", Icon = "corporate_fare",  Color = "oklch(0.72 0.16 295)",  Soft = "oklch(0.72 0.16 295 / 0.16)" },
    ];

    /// <summary>AddressLabel — Home · Work · Billing · Other (issue #325). Mirrors the DS ADDRESS_LABELS.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> AddressLabels =
    [
        new() { Key = "Home",       Label = "Home",       Icon = "home",                Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Work",       Label = "Work",       Icon = "work",                Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Billing",    Label = "Billing",    Icon = "receipt_long",        Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Other",      Label = "Other",      Icon = "category",            Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Postal",     Label = "Postal",     Icon = "markunread_mailbox",  Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Visiting",   Label = "Visiting",   Icon = "storefront",          Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Registered", Label = "Registered", Icon = "account_balance",     Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Branch",     Label = "Branch",     Icon = "apartment",           Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>EmailLabel — the person vocabulary (issue #325 v4) plus the organization one
    /// (issue #47 §6), in ordinal order. Mirrors the DS EMAIL_LABELS. The offered subset for a given
    /// contact is <see cref="EmailLabelsFor"/>, never this whole list.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> EmailLabels =
    [
        // "Personal", not "Home": the member name, the ordinal, the persisted value and the vCard
        // token all stay `Home` — only the rendered string changes (issue #47 §9).
        new() { Key = "Home",    Label = "Personal", Icon = "home",             Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Work",    Label = "Work",     Icon = "work",             Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Other",   Label = "Other",    Icon = "category",         Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "General", Label = "General",  Icon = "alternate_email",  Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Support", Label = "Support",  Icon = "support_agent",    Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Sales",   Label = "Sales",    Icon = "sell",             Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Billing", Label = "Billing",  Icon = "receipt_long",     Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Claims",  Label = "Claims",   Icon = "assignment_late",  Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>PhoneLabel — the person vocabulary (issue #325 v4) plus the organization one
    /// (issue #47 §6), in ordinal order. Mirrors the DS PHONE_LABELS. The offered subset for a given
    /// contact is <see cref="PhoneLabelsFor"/>, never this whole list.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> PhoneLabels =
    [
        new() { Key = "Home",        Label = "Home",        Icon = "home",             Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Work",        Label = "Work",        Icon = "work",             Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Mobile",      Label = "Mobile",      Icon = "smartphone",       Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Other",       Label = "Other",       Icon = "category",         Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Switchboard", Label = "Switchboard", Icon = "phone_in_talk",    Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Support",     Label = "Support",     Icon = "support_agent",    Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Sales",       Label = "Sales",       Icon = "sell",             Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Billing",     Label = "Billing",     Icon = "receipt_long",     Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Claims",      Label = "Claims",      Icon = "assignment_late",  Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Emergency",   Label = "Emergency",   Icon = "emergency",        Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Direct",      Label = "Direct",      Icon = "phone_forwarded",  Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>AccountFileType — the kind of document attached to an account.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> AccountFileTypes =
    [
        new() { Key = "Message",           Label = "Message",            Icon = "mail",              Color = "oklch(0.76 0.13 225)",  Soft = "oklch(0.76 0.13 225 / 0.16)" },
        new() { Key = "Statement",         Label = "Statement",          Icon = "description",       Color = "oklch(0.79 0.115 188)", Soft = "oklch(0.79 0.115 188 / 0.16)" },
        new() { Key = "Contract",          Label = "Contract",           Icon = "history_edu",       Color = "oklch(0.72 0.16 295)",  Soft = "oklch(0.72 0.16 295 / 0.16)" },
        new() { Key = "Tax",               Label = "Tax",                Icon = "request_quote",     Color = "oklch(0.75 0.16 330)",  Soft = "oklch(0.75 0.16 330 / 0.16)" },
        new() { Key = "Documentation",     Label = "Documentation",      Icon = "menu_book",         Color = "oklch(0.77 0.14 110)",  Soft = "oklch(0.77 0.14 110 / 0.16)" },
        new() { Key = "InsurancePolicy",   Label = "Insurance policy",   Icon = "shield",            Color = "oklch(0.74 0.15 30)",   Soft = "oklch(0.74 0.15 30 / 0.16)" },
        new() { Key = "LoanAgreement",     Label = "Loan agreement",     Icon = "gavel",             Color = "oklch(0.72 0.15 265)",  Soft = "oklch(0.72 0.15 265 / 0.16)" },
        new() { Key = "RepaymentSchedule", Label = "Repayment schedule", Icon = "event_repeat",      Color = "oklch(0.78 0.14 160)",  Soft = "oklch(0.78 0.14 160 / 0.16)" },
        new() { Key = "PurchaseAgreement", Label = "Purchase agreement", Icon = "sell",              Color = "oklch(0.79 0.14 60)",   Soft = "oklch(0.79 0.14 60 / 0.16)" },
        new() { Key = "Valuation",         Label = "Valuation",          Icon = "price_check",       Color = "oklch(0.80 0.15 140)",  Soft = "oklch(0.80 0.15 140 / 0.16)" },
        new() { Key = "Warranty",          Label = "Warranty",           Icon = "verified",          Color = "oklch(0.77 0.13 205)",  Soft = "oklch(0.77 0.13 205 / 0.16)" },
        new() { Key = "Registration",      Label = "Registration",       Icon = "app_registration",  Color = "oklch(0.74 0.15 310)",  Soft = "oklch(0.74 0.15 310 / 0.16)" },
        new() { Key = "Prospectus",        Label = "Prospectus",         Icon = "auto_stories",      Color = "oklch(0.78 0.14 95)",   Soft = "oklch(0.78 0.14 95 / 0.16)" },
        new() { Key = "Other",             Label = "Other",              Icon = "insert_drive_file", Color = "oklch(0.74 0.02 250)",  Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>TransactionFileType — the kind of document attached to a transaction.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> TransactionFileTypes =
    [
        new() { Key = "Receipt",             Label = "Receipt",              Icon = "receipt_long",      Color = "oklch(0.80 0.15 150)", Soft = "oklch(0.80 0.15 150 / 0.16)" },
        new() { Key = "Invoice",             Label = "Invoice",              Icon = "receipt",           Color = "oklch(0.80 0.13 85)",  Soft = "oklch(0.80 0.13 85 / 0.16)" },
        new() { Key = "CreditNote",          Label = "Credit note",          Icon = "assignment_return", Color = "oklch(0.72 0.16 22)",  Soft = "oklch(0.72 0.16 22 / 0.16)" },
        new() { Key = "Quote",               Label = "Quote",                Icon = "format_quote",      Color = "oklch(0.72 0.16 295)", Soft = "oklch(0.72 0.16 295 / 0.16)" },
        new() { Key = "PaymentConfirmation", Label = "Payment confirmation", Icon = "price_check",       Color = "oklch(0.76 0.13 225)", Soft = "oklch(0.76 0.13 225 / 0.16)" },
        new() { Key = "Documentation",       Label = "Documentation",        Icon = "menu_book",         Color = "oklch(0.77 0.14 110)", Soft = "oklch(0.77 0.14 110 / 0.16)" },
        new() { Key = "Other",               Label = "Other",                Icon = "insert_drive_file", Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>TaxStatementFileType — the kind of document attached to a tax statement.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> TaxStatementFileTypes =
    [
        new() { Key = "TaxReturn",          Label = "Tax return",          Icon = "assignment",        Color = "oklch(0.75 0.16 330)", Soft = "oklch(0.75 0.16 330 / 0.16)" },
        new() { Key = "TaxAssessment",      Label = "Tax assessment",      Icon = "fact_check",        Color = "oklch(0.72 0.16 295)", Soft = "oklch(0.72 0.16 295 / 0.16)" },
        new() { Key = "SupportingDocument", Label = "Supporting document", Icon = "attach_file",       Color = "oklch(0.77 0.14 110)", Soft = "oklch(0.77 0.14 110 / 0.16)" },
        new() { Key = "Other",              Label = "Other",               Icon = "insert_drive_file", Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>
    /// ContractType — Employment · Service · Rental · Insurance · Subscription · Purchase · Loan ·
    /// Deposit · Membership · Other (issues #174, #157, #187). Mirrors the DS <c>contractTypes</c> registry and the C#
    /// <c>ContractType</c> enum.
    /// </summary>
    /// <remarks>
    /// In READING order, which is deliberately not ordinal order: the four later members carry
    /// ordinals 4–7 while <c>Other</c> keeps ordinal 3, because an ordinal is a wire and persistence
    /// contract and is never renumbered. <c>Other</c> still reads LAST — "none of the above" after the
    /// categories, not in the middle of them — and <see cref="ContractTypeOf"/>'s documented fallback
    /// is the trailing entry, so the two facts hold together rather than by coincidence.
    /// </remarks>
    public static readonly IReadOnlyList<OdsTypeOption> ContractTypes =
    [
        new() { Key = "Employment",   Label = "Employment",   Icon = "work",                Color = "oklch(0.76 0.13 225)", Soft = "oklch(0.76 0.13 225 / 0.16)" },
        new() { Key = "Service",      Label = "Service",      Icon = "home_repair_service", Color = "oklch(0.78 0.14 170)", Soft = "oklch(0.78 0.14 170 / 0.16)" },
        new() { Key = "Rental",       Label = "Rental",       Icon = "cottage",             Color = "oklch(0.79 0.14 60)",  Soft = "oklch(0.79 0.14 60 / 0.16)" },
        new() { Key = "Insurance",    Label = "Insurance",    Icon = "shield",              Color = "oklch(0.75 0.14 290)", Soft = "oklch(0.75 0.14 290 / 0.16)" },
        new() { Key = "Subscription", Label = "Subscription", Icon = "autorenew",           Color = "oklch(0.76 0.14 320)", Soft = "oklch(0.76 0.14 320 / 0.16)" },
        new() { Key = "Purchase",     Label = "Purchase",     Icon = "shopping_bag",        Color = "oklch(0.78 0.14 140)", Soft = "oklch(0.78 0.14 140 / 0.16)" },
        // Loan carries ordinal 8 and reads HERE, after Purchase — a mortgage was filed as a Purchase
        // before this member existed. Its hue is the one wide gap left on the wheel, between Rental
        // (60) and Purchase (140), clearing both by 40 degrees at the same lightness and chroma as its
        // neighbours (the design system's contrast pass, not a value invented here).
        new() { Key = "Loan",         Label = "Loan",         Icon = "account_balance",     Color = "oklch(0.77 0.13 100)", Soft = "oklch(0.77 0.13 100 / 0.16)" },
        // Deposit carries ordinal 9 and reads beside its mirror, Loan (issue #187 §3.4). PROVISIONAL:
        // the design system has no Deposit entry yet, so the glyph and hue are placeholders chosen only
        // to satisfy this registry's guard tests — replace them when the DS registry lands.
        new() { Key = "Deposit",      Label = "Deposit",      Icon = "savings",             Color = "oklch(0.77 0.13 195)", Soft = "oklch(0.77 0.13 195 / 0.16)" },
        new() { Key = "Membership",   Label = "Membership",   Icon = "card_membership",     Color = "oklch(0.77 0.13 20)",  Soft = "oklch(0.77 0.13 20 / 0.16)" },
        new() { Key = "Other",        Label = "Other",        Icon = "description",         Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>
    /// ContractPartyRole — what a linked record DOES in the agreement (issues #121, #157). Mirrors the
    /// DS <c>contractPartyRoles</c> registry and the C# <c>ContractPartyRole</c> enum, in ORDINAL
    /// order. <b>Twenty live members</b>; ordinals 0 (<c>Unspecified</c>) and 5
    /// (<c>ServiceProvider</c>) are retired holes and never reappear here.
    /// </summary>
    /// <remarks>
    /// <b>Which of these a picker may offer depends on the contract's TYPE</b> — see
    /// <see cref="ContractPartyRolesFor"/>, which reads the shared
    /// <c>Odyssey.Dtos.Finance.ContractPartyRoleMatrix</c>. This list is the full vocabulary, used for
    /// rendering a role that already exists; it is not what the picker offers.
    ///
    /// <para>
    /// <c>Broker</c> is deliberately low-chroma: it is legal on every type and should not read as a
    /// category of its own. With <c>Unspecified</c> retired there is no "no role stated" member at all
    /// — a role is required on every write, and the only remaining unset role is a legacy row, drawn
    /// as an absence by <c>PartyRoleLabel</c>.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<OdsTypeOption> ContractPartyRoles =
    [
        new() { Key = "Employee",     Label = "Employee",     Icon = "badge",              Color = "oklch(0.76 0.13 265)", Soft = "oklch(0.76 0.13 265 / 0.16)" },
        new() { Key = "Employer",     Label = "Employer",     Icon = "corporate_fare",     Color = "oklch(0.75 0.14 300)", Soft = "oklch(0.75 0.14 300 / 0.16)" },
        new() { Key = "Buyer",        Label = "Buyer",        Icon = "shopping_bag",       Color = "oklch(0.79 0.14 145)", Soft = "oklch(0.79 0.14 145 / 0.16)" },
        new() { Key = "Seller",       Label = "Seller",       Icon = "sell",               Color = "oklch(0.80 0.13 90)",  Soft = "oklch(0.80 0.13 90 / 0.16)" },
        new() { Key = "Other",        Label = "Other",        Icon = "more_horiz",         Color = "oklch(0.77 0.10 25)",  Soft = "oklch(0.77 0.10 25 / 0.16)" },
        new() { Key = "Landlord",     Label = "Landlord",     Icon = "vpn_key",            Color = "oklch(0.79 0.13 55)",  Soft = "oklch(0.79 0.13 55 / 0.16)" },
        new() { Key = "Tenant",       Label = "Tenant",       Icon = "home",               Color = "oklch(0.78 0.13 35)",  Soft = "oklch(0.78 0.13 35 / 0.16)" },
        new() { Key = "Insurer",      Label = "Insurer",      Icon = "shield",             Color = "oklch(0.75 0.14 285)", Soft = "oklch(0.75 0.14 285 / 0.16)" },
        new() { Key = "Policyholder", Label = "Policyholder", Icon = "assignment_ind",     Color = "oklch(0.76 0.13 255)", Soft = "oklch(0.76 0.13 255 / 0.16)" },
        new() { Key = "Insured",      Label = "Insured",      Icon = "health_and_safety",  Color = "oklch(0.77 0.13 215)", Soft = "oklch(0.77 0.13 215 / 0.16)" },
        new() { Key = "Beneficiary",  Label = "Beneficiary",  Icon = "volunteer_activism", Color = "oklch(0.78 0.13 185)", Soft = "oklch(0.78 0.13 185 / 0.16)" },
        new() { Key = "Lender",       Label = "Lender",       Icon = "savings",            Color = "oklch(0.78 0.13 120)", Soft = "oklch(0.78 0.13 120 / 0.16)" },
        new() { Key = "Borrower",     Label = "Borrower",     Icon = "request_quote",      Color = "oklch(0.78 0.13 165)", Soft = "oklch(0.78 0.13 165 / 0.16)" },
        new() { Key = "Guarantor",    Label = "Guarantor",    Icon = "verified_user",      Color = "oklch(0.76 0.07 330)", Soft = "oklch(0.76 0.07 330 / 0.16)" },
        new() { Key = "Broker",       Label = "Broker",       Icon = "handshake",          Color = "oklch(0.76 0.07 245)", Soft = "oklch(0.76 0.07 245 / 0.16)" },
        // The three OBJECT roles (issue #169) — what the agreement is ABOUT rather than a side of it.
        // Values mirror the DS registry. The DS additionally flags them `object: true`, which its
        // party tile reads to draw them apart; that presentation belongs to the frontend counterpart
        // and is deliberately not implemented here.
        new() { Key = "Object",       Label = "Object",       Icon = "category",           Color = "oklch(0.78 0.11 75)",  Soft = "oklch(0.78 0.11 75 / 0.16)",  IsObject = true },
        new() { Key = "Property",     Label = "Property",     Icon = "holiday_village",    Color = "oklch(0.78 0.11 45)",  Soft = "oklch(0.78 0.11 45 / 0.16)",  IsObject = true },
        new() { Key = "Collateral",   Label = "Collateral",   Icon = "lock",               Color = "oklch(0.78 0.11 105)", Soft = "oklch(0.78 0.11 105 / 0.16)", IsObject = true },
        // The Deposit counterparties (issue #187) — sides of the agreement, so NOT object roles.
        // PROVISIONAL: the design system has no entries for them yet; the glyphs and hues are
        // placeholders chosen only to satisfy this registry's guard tests — replace them when the DS
        // registry lands.
        new() { Key = "Depositor",    Label = "Depositor",    Icon = "move_to_inbox",      Color = "oklch(0.78 0.13 200)", Soft = "oklch(0.78 0.13 200 / 0.16)" },
        new() { Key = "Custodian",    Label = "Custodian",    Icon = "account_balance",    Color = "oklch(0.77 0.13 235)", Soft = "oklch(0.77 0.13 235 / 0.16)" },
    ];

    /// <summary>
    /// The roles a contract of <paramref name="type"/> may hold, as the picker's two groups —
    /// suggested first, then the rest (issue #157 §4.6). Reads the <b>shared</b>
    /// <c>ContractPartyRoleMatrix</c> in <c>Odyssey.Dtos</c>: the legality is the server's own
    /// declaration, and only the presentation (label, glyph, colour, group heading) is client-side.
    /// </summary>
    /// <remarks>
    /// This is deliberately <em>not</em> a client-side copy of a server rule — the defect CLAUDE.md
    /// forbids. The client names the same symbol the write-path validator does, so the picker cannot
    /// offer a role the server would answer with a <c>422</c>, nor withhold one it would accept.
    ///
    /// <para>
    /// A role the registry cannot name is skipped rather than drawn unlabelled: an ordinal newer than
    /// this build is a real version-skew state, and an unnamed option is unpickable in any case.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<OdsTypeSelectGroup> ContractPartyRolesFor(ContractType type)
    {
        var groups = new List<OdsTypeSelectGroup>();

        var suggested = Options(ContractPartyRoleMatrix.SuggestedFor(type));
        if (suggested.Count > 0)
        {
            groups.Add(new OdsTypeSelectGroup(
                $"Suggested for {ContractTypeOf(type).Label.ToLowerInvariant()}", suggested));
        }

        var allowed = Options(ContractPartyRoleMatrix.AllowedFor(type));
        if (allowed.Count > 0)
        {
            groups.Add(new OdsTypeSelectGroup("Also allowed", allowed));
        }

        return groups;

        static List<OdsTypeOption> Options(IReadOnlyList<ContractPartyRole> roles) =>
            [.. roles.Select(ContractPartyRoleOf).OfType<OdsTypeOption>()];
    }

    /// <summary>
    /// ContractEventType — what kind of thing HAPPENED to an agreement (issue #138, extended by
    /// issue #154). Mirrors the DS <c>contractEventTypes</c> registry and the C#
    /// <c>ContractEventType</c> enum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is READING order, and it is no longer ordinal order.</b> It used to be both, because
    /// <c>Other</c> happened to carry the last ordinal as well as being the catch-all. Issue #154
    /// appended nine members at 9–17 while <c>Other</c> kept 8 — an ordinal is a wire and persistence
    /// contract and is never renumbered — so the two orders have parted company here exactly as they
    /// already had on <see cref="ContractTypes"/>.
    /// </para>
    /// <para>
    /// <b><c>Other</c> must therefore stay the LAST entry.</b> <see cref="ContractEventTypeOf"/>
    /// documents the <em>trailing</em> registry entry as its fallback for an ordinal this build does
    /// not know, so reordering this list so that something else ends it would silently render every
    /// unknown event as that member instead.
    /// </para>
    /// <para>
    /// The nine automation members use the verbs a user would: <em>Resumed</em>, not "Unpaused";
    /// <em>Restored</em>, not "Unarchived".
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<OdsTypeOption> ContractEventTypes =
    [
        new() { Key = "Signed",       Label = "Signed",              Icon = "history_edu",        Color = "oklch(0.72 0.16 295)", Soft = "oklch(0.72 0.16 295 / 0.16)" },
        new() { Key = "Amended",      Label = "Amended",             Icon = "edit_document",      Color = "oklch(0.80 0.13 85)",  Soft = "oklch(0.80 0.13 85 / 0.16)" },
        new() { Key = "Renewed",      Label = "Renewed",             Icon = "autorenew",          Color = "oklch(0.78 0.14 170)", Soft = "oklch(0.78 0.14 170 / 0.16)" },
        new() { Key = "Extended",     Label = "Extended",            Icon = "more_time",          Color = "oklch(0.78 0.14 145)", Soft = "oklch(0.78 0.14 145 / 0.16)" },
        new() { Key = "NoticeGiven",  Label = "Notice given",        Icon = "campaign",           Color = "oklch(0.79 0.14 60)",  Soft = "oklch(0.79 0.14 60 / 0.16)" },
        new() { Key = "Terminated",   Label = "Terminated",          Icon = "gavel",              Color = "oklch(0.72 0.15 25)",  Soft = "oklch(0.72 0.15 25 / 0.16)" },
        new() { Key = "PriceChanged", Label = "Price changed",       Icon = "price_change",       Color = "oklch(0.76 0.14 320)", Soft = "oklch(0.76 0.14 320 / 0.16)" },
        new() { Key = "EmailSent",    Label = "Email sent",          Icon = "outgoing_mail",      Color = "oklch(0.77 0.14 205)", Soft = "oklch(0.77 0.14 205 / 0.16)" },
        // ── The nine automation members (issue #154), ordinals 9-17 ──────────────
        new() { Key = "Paused",       Label = "Paused",              Icon = "pause_circle",       Color = "oklch(0.79 0.12 70)",  Soft = "oklch(0.79 0.12 70 / 0.16)" },
        new() { Key = "Unpaused",     Label = "Resumed",             Icon = "play_circle",        Color = "oklch(0.79 0.14 155)", Soft = "oklch(0.79 0.14 155 / 0.16)" },
        new() { Key = "Ready",        Label = "Marked ready",        Icon = "rule",               Color = "oklch(0.76 0.13 260)", Soft = "oklch(0.76 0.13 260 / 0.16)" },
        new() { Key = "Unready",      Label = "Ready withdrawn",     Icon = "remove_done",        Color = "oklch(0.75 0.10 240)", Soft = "oklch(0.75 0.10 240 / 0.16)" },
        new() { Key = "Unsigned",     Label = "Signed date cleared", Icon = "history_toggle_off", Color = "oklch(0.74 0.10 285)", Soft = "oklch(0.74 0.10 285 / 0.16)" },
        new() { Key = "Archived",     Label = "Archived",            Icon = "inventory_2",        Color = "oklch(0.75 0.06 250)", Soft = "oklch(0.75 0.06 250 / 0.16)" },
        new() { Key = "Unarchived",   Label = "Restored",            Icon = "unarchive",          Color = "oklch(0.78 0.12 185)", Soft = "oklch(0.78 0.12 185 / 0.16)" },
        new() { Key = "PartyAdded",   Label = "Party added",         Icon = "person_add",         Color = "oklch(0.79 0.13 130)", Soft = "oklch(0.79 0.13 130 / 0.16)" },
        new() { Key = "PartyRemoved", Label = "Party removed",       Icon = "person_remove",      Color = "oklch(0.74 0.13 15)",  Soft = "oklch(0.74 0.13 15 / 0.16)" },
        // LAST, and it must stay last — the unknown-ordinal fallback is positional.
        new() { Key = "Other",        Label = "Other",               Icon = "more_horiz",         Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>ContractFileType — the kind of document attached to a contract (issue #174). Mirrors
    /// the DS contractFileTypes registry and the C# ContractFileType enum.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> ContractFileTypes =
    [
        new() { Key = "Signed",         Label = "Signed",         Icon = "history_edu",       Color = "oklch(0.72 0.16 295)", Soft = "oklch(0.72 0.16 295 / 0.16)" },
        new() { Key = "Amendment",      Label = "Amendment",      Icon = "edit_document",     Color = "oklch(0.80 0.13 85)",  Soft = "oklch(0.80 0.13 85 / 0.16)" },
        new() { Key = "Correspondence", Label = "Correspondence", Icon = "forum",             Color = "oklch(0.77 0.14 205)", Soft = "oklch(0.77 0.14 205 / 0.16)" },
        new() { Key = "Other",          Label = "Other",          Icon = "insert_drive_file", Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>BudgetCategoryType — the two directions a budget line can take: Expense (money out)
    /// and Income (money in). Mirrors the DS BUDGET_CATEGORY_TYPES and the C# BudgetCategoryType enum
    /// (Expense = 0, Income = 1). Expense reads as a debit (warm red), Income as a credit (green).</summary>
    public static readonly IReadOnlyList<OdsTypeOption> BudgetCategoryTypes =
    [
        new() { Key = "Expense", Label = "Expense", Icon = "trending_down", Color = "oklch(0.72 0.16 22)",  Soft = "oklch(0.72 0.16 22 / 0.16)" },
        new() { Key = "Income",  Label = "Income",  Icon = "trending_up",   Color = "oklch(0.80 0.15 150)", Soft = "oklch(0.80 0.15 150 / 0.16)" },
    ];

    /// <summary>The BudgetCategoryType descriptor for an enum value (falls back to "Expense").</summary>
    public static OdsTypeOption BudgetCategoryTypeOf(BudgetCategoryType type) =>
        BudgetCategoryTypes.FirstOrDefault(t => t.Key == type.ToString()) ?? BudgetCategoryTypes[0];

    /// <summary>
    /// The same two values shaped as a money field's LEAD (Odyssey Design System ·
    /// <c>BUDGET_CATEGORY_DIRECTION_OPTIONS</c>) — the left-edge button that flips the value's
    /// direction in the slot a sign would occupy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A budget item records a <b>direction</b>, not a sign, so its planned amount can carry the
    /// question outright and the form needs no separate category picker beside it — the picker asked
    /// the same question twice, in a second place that could drift.
    /// </para>
    /// <para>
    /// Derived from <see cref="BudgetCategoryTypes"/> rather than written out again, so the lead, the
    /// helper copy and the stored enum cannot disagree. <b>Tone comes from
    /// <see cref="BudgetCategoryDirection.Tone"/>, never from the value</b>: a lead tinted from
    /// <c>Value</c> would emit <c>tone-Expense</c>, which matches no CSS rule and leaves the figure
    /// the wrong colour with nothing failing.
    /// </para>
    /// <para>
    /// A <b>word</b>, not an arrow: a directional glyph beside a figure reads as that figure rising
    /// or falling — the same reason <c>TermDirectionVisuals</c> carries none.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<OdsDirectionOption> BudgetCategoryDirections =
        [.. BudgetCategoryTypes.Select(t =>
        {
            var direction = BudgetCategoryDirectionOf(t.Key);
            return new OdsDirectionOption
            {
                Value = t.Key,
                Label = t.Label,
                Short = direction.Short,
                Tone = direction.Tone,
            };
        })];

    /// <summary>
    /// The direction vocabulary for a budget category — the lead's short word, the finance tone, and
    /// what the direction MEANS for the field's helper line. Falls back to Expense, which is the enum's
    /// zero member and what a row written before the field existed means.
    /// </summary>
    public static BudgetCategoryDirection BudgetCategoryDirectionOf(BudgetCategoryType type) =>
        BudgetCategoryDirectionOf(type.ToString());

    private static BudgetCategoryDirection BudgetCategoryDirectionOf(string key) => key == "Income"
        ? new BudgetCategoryDirection("in", "income", "money into the budget")
        : new BudgetCategoryDirection("out", "expense", "money out of the budget");

    /// <summary>Parses a lead's string value back to the enum; anything unrecognised is Expense.</summary>
    public static BudgetCategoryType BudgetCategoryTypeFrom(string? value) =>
        Enum.TryParse<BudgetCategoryType>(value, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : BudgetCategoryType.Expense;

    /// <summary>The ContractType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption ContractTypeOf(ContractType type) =>
        ContractTypes.FirstOrDefault(t => t.Key == type.ToString()) ?? ContractTypes[^1];

    /// <summary>
    /// The ContractPartyRole descriptor for an enum value, or <see langword="null"/> when this build's
    /// registry does not contain that ordinal (issue #121 appends members; only the initial seven are
    /// fixed, so a client older than the deployment is a real version-skew state).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the <c>?? Types[^1]</c> fallback the rest of this file uses. Falling through
    /// to the last member would render an unrecognised role as the deliberate <c>Other</c> — "somebody
    /// looked and none of these fit" — which is a <i>wrong</i> answer rather than a missing one.
    /// <c>PartyRoleLabel</c> owns the fallback instead, once, so every channel that turns a role into
    /// words says the same thing.
    /// </remarks>
    public static OdsTypeOption? ContractPartyRoleOf(ContractPartyRole role) =>
        ContractPartyRoles.FirstOrDefault(t => t.Key == role.ToString());

    /// <summary>
    /// Whether <paramref name="role"/> names the THING the agreement concerns rather than a side of
    /// it — <c>Object</c>, <c>Property</c> or <c>Collateral</c> (issue #169). Read off the registry's
    /// own <see cref="OdsTypeOption.IsObject"/> flag, so the tile class and the tile ORDER cannot
    /// disagree about which roles they mean.
    /// </summary>
    /// <remarks>
    /// An ordinal this build cannot name answers <see langword="false"/>: a version-skew role is
    /// drawn as an unrecognised one, and guessing it into the object group would assert a
    /// classification this build has no basis for.
    /// </remarks>
    public static bool IsObjectRole(ContractPartyRole role) =>
        ContractPartyRoleOf(role) is { IsObject: true };

    /// <summary>The ContractEventType descriptor for an enum value (falls back to "Other").</summary>
    /// <remarks>
    /// The trailing-entry fallback, like <see cref="ContractTypeOf"/>'s: an ordinal this build does
    /// not know is a version-skew state, and <c>Other</c> — "anything the named members do not
    /// cover" — is an honest answer to it, because the event's own title is what carries the meaning
    /// either way. That is not true of <c>ContractPartyRoleOf</c>, where <c>Other</c> asserts that
    /// somebody looked and none of the roles fit, which is why that one returns null instead.
    /// </remarks>
    public static OdsTypeOption ContractEventTypeOf(ContractEventType type) =>
        ContractEventTypes.FirstOrDefault(t => t.Key == type.ToString()) ?? ContractEventTypes[^1];

    /// <summary>The ContractFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption ContractFileTypeOf(ContractFileType type) =>
        ContractFileTypes.FirstOrDefault(t => t.Key == type.ToString()) ?? ContractFileTypes[^1];

    /// <summary>The ContactType descriptor for an enum key (falls back to "Organization").</summary>
    public static OdsTypeOption ContactTypeOf(string? key) =>
        ContactTypes.FirstOrDefault(t => t.Key == key) ?? ContactTypes[^1];

    /// <summary>
    /// The AddressLabel descriptor for an enum key, resolving <c>Other</c> <b>by key</b>.
    ///
    /// <para>Not the positional <c>[^1]</c> the other registries use: issue #47 appends the
    /// organization members <i>after</i> <c>Other</c>, which is precisely what breaks a positional
    /// fallback. This path renders an undefined ordinal (a hand-edited row, a contact whose type was
    /// switched concurrently), and with <c>[^1]</c> such a row would render as <c>Branch</c> /
    /// <c>Claims</c> / <c>Direct</c> — plausible, specific and wrong, strictly worse than the honest
    /// <c>Other</c>.</para>
    /// </summary>
    public static OdsTypeOption AddressLabelOf(string? key) =>
        AddressLabels.FirstOrDefault(t => t.Key == key) ?? OtherOf(AddressLabels);

    /// <inheritdoc cref="AddressLabelOf"/>
    public static OdsTypeOption EmailLabelOf(string? key) =>
        EmailLabels.FirstOrDefault(t => t.Key == key) ?? OtherOf(EmailLabels);

    /// <inheritdoc cref="AddressLabelOf"/>
    public static OdsTypeOption PhoneLabelOf(string? key) =>
        PhoneLabels.FirstOrDefault(t => t.Key == key) ?? OtherOf(PhoneLabels);

    private static OdsTypeOption OtherOf(IReadOnlyList<OdsTypeOption> registry) =>
        registry.First(t => t.Key == "Other");

    // ── Per-contact-type label projections (issue #47 §3) ──────────────────────

    /// <summary>
    /// The AddressLabel options offered for a contact of this type, in the display order
    /// <see cref="ContactLabelScope"/> declares — so the picker never offers a label the server would
    /// reject, and the first entry is the label a new method opens on.
    ///
    /// <para>Built once per (kind, type), not projected per render.</para>
    /// </summary>
    public static IReadOnlyList<OdsTypeOption> AddressLabelsFor(ContactType type) =>
        type == ContactType.Person ? PersonAddressLabels : OrganizationAddressLabels;

    /// <inheritdoc cref="AddressLabelsFor"/>
    public static IReadOnlyList<OdsTypeOption> EmailLabelsFor(ContactType type) =>
        type == ContactType.Person ? PersonEmailLabels : OrganizationEmailLabels;

    /// <inheritdoc cref="AddressLabelsFor"/>
    public static IReadOnlyList<OdsTypeOption> PhoneLabelsFor(ContactType type) =>
        type == ContactType.Person ? PersonPhoneLabels : OrganizationPhoneLabels;

    private static IReadOnlyList<OdsTypeOption> Project<TLabel>(
        IReadOnlyList<OdsTypeOption> registry, IReadOnlyList<TLabel> labels)
        where TLabel : struct, Enum =>
        [.. labels.Select(l => registry.First(t => t.Key == l.ToString()))];

    private static readonly IReadOnlyList<OdsTypeOption> PersonAddressLabels =
        Project(AddressLabels, ContactLabelScope.AddressLabelsFor(ContactType.Person));
    private static readonly IReadOnlyList<OdsTypeOption> OrganizationAddressLabels =
        Project(AddressLabels, ContactLabelScope.AddressLabelsFor(ContactType.Organization));
    private static readonly IReadOnlyList<OdsTypeOption> PersonEmailLabels =
        Project(EmailLabels, ContactLabelScope.EmailLabelsFor(ContactType.Person));
    private static readonly IReadOnlyList<OdsTypeOption> OrganizationEmailLabels =
        Project(EmailLabels, ContactLabelScope.EmailLabelsFor(ContactType.Organization));
    private static readonly IReadOnlyList<OdsTypeOption> PersonPhoneLabels =
        Project(PhoneLabels, ContactLabelScope.PhoneLabelsFor(ContactType.Person));
    private static readonly IReadOnlyList<OdsTypeOption> OrganizationPhoneLabels =
        Project(PhoneLabels, ContactLabelScope.PhoneLabelsFor(ContactType.Organization));

    /// <summary>The AccountFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption AccountFileTypeOf(AccountFileType kind) =>
        AccountFileTypes.FirstOrDefault(t => t.Key == kind.ToString()) ?? AccountFileTypes[^1];

    /// <summary>The TransactionFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption TransactionFileTypeOf(TransactionFileType kind) =>
        TransactionFileTypes.FirstOrDefault(t => t.Key == kind.ToString()) ?? TransactionFileTypes[^1];

    /// <summary>The TaxStatementFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption TaxStatementFileTypeOf(TaxStatementFileType kind) =>
        TaxStatementFileTypes.FirstOrDefault(t => t.Key == kind.ToString()) ?? TaxStatementFileTypes[^1];

    /// <summary>
    /// The single create row each tag picker offers — one per vocabulary, because a field can only
    /// ever mint the tags it searches (a journal-tag field cannot create a transaction tag). The row
    /// names what it makes: <c>Create "AA" · Transaction tag</c>.
    /// </summary>
    public static class TagCreateKinds
    {
        public static readonly IReadOnlyList<OdsCreateKind> Transaction =
            [new("transaction", "Transaction tag") { Icon = "local_offer" }];

        public static readonly IReadOnlyList<OdsCreateKind> Journal =
            [new("journal", "Journal tag") { Icon = "menu_book" }];

        public static readonly IReadOnlyList<OdsCreateKind> Task =
            [new("task", "Task tag") { Icon = "checklist" }];

        public static readonly IReadOnlyList<OdsCreateKind> Photo =
            [new("photo", "Photo tag") { Icon = "photo" }];
    }

    /// <summary>Project a registry to the inline-create rows a picker offers (Odyssey Design System ·
    /// <c>CONTACT_CREATE_KINDS</c>) — one row per member, in the registry's own order.</summary>
    public static IReadOnlyList<OdsCreateKind> ToCreateKinds(IReadOnlyList<OdsTypeOption> types) =>
        [.. types.Select(t => new OdsCreateKind(t.Key, t.Label) { Icon = t.Icon })];

    /// <summary>
    /// The create rows every contact picker offers — <b>Organization first</b>, then Person.
    ///
    /// <para>A contact linked from a transaction, a file, a contract or a statement merchant is a
    /// company far more often than a person, so the organization row leads and is what a one-click
    /// "create from the extracted name" affordance uses. The order is the design system's
    /// <c>CONTACT_CREATE_KINDS</c>, not <see cref="ContactTypes"/>'s (which reads Person first for
    /// the <i>type</i> picker on the contact record itself).</para>
    /// </summary>
    public static readonly IReadOnlyList<OdsCreateKind> ContactCreateKinds =
    [
        new("Organization", "Organization") { Icon = ContactTypeOf("Organization").Icon },
        new("Person", "Person") { Icon = ContactTypeOf("Person").Icon },
    ];

    /// <summary>Project a registry to <see cref="OdsOption"/>s carrying each member's leading glyph + color.</summary>
    public static IReadOnlyList<OdsOption> ToOptions(IReadOnlyList<OdsTypeOption> types) =>
        [.. types.Select(t => new OdsOption(t.Key, t.Label) { Icon = t.Icon, IconColor = t.Color })];

    /// <summary>Pre-built option lists for the domain pickers.</summary>
    public static readonly IReadOnlyList<OdsOption> ContactOptions = ToOptions(ContactTypes);
    // No AddressLabelOptions/EmailLabelOptions/PhoneLabelOptions: an unfiltered union of a label
    // registry is a picker that offers labels the server rejects. The per-contact-type projections
    // above supersede them (issue #47 §3).
    public static readonly IReadOnlyList<OdsOption> SexOptions =
        [new("Male", "Male") { Icon = "man" }, new("Female", "Female") { Icon = "woman" }];
    public static readonly IReadOnlyList<OdsOption> AccountFileOptions = ToOptions(AccountFileTypes);
    public static readonly IReadOnlyList<OdsOption> TransactionFileOptions = ToOptions(TransactionFileTypes);
    public static readonly IReadOnlyList<OdsOption> TaxStatementFileOptions = ToOptions(TaxStatementFileTypes);
    public static readonly IReadOnlyList<OdsOption> ContractOptions = ToOptions(ContractTypes);
    public static readonly IReadOnlyList<OdsOption> ContractFileOptions = ToOptions(ContractFileTypes);
    public static readonly IReadOnlyList<OdsOption> ContractPartyRoleOptions = ToOptions(ContractPartyRoles);
}

/// <summary>
/// The direction vocabulary for one <see cref="BudgetCategoryType"/> — the sibling of
/// <c>TermDirectionInfo</c>, for the surface where a budget line's amount carries its own direction.
/// </summary>
/// <param name="Short">The lead's short word ("out", "in").</param>
/// <param name="Tone">"income" / "expense" — the finance semantics, never the brand hues.</param>
/// <param name="Sentence">What the direction MEANS, for the field's helper line.</param>
public sealed record BudgetCategoryDirection(string Short, string Tone, string Sentence)
{
    /// <summary>The colour of this side's figures.</summary>
    public string Color => Tone == "income" ? "var(--finance-income)" : "var(--finance-expense)";
}
