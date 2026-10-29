using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.Finance.ExchangeRateSync.Providers;
using Odyssey.Core.SystemSettings;

namespace Odyssey.Core.Finance.ExchangeRateSync;

public static class ExchangeRateSyncServiceCollectionExtensions
{
    /// <summary>
    /// The exchange-rate sync path (issue #349), shared by <c>Odyssey.Api</c> (the currency list, the
    /// switches, the purge and starting a manual run) and <c>Odyssey.Worker</c> (executing every run), so
    /// both hosts reach Norges Bank with the same client. Needs an <c>OdysseyContext</c> and
    /// <c>IMemoryCache</c> from the host.
    /// </summary>
    /// <remarks>
    /// The Norges Bank client has a COMPILE-TIME base address that no setting or input can change, does not
    /// follow redirects (a 3xx is ProviderMalformed), decompresses only gzip/deflate/brotli (the 10 MB cap in
    /// <see cref="NorgesBankClient"/> counts inflated bytes) and has its loggers removed. A plain HttpClient:
    /// no resilience handler, whose own timeouts and retries would compound with the client's 15 s timeout
    /// and single retry. The User-Agent names no version, so a request does not disclose the build.
    /// </remarks>
    public static IServiceCollection AddExchangeRateSync(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(NorgesBankClient), http =>
            {
                http.BaseAddress = new Uri(NorgesBankClient.BaseAddress);
                // The client's own linked token enforces the 15 s; this is only a backstop.
                http.Timeout = TimeSpan.FromSeconds(60);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(ExchangeRateSyncConstants.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            })
            .RemoveAllLoggers();

        services.AddSingleton(ExchangeRateSyncPacing.Default);
        services.AddTransient(sp => new NorgesBankClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(NorgesBankClient)),
            sp.GetRequiredService<ExchangeRateSyncPacing>()));
        // Scoped, on the caller's context: the list lives in the database and the refresh window's last
        // attempt in the host's IMemoryCache, so nothing it holds has to outlive a request.
        services.AddScoped<ExchangeRateSupportedList>();
        services.AddScoped<IExchangeRateSyncSettingsLookup, ExchangeRateSyncSettingsLookup>();
        services.AddScoped<ExchangeRateSyncService>();
        return services;
    }
}
