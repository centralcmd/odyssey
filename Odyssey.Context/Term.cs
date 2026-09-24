using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A time-versioned entry recording the value of one named series on an <b>owner</b> — an account or a
/// contract (issue #135) — such as a rate, or the price of one named charge, effective from a given
/// date. The series key is <c>(owner, LabelKey)</c>, so an owner can hold several concurrently
/// in-force terms told apart by their labels. There is no explicit end date: the value in force on a
/// date is the entry with the greatest <see cref="EffectiveFrom"/> on or before it <em>within its own
/// series</em>. Each composite index backs both history listing and current-value resolution for its
/// owner.
///
/// <para>
/// Exactly one of <see cref="AccountId"/> and <see cref="ContractId"/> is populated. The domain
/// service is the real guard (the owner comes from the route and is never bound from a request body);
/// <c>CK_Terms_ExactlyOneOwner</c> is the database backstop. An account term and a contract term never
/// share a series, so supersession within one owner can never be disturbed by the other.
/// </para>
/// </summary>
[Index(nameof(AccountId), nameof(LabelKey), nameof(EffectiveFrom))]
[Index(nameof(ContractId), nameof(LabelKey), nameof(EffectiveFrom))]
public class Term : IEffectiveDated
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid TermId { get; set; }

    /// <summary>
    /// The owning account, or null when this term belongs to a contract. Nullable since issue #135
    /// widened the table to two owners; every pre-#135 row has it populated.
    /// </summary>
    public Guid? AccountId { get; set; }

    public Account? Account { get; set; }

    /// <summary>
    /// The owning contract, or null when this term belongs to an account (issue #135).
    /// </summary>
    public Guid? ContractId { get; set; }

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

    [Required]
    [Precision(18, 6)]
    public decimal Value { get; set; }

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
