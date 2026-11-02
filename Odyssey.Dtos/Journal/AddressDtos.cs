using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Create/replace payload for a contact address (issue #325 §7). Since issue #380 an address is a link to
/// a <c>Location</c>, given <b>either</b> by <see cref="LocationId"/> or by the six fields (§5.3):
/// <list type="bullet">
/// <item>by id: needs <c>locations.read</c> when it changes the link, and an address-complete place;</item>
/// <item>by fields: needs <c>locations.create</c>, and <see cref="Line1"/>, <see cref="City"/> and
/// <see cref="CountryCode"/> — required by the service (<c>422</c> keyed on the first one missing), because
/// the id path leaves them out.</item>
/// </list>
/// Sending both is allowed only when the fields equal the linked place's; otherwise <c>422</c> keyed
/// <c>locationId</c>. That is what lets a client round-trip an <see cref="ExistingAddress"/> unchanged.
/// </summary>
public sealed record NewAddress
{
    [Required]
    [EnumDataType(typeof(AddressLabel))]
    public AddressLabel Label { get; set; }

    public bool IsPrimary { get; set; }

    /// <summary>The place to link, by id. See the type's remarks for the either/or rule.</summary>
    public Guid? LocationId { get; set; }

    [StringLength(ContactMethodLimits.AddressLineMaxLength)]
    public string? Line1 { get; set; }

    [StringLength(ContactMethodLimits.AddressLineMaxLength)]
    public string? Line2 { get; set; }

    [StringLength(ContactMethodLimits.CityMaxLength)]
    public string? City { get; set; }

    [StringLength(ContactMethodLimits.PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [StringLength(ContactMethodLimits.RegionMaxLength)]
    public string? Region { get; set; }

    [StringLength(ContactMethodLimits.CountryCodeLength)]
    [RegularExpression(LocationRules.CountryCodePattern)]
    public string? CountryCode { get; set; }

    /// <summary>
    /// How a change to the fields applies to the shared place (§5.3, v6). Only the address <c>PUT</c> reads
    /// it, and only when the fields change; it is then required (<c>422</c> keyed <c>applyTo</c>). A change of
    /// case or whitespace only is applied in place whatever this says.
    /// </summary>
    [EnumDataType(typeof(AddressApplyTo))]
    public AddressApplyTo? ApplyTo { get; set; }

    /// <summary>
    /// The <see cref="ExistingAddress.OtherUses"/> the client showed. Required whenever the write edits the
    /// place in place; the server recounts under a row lock and refuses a different number with <c>409</c>
    /// keyed <c>applyTo</c> (§5.3, V6-2).
    /// </summary>
    [Range(0, int.MaxValue)]
    public int? ExpectedOtherUses { get; set; }

    // ── Geocode (issue #380 §5.3a, v9): optional and write-only ─────────────────────────────────────
    // Used only when the fields path CREATES a place: a match on the address key links the existing place
    // and ignores them, so a geocode never overwrites a place someone else curated. Ignored with a
    // LocationId and by an in-place (AllUses or case-only) edit. The coordinates and time zone are
    // all-or-none (422 keyed latitude) and Source is Manual, Kartverket or OpenMeteo (422 keyed source).

    /// <summary>The geocoded latitude of a place this write creates. See the geocode rules above.</summary>
    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Latitude { get; set; }

    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Longitude { get; set; }

    [StringLength(IanaTimeZones.MaxLength)]
    [IanaTimeZone]
    public string? TimeZoneId { get; set; }

    [EnumDataType(typeof(LocationSource))]
    public LocationSource? Source { get; set; }

    [StringLength(LocationRules.SourceReferenceMaxLength)]
    public string? SourceReference { get; set; }

    /// <summary>Redacted: an address is personal data, and a record prints its members wherever it is logged.</summary>
    public override string ToString() => nameof(NewAddress) + " { (redacted) }";
}

/// <summary>
/// Read projection of a contact address (issue #325 §7). The fields are projected from the linked place
/// under <c>contacts.read</c> alone (issue #380 AC 5).
/// </summary>
public sealed record ExistingAddress
{
    public required Guid Id { get; set; }
    public required Guid ContactId { get; set; }
    public AddressLabel Label { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>The linked place (issue #380).</summary>
    public Guid LocationId { get; set; }

    [StringLength(ContactMethodLimits.AddressLineMaxLength)]
    public required string Line1 { get; set; }

    [StringLength(ContactMethodLimits.AddressLineMaxLength)]
    public string? Line2 { get; set; }

    [StringLength(ContactMethodLimits.CityMaxLength)]
    public required string City { get; set; }

    [StringLength(ContactMethodLimits.PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [StringLength(ContactMethodLimits.RegionMaxLength)]
    public string? Region { get; set; }

    [StringLength(ContactMethodLimits.CountryCodeLength)]
    public required string CountryCode { get; set; }

    /// <summary>
    /// How many OTHER records use the same place — this row excluded — summed over only the kinds the caller
    /// can read (issue #380 §5.3, decision 6). An integer, never a list. Ignored in a request body.
    /// </summary>
    public int OtherUses { get; set; }

    /// <summary>
    /// Whether the place has map coordinates (§5.3, v7), so the client can warn that an <c>AllUses</c> edit
    /// keeps them. Says only that it is geocoded, never where. Ignored in a request body.
    /// </summary>
    public bool PlaceHasCoordinates { get; set; }

    /// <summary>Redacted: an address is personal data, and a record prints its members wherever it is logged.</summary>
    public override string ToString() => nameof(ExistingAddress) + " { (redacted) }";
}
