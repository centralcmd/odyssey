using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.SystemSettings;

namespace Odyssey.Core.Weather;

public static class WeatherServiceCollectionExtensions
{
    /// <summary>
    /// The weather fetch path (issue #348), shared by <c>Odyssey.Api</c> (on-demand reads and the Admin
    /// refresh) and <c>Odyssey.Worker</c> (the schedule), so both hosts fetch with the same clients.
    /// Needs <c>ISecretSettingsReader</c>, an <c>OdysseyContext</c> and <c>IMemoryCache</c> from the host.
    /// </summary>
    /// <remarks>
    /// The two MET clients follow the geocoding clients exactly: a compile-time base address, no redirects,
    /// gzip/deflate/brotli only (the size caps in <see cref="WeatherHttp"/> count inflated bytes), and every
    /// HttpClient logger removed — the outbound URL carries the coordinates (§7.7). The User-Agent carries the
    /// admin's contact and is set per request; the Frost credential is attached per request too.
    /// </remarks>
    public static IServiceCollection AddWeatherFetching(this IServiceCollection services)
    {
        services.AddScoped<IWeatherSettingsLookup, WeatherSettingsLookup>();
        services.AddSingleton<IWeatherLocationRules, WeatherLocationRules>();
        services.AddScoped<WeatherFetchService>();

        foreach (var (client, baseAddress) in new[]
        {
            (nameof(MetNorwayForecastSource), MetNorwayForecastSource.BaseAddress),
            (nameof(FrostObservationSource), FrostObservationSource.BaseAddress),
        })
        {
            services.AddHttpClient(client, http =>
                {
                    http.BaseAddress = new Uri(baseAddress);
                    // Each call enforces its own budget (5 s on demand, 10 s per scheduled attempt); this is a backstop.
                    http.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                })
                .RemoveAllLoggers();
        }

        services.AddTransient<IWeatherForecastSource>(sp =>
            new MetNorwayForecastSource(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(MetNorwayForecastSource))));
        services.AddTransient<IWeatherObservationSource>(sp =>
            new FrostObservationSource(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FrostObservationSource))));
        return services;
    }
}
