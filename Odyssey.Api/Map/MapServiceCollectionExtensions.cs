using Microsoft.Extensions.Options;

namespace Odyssey.Api.Map;

public static class MapServiceCollectionExtensions
{
    /// <summary>
    /// Binds and start-up-validates the <c>Map</c> section (issue #370). Failing closed is deliberate: the
    /// alternative is a running API that hands the client a style the CSP blocks.
    /// </summary>
    public static IServiceCollection AddMapStyles(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MapOptions>()
            .Bind(configuration.GetSection(MapOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MapOptions>, MapOptionsValidator>();

        return services;
    }
}
