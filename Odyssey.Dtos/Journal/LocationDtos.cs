using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.RegularExpressions;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Where a location's coordinates came from (issue #350 §4). <b>Advisory provenance</b> — asserted by the
/// client and never trusted for anything. <c>0</c> is a permanent hole so an omitted value cannot bind to a
/// real member, and an ordinal is never reused.
/// </summary>
public enum LocationSource
{
    Manual = 1,
    Kartverket = 2,
    OpenMeteo = 3,

    /// <summary>Created by the one-time conversion of a free-text event, series or entry place (issue #380 §11).</summary>
    Converted = 4,

    /// <summary>Created from a contact's postal address (issue #380): by the conversion, the resolver or vCard import.</summary>
    ContactAddress = 5,

    /// <summary>Created from a real-estate property's address (issue #380).</summary>
    PropertyAddress = 6,

    /// <summary>Created by an ICS import (issue #380 §8.4).</summary>
    Imported = 7,
}

/// <summary>Which geocoding provider receives a search. Only the selected one ever sees the query.</summary>
public enum LocationSearchRegion
{
    Norway = 1,
    World = 2,
}

/// <summary>What a geocoding candidate is: a street address, a place name, or a locality (town, area).</summary>
public enum LocationCandidateKind
{
    Address = 1,
    PlaceName = 2,
    Locality = 3,
}

public enum LocationSortBy
{
    Name,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// The shared limits and rules of the location contract — one declaration named by the DTO attributes, the
/// service and the geocoding normalization, so no two of them can disagree.
/// </summary>
public static partial class LocationRules
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 1000;
    public const int AddressTextMaxLength = 300;
    public const int SourceReferenceMaxLength = 64;

    // The structured address fields (issue #380 §4). Declared here since a location became the one address
    // record; ContactMethodLimits names these, so the contact DTOs and the location columns cannot drift.
    public const int AddressLineMaxLength = 256;
    public const int PostalCodeMaxLength = 32;
    public const int CityMaxLength = 128;
    public const int RegionMaxLength = 128;
    public const int CountryCodeLength = 2;

    /// <summary>Display-only: a candidate's description is never stored, so it has its own, shorter cap.</summary>
    public const int CandidateDescriptionMaxLength = 200;

    public const int AltitudeMin = -500;
    public const int AltitudeMax = 9000;

    /// <summary>Coordinates are stored to 5 decimal places (≈ 1 m).</summary>
    public const int CoordinateScale = 5;

    public const int QueryMaxLength = 100;

    /// <summary>The upper bound of every minimum, i.e. the attribute floor before the per-region rule.</summary>
    public const int QueryMinLengthAnyRegion = 2;

    /// <summary>
    /// The per-region minimum query length, counted after <see cref="NormalizeQuery"/> (N12 amendment):
    /// 2 for Norway so <c>Ål</c>, <c>Os</c> and <c>Ås</c> can be found, 3 for World.
    /// </summary>
    public static int MinQueryLength(LocationSearchRegion region) => region == LocationSearchRegion.Norway ? 2 : 3;

