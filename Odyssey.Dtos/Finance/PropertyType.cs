namespace Odyssey.Dtos.Finance;

/// <summary>
/// The subtype of a <c>Property</c> (issue #167). Fixed at creation — a type change is refused rather
/// than performed, because it would silently destroy the outgoing subtype's fields and leave an
/// estimate history describing a different thing.
///
/// <para>
/// <b>Ordinals are a persistence and wire contract and are never renumbered</b>; a future subtype
/// appends, together with its own detail table.
/// </para>
/// </summary>
public enum PropertyType
{
    RealEstate = 0,
    Vehicle = 1,
}

/// <summary>The kind of a real-estate property (issue #167). Ordinals are persisted and never renumbered.</summary>
public enum RealEstateKind
{
    House = 0,
    Apartment = 1,
    Cabin = 2,
    Plot = 3,
    Commercial = 4,
    Other = 5,
}

/// <summary>The kind of a vehicle property (issue #167). Ordinals are persisted and never renumbered.</summary>
public enum VehicleKind
{
    Car = 0,
    Motorcycle = 1,
    Boat = 2,
    Trailer = 3,
    Other = 4,
}

/// <summary>
/// List-filter status for properties, <b>derived, never stored</b> (issue #167 §8): <see cref="Archived"/>
/// when the <c>Archived</c> column is set, else <see cref="Disposed"/> when a <c>DisposedDate</c> on or
/// before now is set, else <see cref="Owned"/>. Mirrors how <see cref="AccountStatus"/> is derived.
/// </summary>
public enum PropertyStatus
{
    Owned,
    Disposed,
    Archived,
}
