using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Total assets, total liabilities and net worth converted into the user's main currency,
/// plus the accounts whose balance could not be converted (no rate to the main currency).
/// </summary>
public sealed record AccountTotals
{
    [StringLength(3)]
    public required string MainCurrencyCode { get; set; }

    public required decimal TotalAssets { get; set; }

    public required decimal TotalLiabilities { get; set; }

    public required decimal NetWorth { get; set; }

    public List<UnconvertedAccount> UnconvertedAccounts { get; set; } = [];
}

/// <summary>An account that contributed 0 to the totals because no rate to the main currency exists.</summary>
public sealed record UnconvertedAccount
{
    public required Guid AccountId { get; set; }

    public required string Name { get; set; }

    [StringLength(3)]
    public required string CurrencyCode { get; set; }
}
