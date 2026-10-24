using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Weather;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IWeatherSettingsLookup"/> (issue #348 §11) on the shared 30s TTL, evicted by
/// <see cref="SystemSettingsService"/> when any of the four rows changes.
///
/// <para>
/// <strong>The contact fails closed.</strong> A stored contact the shape rule rejects reads as "not set", so
/// no MET request is made with it — it never reaches a request header. The interval's conservative
/// direction is <strong>larger</strong> (fewer requests); the tracked cap's is <strong>smaller</strong>.
/// A degraded resolution is not cached, so recovery is immediate on the next good read.
/// </para>
/// </summary>
public sealed class WeatherSettingsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<WeatherSettingsLookup> logger) : IWeatherSettingsLookup
{
    internal const string CacheKey = "system-settings:weather";

    /// <summary>The keys this entry serves; <c>SystemSettingsCacheEvictionTests</c> reads it.</summary>
    private static readonly string[] Keys =
    [
        SystemSettingsKeys.WeatherUserAgentContact,
        SystemSettingsKeys.WeatherRefreshIntervalMinutes,
        SystemSettingsKeys.WeatherMaxTrackedLocations,
        SystemSettingsKeys.WeatherDefaultLocationId,
    ];

    private static readonly IntSettingSpec Interval = new(
        SystemSettingsKeys.WeatherRefreshIntervalMinutes,
        SystemSettingsDefaults.WeatherRefreshIntervalMinutes,
        SystemSettingsBounds.WeatherRefreshIntervalMinutesMin,
        SystemSettingsBounds.WeatherRefreshIntervalMinutesMax,
        ConservativeDirection.Larger);

    private static readonly IntSettingSpec MaxTracked = new(
        SystemSettingsKeys.WeatherMaxTrackedLocations,
        SystemSettingsDefaults.WeatherMaxTrackedLocations,
        SystemSettingsBounds.WeatherMaxTrackedLocationsMin,
        SystemSettingsBounds.WeatherMaxTrackedLocationsMax);

    private readonly IntSettingResolver resolver = new(cache, logger, CacheKey);

    public async Task<WeatherSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out WeatherSettings? cached) && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await resolver.ReadAsync(context, Keys, "weather settings", cancellationToken);
        var interval = resolver.Resolve(Interval, values, readFailed);
        var maxTracked = resolver.Resolve(MaxTracked, values, readFailed);

        var degraded = readFailed || interval.IsDegraded || maxTracked.IsDegraded;
        string? contact = null;
        if (values.TryGetValue(SystemSettingsKeys.WeatherUserAgentContact, out var storedContact) && storedContact.Length > 0)
        {
            if (WeatherRules.ValidateContact(storedContact) is null)
            {
                contact = storedContact;
            }
            else
            {
                degraded = true;
                logger.LogError("The stored weather contact is not a usable email or https URL; weather fetching is off until it is fixed.");
            }
        }

        Guid? householdDefault = null;
        if (values.TryGetValue(SystemSettingsKeys.WeatherDefaultLocationId, out var storedDefault) && storedDefault.Length > 0)
        {
            if (Guid.TryParse(storedDefault, out var id))
            {
                householdDefault = id;
            }
            else
            {
                degraded = true;
            }
        }

        var settings = new WeatherSettings(contact, interval.Value, maxTracked.Value, householdDefault, degraded);
        if (!degraded)
        {
            cache.Set(CacheKey, settings, IntSettingResolver.CacheTtl);
        }

        return settings;
    }
}
