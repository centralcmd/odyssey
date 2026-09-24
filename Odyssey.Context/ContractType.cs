namespace Odyssey.Context;

/// <summary>
/// What kind of agreement a contract records. <b>Ordinals are a wire and persistence contract and are
/// never renumbered</b>: the four members added after the original set append at 4–7 and
/// <see cref="Loan"/> and <see cref="Deposit"/> append at 8 and 9, so <see cref="Other"/> keeps ordinal 3 even though it reads last
/// everywhere a human sees the list. The reading order lives in the client's
/// <c>OdsTypeRegistries.ContractTypes</c>, not here.
/// </summary>
public enum ContractType
{
    Employment = 0,
    Service = 1,
    Rental = 2,
    Other = 3,
    Insurance = 4,
    Subscription = 5,
    Purchase = 6,
    Membership = 7,

    /// <summary>
    /// A loan or mortgage — money advanced under an agreement to repay (issue #157 §4.1). Appended, and
    /// read between <see cref="Purchase"/> and <see cref="Membership"/>: before this member existed a
    /// mortgage was filed as a <see cref="Purchase"/> with a Buyer and a Seller.
    /// </summary>
    Loan = 8,

    /// <summary>
    /// A deposit — money placed with another party under an agreement to have it returned: a
    /// fixed-term bank deposit, a notice account agreement, a rental deposit held by a landlord
    /// (issue #187). The mirror of <see cref="Loan"/>, and read directly after it; before this member
    /// existed such an agreement was filed as <see cref="Other"/>, or as a <see cref="Loan"/> with its
    /// roles reversed.
    /// </summary>
    Deposit = 9,
}
