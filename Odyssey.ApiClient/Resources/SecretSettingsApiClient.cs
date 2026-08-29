using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the encrypted secret settings endpoints (issue #444):
/// <c>GET /api/system-settings/secrets</c>, <c>PUT</c> and <c>DELETE</c> per key.
///
/// <para>
/// <strong>There is no read of a value here, because the API has no code path that returns one.</strong>
/// <see cref="GetAsync"/> returns status only — key, state and attribution — and both writes return
/// <c>204</c> with no body, so nothing on this interface can hand a caller a credential.
/// </para>
///
/// <para>
/// Every call requires <c>system-settings.read</c>; the two writes additionally require
/// <c>system-settings.security.update</c>, enforced server-side at the action so an unknown key is a
/// <c>403</c> for a caller without the claim and a <c>404</c> for one holding it.
/// </para>
/// </summary>
public interface ISecretSettingsApiClient
{
    /// <summary>One entry per registered key, whether or not a value is stored.</summary>
    Task<ApiResult<List<SecretSettingStatusDto>>> GetAsync(CancellationToken ct = default);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>. No response body.</summary>
    Task<ApiResult> SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Removes the stored value. Idempotent — clearing an absent key also succeeds.</summary>
    Task<ApiResult> ClearAsync(string key, CancellationToken ct = default);
}

/// <inheritdoc cref="ISecretSettingsApiClient" />
public sealed class SecretSettingsApiClient(IOdysseyApi api) : ISecretSettingsApiClient
{
    private const string Url = "api/system-settings/secrets";

    public Task<ApiResult<List<SecretSettingStatusDto>>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<List<SecretSettingStatusDto>>(Url, ct);

    public Task<ApiResult> SetAsync(string key, string value, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Url}/{Uri.EscapeDataString(key)}", new SecretSettingUpdate { Value = value }, ct);

    public Task<ApiResult> ClearAsync(string key, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Url}/{Uri.EscapeDataString(key)}", null, ct);
}
