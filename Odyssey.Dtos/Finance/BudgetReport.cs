namespace Odyssey.Dtos.Finance;

public sealed record BudgetReport
{
    public List<ExistingTransaction> Transactions { get; set; } = new();
    public List<ExistingTransactionReport> ExistingTransactionReport { get; set; } = new();
    public string? CurrencyCode { get; set; }
    public int ExcludedTransactionCount { get; set; }
    public Dictionary<string, int> ExcludedCurrencies { get; set; } = new();
}