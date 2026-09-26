using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// One entry in a property's chronological event log (issue #209): a repair, an inspection, a tyre
/// change, a sale. An owned child of the property — it dies with it (<c>CASCADE</c>).
/// </summary>
/// <remarks>
/// <para>
/// Stored in the shared <c>Events</c> table beside <see cref="ContractEvent"/> as the
/// <see cref="EventOwnerKind.Property"/> branch of <see cref="OwnedEvent"/>; see that type for why the
/// table is shared and what contains it.
/// </para>
/// <para>
/// <b><see cref="Type"/> is always written explicitly.</b> The physical column keeps the contract
/// branch's <c>DEFAULT 8</c> (contract <c>Other</c>), so a property row that ever omitted it would land
/// on <c>8</c> — and be rejected by <c>CK_Events_TypeMatchesOwner</c> rather than slip in as a
/// disguised contract type. No member of <see cref="PropertyEventType"/> equals the CLR default, so EF
/// never treats a real value as "unset".
/// </para>
/// <para>
/// Matrix legality (which types a real-estate property or a vehicle may carry) is enforced in the
/// service, not the database: it depends on the owning property's <c>Type</c>, which a <c>CHECK</c>
/// cannot see. That is safe because <c>Property.Type</c> is immutable.
/// </para>
/// </remarks>
public class PropertyEvent : OwnedEvent
{
    /// <summary>
    /// Nullable in the shared <c>Events</c> table (a contract row has none), required on this type.
    /// </summary>
    [Required]
    public required Guid PropertyId { get; set; }

    [ForeignKey(nameof(PropertyId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Property? Property { get; set; }

    /// <summary>Shares the physical <c>Type</c> column with <see cref="ContractEvent.Type"/>; 100–199.</summary>
    [Required]
    public PropertyEventType Type { get; set; } = PropertyEventType.Other;
}
