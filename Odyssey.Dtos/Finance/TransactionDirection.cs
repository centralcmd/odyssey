namespace Odyssey.Dtos.Finance;

/// <summary>
/// List-filter direction for transactions, derived at query time from the signed amount:
/// <see cref="Income"/> (amount &gt;= 0) or <see cref="Expense"/> (amount &lt; 0).
/// </summary>
public enum TransactionDirection
{
    Income,
    Expense,
}
