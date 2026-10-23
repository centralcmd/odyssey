using System.Security.Cryptography;
using System.Text;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Geocoding;

/// <summary>
/// <c>POST /api/locations/search</c> (issue #350 §3): picks the region's provider, normalizes and
/// de-duplicates its candidates, and caches complete results. Nothing is written to the database, and the
/// query is never logged or persisted — the cache key is its SHA-256.
/// </summary>
public sealed class LocationSearchService
{
    public const int MaxCandidates = 20;

    private readonly IReadOnlyDictionary<LocationSearchRegion, ILocationSearchSource> sources;
    private readonly LocationSearchCache cache;

    public LocationSearchService(IEnumerable<ILocationSearchSource> sources, LocationSearchCache cache)
    {
        this.sources = sources.ToDictionary(source => source.Region);
        this.cache = cache;
    }

    /// <summary>
    /// A complete result already cached for this search, without calling a provider. The controller asks
    /// first, so a cache hit spends none of the instance-wide budget — that budget protects the providers'
    /// fair-use limits, and a cache hit never reaches them.
    /// </summary>
    public bool TryGetCached(LocationSearchRequest request, out LocationSearchResponse? response)
    {
        var (_, _, key) = Resolve(request);
        return cache.TryGet(key, out response) && response is not null;
    }

    public async Task<LocationSearchResponse> SearchAsync(LocationSearchRequest request, CancellationToken cancellationToken = default)
    {
        var (region, query, key) = Resolve(request);
        if (cache.TryGet(key, out var cached) && cached is not null)
        {
            return cached;
        }

        // The shared call runs on no caller's token; each caller stops waiting on its own.
        return await cache.Join(key, () => RunAsync(key, region, query)).WaitAsync(cancellationToken);
    }

    private static (LocationSearchRegion Region, string Query, string Key) Resolve(LocationSearchRequest request)
    {
        var region = request.Region ?? throw new DomainValidationException("A region is required.", code: null, field: "region");
        var query = LocationRules.NormalizeQuery(request.Query);
        if (query.Length < LocationRules.MinQueryLength(region) || query.Length > LocationRules.QueryMaxLength)
        {
            throw new DomainValidationException("The search text is too short or too long.", code: null, field: "query");
        }

        return (region, query, $"{region}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)))}");
    }

    private async Task<LocationSearchResponse> RunAsync(string key, LocationSearchRegion region, string query)
    {
        var source = sources[region];
        var result = await source.SearchAsync(query, CancellationToken.None);
        var response = new LocationSearchResponse
        {
            Region = region,
            Candidates = Normalize(result.Candidates),
            Partial = result.Partial,
            Attribution = LocationAttributions.For(region),
        };

        if (!response.Partial)
        {
            cache.Set(key, response);
        }

        return response;
    }

    /// <summary>
    /// §8 Candidate normalization: provider text cleaned and truncated to the ENTITY limits (so a picked
    /// candidate always fits <see cref="NewLocation"/>), coordinates rounded to 5 decimals, a time zone this
    /// server cannot resolve replaced by null (the client asks for one — never a 400 the user cannot fix),
    /// an altitude outside the entity range dropped, a reference of the wrong shape dropped, then
    /// de-duplicated on rounded coordinates + normalized name (first wins) and capped.
    /// </summary>
    internal static IReadOnlyList<LocationCandidate> Normalize(IEnumerable<LocationCandidate> raw)
    {
        var seen = new HashSet<(decimal, decimal, string)>();
        var output = new List<LocationCandidate>();
        foreach (var candidate in raw)
        {
            var name = LocationText.CleanAndTruncate(candidate.Name, LocationRules.NameMaxLength);
            if (name is null
                || candidate.Latitude is < -90 or > 90
                || candidate.Longitude is < -180 or > 180)
            {
                continue;
            }

            var lat = LocationService.Round(candidate.Latitude);
            var lon = LocationService.Round(candidate.Longitude);
            if (!seen.Add((lat, lon, LocationRules.NormalizeQuery(name))))
            {
                continue;
            }

            var reference = LocationText.Clean(candidate.SourceReference);
            output.Add(new LocationCandidate
            {
                Kind = candidate.Kind,
                Name = name,
                Description = LocationText.CleanAndTruncate(candidate.Description, LocationRules.CandidateDescriptionMaxLength),
                AddressText = LocationText.CleanAndTruncate(candidate.AddressText, LocationRules.AddressTextMaxLength),
                Latitude = lat,
                Longitude = lon,
                AltitudeMeters = candidate.AltitudeMeters is >= LocationRules.AltitudeMin and <= LocationRules.AltitudeMax
                    ? candidate.AltitudeMeters
                    : null,
                TimeZoneId = IanaTimeZones.IsValid(candidate.TimeZoneId) ? candidate.TimeZoneId : null,
                Source = candidate.Source,
                SourceReference = reference is not null && LocationRules.IsValidReference(candidate.Source, reference) ? reference : null,
            });

            if (output.Count == MaxCandidates)
            {
                break;
            }
        }

        return output;
    }
}
