using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Geocoding;

/// <summary>
/// One geocoding provider, serving one <see cref="LocationSearchRegion"/>. Implementations map the
/// provider's response onto <see cref="LocationCandidate"/> fields and nothing more; cleaning, truncation,
/// rounding, time-zone and reference checks and de-duplication are <see cref="LocationSearchService"/>'s,
/// so they apply identically to every provider.
/// </summary>
public interface ILocationSearchSource
{
    LocationSearchRegion Region { get; }

    /// <summary>
    /// Searches the provider. Throws <see cref="GeocodingUnavailableException"/> when nothing usable came
    /// back; a provider made of two services returns the surviving half with <c>Partial</c> set.
    /// </summary>
    Task<LocationSourceResult> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed record LocationSourceResult(IReadOnlyList<LocationCandidate> Candidates, bool Partial);
