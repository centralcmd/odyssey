namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the per-user preference store — a key/value surface the client uses for the theme
/// and main currency, and for each list page's persisted UI state.
/// </summary>
/// <remarks>
/// A <c>404</c> means "nothing saved for this key yet", which is normal for a fresh user and must not
/// be confused with a real failure — callers branch on <see cref="ApiResult{T}.Status"/> so they never
/// cache defaults over a user's real saved values.
/// </remarks>
public interface IUserPreferencesApiClient
{
    /// <summary>Reads the value stored under <paramref name="key"/>. <c>404</c> when unset.</summary>
    Task<ApiResult<TValue>> GetAsync<TValue>(string key, CancellationToken ct = default);

    /// <summary>Creates or replaces the value stored under <paramref name="key"/>.</summary>
    Task<ApiResult> PutAsync(string key, object value, CancellationToken ct = default);
}

/// <inheritdoc cref="IUserPreferencesApiClient" />
public sealed class UserPreferencesApiClient(IOdysseyApi api) : IUserPreferencesApiClient
{
    private const string Base = "api/user-preferences";

    public Task<ApiResult<TValue>> GetAsync<TValue>(string key, CancellationToken ct = default) =>
        api.GetAsync<TValue>($"{Base}/{Uri.EscapeDataString(key)}", ct);

    public Task<ApiResult> PutAsync(string key, object value, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{Uri.EscapeDataString(key)}", value, ct);
}
