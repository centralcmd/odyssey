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
