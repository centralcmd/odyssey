using Odyssey.Dtos.Journal;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for <c>GET /api/map-styles</c> (issue #370): the location map's basemap style URLs and
/// attribution, gated on <c>locations.read</c>. The response is cacheable for five minutes, so a caller
/// fetches it once per session.
/// </summary>
public interface IMapStylesApiClient
{
    Task<ApiResult<MapStyles>> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IMapStylesApiClient" />
public sealed class MapStylesApiClient(IOdysseyApi api) : IMapStylesApiClient
{
    private const string Url = "api/map-styles";

    public Task<ApiResult<MapStyles>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<MapStyles>(Url, ct);
}
