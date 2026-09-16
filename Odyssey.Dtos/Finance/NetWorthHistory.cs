using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// A net-worth-over-time series, reconstructed on read from stored transactions, account estimates and
/// exchange rates (issue #90). Every point is derived from the data as of that point's own instant —
/// nothing is interpolated, and no past point is scaled by the present figure.
/// </summary>
/// <remarks>
/// Reconstruction rather than a snapshot table is what makes the series correct <b>retroactively</b>:
/// a back-dated transaction, a corrected estimate or a fixed exchange rate repairs the history it
/// should have produced, where a stored snapshot would preserve the mistake.
/// </remarks>
public sealed record NetWorthHistory
{
    [StringLength(3)]
    public required string MainCurrencyCode { get; set; }

    public required NetWorthInterval Interval { get; set; }

    /// <summary>The effective window start — the caller's <c>from</c> snapped outward to its period start.</summary>
    public required DateOnly From { get; set; }

    public required DateOnly To { get; set; }

    /// <summary>Set only when <see cref="Points"/> is empty, and then always set. See <see cref="NetWorthEmptyReason"/>.</summary>
    public NetWorthEmptyReason? EmptyReason { get; set; }

    public List<NetWorthHistoryPoint> Points { get; set; } = [];

    /// <summary>
    /// The accounts that contributed 0 to at least one point because no rate to the main currency was
    /// in force then. The same record type <c>/accounts/totals</c> already returns under the same
    /// claim; an account appears at most once however many points it understated.
    /// </summary>
    public List<UnconvertedAccount> UnconvertedAccounts { get; set; } = [];
}

/// <summary>
/// One point of a <see cref="NetWorthHistory"/>: the figures as of <see cref="Date"/>, plus counts
/// saying how they were arrived at.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Date"/> is the period END</b>, which is the instant the point describes. Net worth is
/// a stock, not a flow: a flow series is legitimately labelled by period start, but a stock has exactly
/// one instant it is true at and the label has to name it. A point covering October 2024 is dated
/// <c>2024-11-01</c>. Mislabelling a financial figure by one period is the same defect this feature
/// exists to fix, in a smaller shape.
/// </para>
/// <para>
/// All three flags are <b>counts</b>, never lists — the accounts themselves would be a per-period
/// disclosure the figures do not need.
/// </para>
/// </remarks>
public sealed record NetWorthHistoryPoint
{
    /// <summary>The instant this point describes: the exclusive upper bound of the period it covers.</summary>
    public required DateOnly Date { get; set; }

    public required decimal TotalAssets { get; set; }

    public required decimal TotalLiabilities { get; set; }

    public required decimal NetWorth { get; set; }

    /// <summary>
    /// Accounts that existed at this point but had no rate to the main currency, so they contributed
    /// 0 and this figure is <b>understated</b>. Non-zero makes the point <c>Partial</c>.
    /// </summary>
    public required int UnconvertedAccountCount { get; set; }

    /// <summary>
    /// Accounts whose in-force estimate changed during this period, so the step to this point is a
    /// <b>real revaluation</b> rather than an understatement. Disclosed, never smoothed — smoothing it
    /// would re-introduce the invented-curve defect. Never increments
    /// <see cref="UnconvertedAccountCount"/> and never suppresses the delta.
    /// </summary>
    public required int RevaluedAccountCount { get; set; }

    /// <summary>
    /// Accounts that existed at this point <b>and</b> converted successfully. Existence alone would
    /// make "nothing convertible" unreachable and would report a wholly-understated point as healthy.
    /// </summary>
    public required int ContributingAccountCount { get; set; }
}
