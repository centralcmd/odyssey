using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;

namespace Odyssey.Client.Tests;

/// <summary>
/// The services the shared smart-tags section injects, as inert mocks — for a test that renders a
/// record view containing the section but does not exercise it. The contract record renders the
/// section unconditionally since issue #226, so every <c>ContractDetailView</c> host needs these.
/// </summary>
internal static class SmartTagSectionServices
{
    public static IServiceCollection AddInertSmartTagSection(this IServiceCollection services)
    {
        services.AddSingleton(Mock.Of<ITransactionsApiClient>());
        services.AddSingleton(Mock.Of<IAccountsApiClient>());
        services.AddSingleton(Mock.Of<IPropertiesApiClient>());
        services.AddSingleton(Mock.Of<IAccountLimitsCache>());
        services.AddSingleton(Mock.Of<IContractLimitsCache>());
        services.AddSingleton(Mock.Of<IPropertyLimitsCache>());
        return services;
    }
}
