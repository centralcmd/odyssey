using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context.Secrets;

namespace Odyssey.Core.SystemSettings;

public static class SecretSettingsServiceCollectionExtensions
{
    /// <summary>
    /// The read side of the encrypted secret store (issue #444), shared by every host that consumes a
    /// secret: the registry (its environment filter keeps a restored staging-only row inert), the protector
    /// over the host's Data Protection key ring, and the reader. The write side — the service, the
    /// durability check and the endpoints — is the API's alone.
    /// </summary>
    public static IServiceCollection AddSecretSettingsReading(this IServiceCollection services)
    {
        services.AddSingleton<SecretSettingsRegistry>();
        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddScoped<ISecretSettingsReader, SecretSettingsReader>();
        return services;
    }
}
