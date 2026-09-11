namespace Odyssey.Dtos.Finance;

public enum TermKind
{
    Unknown = 0,

    // ---- Rates ----
    InterestRate = 1,
    ExpectedReturn = 2,

    // ---- Fees ----
    // One kind: the label names the fee, so the old four-way split said nothing the label does not.
    Fee = 10,
}
