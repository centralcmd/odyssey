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
        new() { Key = "Home",    Label = "Home",    Icon = "home",         Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Work",    Label = "Work",    Icon = "work",         Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Billing", Label = "Billing", Icon = "receipt_long", Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Other",   Label = "Other",   Icon = "category",     Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>EmailLabel — Home · Work · Other (issue #325 v4). Mirrors the DS EMAIL_LABELS.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> EmailLabels =
    [
        new() { Key = "Home",  Label = "Home",  Icon = "home",     Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Work",  Label = "Work",  Icon = "work",     Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Other", Label = "Other", Icon = "category", Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
    ];

    /// <summary>PhoneLabel — Home · Work · Mobile · Other (issue #325 v4). Mirrors the DS PHONE_LABELS.</summary>
    public static readonly IReadOnlyList<OdsTypeOption> PhoneLabels =
    [
        new() { Key = "Home",   Label = "Home",   Icon = "home",       Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Work",   Label = "Work",   Icon = "work",       Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Mobile", Label = "Mobile", Icon = "smartphone", Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
        new() { Key = "Other",  Label = "Other",  Icon = "category",   Color = "oklch(0.74 0.02 250)", Soft = "oklch(0.74 0.02 250 / 0.16)" },
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

    /// <summary>The AddressLabel descriptor for an enum key (falls back to "Other").</summary>
    public static OdsTypeOption AddressLabelOf(string? key) =>
        AddressLabels.FirstOrDefault(t => t.Key == key) ?? AddressLabels[^1];

    /// <summary>The EmailLabel descriptor for an enum key (falls back to "Other").</summary>
    public static OdsTypeOption EmailLabelOf(string? key) =>
        EmailLabels.FirstOrDefault(t => t.Key == key) ?? EmailLabels[^1];

    /// <summary>The PhoneLabel descriptor for an enum key (falls back to "Other").</summary>
    public static OdsTypeOption PhoneLabelOf(string? key) =>
        PhoneLabels.FirstOrDefault(t => t.Key == key) ?? PhoneLabels[^1];

    /// <summary>The AccountFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption AccountFileTypeOf(AccountFileType kind) =>
        AccountFileTypes.FirstOrDefault(t => t.Key == kind.ToString()) ?? AccountFileTypes[^1];

    /// <summary>The TransactionFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption TransactionFileTypeOf(TransactionFileType kind) =>
        TransactionFileTypes.FirstOrDefault(t => t.Key == kind.ToString()) ?? TransactionFileTypes[^1];

    /// <summary>The TaxStatementFileType descriptor for an enum value (falls back to "Other").</summary>
    public static OdsTypeOption TaxStatementFileTypeOf(TaxStatementFileType kind) =>
        TaxStatementFileTypes.FirstOrDefault(t => t.Key == kind.ToString()) ?? TaxStatementFileTypes[^1];

    /// <summary>Project a registry to <see cref="OdsOption"/>s carrying each member's leading glyph + color.</summary>
    public static IReadOnlyList<OdsOption> ToOptions(IReadOnlyList<OdsTypeOption> types) =>
        [.. types.Select(t => new OdsOption(t.Key, t.Label) { Icon = t.Icon, IconColor = t.Color })];

    /// <summary>Pre-built option lists for the domain pickers.</summary>
    public static readonly IReadOnlyList<OdsOption> ContactOptions = ToOptions(ContactTypes);
    public static readonly IReadOnlyList<OdsOption> RelationshipOptions = ToOptions(RelationshipTypes);
    public static readonly IReadOnlyList<OdsOption> AddressLabelOptions = ToOptions(AddressLabels);
    public static readonly IReadOnlyList<OdsOption> EmailLabelOptions = ToOptions(EmailLabels);
    public static readonly IReadOnlyList<OdsOption> PhoneLabelOptions = ToOptions(PhoneLabels);
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
