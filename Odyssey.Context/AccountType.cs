namespace Odyssey.Context;

public enum AccountType
{
    Unknown = 0,

    // ---- Assets ----
    Cash = 1,
    CheckingAccount = 2,
    SavingsAccount = 3,
    InvestmentAccount = 4,
    PensionAccount = 5,
    Property = 6,
    Vehicle = 7,
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
