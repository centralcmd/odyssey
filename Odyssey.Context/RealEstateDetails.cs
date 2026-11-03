using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Real-estate sub-record of a <see cref="Property"/> whose <c>Type</c> is
/// <see cref="PropertyType.RealEstate"/> (issue #167). 1:1 with the parent, sharing its primary key —
/// the <see cref="PersonDetails"/> shape — and cascade-deleted with it.
///
/// <para>
/// The address is personal data (it identifies a natural person) and is never echoed in an error body or
/// a log line. Since issue #380 it is the linked <see cref="Location"/> (optional, <c>RESTRICT</c>), which
/// <c>properties.read</c> reads through this record and <c>locations.read</c> reads directly (product
/// decision 3).
/// </para>
/// </summary>
public class RealEstateDetails
{
    [Key]
    public Guid PropertyId { get; set; }

    public Property Property { get; set; } = null!;

    [Required]
    public RealEstateKind Kind { get; set; }

    /// <summary>The property's address (issue #380), or null when it has none.</summary>
    public Guid? LocationId { get; set; }

    public Location? Location { get; set; }

    /// <summary>The land-registry identifier — free text, deliberately not parsed.</summary>
    [StringLength(64)]
    public string? CadastralNumber { get; set; }

    [Precision(18, 2)]
    public decimal? LivingAreaSqm { get; set; }

    [Precision(18, 2)]
    public decimal? PlotAreaSqm { get; set; }

    public int? BuildYear { get; set; }

    /// <summary>
    /// The homeowner association (<i>borettslag</i>/<i>sameie</i>/HOA) administering the property
    /// (issue #217) — an <c>Organization</c> contact. A real FK with <c>ON DELETE SET NULL</c> and no
    /// navigation on either side, the <c>Account.CustodianId</c> shape: finance read paths resolve it
    /// through <c>IContactLookup</c>. The Organization-only rule is enforced by <c>PropertyService</c>,
    /// since a <c>CHECK</c> cannot reference another table.
    /// </summary>
    public Guid? HomeownerAssociationId { get; set; }
}
