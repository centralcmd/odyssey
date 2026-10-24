using System.Net;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Tests;

/// <summary>
/// A stand-in <see cref="IExchangeRateSyncApiClient"/> for page fixtures that render the exchange-rate
/// sync status panel (the System settings card hosts one) without testing it: idle status, empty lists.
/// </summary>
internal static class ExchangeRateSyncFakes
{
    public static ExchangeRateSyncStatus IdleStatus() => new()
    {
        State = ExchangeRateSyncState.Idle,
        ProviderDisplayName = "Provider",
        Attribution = "Source: Provider",
        BaseCurrencyCode = "BASE",
        RunTimeoutMinutes = 60,
        SupportedListAvailable = true,
    };

    public static Mock<IExchangeRateSyncApiClient> Client(ExchangeRateSyncStatus? status = null)
    {
        var mock = new Mock<IExchangeRateSyncApiClient>();
        mock.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExchangeRateSyncStatus>.Success(status ?? IdleStatus(), HttpStatusCode.OK));
        mock.Setup(c => c.ListCurrenciesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool?>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingExchangeRateSyncCurrency>>.Success(
                new PagedResult<ExistingExchangeRateSyncCurrency> { Items = [], TotalCount = 0 }, HttpStatusCode.OK));
        mock.Setup(c => c.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingExchangeRateSyncRun>>.Success(
                new PagedResult<ExistingExchangeRateSyncRun> { Items = [], TotalCount = 0 }, HttpStatusCode.OK));
        return mock;
    }
}

/// <summary>A signed-in principal holding exactly <paramref name="permissions"/>.</summary>
internal sealed class SignedInWith(IReadOnlyList<string> permissions)
    : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
{
    public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [.. permissions.Select(p => new System.Security.Claims.Claim(Odyssey.Dtos.Authorization.PermissionClaims.Type, p))],
                "test"))));
}
