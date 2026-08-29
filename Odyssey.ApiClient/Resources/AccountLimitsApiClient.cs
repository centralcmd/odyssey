using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for <c>GET /api/account-limits</c> (issue #434 key 15): the effective per-account
/// limits, for any authenticated caller — no permission claim, because the Accounts page needs the
/// smart-tag cap and is used by roles holding no system-settings claim. A <c>503</c> (a degraded read
/// on the server) surfaces as an ordinary failed <see cref="ApiResult{T}"/>, and the caller falls back
/// to the shipped default for that attempt.
/// </summary>
public interface IAccountLimitsApiClient
{
    Task<ApiResult<AccountLimitsDto>> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IAccountLimitsApiClient" />
public sealed class AccountLimitsApiClient(IOdysseyApi api) : IAccountLimitsApiClient
{
    private const string Url = "api/account-limits";

    public Task<ApiResult<AccountLimitsDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<AccountLimitsDto>(Url, ct);
}
