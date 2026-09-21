using Odyssey.Dtos.Application;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for <c>GET /api/contract-limits</c> (issue #166): the effective per-contract limits,
/// for any authenticated caller — no permission claim, because a contract's smart-tag section needs
/// the cap and is used by roles holding no system-settings claim.
/// </summary>
/// <remarks>
/// A <c>503</c> means the server's read is <b>degraded</b>, and unlike its account sibling that
/// distinction is load-bearing here: the design system's contract host stops pre-checking rather than
/// guessing a ceiling, so the caller has to be able to tell "the cap is N" from "the cap is unknown".
/// The failure therefore surfaces as an ordinary failed <see cref="ApiResult{T}"/> and
/// <c>ContractLimitsCache</c> turns it into a degraded reading rather than a silent fallback.
/// </remarks>
public interface IContractLimitsApiClient
{
    Task<ApiResult<ContractLimitsDto>> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IContractLimitsApiClient" />
public sealed class ContractLimitsApiClient(IOdysseyApi api) : IContractLimitsApiClient
{
    private const string Url = "api/contract-limits";

    public Task<ApiResult<ContractLimitsDto>> GetAsync(CancellationToken ct = default) =>
        api.GetAsync<ContractLimitsDto>(Url, ct);
}
