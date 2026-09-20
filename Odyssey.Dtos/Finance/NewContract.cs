using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewContract
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Name { get; set; }

    [EnumDataType(typeof(ContractType))]
    public ContractType Type { get; set; } = ContractType.Other;

    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>
    /// Start of a term contract (optional — null is an open-started ongoing agreement). Leave this and
    /// <see cref="EndDate"/> null when <see cref="CompletionDate"/> is set (a one-off).
    /// </summary>
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Completion date of a one-off contract — a point-in-time agreement with no ongoing term. When set,
    /// the contract is a one-off and <see cref="StartDate"/>/<see cref="EndDate"/> are ignored.
    /// </summary>
    public DateTime? CompletionDate { get; set; }

    /// <summary>
    /// Marked ready for signature on this date (issue #145) — optional. Null means the contract has
    /// not been marked ready, which is the default and a healthy state.
    ///
    /// <para>
    /// <b>No data annotation, deliberately.</b> Neither bound is a compile-time constant: "not in the
    /// future" is evaluated against the injected <c>TimeProvider</c> and "<c>Signed &gt;= Ready</c>"
    /// is a cross-field rule, so both live in the service. A decorative attribute that can never fire
    /// is the defect to avoid here.
    /// </para>
    /// </summary>
    public DateTime? Ready { get; set; }

    /// <summary>
    /// Signed by all parties on this date (issue #145) — optional. Null means unsigned, which is the
    /// default and a healthy state.
    ///
    /// <para>
    /// Accepted on create as well as update so a paper contract signed last month can be entered in
    /// one call. The three signature guards — a signed date needs a ready date
    /// (<c>contract_signed_requires_ready</c>), cannot precede it
    /// (<c>contract_signed_before_ready</c>), and neither may be dated in the future
    /// (<c>contract_signature_date_in_future</c>) — are shared by both write paths.
    /// </para>
    /// </summary>
    public DateTime? Signed { get; set; }
}
