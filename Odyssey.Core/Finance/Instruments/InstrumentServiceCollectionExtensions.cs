using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Core.Finance.Instruments.Sources;

namespace Odyssey.Core.Finance.Instruments;

public static class InstrumentServiceCollectionExtensions
{
    /// <summary>
    /// The instrument-price services both hosts share (issue #446): the source catalogue (empty until a source is
    /// registered with <c>AddInstrumentPriceSource&lt;T&gt;()</c>), the settings lookup and the domain services.
    /// Idempotent.
    /// </summary>
    public static IServiceCollection AddInstrumentPrices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InstrumentPriceSourceCatalogue>();
        services.TryAddScoped<IInstrumentPriceSettingsLookup, InstrumentPriceSettingsLookup>();
        services.TryAddScoped<CurrencyConversionService>();
        services.TryAddScoped<InstrumentService>();
        services.TryAddScoped<InstrumentPriceService>();
        services.TryAddScoped<InstrumentWatchlistService>();
        return services;
    }
}

public static class InstrumentPriceSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers a price source and its framework-built transport (issue #446 §3.2): a named client whose base address
    /// is the source's compiled https host, that never follows a redirect, decompresses (the 2 MB cap is applied by
    /// <see cref="Sources.SourceHttp"/> to the inflated stream), and has its default request loggers removed — they
    /// would log the URI, and a query-string credential with it. Call it in both hosts; only the worker fetches.
    /// </summary>
    public static IServiceCollection AddInstrumentPriceSource<TSource>(this IServiceCollection services)
        where TSource : class, Sources.IInstrumentPriceSource, Sources.IInstrumentPriceSourceDeclaration
    {
        services.AddHttpClient(Sources.SourceHttpLimits.ClientName(TSource.DeclaredKey), http =>
            {
                http.BaseAddress = new Uri($"https://{TSource.DeclaredHost}/");
                // SourceHttp enforces the 30 s attempt and 120 s total timeouts itself.
                http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                http.MaxResponseContentBufferSize = Sources.SourceHttpLimits.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
            })
            .RemoveAllLoggers();
        services.AddSingleton<TSource>();
        services.AddSingleton<Sources.IInstrumentPriceSource>(sp => sp.GetRequiredService<TSource>());
        return services.AddInstrumentPriceSync();
    }

    /// <summary>
    /// The production price sources (issue #447) and the sync. Both hosts call this one registration, so the API's
    /// catalogue (status, bindings) and the worker's (fetching) cannot disagree.
    /// </summary>
    public static IServiceCollection AddProductionInstrumentPriceSources(this IServiceCollection services) =>
        services.AddInstrumentPriceSource<Sources.Tiingo.TiingoPriceSource>();

    /// <summary>The sync's services, shared by the API (status, bindings, run-now) and the worker (running it). Idempotent.</summary>
    public static IServiceCollection AddInstrumentPriceSync(this IServiceCollection services)
    {
        services.AddInstrumentPrices();
        services.AddHttpClient();
        services.TryAddSingleton<Sources.ISourceRequestQuota, Sources.SourceRequestQuota>();
        services.TryAddScoped<Sync.InstrumentPriceSyncService>();
        services.TryAddScoped<Sync.InstrumentPriceSourceAdminService>();
        Jobs.JobRunServiceCollectionExtensions.AddJobRunRecording(services);
        return services;
    }
}
