using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the self-service profile endpoints (issue #316): <c>GET</c>/<c>PUT /api/profile</c>,
/// always the authenticated caller's own row. <see cref="GetAsync"/> distinguishes a successful load
/// (even an empty, incomplete profile) from a transport failure so the onboarding gate can fail open.
/// </summary>
public interface IProfileApiClient
{
    /// <summary>
    /// Loads the current user's profile. The result is a failure only on a transport/parse failure
    /// (the onboarding gate treats that as fail-open); a brand-new user loads a non-null, incomplete
    /// profile as a success.
    /// </summary>
    Task<ApiResult<ProfileDto>> GetAsync(CancellationToken ct = default);

    /// <summary>Saves the profile. On failure the result carries the parsed problem (400 → inline errors).</summary>
    Task<ApiResult> SaveAsync(ProfileDto profile, CancellationToken ct = default);
}

/// <inheritdoc cref="IProfileApiClient" />
public sealed class ProfileApiClient(IOdysseyApi api) : IProfileApiClient
{
    private const string Url = "api/profile";

    public Task<ApiResult<ProfileDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<ProfileDto>(Url, ct);

    public Task<ApiResult> SaveAsync(ProfileDto profile, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, Url, profile, ct);
}
