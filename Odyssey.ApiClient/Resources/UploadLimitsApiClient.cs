using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for <c>GET /api/upload-limits</c> (issue #421 Wave 4): the effective upload cap, for
/// any authenticated caller — no permission claim, because every upload dialog needs it and they are
/// used by roles holding no system-settings claim. A <c>503</c> (a degraded read on the server)
/// surfaces as an ordinary failed <see cref="ApiResult{T}"/>, and the caller falls back to the shipped
/// default for that attempt.
/// </summary>
public interface IUploadLimitsApiClient
{
    Task<ApiResult<UploadLimitsDto>> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IUploadLimitsApiClient" />
public sealed class UploadLimitsApiClient(IOdysseyApi api) : IUploadLimitsApiClient
{
    private const string Url = "api/upload-limits";

    public Task<ApiResult<UploadLimitsDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<UploadLimitsDto>(Url, ct);
}
