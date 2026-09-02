namespace Odyssey.Dtos.Finance;

/// <summary>
/// Minimal, data-minimised projection of the insured account exposed through the insurance read path
/// (issue #175 §10 #4). Deliberately drops balances and notes so a caller holding only
/// <c>insurance.read</c> cannot read the richer record gated by <c>accounts.read</c>.
/// </summary>
public sealed record InsuredAccountReference
{
    public required Guid AccountId { get; set; }

    public required string Name { get; set; }

    public AccountType Type { get; set; }

    /// <inheritdoc cref="PolicyContactReference.FromDate" />
    public DateTime? FromDate { get; set; }

    /// <inheritdoc cref="PolicyContactReference.ToDate" />
    public DateTime? ToDate { get; set; }
}
