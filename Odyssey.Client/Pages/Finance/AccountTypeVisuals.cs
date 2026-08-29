using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>An account type's high-level grouping in the design-system registry.</summary>
public enum AccountGroup
{
    Asset,
    Liability,
}

/// <summary>
/// Single source of truth for how each <see cref="AccountType"/> renders in the UI:
/// human label, Material Icons ligature, asset/liability grouping, and the design-system
/// color tokens for its icon badge (foreground glyph + soft tinted background). Mirrors the
/// canonical account-type registry in the Odyssey Design System. Shared by the accounts
/// list, the type picker in the create/edit dialogs, and anywhere a type badge appears.
/// </summary>
public static class AccountTypeVisuals
{
    /// <summary>All selectable account types (everything except <see cref="AccountType.Unknown"/>),
    /// ordered assets-first then liabilities to mirror the design-system registry.</summary>
    public static readonly AccountType[] Selectable =
        Enum.GetValues<AccountType>().Where(t => t != AccountType.Unknown).ToArray();

    /// <summary>Selectable asset types, in registry order.</summary>
    public static readonly AccountType[] Assets =
        Selectable.Where(t => Group(t) == AccountGroup.Asset).ToArray();

    /// <summary>Selectable liability types, in registry order.</summary>
    public static readonly AccountType[] Liabilities =
        Selectable.Where(t => Group(t) == AccountGroup.Liability).ToArray();

    /// <summary>Whether a type counts toward assets or liabilities.</summary>
    public static AccountGroup Group(AccountType type) => type switch
    {
        AccountType.CreditCard or
        AccountType.Mortgage or
        AccountType.StudentLoan or
        AccountType.PersonalLoan or
        AccountType.CarLoan or
        AccountType.TaxDebt or
        AccountType.OtherLiability => AccountGroup.Liability,
        _ => AccountGroup.Asset,
    };

    public static string Label(AccountType type) => type switch
    {
        // Assets
        AccountType.Cash              => "Cash",
        AccountType.CheckingAccount   => "Checking",
        AccountType.SavingsAccount    => "Savings",
        AccountType.InvestmentAccount => "Investment",
        AccountType.PensionAccount    => "Pension",
        AccountType.Property          => "Property",
        AccountType.Vehicle           => "Vehicle",
        AccountType.OtherAsset        => "Other asset",
        // Liabilities
        AccountType.CreditCard        => "Credit card",
        AccountType.Mortgage          => "Mortgage",
        AccountType.StudentLoan       => "Student loan",
        AccountType.PersonalLoan      => "Personal loan",
        AccountType.CarLoan           => "Car loan",
        AccountType.TaxDebt           => "Tax debt",
        AccountType.OtherLiability    => "Other liability",
        _ => "Unknown",
    };

    /// <summary>Raw Material Icons ligature for use in a <c>&lt;span class="material-icons"&gt;</c>.</summary>
    public static string MaterialIcon(AccountType type) => type switch
    {
        // Assets
        AccountType.Cash              => "payments",
        AccountType.CheckingAccount   => "account_balance",
        AccountType.SavingsAccount    => "savings",
        AccountType.InvestmentAccount => "trending_up",
        AccountType.PensionAccount    => "elderly",
        AccountType.Property          => "home",
        AccountType.Vehicle           => "directions_car",
        AccountType.OtherAsset        => "category",
        // Liabilities
        AccountType.CreditCard        => "credit_card",
        AccountType.Mortgage          => "home_work",
        AccountType.StudentLoan       => "school",
        AccountType.PersonalLoan      => "account_balance_wallet",
        AccountType.CarLoan           => "directions_car",
        AccountType.TaxDebt           => "receipt_long",
        AccountType.OtherLiability    => "category",
        _ => "help",
    };

    /// <summary>Foreground glyph color — a design-system CSS variable valid in both themes.</summary>
    public static string FgColor(AccountType type) => type switch
    {
        // Assets
        AccountType.Cash              => "var(--acct-cash)",
        AccountType.CheckingAccount   => "var(--acct-checking)",
        AccountType.SavingsAccount    => "var(--acct-savings)",
        AccountType.InvestmentAccount => "var(--acct-investment)",
        AccountType.PensionAccount    => "var(--acct-pension)",
        AccountType.Property          => "var(--acct-property)",
        AccountType.Vehicle           => "var(--acct-vehicle)",
        AccountType.OtherAsset        => "var(--acct-other-asset)",
        // Liabilities
        AccountType.CreditCard        => "var(--acct-credit)",
        AccountType.Mortgage          => "var(--acct-mortgage)",
        AccountType.StudentLoan       => "var(--acct-student-loan)",
        AccountType.PersonalLoan      => "var(--acct-personal-loan)",
        AccountType.CarLoan           => "var(--acct-car-loan)",
        AccountType.TaxDebt           => "var(--acct-tax-debt)",
        AccountType.OtherLiability    => "var(--acct-other-liability)",
        _ => "var(--mud-palette-text-secondary)",
    };

    /// <summary>Soft tinted badge background — a design-system CSS variable valid in both themes.</summary>
    public static string BgColor(AccountType type) => type switch
    {
        // Assets
        AccountType.Cash              => "var(--acct-cash-soft)",
        AccountType.CheckingAccount   => "var(--acct-checking-soft)",
        AccountType.SavingsAccount    => "var(--acct-savings-soft)",
        AccountType.InvestmentAccount => "var(--acct-investment-soft)",
        AccountType.PensionAccount    => "var(--acct-pension-soft)",
        AccountType.Property          => "var(--acct-property-soft)",
        AccountType.Vehicle           => "var(--acct-vehicle-soft)",
        AccountType.OtherAsset        => "var(--acct-other-asset-soft)",
        // Liabilities
        AccountType.CreditCard        => "var(--acct-credit-soft)",
        AccountType.Mortgage          => "var(--acct-mortgage-soft)",
        AccountType.StudentLoan       => "var(--acct-student-loan-soft)",
        AccountType.PersonalLoan      => "var(--acct-personal-loan-soft)",
        AccountType.CarLoan           => "var(--acct-car-loan-soft)",
        AccountType.TaxDebt           => "var(--acct-tax-debt-soft)",
        AccountType.OtherLiability    => "var(--acct-other-liability-soft)",
        _ => "var(--mud-palette-action-disabled-background)",
    };
}
