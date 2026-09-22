namespace Odyssey.Dtos.Finance;

/// <summary>Contract counts bucketed by derived status (issue #174 §7).</summary>
public sealed record ContractStatusCounts
{
    public int Active { get; set; }

    public int Upcoming { get; set; }

    public int Expired { get; set; }

    public int Archived { get; set; }

    /// <summary>
    /// A real bucket (issue #140), unlike <see cref="EndingSoon"/>: the seven are mutually exclusive
    /// derived statuses and still sum to <see cref="ContractSummary.TotalContracts"/>.
    /// </summary>
    public int Paused { get; set; }

    /// <summary>
    /// Recorded, not yet marked ready for signature (issue #145). A real bucket like the five before
    /// it: on file but not in force, so it is excluded from the run rate and the upcoming charges and
    /// <b>included</b> in <see cref="ContractSummary.CountsByType"/> — that is a headcount of the
    /// agreements on file, not a cost split, and a draft is still a contract of its type. The same
    /// deliberate divergence <see cref="Paused"/> already has.
    /// </summary>
    public int Draft { get; set; }

    /// <summary>
    /// Marked ready for signature and not yet signed (issue #145). Counted and excluded on exactly
    /// the same terms as <see cref="Draft"/>.
    /// </summary>
    public int Ready { get; set; }

    /// <summary>
    /// A SLICE of <see cref="Active"/>, never an eighth status: the Active contracts whose end date
    /// falls inside <see cref="ContractSummary.EndingWindowDays"/>. It is reported beside the seven
    /// because the cliff is the thing a reader acts on, but it is already counted in
    /// <see cref="Active"/> — adding it to the seven would double-count the set.
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
/// What the agreements on file cost to run and what they bring in: the in-force <c>Fee</c>/<c>Amount</c>
/// terms of the <b>Active</b> contracts, each projected by its cadence
/// (<c>Value ÷ IntervalCount × periods</c>) and converted to <see cref="BaseCurrency"/>.
///
/// <para>
/// Since issue #159 each term also says which way its money moves, and the two sides are reported
/// separately: <b>no gross total ever mixes directions</b>. <see cref="Monthly"/>/<see cref="Yearly"/>/
/// <see cref="ByType"/> count outgoing terms only, <see cref="IncomingMonthly"/>/
/// <see cref="IncomingYearly"/>/<see cref="IncomingByType"/> incoming terms only, and the one figure
/// that crosses them says so in its name. The base currency is elected from BOTH directions and before
/// the split, so there is one base per response and never one per direction.
/// </para>
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

    /// <summary>
    /// Converted monthly total of the <b>outgoing</b> terms; null when nothing convertible carries a
    /// rate. The name and the meaning are both unchanged by issue #159: every term that existed
    /// before it is <c>Outgoing</c>, so "the run rate" and "the outgoing run rate" are the same
    /// number on every existing file.
    /// </summary>
    public decimal? Monthly { get; set; }

    /// <summary>Converted yearly total of the outgoing terms; null when nothing convertible carries a rate.</summary>
    public decimal? Yearly { get; set; }

    /// <summary>The outgoing read split by contract type, in enum order.</summary>
    public List<ContractRunRateTypeRow> ByType { get; set; } = new();

    /// <summary>
    /// Converted monthly total of the <b>incoming</b> terms (issue #159) — same units, same base
    /// currency, same exclusions. Null when that side contributes nothing convertible, which is a
    /// healthy state for a household that only records costs.
    /// </summary>
    public decimal? IncomingMonthly { get; set; }

    /// <summary>Converted yearly total of the incoming terms; null on the same terms as <see cref="IncomingMonthly"/>.</summary>
    public decimal? IncomingYearly { get; set; }

    /// <summary>The incoming read split by contract type, in enum order. Same row shape as <see cref="ByType"/>.</summary>
    public List<ContractRunRateTypeRow> IncomingByType { get; set; } = new();

