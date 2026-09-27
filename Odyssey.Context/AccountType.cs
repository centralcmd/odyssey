namespace Odyssey.Context;

/// <summary>
/// The kind of an account. Ordinals are a persistence and wire contract and are never renumbered.
///
/// <para>
/// <b>6 and 7 are permanent holes.</b> They were <c>Property</c> and <c>Vehicle</c>, retired when every
/// such account moved onto a <c>Property</c> record (issue #218). Reusing either would make an
/// unmigrated row mean something new; <c>CK_Accounts_AccountTypeNotRetired</c> forbids both in the
/// database, and <c>[EnumDataType]</c> rejects them on the wire.
/// </para>
/// </summary>
public enum AccountType
{
    Unknown = 0,

    // ---- Assets ----
    Cash = 1,
    CheckingAccount = 2,
    SavingsAccount = 3,
    InvestmentAccount = 4,
    PensionAccount = 5,
    // 6 (Property) and 7 (Vehicle) are retired — permanent holes, never reused.
    OtherAsset = 8,

    // ---- Liabilities ----
    CreditCard = 9,
    Mortgage = 10,
    StudentLoan = 11,
    PersonalLoan = 12,
    CarLoan = 13,
    TaxDebt = 14,
    OtherLiability = 15,
}
