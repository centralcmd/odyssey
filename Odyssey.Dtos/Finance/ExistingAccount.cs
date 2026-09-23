using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingAccount
{
    public required Guid AccountId { get; set; }
    public required string Description { get; set; }
    public required string Name { get; set; }
    public string? AccountNumber { get; set; }
    public AccountType AccountType { get; set; }
    public required DateTime Opened { get; set; }
    public DateTime? Closed { get; set; }
    public DateTime? Archived { get; set; }

    [StringLength(3)]
    public string CurrencyCode { get; set; } = "USD";

    /// <summary>Number of transactions recorded against this account (computed server-side).</summary>
    public int TransactionCount { get; set; }

    /// <summary>Number of files attached to this account (computed server-side).</summary>
    public int FileCount { get; set; }

    /// <summary>Number of value-estimate entries recorded for this account (computed server-side).</summary>
    public int EstimateCount { get; set; }

    /// <summary>Number of term (rate/fee) entries recorded for this account (computed server-side).</summary>
    public int TermCount { get; set; }

    /// <summary>Number of smart tags configured on this account (computed server-side).</summary>
    public int SmartTagCount { get; set; }

    /// <summary>Sum of this account's signed transaction amounts (computed server-side).</summary>
    public decimal Balance { get; set; }

    /// <summary>The currently-effective estimated value for this account (e.g. a property's appraised
    /// worth), or <c>null</c> when no estimate is in force. Computed server-side from the account's
    /// estimate history — the latest entry on or before now. Always expressed in the account currency
    /// (<see cref="CurrentEstimatedValueCurrencyCode"/>).</summary>
    public decimal? CurrentEstimatedValue { get; set; }

    /// <summary>The currency of <see cref="CurrentEstimatedValue"/> (always the account currency);
    /// <c>null</c> when there is no estimate in force.</summary>
    [StringLength(3)]
    public string? CurrentEstimatedValueCurrencyCode { get; set; }

    /// <summary>When the current estimate took effect — the "in force since" on the card's Current band.</summary>
    public DateTime? CurrentEstimatedValueEffectiveFrom { get; set; }

    /// <summary>
    /// Every in-force term, one per series, for the record card's "Current" band.
    ///
    /// <para>
    /// It costs no extra query: the enrichment runs one pass over the term composite index for every
    /// account on the page rather than a per-account follow-up.
    /// </para>
    /// </summary>
    public List<AccountCurrentTerm> CurrentTerms { get; set; } = new();

    /// <summary>The raw link to the contact that holds this account (its custodian), or
    /// <c>null</c> when there is no custodian.</summary>
    public Guid? CustodianId { get; set; }

    /// <summary>The resolved custodian contact (a slim, description-free projection), or
    /// <c>null</c> when there is no custodian. Computed server-side via an explicit projection — the
    /// entity navigation is never auto-mapped onto this property (see MapsterConfig).</summary>
    public Custodian? Custodian { get; set; }
}
