using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for <c>GET /api/import-limits</c> (issue #343 §7 item 3): the effective import/export
/// volume caps, for any authenticated caller — no permission claim. A <c>503</c> (a degraded read on
/// the server — see <c>ImportExportLimitsLookup</c>/arch N1) surfaces as an ordinary failed
/// <see cref="ApiResult{T}"/>; callers fall back to their own compile-time default constants for that
/// attempt (issue #343 §11 "GET /api/import-limits fails in the client").
/// </summary>
public interface IImportLimitsApiClient
{
    Task<ApiResult<ImportLimitsDto>> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IImportLimitsApiClient" />
public sealed class ImportLimitsApiClient(IOdysseyApi api) : IImportLimitsApiClient
{
    private const string Url = "api/import-limits";

    public Task<ApiResult<ImportLimitsDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<ImportLimitsDto>(Url, ct);
}
