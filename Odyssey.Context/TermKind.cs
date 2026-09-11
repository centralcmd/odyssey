namespace Odyssey.Context;

public enum TermKind
{
    Unknown = 0,

    // ---- Rates ----
    InterestRate = 1,   // contractual interest (percentage)
    ExpectedReturn = 2, // optional, informational target/expected annual return (percentage)

    // ---- Fees ----
    // One kind, deliberately. The former ManagementFee/ServiceFee/TransactionFee/OtherFee carried no
    // behaviour between them — all four were eligible on every account type and differed only by a
    // label, an icon and a default billing period — so once a term could carry a user-authored Label
    // the enum was duplicating what the label says better. It keeps ordinal 10, the head of the old
    // fee band; 11, 12 and 99 were remapped onto it by AddAccountTermLabel's successor migration.
    Fee = 10,
}