    /// <summary>The search-cache normalization (§3): trim, collapse whitespace, NFC, invariant lower case.</summary>
    public static string NormalizeQuery(string? query) =>
        Whitespace().Replace((query ?? string.Empty).Normalize(NormalizationForm.FormC).Trim(), " ").ToLowerInvariant();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex("^[0-9A-Za-z-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KartverketReference();

    [GeneratedRegex("^[0-9]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenMeteoReference();

    /// <summary>
    /// Whether <paramref name="reference"/> has the provider-specific safe shape for <paramref name="source"/>
    /// (§8, S2/N5). A <see cref="LocationSource.Manual"/> location carries no reference at all. The shape is
    /// constrained now so a later "refresh from source" never has to accept an arbitrary stored string — that
    /// feature must still re-validate before use.
    /// </summary>
    public static bool IsValidReference(LocationSource source, string? reference) => source switch
    {
        LocationSource.Manual => reference is null,
        LocationSource.Kartverket => reference is null || KartverketReference().IsMatch(reference),
        LocationSource.OpenMeteo => reference is null || OpenMeteoReference().IsMatch(reference),
        // The issue #380 sources (Converted … Imported) are set by the application and carry no reference
        // (§4, A-R9). Whether a caller may assert one at all is IsAssertable's question, not this one's.
        LocationSource.Converted or LocationSource.ContactAddress or LocationSource.PropertyAddress
            or LocationSource.Imported => reference is null,
        _ => false,
    };

    /// <summary>
    /// Whether a caller may assert <paramref name="source"/> (issue #380 §4, A-R9): only the three a person
    /// or a geocoder produces. The other four are set by the application. A <c>PUT</c> may also keep the
    /// stored value, whatever it is — that rule needs the stored row, so the service applies it.
    /// </summary>
    public static bool IsAssertable(LocationSource source) =>
        source is LocationSource.Manual or LocationSource.Kartverket or LocationSource.OpenMeteo;

    /// <summary>The <c>countryCode</c> shape (§8.3): two ASCII letters, either case; stored upper-cased.</summary>
    public const string CountryCodePattern = "^[A-Za-z]{2}$";

    internal static IEnumerable<ValidationResult> ValidateReference(LocationSource? source, string? reference)
    {
        if (source is { } known && !IsValidReference(known, reference))
        {
            yield return new ValidationResult(
                known == LocationSource.Manual
                    ? "A location entered by hand carries no source reference."
                    : $"The source reference is not a valid {known} reference.",
                [nameof(NewLocation.SourceReference)]);
        }
    }
}

/// <summary>Credit for source-derived data, derived from <see cref="LocationSource"/> on every read.</summary>
public sealed record LocationAttribution
{
    public required string Text { get; set; }

    public required string License { get; set; }

    public required string Url { get; set; }
}

public sealed record ExistingLocation
{
    public required Guid LocationId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public string? AddressText { get; set; }

    // The structured address (issue #380 §4). Optional; a location is the one address record.
    public string? Line1 { get; set; }

    public string? Line2 { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? Region { get; set; }

    public string? CountryCode { get; set; }

    /// <summary>
    /// Null together with <see cref="Longitude"/> and <see cref="TimeZoneId"/> when the place has no
    /// coordinates — a converted or address-derived place until someone geocodes it (issue #380 §5.5).
    /// </summary>
    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public int? AltitudeMeters { get; set; }

    public string? TimeZoneId { get; set; }

    /// <summary>Whether the three coordinate fields are set (they are all-or-none).</summary>
    public bool HasCoordinates { get; set; }

    /// <summary>
    /// Set on a location merged away into another (§5.6): archived, frozen, and deletable only. A later
    /// find-or-create of its address links to the survivor.
    /// </summary>
    public Guid? MergedIntoLocationId { get; set; }

    /// <summary>
    /// How many records of each kind use this place (issue #380 §5.5, decision 6). A kind the caller cannot
    /// read is <see langword="null"/>, never 0, and there is deliberately no total.
    /// </summary>
    public LocationUsage? Usage { get; set; }

    /// <summary>
    /// The weather point this place links to (issue #387 §5 endpoint 11), with the four flags that decide which
    /// weather sections the record draws. Null when unlinked, and null for a caller without <c>weather.read</c>.
    /// </summary>
    public LinkedWeatherPointRef? WeatherPoint { get; set; }

    /// <summary>Whole kilometres to the linked point's current site; null when unlinked, without coordinates or without <c>weather.read</c>.</summary>
    public int? WeatherPointDistanceKm { get; set; }

    /// <summary>
    /// Opaque: <c>WeatherRules.Fingerprint</c> of the place's rounded position, altitude and time zone — what a
    /// weather point copies from it. Sent back as <c>expectedLocationFingerprint</c>; null without coordinates.
    /// </summary>
    public string? WeatherFingerprint { get; set; }

    public LocationSource Source { get; set; }

    public string? SourceReference { get; set; }

    /// <summary>Null for <see cref="LocationSource.Manual"/>. Derived, never stored (N6).</summary>
    public LocationAttribution? Attribution { get; set; }

    public DateTime? Archived { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Redacted: a record prints its members, and MVC logs bound action arguments at Trace — which would
    /// write the address, name and description into the log (issue #350 §7.7).
    /// </summary>
    public override string ToString() => nameof(ExistingLocation) + " { (redacted) }";
}

public sealed record NewLocation : IValidatableObject
{
    [Required]
    [StringLength(LocationRules.NameMaxLength, MinimumLength = 1)]
    public required string Name { get; set; }

    [StringLength(LocationRules.DescriptionMaxLength)]
    public string? Description { get; set; }

    [StringLength(LocationRules.AddressTextMaxLength)]
    public string? AddressText { get; set; }

    [StringLength(LocationRules.AddressLineMaxLength)]
    public string? Line1 { get; set; }

    [StringLength(LocationRules.AddressLineMaxLength)]
    public string? Line2 { get; set; }

    [StringLength(LocationRules.PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [StringLength(LocationRules.CityMaxLength)]
    public string? City { get; set; }

    [StringLength(LocationRules.RegionMaxLength)]
    public string? Region { get; set; }

    [StringLength(LocationRules.CountryCodeLength)]
    [RegularExpression(LocationRules.CountryCodePattern)]
    public string? CountryCode { get; set; }

    /// <summary>
    /// The coordinates and time zone are optional since issue #380, but all-or-none: a partial set is a
    /// <c>422</c> keyed <c>latitude</c>, decided by the service.
    /// </summary>
    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Latitude { get; set; }

    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Longitude { get; set; }

    [Range(LocationRules.AltitudeMin, LocationRules.AltitudeMax)]
    public int? AltitudeMeters { get; set; }

    [StringLength(IanaTimeZones.MaxLength)]
    [IanaTimeZone]
    public string? TimeZoneId { get; set; }

    /// <summary>Nullable so an omitted value is a clear <c>400</c> rather than binding to the hole at 0.</summary>
    [Required]
    [EnumDataType(typeof(LocationSource))]
    public LocationSource? Source { get; set; }

    [StringLength(LocationRules.SourceReferenceMaxLength)]
    public string? SourceReference { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        LocationRules.ValidateReference(Source, SourceReference);

    /// <summary>
    /// Redacted: a record prints its members, and MVC logs bound action arguments at Trace — which would
    /// write the address, name and description into the log (issue #350 §7.7).
    /// </summary>
    public override string ToString() => nameof(NewLocation) + " { (redacted) }";
}

/// <summary>Full replacement (§5 endpoint 4): <see cref="Archived"/> archives or restores. Last writer wins.</summary>
public sealed record UpdateLocation : IValidatableObject
{
    [Required]
    [StringLength(LocationRules.NameMaxLength, MinimumLength = 1)]
    public required string Name { get; set; }

    [StringLength(LocationRules.DescriptionMaxLength)]
    public string? Description { get; set; }

    [StringLength(LocationRules.AddressTextMaxLength)]
    public string? AddressText { get; set; }

    [StringLength(LocationRules.AddressLineMaxLength)]
    public string? Line1 { get; set; }

    [StringLength(LocationRules.AddressLineMaxLength)]
    public string? Line2 { get; set; }

    [StringLength(LocationRules.PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [StringLength(LocationRules.CityMaxLength)]
    public string? City { get; set; }

    [StringLength(LocationRules.RegionMaxLength)]
    public string? Region { get; set; }

    [StringLength(LocationRules.CountryCodeLength)]
    [RegularExpression(LocationRules.CountryCodePattern)]
    public string? CountryCode { get; set; }

    /// <summary>
    /// The coordinates and time zone are optional since issue #380, but all-or-none: a partial set is a
    /// <c>422</c> keyed <c>latitude</c>, decided by the service.
    /// </summary>
    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Latitude { get; set; }

    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Longitude { get; set; }

    [Range(LocationRules.AltitudeMin, LocationRules.AltitudeMax)]
    public int? AltitudeMeters { get; set; }

    [StringLength(IanaTimeZones.MaxLength)]
    [IanaTimeZone]
    public string? TimeZoneId { get; set; }

    [Required]
    [EnumDataType(typeof(LocationSource))]
    public LocationSource? Source { get; set; }

    [StringLength(LocationRules.SourceReferenceMaxLength)]
    public string? SourceReference { get; set; }

    public bool Archived { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        LocationRules.ValidateReference(Source, SourceReference);

    /// <summary>
    /// Redacted: a record prints its members, and MVC logs bound action arguments at Trace — which would
    /// write the address, name and description into the log (issue #350 §7.7).
    /// </summary>
    public override string ToString() => nameof(UpdateLocation) + " { (redacted) }";
}

/// <summary>
/// <c>GET /api/locations</c>. <see cref="Status"/> is the shared <see cref="ArchivalStatus"/>, as on
/// contacts: omitted returns every row, and the page sends <c>Active</c> by default (issue #350 amendment,
/// which replaced the original <c>IncludeArchived</c>). <c>Search</c> is a local filter over stored rows and
/// never leaves the deployment.
/// </summary>
public sealed class LocationsQueryParams : QueryParams<LocationSortBy>
{
    public ArchivalStatus? Status { get; set; }

    /// <summary><c>true</c>: only places with coordinates; <c>false</c>: only places without; omitted: both.</summary>
    public bool? HasCoordinates { get; set; }

    /// <summary>
    /// <c>true</c>: only places something references; <c>false</c>: only unused ones. "Referenced" is any
    /// reference the delete guard counts — records or a merge forwarder (issue #380 §5.5, decision 9). A weather
    /// link is not a reference (issue #387 §8).
    /// </summary>
    public bool? Referenced { get; set; }
}

/// <summary>
/// Per-kind use counts of a location (issue #380 §5.5). Each is filled only when the caller holds that
/// kind's read claim, and is <see langword="null"/> otherwise. A recurring series' generated occurrences
/// count as calendar events, and the series once more in <see cref="RecurringSeries"/>. Merge forwarders are
/// not records and are not counted; a weather link is not a use at all.
/// </summary>
public sealed record LocationUsage
{
    public int? Contacts { get; set; }

    public int? Properties { get; set; }

    public int? CalendarEvents { get; set; }

    public int? RecurringSeries { get; set; }

    public int? JournalEntries { get; set; }
}

/// <summary>
/// The read embed of a linked location on an event, series or entry (issue #380 §5.1): the name, the archived
/// flag and whether it has coordinates — nothing else. <see langword="null"/> without <c>locations.read</c>.
/// </summary>
public sealed record LocationRef
{
    public required Guid LocationId { get; set; }

    public required string Name { get; set; }

    public bool Archived { get; set; }

    public bool HasCoordinates { get; set; }

    /// <summary>Redacted: the name is a place, and a record prints its members wherever it is logged.</summary>
    public override string ToString() => nameof(LocationRef) + " { (redacted) }";
}

/// <summary><c>POST /api/locations/{sourceId}/merge</c> (issue #380 §5.6).</summary>
public sealed record MergeLocationRequest
{
    /// <summary>The surviving location. Nullable so an omitted value is a <c>400</c>, not <see cref="Guid.Empty"/>.</summary>
    [Required]
    public Guid? TargetLocationId { get; set; }
}

/// <summary>
/// How an address edit on a contact or a property applies to the shared place (issue #380 §5.3, v6).
/// </summary>
public enum AddressApplyTo
{
    /// <summary>Copy-on-write: find or create the place for the new fields and move only this record's link.</summary>
    ThisRecord = 1,

    /// <summary>Edit the linked place in place, for every record using it. Needs <c>locations.update</c>.</summary>
    AllUses = 2,
}

/// <summary>
/// <c>POST /api/locations/search</c>. The text travels in the BODY, never a URL, so no access log, hosting
/// log line, browser history or trace attribute records it (B2, S1).
/// </summary>
public sealed record LocationSearchRequest : IValidatableObject
{
    [Required]
    [StringLength(LocationRules.QueryMaxLength, MinimumLength = LocationRules.QueryMinLengthAnyRegion)]
    public required string Query { get; set; }

    [Required]
    [EnumDataType(typeof(LocationSearchRegion))]
    public LocationSearchRegion? Region { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Region is not { } region)
        {
            yield break;
        }

        var length = LocationRules.NormalizeQuery(Query).Length;
        var min = LocationRules.MinQueryLength(region);
        if (length < min)
        {
            yield return new ValidationResult(
                $"Type at least {min} characters to search {(region == LocationSearchRegion.Norway ? "in Norway" : "worldwide")}.",
                [nameof(Query)]);
        }
    }

    /// <summary>
    /// Redacted: MVC logs bound action arguments at Trace, and a record prints its members — so without
    /// this the search text would reach the log the body-not-URL design keeps it out of (B2, S1).
    /// </summary>
    public override string ToString() => nameof(LocationSearchRequest) + " { (redacted) }";
}

public sealed record LocationCandidate
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public LocationCandidateKind Kind { get; set; }

    public string? AddressText { get; set; }

    public decimal Latitude { get; set; }

    public decimal Longitude { get; set; }

    public int? AltitudeMeters { get; set; }

    /// <summary>Null when the provider's zone is not one this server resolves; the client then asks for one.</summary>
    public string? TimeZoneId { get; set; }

    public LocationSource Source { get; set; }

    public string? SourceReference { get; set; }

    // The structured address of a Kartverket ADDRESS result (issue #380 §5.5a, v9): adressetekst, postnummer
    // and poststed as returned, and "NO". Null on a place name or an Open-Meteo locality. Cleaned and capped
    // to the location column limits, so a picked candidate always fits a NewAddress or a NewLocation.

    public string? Line1 { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? CountryCode { get; set; }

    /// <summary>Redacted for the same reason as <see cref="LocationSearchRequest"/>: it echoes what was searched.</summary>
    public override string ToString() => nameof(LocationCandidate) + " { (redacted) }";
}

public sealed record LocationSearchResponse
{
    public LocationSearchRegion Region { get; set; }

    public IReadOnlyList<LocationCandidate> Candidates { get; set; } = [];

    /// <summary>One of the two Kartverket services failed; the other's results are returned and not cached.</summary>
    public bool Partial { get; set; }

    public required LocationAttribution Attribution { get; set; }
}
