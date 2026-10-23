using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal;

/// <summary>
/// The credit owed for source-derived location data, derived from <see cref="LocationSource"/> on every
/// read and never stored (issue #350 §5, N6), so data from a provider always carries its licence wherever
/// it is shown. Both providers publish under CC BY 4.0.
/// </summary>
public static class LocationAttributions
{
    public static readonly LocationAttribution Kartverket = new()
    {
        Text = "© Kartverket",
        License = "CC BY 4.0",
        Url = "https://www.kartverket.no/",
    };

    public static readonly LocationAttribution OpenMeteo = new()
    {
        Text = "© Open-Meteo",
        License = "CC BY 4.0",
        Url = "https://open-meteo.com/",
    };

    // A fresh copy per read: the records are mutable DTOs and a shared instance would be one assignment
    // away from rewriting every location's credit.
    public static LocationAttribution? For(LocationSource source) => source switch
    {
        LocationSource.Kartverket => Kartverket with { },
        LocationSource.OpenMeteo => OpenMeteo with { },
        _ => null,
    };

    public static LocationAttribution For(LocationSearchRegion region) =>
        region == LocationSearchRegion.Norway ? Kartverket with { } : OpenMeteo with { };
}
