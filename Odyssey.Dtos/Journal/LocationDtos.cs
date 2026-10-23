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
        _ => false,
    };

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

    public decimal Latitude { get; set; }

    public decimal Longitude { get; set; }

    public int? AltitudeMeters { get; set; }

    public required string TimeZoneId { get; set; }

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

    [Required]
    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Latitude { get; set; }

    [Required]
    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Longitude { get; set; }

    [Range(LocationRules.AltitudeMin, LocationRules.AltitudeMax)]
    public int? AltitudeMeters { get; set; }

    [Required]
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

    [Required]
    [Range(typeof(decimal), "-90", "90", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Latitude { get; set; }

    [Required]
    [Range(typeof(decimal), "-180", "180", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Longitude { get; set; }

    [Range(LocationRules.AltitudeMin, LocationRules.AltitudeMax)]
    public int? AltitudeMeters { get; set; }

    [Required]
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
