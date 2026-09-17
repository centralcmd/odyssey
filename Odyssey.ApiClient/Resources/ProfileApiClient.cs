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

    // ── Profile picture (issue #94 §7) ───────────────────────────────────────────
    // Self-scoped: neither write takes a user id, in route or body, so there is no id to tamper with.

    /// <summary>
    /// Attaches or replaces the caller's own picture, returning the new <c>ImageVersion</c>. The
    /// caller must re-key the image URL with it — without that the <c>src</c> string is unchanged and
    /// the browser never re-requests, so a replace would simply not appear.
    /// </summary>
    Task<ApiResult<ProfileImageVersionDto>> UploadImageAsync(ApiUpload image, CancellationToken ct = default);

    /// <summary>Removes the caller's own picture and its bytes. <c>404</c> when there is none.</summary>
    Task<ApiResult> DeleteImageAsync(CancellationToken ct = default);

    /// <summary>
    /// The absolute URL of a user's picture, resolved against the configured API base the same way the
    /// typed clients form request URLs — so it works both behind the nginx <c>/api/</c> proxy and
    /// against an absolute API host. Suitable as an <c>&lt;img src&gt;</c>; the auth cookie rides along.
    ///
    /// <para>
    /// <paramref name="imageVersion"/> is appended as <c>?v=</c>. The server does not bind it at all —
    /// it is purely what makes the URL string change on a replace, which is what makes the browser
    /// re-request and the <c>no-cache</c>/<c>ETag</c> revalidation take effect. That is why the URL is
    /// never hand-built at a call site.
    /// </para>
    /// </summary>
    string ImageUrl(string userId, Guid imageVersion);
}

/// <inheritdoc cref="IProfileApiClient" />
public sealed class ProfileApiClient(IOdysseyApi api) : IProfileApiClient
{
    private const string Url = "api/profile";

    public Task<ApiResult<ProfileDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<ProfileDto>(Url, ct);

    public Task<ApiResult> SaveAsync(ProfileDto profile, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, Url, profile, ct);

    public Task<ApiResult<ProfileImageVersionDto>> UploadImageAsync(ApiUpload image, CancellationToken ct = default) =>
        api.UploadAsync<ProfileImageVersionDto>($"{Url}/image", image, ct: ct);

    public Task<ApiResult> DeleteImageAsync(CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Url}/image", body: null, ct);

    // The READ is its own plural resource, deliberately not under /api/users: it takes a target id and
    // must not appear to inherit that controller's users.read gate.
    public string ImageUrl(string userId, Guid imageVersion) =>
        new Uri(api.BaseAddress!, $"api/profile-images/{Uri.EscapeDataString(userId)}?v={imageVersion}").ToString();
}
