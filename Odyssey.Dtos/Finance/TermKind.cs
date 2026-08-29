namespace Odyssey.Dtos.Finance;

public enum TermKind
{
    Unknown = 0,

    // ---- Rates ----
    InterestRate = 1,
    ExpectedReturn = 2,

    // ---- Fees ----
    ManagementFee = 10,
    ServiceFee = 11,
    TransactionFee = 12,
    OtherFee = 99,
}
