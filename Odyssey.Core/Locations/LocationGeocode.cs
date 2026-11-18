using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Locations;

/// <summary>
/// The optional geocode a contact address or property write carries on the fields path (issue #380 §5.3a,
/// v9): coordinates, a time zone and where they came from. <see cref="LocationResolver.FindOrCreateAsync"/>
/// applies it <b>only when it creates a place</b> — a match on the address key, a merge forwarder's
/// survivor included, links the existing place unchanged, so a geocode never overwrites a place someone
/// else curated.
/// </summary>
public sealed record LocationGeocode(
    decimal Latitude, decimal Longitude, string TimeZoneId, LocationSource Source, string? SourceReference)
{
    public const string LatitudeField = "latitude";
    public const string SourceField = "source";

    /// <summary>
    /// The geocode the five request members describe, validated by the <c>POST /api/locations</c> rules:
    /// <see langword="null"/> when none is sent; the coordinates and time zone all-or-none (<c>422</c> keyed
    /// <c>latitude</c>, which also refuses a source sent without coordinates — there is nothing to attribute);
    /// the ranges and the IANA zone (<c>400</c>, normally already refused by model validation); a source a
    /// caller may assert (<c>422</c> keyed <c>source</c>); and that source's reference shape (<c>400</c>).
    /// Coordinates are rounded to the stored scale.
    /// </summary>
    public static LocationGeocode? From(
        decimal? latitude, decimal? longitude, string? timeZoneId, LocationSource? source, string? sourceReference)
    {
        var zone = LocationTextCleaner.Clean(timeZoneId);
        var reference = LocationTextCleaner.Clean(sourceReference);
        if (latitude is null && longitude is null && zone is null && source is null && reference is null)
        {
            return null;
        }

        if (latitude is not { } lat || longitude is not { } lon || zone is null)
        {
            throw new DomainUnprocessableException(
                "Give the latitude, longitude and time zone together, or none of them.", LatitudeField);
        }

        if (lat is < -90 or > 90)
        {
            throw new DomainValidationException("The latitude must be between -90 and 90.", code: null, field: LatitudeField);
        }

        if (lon is < -180 or > 180)
        {
            throw new DomainValidationException("The longitude must be between -180 and 180.", code: null, field: "longitude");
        }

        if (!IanaTimeZones.IsValid(zone))
        {
            throw new DomainValidationException("The time zone must be an IANA time zone, e.g. Europe/Oslo.", code: null, field: "timeZoneId");
        }

        if (source is not { } known || !LocationRules.IsAssertable(known))
        {
            throw new DomainUnprocessableException(
                "A geocoded address comes from a search or is entered by hand (Manual, Kartverket or OpenMeteo).", SourceField);
        }

        if (!LocationRules.IsValidReference(known, reference))
        {
            throw new DomainValidationException("The source reference is not valid for this source.", code: null, field: "sourceReference");
        }

        return new LocationGeocode(LocationService.Round(lat), LocationService.Round(lon), zone, known, reference);
    }

    /// <summary>Redacted: a geocoded address is where someone lives.</summary>
    public override string ToString() => nameof(LocationGeocode) + " { (redacted) }";
}
