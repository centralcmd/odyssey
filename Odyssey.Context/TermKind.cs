namespace Odyssey.Context;

public enum TermKind
{
    Unknown = 0,

    // ---- Rates ----
    InterestRate = 1,   // contractual interest (percentage)
    ExpectedReturn = 2, // optional, informational target/expected annual return (percentage)

    // ---- Fees ----
    ManagementFee = 10,  // fund/platform/management fee (typically percentage)
    ServiceFee = 11,     // periodic account/service fee (typically amount)
    TransactionFee = 12, // per-transaction fee (amount or percentage)
    OtherFee = 99,
}
