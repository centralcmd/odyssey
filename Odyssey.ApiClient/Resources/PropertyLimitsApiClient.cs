using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for <c>GET /api/property-limits</c> (issue #167): the effective per-property limits,
/// for any authenticated caller — no permission claim, because a property's smart-tag section needs
/// the cap and is used by roles holding no system-settings claim.
/// </summary>
/// <remarks>
/// A <c>503</c> means the server's read is <b>degraded</b>; it surfaces as an ordinary failed
/// <see cref="ApiResult{T}"/>, so a caller can tell "the cap is N" from "the cap is unknown".
/// </remarks>
public interface IPropertyLimitsApiClient
{
    Task<ApiResult<PropertyLimitsDto>> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IPropertyLimitsApiClient" />
public sealed class PropertyLimitsApiClient(IOdysseyApi api) : IPropertyLimitsApiClient
{
    private const string Url = "api/property-limits";

    public Task<ApiResult<PropertyLimitsDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<PropertyLimitsDto>(Url, ct);
}
