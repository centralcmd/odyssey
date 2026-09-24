using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A time-versioned entry recording the value of one named series on a <b>contract</b> — such as a
/// rate, or the price of one named charge, effective from a given date. The series key is
/// <c>(ContractId, LabelKey)</c>, so a contract can hold several concurrently in-force terms told
/// apart by their labels. There is no explicit end date: the value in force on a date is the entry
/// with the greatest <see cref="EffectiveFrom"/> on or before it <em>within its own series</em>. The
/// composite index backs both history listing and current-value resolution.
///
/// <para>
/// A contract is the only owner. Terms could once also be recorded directly on an account (issue
/// #135); issue #190 moved every one of those onto a contract the account takes part in
/// (<c>MoveAccountTermsToContracts</c>) and dropped the account owner from the schema.
/// </para>
/// </summary>
[Index(nameof(ContractId), nameof(LabelKey), nameof(EffectiveFrom))]
public class Term : IEffectiveDated
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid TermId { get; set; }

    /// <summary>
    /// The owning contract.
    /// </summary>
    public Guid ContractId { get; set; }

    public Contract? Contract { get; set; }

    /// <summary>
    /// The user's own wording for this series ("ATM withdrawal · abroad"), in their own casing.
    /// Required on every write; nullable only because a row predating that rule may carry none.
    /// </summary>
    [StringLength(64)]
    public string? Label { get; set; }

    /// <summary>
    /// <see cref="Label"/> normalized and case-folded — the column that participates in the series
    /// key. Written ONLY by the domain service and never bound from a request. It is persisted rather
    /// than folded at query time because MariaDB's default collation is case-insensitive while the EF
    /// InMemory provider compares ordinally, so a predicate over <see cref="Label"/> would mean
    /// different things on the two test tiers.
    /// </summary>
    [StringLength(64)]
    public string? LabelKey { get; set; }

    [Required]
    public TermValueUnit ValueUnit { get; set; }

    /// <summary>
    /// Which way the money moves, from the household's perspective (issue #159).
    /// <see cref="TermDirection.Outgoing"/> is the default and the value every pre-#159 row backfills
    /// to, which is what makes the migration behaviour-preserving: every figure the contracts roll-up
    /// returned before it returns the same number after.
    ///
    /// <para>
    /// Not part of the series key, the duplicate guard or supersession — the direction that counts is
    /// the one on the entry currently in force. Never a SQL predicate either, so the column carries no
    /// index of its own.
    /// </para>
    /// </summary>
    [Required]
    public TermDirection Direction { get; set; }

    /// <summary>
    /// The numeric value. Non-null IFF <see cref="ValueUnit"/> is <see cref="TermValueUnit.Percentage"/>
    /// or <see cref="TermValueUnit.Amount"/>; null on the two non-numeric kinds (issue #192), which is
    /// backed up by <c>CK_Terms_ValueMatchesUnit</c>.
    /// </summary>
    [Precision(18, 6)]
    public decimal? Value { get; set; }

    /// <summary>
    /// A <see cref="TermValueUnit.Text"/> term's value: one plain line, stored trimmed. Non-null IFF
    /// <see cref="ValueUnit"/> is <c>Text</c>. User-supplied free text, so it never reaches the operator log.
    /// </summary>
    [StringLength(Odyssey.Dtos.Finance.TermTextValue.MaxLength)]
    public string? TextValue { get; set; }

    /// <summary>
    /// A <see cref="TermValueUnit.DateTime"/> term's value: an instant, always stored as UTC. Non-null
    /// IFF <see cref="ValueUnit"/> is <c>DateTime</c>. Stored for the record only — nothing schedules
    /// from it.
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// The cadence UNIT. Optional.
    /// </summary>
    public Interval? Interval { get; set; }

    /// <summary>
    /// How many <see cref="Interval"/> units between charges — 3 with <c>Monthly</c> is quarterly.
    /// Non-null IFF <see cref="Interval"/> is periodic (Daily/Weekly/Monthly/Annually); null in every
    /// other case, including when the interval itself is null. A meaningless 1 on a one-time fee
    /// would be indistinguishable from a deliberate one on the next read-modify-write round trip.
    /// </summary>
    public int? IntervalCount { get; set; }

    /// <summary>
    /// The date the term is first actually CHARGED, as distinct from <see cref="EffectiveFrom"/>,
    /// when its price took effect. Pure record-keeping: nothing schedules, accrues or projects from
    /// it, and it enters neither the series key, supersession nor the duplicate guard. No ordering
    /// against <see cref="EffectiveFrom"/> is imposed in either direction.
    /// </summary>
    public DateTime? AnchorDate { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }

    [Required]
    public DateTime CreatedAtUtc { get; set; }
}
