using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdateContract
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Name { get; set; }

    [EnumDataType(typeof(ContractType))]
    public ContractType Type { get; set; } = ContractType.Other;

    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>
    /// The number printed on the paperwork (issue #181) — optional. Trimmed, and blank collapses to
    /// null. Any printable character is accepted, non-Latin and non-BMP included; only Unicode
    /// control, format, private-use and unassigned characters are refused. The pattern names those
    /// four categories rather than <c>\p{C}</c>, which also holds <c>Cs</c> and would reject every
    /// surrogate pair — .NET regex matches UTF-16 code units.
    ///
    /// <para>
    /// <b>Full-replacement semantics</b>, like <see cref="Description"/>: null or omitted
    /// <b>clears</b> it. Every caller that rebuilds this DTO from a loaded record must carry it forward;
    /// <c>ContractPauseSurfaceTests.RequiredStamps</c> keeps that a build failure.
    /// </para>
    /// </summary>
    [StringLength(ContractReferenceNumber.MaxLength)]
    [RegularExpression(ContractReferenceNumber.Pattern, ErrorMessage = ContractReferenceNumber.InvalidCharactersMessage)]
    public string? ReferenceNumber { get; set; }

    /// <summary>Start of a term contract (optional). Null when this is a one-off (see <see cref="CompletionDate"/>).</summary>
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>Completion date of a one-off contract; when set, term dates are ignored.</summary>
    public DateTime? CompletionDate { get; set; }

    /// <summary>
    /// Archive (retain but hide) the contract when true, or unarchive when false. Archiving keeps the
    /// contract and its parties/files but drops it from the default list; deletion (<c>DELETE</c>) is
    /// the separate, permanent operation. There is no dedicated archive/unarchive endpoint — the
    /// archive state is toggled here as part of the regular update (issue #174 §6/§7).
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// Pause (temporarily suspend) the contract when true, or resume when false (issue #140). A pause
    /// suspends what the agreement costs — it leaves the run rate and the upcoming charges — without
    /// ending it, hiding it, locking it or touching its price history. Like <see cref="IsArchived"/>
    /// it rides the regular update; there is no dedicated pause endpoint.
    ///
    /// <para>
    /// Only a contract deriving as <c>Active</c> may be paused; anything else is refused with a
    /// <c>400</c> carrying <c>contract_pause_requires_active</c>. Clearing is never refused. Because
    /// <c>PUT</c> is a full replacement, an omitted <c>isPaused</c> reads as false and therefore
    /// <b>resumes</b> a paused contract — the same semantics <see cref="IsArchived"/> already has.
    /// </para>
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Marked ready for signature on this date (issue #145), with <b>full-replacement</b> semantics: a
    /// present value sets the stamp, <see langword="null"/> or omitted <b>clears</b> it.
    ///
    /// <para>
    /// <b>This is not the "null means unchanged" shape, deliberately.</b> <c>PUT /api/contracts/{id}</c>
    /// is a documented full replacement — <see cref="StartDate"/>, <see cref="EndDate"/> and
    /// <see cref="CompletionDate"/> are already replaced wholesale, and an omitted
    /// <see cref="IsArchived"/>/<see cref="IsPaused"/> already <i>unarchives</i> and <i>resumes</i>.
    /// Giving these two fields alone the opposite convention would put two opposite meanings in one
    /// DTO and, decisively, would make the rule "clearing <see cref="Signed"/> is always allowed"
    /// inexpressible — there would be no value that means "clear it".
    /// </para>
    ///
    /// <para>
    /// The consequence is that <b>every</b> caller that rebuilds this DTO from a record it did not
    /// fully author has to carry both stamps forward, exactly as it already carries
    /// <see cref="IsArchived"/> and <see cref="IsPaused"/>: an omission on an Archive or Pause write
    /// would clear them, flip a signed contract to <c>Draft</c> and drop it out of the run rate.
    /// <c>ContractPauseSurfaceTests.Every_contract_write_carries_both_stamps_forward</c> is the
    /// source-lint that keeps that a build failure rather than a thing to remember.
    /// </para>
    /// </summary>
    public DateTime? Ready { get; set; }

    /// <summary>
    /// Signed by all parties on this date (issue #145), with the same full-replacement semantics as
    /// <see cref="Ready"/>: a present value sets the stamp, null or omitted clears it.
    ///
    /// <para>
    /// <b>Clearing is never refused</b>, on a contract in any state — a guard on the way out is how a
    /// row gets stranded, which is the rule <c>EnsurePausable</c> already states. Setting it is
    /// guarded: it requires <see cref="Ready"/> (<c>contract_signed_requires_ready</c>), cannot
    /// precede it (<c>contract_signed_before_ready</c>), and neither stamp may be dated in the future
    /// (<c>contract_signature_date_in_future</c>).
    /// </para>
    /// </summary>
    public DateTime? Signed { get; set; }
}
