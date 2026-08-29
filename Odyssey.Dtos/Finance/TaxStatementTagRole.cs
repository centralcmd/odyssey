namespace Odyssey.Dtos.Finance;

public enum TaxStatementTagRole
{
    TaxPayment = 0, // contributes to derived "advance tax paid" (within-year)
    Income = 1,     // contributes to derived "actual income"
}
