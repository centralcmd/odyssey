namespace Odyssey.Dtos.Finance;

public sealed record ExistingTransactionReport
{
    public decimal Sum { get; set; }
    public required ExistingTransactionTag ExistingTransactionTag { get; set; }
}