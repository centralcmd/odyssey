namespace Odyssey.Dtos.Finance;

/// <summary>
/// The property-type × event-type matrix (issue #209 §4.2): which <see cref="PropertyEventType"/>
/// members are legal on a <see cref="PropertyType.RealEstate"/> property and on a
/// <see cref="PropertyType.Vehicle"/>, and which members only the server may write.
/// <b>17 legal cells per property type, 34 of 42.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>The single declaration, not a server rule the client re-implements</b> — the
/// <see cref="ContractPartyRoleMatrix"/> precedent. It lives in <c>Odyssey.Dtos</c>, which holds zero
/// project references, so the write-path validator and the type picker name one symbol.
/// </para>
/// <para>
/// <b>System-only members are legal cells.</b> <see cref="PropertyEventType.Archived"/>,
/// <see cref="PropertyEventType.Unarchived"/>, <see cref="PropertyEventType.AcquisitionDateCleared"/>
/// and <see cref="PropertyEventType.DisposalReversed"/> are recorded by the server on every property
/// type, so they count towards the 17; <see cref="IsSystemOnly"/> is the separate question of whether
/// a person may <em>introduce</em> one. A <c>POST</c> never may, and a <c>PUT</c> may only keep one the
/// row already carries.
/// </para>
/// <para>
/// Matrix legality is enforced in the service rather than a <c>CHECK</c>, because it depends on the
/// owning property's type. That is safe only because <c>Property.Type</c> is immutable (issue #167): a
/// legal event can never become illegal later.
/// </para>
/// </remarks>
public static class PropertyEventTypeMatrix
{
    /// <summary>Written only by the server alongside a lifecycle change (§4.2, 109–112).</summary>
    public static readonly IReadOnlyList<PropertyEventType> SystemOnly =
    [
        PropertyEventType.Archived,
        PropertyEventType.Unarchived,
        PropertyEventType.AcquisitionDateCleared,
        PropertyEventType.DisposalReversed,
    ];

    /// <summary>Legal on every property type — the thirteen universal members, system-only ones included.</summary>
    public static readonly IReadOnlyList<PropertyEventType> Universal =
    [
        PropertyEventType.Acquired,
        PropertyEventType.Disposed,
        PropertyEventType.Valued,
        PropertyEventType.Maintenance,
        PropertyEventType.Repair,
        PropertyEventType.Damage,
        PropertyEventType.Inspection,
        PropertyEventType.InsuranceChanged,
        PropertyEventType.Other,
        PropertyEventType.Archived,
        PropertyEventType.Unarchived,
        PropertyEventType.AcquisitionDateCleared,
        PropertyEventType.DisposalReversed,
    ];

    private static readonly Dictionary<PropertyType, IReadOnlyList<PropertyEventType>> Specific = new()
    {
        [PropertyType.RealEstate] =
        [
            PropertyEventType.Renovation,
            PropertyEventType.TaxAssessed,
            PropertyEventType.TenancyStarted,
            PropertyEventType.TenancyEnded,
        ],
        [PropertyType.Vehicle] =
        [
            PropertyEventType.Serviced,
            PropertyEventType.TyreChange,
            PropertyEventType.PeriodicInspection,
            PropertyEventType.Registered,
        ],
    };

    /// <summary>The property types the matrix declares a column for.</summary>
    public static IReadOnlyCollection<PropertyType> DeclaredTypes => Specific.Keys;

    /// <summary>The members specific to one property type (four each); empty for an undeclared type.</summary>
    public static IReadOnlyList<PropertyEventType> SpecificTo(PropertyType propertyType) =>
        Specific.TryGetValue(propertyType, out var members) ? members : [];

    /// <summary>
    /// Every member legal on <paramref name="propertyType"/>, type-specific ones first — the picker's
    /// reading order. Empty for an undeclared type, so an unknown property type is legal for nothing.
    /// </summary>
    public static IReadOnlyList<PropertyEventType> LegalFor(PropertyType propertyType) =>
        Specific.TryGetValue(propertyType, out var members) ? [.. members, .. Universal] : [];

    /// <summary>Whether <paramref name="eventType"/> may appear on a property of <paramref name="propertyType"/>.</summary>
    public static bool IsLegal(PropertyType propertyType, PropertyEventType eventType) =>
        LegalFor(propertyType).Contains(eventType);

    /// <summary>Whether only the server may introduce <paramref name="eventType"/>.</summary>
    public static bool IsSystemOnly(PropertyEventType eventType) => SystemOnly.Contains(eventType);
}
