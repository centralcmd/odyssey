namespace Odyssey.Dtos.Finance;

/// <summary>Contract counts bucketed by derived status (issue #174 §7).</summary>
public sealed record ContractStatusCounts
{
    public int Active { get; set; }

    public int Upcoming { get; set; }

    public int Expired { get; set; }

    public int Archived { get; set; }

    /// <summary>
    /// A SLICE of <see cref="Active"/>, never a fifth status: the Active contracts whose end date
    /// falls inside <see cref="ContractSummary.EndingWindowDays"/>. It is reported beside the four
    /// because the cliff is the thing a reader acts on, but it is already counted in
    /// <see cref="Active"/> — adding it to the four would double-count the set.
    /// </summary>
    public int EndingSoon { get; set; }
}

/// <summary>A typed count used by the contract summary rollup.</summary>
public sealed record ContractTypeCount
{
    public ContractType Type { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// One contract type's share of the run rate, in <see cref="ContractRunRate.BaseCurrency"/>. Present
/// only for the types that actually carry an in-force periodic fee, so an absent type means "no
/// recurring price on file", never "zero".
/// </summary>
public sealed record ContractRunRateTypeRow
{
    public ContractType Type { get; set; }

    public decimal Monthly { get; set; }

    public decimal Yearly { get; set; }

    /// <summary>In-force fee TERMS behind the figure, not contracts — one contract can price several.</summary>
    public int Count { get; set; }
}

/// <summary>
/// What the agreements on file cost to run: the in-force <c>Fee</c>/<c>Amount</c> terms of the
/// <b>Active</b> contracts, each projected by its cadence (<c>Value ÷ IntervalCount × periods</c>) and
/// converted to <see cref="BaseCurrency"/>.
///
/// <para>
/// One-time and per-occurrence fees are excluded <i>by construction</i> — they name an occasion rather
/// than a rhythm, so there is no rate to project — and a rate term (a percentage) carries no amount at
/// all. A currency with no rate to base is named in <see cref="UnconvertedCurrencies"/> rather than
/// silently folded in at 1:1, so a partial total is always legible as one.
/// </para>
/// </summary>
public sealed record ContractRunRate
{
    public required string BaseCurrency { get; set; }

    /// <summary>Converted monthly total; null when nothing convertible carries a rate.</summary>
    public decimal? Monthly { get; set; }

    /// <summary>Converted yearly total; null when nothing convertible carries a rate.</summary>
    public decimal? Yearly { get; set; }

    /// <summary>The same read split by contract type, in enum order.</summary>
    public List<ContractRunRateTypeRow> ByType { get; set; } = new();

    /// <summary>Currencies present in the run rate with no rate to <see cref="BaseCurrency"/>.</summary>
    public List<string> UnconvertedCurrencies { get; set; } = new();
}

/// <summary>
/// A derived next charge: the soonest occurrence of one of a contract's in-force periodic fee terms,
/// projected from that term's cadence anchor (<c>AnchorDate</c>, else <c>EffectiveFrom</c>) and never
/// past the contract's own end date.
///
/// <para>
/// <b>Nothing is scheduled or stored.</b> This is the term history read forward, the way
/// <c>SubscriptionRenewal</c> reads a billing interval forward — the anchor is never advanced.
/// </para>
/// </summary>
public sealed record ContractUpcomingCharge
{
    public required Guid ContractId { get; set; }

    public required string Name { get; set; }

    public ContractType Type { get; set; }

    /// <summary>The fee series' own name, as the user wrote it.</summary>
    public string? Label { get; set; }

    public decimal Amount { get; set; }

    public required string CurrencyCode { get; set; }

    public Interval Interval { get; set; }

    public int IntervalCount { get; set; }

    public required DateTime ChargeDate { get; set; }

    /// <summary>Whole days from today to <see cref="ChargeDate"/> (0 = today).</summary>
    public int DaysUntil { get; set; }
}

/// <summary>
/// Summary rollup for the contracts page header (issue #174 §7): totals plus counts by status and by
/// type, the recurring-cost run rate, and the derived upcoming charges. Archived contracts are counted
/// in <see cref="CountsByStatus"/> but excluded from the active totals, the by-type breakdown, the run
/// rate and the charges.
/// </summary>
public sealed record ContractSummary
{
    public int TotalContracts { get; set; }

    public required ContractStatusCounts CountsByStatus { get; set; }

    public List<ContractTypeCount> CountsByType { get; set; } = new();

    public required ContractRunRate RunRate { get; set; }

    public List<ContractUpcomingCharge> UpcomingCharges { get; set; } = new();

    /// <summary>
    /// The effective "ending soon" window, in days. Served rather than held as a client constant: the
    /// value is admin-editable, and a page holding its own copy would label a row with one number
    /// while the server counted by another.
    /// </summary>
    public int EndingWindowDays { get; set; }

    /// <summary>The effective look-ahead behind <see cref="UpcomingCharges"/>, in days.</summary>
    public int ChargeWindowDays { get; set; }
}
