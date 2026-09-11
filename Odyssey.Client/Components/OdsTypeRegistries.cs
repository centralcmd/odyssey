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

    /// <summary>RelationshipType — a person contact's relationship to the user (issue #325).</summary>
    public static readonly IReadOnlyList<OdsTypeOption> RelationshipTypes =
    [
        new() { Key = "Family",   Label = "Family",   Icon = "family_restroom", Color = "oklch(0.80 0.15 150)", Soft = "oklch(0.80 0.15 150 / 0.16)" },
        new() { Key = "Landlord", Label = "Landlord", Icon = "home",            Color = "oklch(0.77 0.14 55)",  Soft = "oklch(0.77 0.14 55 / 0.16)" },
        new() { Key = "Employer", Label = "Employer", Icon = "work",            Color = "oklch(0.76 0.13 225)", Soft = "oklch(0.76 0.13 225 / 0.16)" },
        new() { Key = "Other",    Label = "Other",    Icon = "category",        Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
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

    /// <summary>InsurancePolicyType — Home · Contents · Building · Vehicle · Travel · Life · Health ·
    /// Accident · Liability · Pet · Property · Other (issue #175). Mirrors the DS INSURANCE_POLICY_TYPES.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> InsurancePolicyTypes =
    [
        new() { Key = "Home",      Label = "Home",      Icon = "house",             Color = "oklch(0.72 0.14 255)", Soft = "oklch(0.72 0.14 255 / 0.16)" },
        new() { Key = "Contents",  Label = "Contents",  Icon = "chair",             Color = "oklch(0.72 0.16 295)", Soft = "oklch(0.72 0.16 295 / 0.16)" },
        new() { Key = "Building",  Label = "Building",  Icon = "apartment",         Color = "oklch(0.76 0.13 225)", Soft = "oklch(0.76 0.13 225 / 0.16)" },
        new() { Key = "Vehicle",   Label = "Vehicle",   Icon = "directions_car",    Color = "oklch(0.78 0.14 170)", Soft = "oklch(0.78 0.14 170 / 0.16)" },
        new() { Key = "Travel",    Label = "Travel",    Icon = "flight",            Color = "oklch(0.77 0.13 205)", Soft = "oklch(0.77 0.13 205 / 0.16)" },
        new() { Key = "Life",      Label = "Life",      Icon = "favorite",          Color = "oklch(0.72 0.16 8)",   Soft = "oklch(0.72 0.16 8 / 0.16)" },
        new() { Key = "Health",    Label = "Health",    Icon = "health_and_safety", Color = "oklch(0.80 0.15 150)", Soft = "oklch(0.80 0.15 150 / 0.16)" },
        new() { Key = "Accident",  Label = "Accident",  Icon = "personal_injury",   Color = "oklch(0.79 0.14 60)",  Soft = "oklch(0.79 0.14 60 / 0.16)" },
        new() { Key = "Liability", Label = "Liability", Icon = "gavel",             Color = "oklch(0.72 0.15 265)", Soft = "oklch(0.72 0.15 265 / 0.16)" },
        new() { Key = "Pet",       Label = "Pet",       Icon = "pets",              Color = "oklch(0.79 0.14 78)",  Soft = "oklch(0.79 0.14 78 / 0.16)" },
        new() { Key = "Property",  Label = "Property",  Icon = "home_work",         Color = "oklch(0.75 0.16 330)", Soft = "oklch(0.75 0.16 330 / 0.16)" },
        new() { Key = "Other",     Label = "Other",     Icon = "shield",            Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>PolicyFileType — the kind of document attached to an insurance policy or renewal
    /// (issue #175). Mirrors the DS POLICY_FILE_TYPES and the C# PolicyFileType enum.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> PolicyFileTypes =
    [
        new() { Key = "Contract",           Label = "Contract",           Icon = "history_edu",       Color = "oklch(0.72 0.16 295)", Soft = "oklch(0.72 0.16 295 / 0.16)" },
        new() { Key = "Invoice",            Label = "Invoice",            Icon = "receipt",           Color = "oklch(0.80 0.13 85)",  Soft = "oklch(0.80 0.13 85 / 0.16)" },
        new() { Key = "TermsAndConditions", Label = "Terms & conditions", Icon = "menu_book",         Color = "oklch(0.77 0.14 110)", Soft = "oklch(0.77 0.14 110 / 0.16)" },
        new() { Key = "PolicyDocument",     Label = "Policy document",    Icon = "shield",            Color = "oklch(0.72 0.16 282)", Soft = "oklch(0.72 0.16 282 / 0.16)" },
        new() { Key = "ClaimDocument",      Label = "Claim document",     Icon = "assignment_late",   Color = "oklch(0.72 0.16 22)",  Soft = "oklch(0.72 0.16 22 / 0.16)" },
        new() { Key = "Other",              Label = "Other",              Icon = "insert_drive_file", Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>ContractType — Employment · Service · Rental · Other (issue #174). Mirrors the DS
    /// contractTypes registry and the C# ContractType enum.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> ContractTypes =
    [
        new() { Key = "Employment", Label = "Employment", Icon = "work",                Color = "oklch(0.76 0.13 225)", Soft = "oklch(0.76 0.13 225 / 0.16)" },
        new() { Key = "Service",    Label = "Service",    Icon = "home_repair_service", Color = "oklch(0.78 0.14 170)", Soft = "oklch(0.78 0.14 170 / 0.16)" },
        new() { Key = "Rental",     Label = "Rental",     Icon = "cottage",             Color = "oklch(0.79 0.14 60)",  Soft = "oklch(0.79 0.14 60 / 0.16)" },
        new() { Key = "Other",      Label = "Other",      Icon = "description",         Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
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

    /// <summary>BillingInterval — Daily · Weekly · Monthly · Yearly (issue #293). Mirrors the DS
    /// BILLING_INTERVALS registry and the C# BillingInterval enum. Order is the enum's numeric order
    /// (Daily &lt; Weekly &lt; Monthly &lt; Yearly), which is also how the list sorts by "Frequency".</summary>
    public static readonly IReadOnlyList<OdsTypeOption> BillingIntervals =
    [
        new() { Key = "Daily",   Label = "Daily",   Icon = "today",          Color = "oklch(0.79 0.13 205)", Soft = "oklch(0.79 0.13 205 / 0.16)" },
        new() { Key = "Weekly",  Label = "Weekly",  Icon = "view_week",      Color = "oklch(0.78 0.14 168)", Soft = "oklch(0.78 0.14 168 / 0.16)" },
        new() { Key = "Monthly", Label = "Monthly", Icon = "calendar_month", Color = "oklch(0.72 0.14 255)", Soft = "oklch(0.72 0.14 255 / 0.16)" },
        new() { Key = "Yearly",  Label = "Yearly",  Icon = "event_repeat",   Color = "oklch(0.72 0.16 295)", Soft = "oklch(0.72 0.16 295 / 0.16)" },
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

    /// <summary>The InsurancePolicyType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption InsurancePolicyTypeOf(InsurancePolicyType type) =>
        InsurancePolicyTypes.FirstOrDefault(t => t.Key == type.ToString()) ?? InsurancePolicyTypes[^1];

    /// <summary>The BillingInterval descriptor for an enum value (falls back to "Monthly").</summary>
    public static OdsTypeOption BillingIntervalOf(BillingInterval interval) =>
        BillingIntervals.FirstOrDefault(t => t.Key == interval.ToString()) ?? BillingIntervals[2];

    /// <summary>The ContractType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption ContractTypeOf(ContractType type) =>
        ContractTypes.FirstOrDefault(t => t.Key == type.ToString()) ?? ContractTypes[^1];

    /// <summary>The ContractFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption ContractFileTypeOf(ContractFileType type) =>
        ContractFileTypes.FirstOrDefault(t => t.Key == type.ToString()) ?? ContractFileTypes[^1];

    /// <summary>The PolicyFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption PolicyFileTypeOf(PolicyFileType type) =>
        PolicyFileTypes.FirstOrDefault(t => t.Key == type.ToString()) ?? PolicyFileTypes[^1];

    /// <summary>The ContactType descriptor for an enum key (falls back to "Organization").</summary>
    public static OdsTypeOption ContactTypeOf(string? key) =>
        ContactTypes.FirstOrDefault(t => t.Key == key) ?? ContactTypes[^1];

    /// <summary>The RelationshipType descriptor for an enum key (falls back to "Other").</summary>
    public static OdsTypeOption RelationshipTypeOf(string? key) =>
        RelationshipTypes.FirstOrDefault(t => t.Key == key) ?? RelationshipTypes[^1];

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
    /// <para>A contact linked from a transaction, a file, a subscription or a statement merchant is a
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
    public static readonly IReadOnlyList<OdsOption> RelationshipOptions = ToOptions(RelationshipTypes);
    // No AddressLabelOptions/EmailLabelOptions/PhoneLabelOptions: an unfiltered union of a label
    // registry is a picker that offers labels the server rejects. The per-contact-type projections
    // above supersede them (issue #47 §3).
    public static readonly IReadOnlyList<OdsOption> SexOptions = [new("Male", "Male"), new("Female", "Female")];
    public static readonly IReadOnlyList<OdsOption> AccountFileOptions = ToOptions(AccountFileTypes);
    public static readonly IReadOnlyList<OdsOption> TransactionFileOptions = ToOptions(TransactionFileTypes);
    public static readonly IReadOnlyList<OdsOption> TaxStatementFileOptions = ToOptions(TaxStatementFileTypes);
    public static readonly IReadOnlyList<OdsOption> InsurancePolicyOptions = ToOptions(InsurancePolicyTypes);
    public static readonly IReadOnlyList<OdsOption> PolicyFileOptions = ToOptions(PolicyFileTypes);
    public static readonly IReadOnlyList<OdsOption> ContractOptions = ToOptions(ContractTypes);
    public static readonly IReadOnlyList<OdsOption> ContractFileOptions = ToOptions(ContractFileTypes);
    public static readonly IReadOnlyList<OdsOption> BillingIntervalOptions = ToOptions(BillingIntervals);
}
