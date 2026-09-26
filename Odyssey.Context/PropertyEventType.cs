namespace Odyssey.Context;

/// <summary>
/// What kind of thing happened to a property (issue #209 §4.2). The persistence mirror of
/// <c>Odyssey.Dtos.Finance.PropertyEventType</c>: identical names and ordinals, cast by ordinal, and a
/// guard test fails the build if the two drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordinals start at 100 and are a wire and persistence contract.</b> Property rows share the
/// <c>Events</c> table's <c>Type</c> column with <see cref="ContractEventType"/>, which stays below 100,
/// so a stored value always identifies its enum; <c>CK_Events_TypeMatchesOwner</c> holds each owner to
/// its range (100–199 here). Never renumber a member and never reuse a retired ordinal.
/// </para>
/// <para>
/// <see cref="Other"/> is the catch-all and the entity default but is <b>not</b> the last ordinal, so the
/// client registry pulls it to the end of the reading order — the same split as
/// <see cref="ContractEventType"/>.
/// </para>
/// </remarks>
public enum PropertyEventType
{
    Acquired = 100,
    Disposed = 101,
    Valued = 102,
    Maintenance = 103,
    Repair = 104,
    Damage = 105,
    Inspection = 106,
    InsuranceChanged = 107,
    Other = 108,

    /// <summary>System only: the property was archived.</summary>
    Archived = 109,

    /// <summary>System only: the property was restored from the archive.</summary>
    Unarchived = 110,

    /// <summary>System only: the acquired date was cleared.</summary>
    AcquisitionDateCleared = 111,

    /// <summary>System only: the disposed date was cleared.</summary>
    DisposalReversed = 112,

    Renovation = 113,
    TaxAssessed = 114,
    TenancyStarted = 115,
    TenancyEnded = 116,
    Serviced = 117,
    TyreChange = 118,
    PeriodicInspection = 119,
    Registered = 120,
}
