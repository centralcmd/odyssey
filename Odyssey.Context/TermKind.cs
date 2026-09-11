namespace Odyssey.Context;

/// <summary>
/// What an <see cref="AccountTerm"/> prices. THREE values, not a taxonomy: a kind earns an enum value
/// when the application treats it differently — eligibility, ordering, or a headline surface that has
/// to pick it out. The two rates qualify (the account row headlines the rate, the step chart picks its
/// series, and the cost-rate tint keys off <see cref="InterestRate"/>); a fee's category does not, and
/// is carried by the term's own <see cref="AccountTerm.Label"/> instead.
/// </summary>
public enum TermKind
{
    Unknown = 0,

    // ---- Rates: distinct quoted numbers some surface must single out, so neither can be a label ----
    InterestRate = 1,   // contractual interest (percentage)
    ExpectedReturn = 2, // optional, informational target/expected annual return (percentage)

    // ---- Fee: one kind, always named by its Label. Keeps ordinal 10, the head of the former fee
    //      band (ManagementFee), so rows written before the collapse need no remap. ----
    Fee = 10,
}
