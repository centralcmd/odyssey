using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the admin-configurable runtime settings endpoints (issue #349):
/// <c>GET</c>/<c>PUT /api/system-settings</c>. Both require <c>system-settings.read</c>; <c>PUT</c>'s
/// per-field write claims are enforced server-side, not here — the caller is responsible for sending
/// <c>null</c> on <see cref="SystemSettingsUpdate"/> for every field it lacks the matching claim for.
/// </summary>
public interface ISystemSettingsApiClient
{
    Task<ApiResult<SystemSettingsDto>> GetAsync(CancellationToken ct = default);

    /// <summary>Whole-resource PUT. Returns the refreshed <see cref="SystemSettingsDto"/> on success.</summary>
    Task<ApiResult<SystemSettingsDto>> UpdateAsync(SystemSettingsUpdate update, CancellationToken ct = default);
}

/// <inheritdoc cref="ISystemSettingsApiClient" />
public sealed class SystemSettingsApiClient(IOdysseyApi api) : ISystemSettingsApiClient
{
    private const string Url = "api/system-settings";

    public Task<ApiResult<SystemSettingsDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<SystemSettingsDto>(Url, ct);

    public Task<ApiResult<SystemSettingsDto>> UpdateAsync(SystemSettingsUpdate update, CancellationToken ct = default) =>
        api.SendAsync<SystemSettingsDto>(HttpMethod.Put, Url, update, ct);
}
