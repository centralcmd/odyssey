using Odyssey.Dtos.Application;
using Odyssey.Dtos;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for the user-administration endpoints (<c>users.read</c> / <c>users.update</c> /
/// <c>users.delete</c>), plus the role catalogue the Roles page renders.
/// </summary>
/// <remarks>
/// Deletion is identity-only — purging a user's domain data is a separate, still-pending concern, so
/// this client deliberately exposes no cascade.
/// </remarks>
public interface IUsersApiClient
{
    Task<ApiResult<PagedResult<ExistingUser>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? roles = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default);

    Task<ApiResult<List<ExistingUser>>> ListAllAsync(CancellationToken ct = default);

    Task<ApiResult<ExistingUser>> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The role catalogue with each role's permission claims — read by both the Users and Roles pages.
    /// Not paginated server-side.
    /// </summary>
    Task<ApiResult<List<ExistingRole>>> ListRolesAsync(CancellationToken ct = default);

    /// <summary>Patches the account flags (confirmed, locked out, approved).</summary>
    Task<ApiResult> UpdateAsync(string id, UpdatedUser user, CancellationToken ct = default);

    /// <summary>Replaces the user's role. Separate from <see cref="UpdateAsync"/> server-side.</summary>
    Task<ApiResult> UpdateRoleAsync(string id, UpdatedUserRole role, CancellationToken ct = default);

    /// <summary>
    /// Triggers an admin-initiated password reset for <paramref name="id"/> (issue #406): the target is
    /// mailed a reset link, their live sessions are revoked, and they must set a new password before the
    /// account can be used again. No temporary password exists at any point.
    /// </summary>
    /// <remarks>
    /// The empty body is the contract — there is nothing for the caller to supply. Four outcomes the
    /// caller must keep apart, because they mean different things to the admin: <c>200</c> with
    /// <see cref="PasswordResetDispatch.EmailDelivered"/> <c>true</c> (sent), <c>200</c> with
    /// <c>false</c> (the reset was applied but the relay refused the message — tell the user to use
    /// Forgot password), <c>422</c> (no confirmed address, nothing happened), and <c>429</c> (throttled,
    /// nothing happened). A failed send is deliberately not an error status: the state change is
    /// committed and retrying the whole call would be wrong.
    /// </remarks>
    Task<ApiResult<PasswordResetDispatch>> SendPasswordResetAsync(string id, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default);
}

/// <inheritdoc cref="IUsersApiClient" />
public sealed class UsersApiClient(IOdysseyApi api) : IUsersApiClient
{
    private const string Base = "api/users";

    public Task<ApiResult<PagedResult<ExistingUser>>> ListAsync(
        int page, int pageSize, string? search = null, IReadOnlyCollection<string>? roles = null,
        string? sortBy = null, string? sortDir = null, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingUser>(
            PagedQuery.For(Base)
                .Window(page, pageSize)
                .Add("search", search)
                .AddMany("roles", roles)
                .Add("sortBy", sortBy)
                .Add("sortDir", sortDir)
                .Build(),
            ct);

    public Task<ApiResult<List<ExistingUser>>> ListAllAsync(CancellationToken ct = default) =>
        api.GetAllAsync<ExistingUser>(PagedQuery.For(Base).Build(), ct);

    public Task<ApiResult<ExistingUser>> GetAsync(string id, CancellationToken ct = default) =>
        api.GetAsync<ExistingUser>($"{Base}/{Uri.EscapeDataString(id)}", ct);

    public Task<ApiResult<List<ExistingRole>>> ListRolesAsync(CancellationToken ct = default) =>
        api.GetAsync<List<ExistingRole>>($"{Base}/roles", ct);

    public Task<ApiResult> UpdateAsync(string id, UpdatedUser user, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Patch, $"{Base}/{Uri.EscapeDataString(id)}", user, ct);

    public Task<ApiResult> UpdateRoleAsync(string id, UpdatedUserRole role, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/{Uri.EscapeDataString(id)}/role", role, ct);

    public Task<ApiResult<PasswordResetDispatch>> SendPasswordResetAsync(string id, CancellationToken ct = default) =>
        api.SendAsync<PasswordResetDispatch>(
            HttpMethod.Post, $"{Base}/{Uri.EscapeDataString(id)}/password-reset", null, ct);

    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/{Uri.EscapeDataString(id)}", null, ct);
}
