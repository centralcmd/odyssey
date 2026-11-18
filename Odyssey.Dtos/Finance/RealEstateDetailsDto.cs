using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Real-estate fields embedded in <see cref="NewProperty"/>/<see cref="ExistingProperty"/> (issue #167).
/// Carries no <c>PropertyId</c>: the detail row is an owned 1:1 child keyed by its parent, so there is
/// nothing to over-post. The service additionally uppercases <see cref="CountryCode"/> and rejects a
/// <see cref="BuildYear"/> in the future — a clock-dependent bound no <c>[Range]</c> can carry.
/// </summary>
public sealed record RealEstateDetailsDto
{
    [Required]
    [EnumDataType(typeof(RealEstateKind))]
    public RealEstateKind Kind { get; set; }

    /// <summary>
    /// The property's place, by id (issue #380 §5.4). Either this or the four address fields: by id needs
    /// <c>locations.read</c> when it changes the link and an address-complete place; by fields needs
    /// <c>locations.create</c> and accepts a partial address. Both sent with fields that differ from the
    /// linked place's is a <c>422</c> keyed <c>locationId</c>. All four fields empty and no id unlinks.
    /// </summary>
    public Guid? LocationId { get; set; }

    /// <summary>The place's <c>Line1</c>.</summary>
    [StringLength(LocationRules.AddressLineMaxLength)]
    public string? AddressLine { get; set; }

    [StringLength(LocationRules.PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [StringLength(LocationRules.CityMaxLength)]
    public string? City { get; set; }

    /// <summary>ISO 3166-1 alpha-2; stored uppercased.</summary>
    [StringLength(2, MinimumLength = 2)]
    [RegularExpression(LocationRules.CountryCodePattern)]
    public string? CountryCode { get; set; }

    /// <summary>
    /// How a change to the four fields applies to the shared place (§5.4, v6) — the contact address rule.
    /// Read on <c>PUT</c> only, and required when the fields change. An <c>AllUses</c> edit writes these four
    /// fields only and leaves the place's <c>Line2</c> and <c>Region</c> as they are.
    /// </summary>
    [EnumDataType(typeof(AddressApplyTo))]
    public AddressApplyTo? ApplyTo { get; set; }

    /// <summary>The <see cref="OtherUses"/> the client showed; required when the write edits the place in place.</summary>
    [Range(0, int.MaxValue)]
    public int? ExpectedOtherUses { get; set; }

    // ── Geocode (issue #380 §5.3a, v9): optional and REQUEST-only ───────────────────────────────────
    // Used only when the fields path CREATES a place; a match on the address key links the existing place
    // and ignores them. Ignored with a LocationId, on an unlink and by an in-place edit. All-or-none
    // coordinates (422 keyed latitude); Source is Manual, Kartverket or OpenMeteo (422 keyed source). A read
    // never fills them, and a null is not written, so no response carries the members at all.

    /// <summary>Request only: the geocoded latitude of a place this write creates.</summary>
    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Latitude { get; set; }

    /// <summary>Request only.</summary>
    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Longitude { get; set; }

    /// <summary>Request only.</summary>
    [StringLength(IanaTimeZones.MaxLength)]
    [IanaTimeZone]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeZoneId { get; set; }

    /// <summary>Request only.</summary>
    [EnumDataType(typeof(LocationSource))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LocationSource? Source { get; set; }

    /// <summary>Request only.</summary>
    [StringLength(LocationRules.SourceReferenceMaxLength)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceReference { get; set; }

    /// <summary>
    /// Response only (ignored in a request): how many OTHER records use the linked place, over only the kinds
    /// the caller can read. <see langword="null"/> when the property has no place.
    /// </summary>
    public int? OtherUses { get; set; }

    /// <summary>Response only: whether the linked place has map coordinates. <see langword="null"/> with no place.</summary>
    public bool? PlaceHasCoordinates { get; set; }

    /// <summary>The land-registry identifier (e.g. Norwegian <i>gårds- og bruksnummer</i>) — free text, never parsed.</summary>
    [StringLength(64)]
    public string? CadastralNumber { get; set; }

    [Range(0, 1_000_000)]
    public decimal? LivingAreaSqm { get; set; }

    [Range(0, 1_000_000)]
    public decimal? PlotAreaSqm { get; set; }

    [Range(1000, 2200)]
    public int? BuildYear { get; set; }

    /// <summary>
    /// The homeowner association's contact id (issue #217) — scalar only, so a request can never create
    /// or mutate a contact. <c>null</c> or omitted means none; because <c>PUT</c> is a full replace, an
    /// omitted value clears a stored link. The target must exist, be an <c>Organization</c> and not be
    /// archived — rules that depend on persisted rows, so the service checks them, and only when the id
    /// changes. The resolved name is on <see cref="ExistingProperty.HomeownerAssociation"/>.
    /// </summary>
    public Guid? HomeownerAssociationId { get; set; }
}
