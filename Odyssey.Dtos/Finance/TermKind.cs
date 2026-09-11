namespace Odyssey.Dtos.Finance;

/// <summary>
/// What an account term prices. A fee's category is carried by the term's <c>Label</c>, not by an
/// enum value — only the two rate kinds are treated differently by the application.
/// </summary>
public enum TermKind
{
    Unknown = 0,

    // ---- Rates ----
    InterestRate = 1,
    ExpectedReturn = 2,

    // ---- Fee: keeps ordinal 10 (the former ManagementFee) so no persisted value shifts meaning ----
    Fee = 10,
}
