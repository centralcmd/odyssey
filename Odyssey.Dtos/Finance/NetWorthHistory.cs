using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// A net-worth-over-time series, reconstructed on read from stored transactions, account estimates,
/// property estimates (when <see cref="PropertiesIncluded"/>, issue #214) and exchange rates (issue #90). Every point is derived from the data as of that point's own instant —
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
    /// in force then. The same record type <c>/accounts/totals</c> returns; an account appears at most
    /// once however many points it understated.
    /// </summary>
    public List<UnconvertedAccount> UnconvertedAccounts { get; set; } = [];

    /// <summary>
    /// Whether property value is part of every figure in this series (issue #214). Claim-decided, never
    /// data-decided, and set on every response — empty ones included, because the client picks its
    /// empty copy by <c>(EmptyReason, PropertiesIncluded)</c>.
    /// </summary>
    public bool PropertiesIncluded { get; set; }

    /// <summary>
    /// The held, valued properties that contributed 0 to at least one point because no rate to the
    /// main currency was in force then. Each appears at most once. Empty when not included.
    /// </summary>
    public List<UnconvertedProperty> UnconvertedProperties { get; set; } = [];
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
/// All the flags are <b>counts</b>, never lists — the accounts or properties themselves would be a
/// per-period disclosure the figures do not need.
/// </para>
/// </remarks>
public sealed record NetWorthHistoryPoint
{
    /// <summary>The instant this point describes: the exclusive upper bound of the period it covers.</summary>
    public required DateOnly Date { get; set; }

    /// <summary>Account assets, plus <see cref="PropertyValue"/> when properties are included.</summary>
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

    /// <summary>
    /// The converted value of the properties held at this point, each at the estimate in force then.
    /// <c>null</c> when properties are not included; <c>0</c> when included but nothing was valued.
    /// </summary>
    public decimal? PropertyValue { get; set; }

    /// <summary>Properties held at this point, valued and converted.</summary>
    public int ContributingPropertyCount { get; set; }

    /// <summary>
    /// Properties held and valued at this point but with no rate to the main currency, so this figure
    /// is <b>understated</b>. Non-zero makes the point <c>Partial</c>, exactly like
    /// <see cref="UnconvertedAccountCount"/>.
    /// </summary>
    public int UnconvertedPropertyCount { get; set; }

    /// <summary>
    /// Properties whose in-force estimate changed during this period while held. Never counts an
    /// acquisition or a disposal, and never a property's first held point.
    /// </summary>
    public int RevaluedPropertyCount { get; set; }

    /// <summary>
    /// Properties held at this point with no estimate in force, so they count as 0. Disclosure only:
    /// this does <b>not</b> make the point <c>Partial</c> (issue #214 D1).
    /// </summary>
    public int UnvaluedPropertyCount { get; set; }
}
