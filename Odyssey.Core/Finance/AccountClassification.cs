using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Finance;

/// <summary>
/// Whether an <see cref="AccountType"/> counts towards assets, towards liabilities, or towards
/// neither — the single definition shared by every surface that splits a portfolio in two.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>AccountTotalsService</c> and <c>NetWorthHistoryService</c> each declared the
/// same two range checks, and issue #90 AC2 requires the two endpoints to agree <b>exactly</b> — the
/// history's final point must equal the totals to the cent. Two copies of a range check is the one
/// place a new <see cref="AccountType"/> could break that silently: extend one range, miss the other,
/// and both files compile, both endpoints answer, and they disagree only for accounts of the new type.
/// </para>
/// <para>
/// The ranges are ordinal, not a list, so a type added <b>inside</b> either span is classified without
/// touching this file — and a type added outside both is <see cref="Unclassified"/> rather than
/// silently counted as an asset. <c>AccountType.Unknown</c> (0) sits outside both on purpose and is
/// excluded from every total.
/// </para>
/// </remarks>
public static class AccountClassification
{
    /// <summary>Asset account types — <see cref="AccountType.Cash"/> (1) through <see cref="AccountType.OtherAsset"/> (8).</summary>
    public static bool IsAsset(AccountType type) => type is >= AccountType.Cash and <= AccountType.OtherAsset;

    /// <summary>Liability account types — <see cref="AccountType.CreditCard"/> (9) through <see cref="AccountType.OtherLiability"/> (15).</summary>
    public static bool IsLiability(AccountType type) => type is >= AccountType.CreditCard and <= AccountType.OtherLiability;

    /// <summary>
    /// Neither an asset nor a liability, so it contributes to no total. Today that is only
    /// <see cref="AccountType.Unknown"/>; it is expressed as the negation of the two spans rather than
    /// as an equality check so a future out-of-range member is excluded by default rather than
    /// miscounted.
    /// </summary>
    public static bool Unclassified(AccountType type) => !IsAsset(type) && !IsLiability(type);
}