    /// <summary>
    /// Incoming minus outgoing, monthly — the ONLY signed figure in the payload, and the only one that
    /// crosses the two directions.
    ///
    /// <para>
    /// Computed as <c>(incoming ?? 0) − (outgoing ?? 0)</c> from the <b>unrounded</b> sums and rounded
    /// once, so a household with income and no recorded costs still gets a net. It is null <b>only
    /// when both</b> sides are. It covers exactly the convertible subset the two grosses cover, so it
    /// is a partial net precisely when they are partial — <see cref="UnconvertedCurrencies"/> is what
    /// makes that legible.
    /// </para>
    /// </summary>
    public decimal? NetMonthly { get; set; }

    /// <summary>Incoming minus outgoing, yearly. Same rules as <see cref="NetMonthly"/>.</summary>
    public decimal? NetYearly { get; set; }

    /// <summary>
    /// Currencies present in the run rate with no rate to <see cref="BaseCurrency"/>. ONE list covers
    /// both directions: a currency with no rate is named once and excluded from both grosses and from
    /// the net, never folded in at 1:1.
    /// </summary>
    public List<string> UnconvertedCurrencies { get; set; } = new();
}

/// <summary>
/// A derived next money movement: the soonest occurrence of one of a contract's in-force periodic fee
/// terms — a charge or, since issue #159, a receipt. The record's name predates direction and is kept
/// rather than renamed: it carries one dated movement either way, and which way is said by the list it
/// appears in (<see cref="ContractSummary.UpcomingCharges"/> or
/// <see cref="ContractSummary.UpcomingReceipts"/>) rather than by a field on the row. The occurrence is
/// projected from that term's cadence anchor (<c>AnchorDate</c>, else <c>EffectiveFrom</c>) and never
/// falls past the contract's own end date.
///
/// <para>
/// <b>Nothing is scheduled or stored.</b> This is the term history read forward at request time —
/// the anchor is never advanced.
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
/// type, the direction-aware run rate, and the derived upcoming movements — outgoing charges and, since
/// issue #159, incoming receipts. Archived contracts are counted
/// in <see cref="CountsByStatus"/> but excluded from the active totals, the by-type breakdown, the run
/// rate and the charges.
///
/// <para>
/// A <b>paused</b> contract (issue #140) is excluded from the run rate, its by-type split and the
/// charges — it is not costing anything while suspended — but stays in <see cref="CountsByType"/>,
/// which is a headcount of the agreements on file rather than a cost split. The two by-type reads
/// answer different questions and this is the one place they are answered differently.
/// </para>
/// </summary>
public sealed record ContractSummary
{
    public int TotalContracts { get; set; }

    public required ContractStatusCounts CountsByStatus { get; set; }

    public List<ContractTypeCount> CountsByType { get; set; } = new();

    public required ContractRunRate RunRate { get; set; }

    /// <summary>
    /// The soonest OUTGOING movement of each contract inside the look-ahead window. Bounded by
    /// <c>ContractMaxSummaryCharges</c>, which since issue #159 bounds each list separately so a file
    /// with many charges cannot starve <see cref="UpcomingReceipts"/>.
    /// </summary>
    public List<ContractUpcomingCharge> UpcomingCharges { get; set; } = new();

    /// <summary>
    /// The soonest INCOMING movement of each contract inside the same window (issue #159), in the same
    /// row shape. The LIST names the direction — the row carries no direction field, so there is no
    /// second copy of the fact to drift. A contract that pays a salary on the 25th and deducts a fee
    /// on the 1st contributes one row to each list.
    /// </summary>
    public List<ContractUpcomingCharge> UpcomingReceipts { get; set; } = new();

    /// <summary>
    /// The effective "ending soon" window, in days. Served rather than held as a client constant: the
    /// value is admin-editable, and a page holding its own copy would label a row with one number
    /// while the server counted by another.
    /// </summary>
    public int EndingWindowDays { get; set; }

    /// <summary>The effective look-ahead behind <see cref="UpcomingCharges"/>, in days.</summary>
    public int ChargeWindowDays { get; set; }
}
