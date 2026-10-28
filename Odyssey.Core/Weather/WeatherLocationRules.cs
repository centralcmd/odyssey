using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Weather;

/// <summary>
/// The cross-table rule behind the <c>WeatherDefaultLocationId</c> setting (issue #348 §11): a household
/// default must be <b>selectable</b> — the location exists, is not archived, has weather enabled and is not
/// being purged. Asked by <c>SystemSettingsService</c> inside its write transaction; the shape rule (a GUID or
/// empty) stays on the descriptor.
/// </summary>
public interface IWeatherLocationRules
{
    Task<bool> IsSelectableAsync(OdysseyContext context, Guid locationId, CancellationToken cancellationToken);
}

public sealed class WeatherLocationRules : IWeatherLocationRules
{
    public Task<bool> IsSelectableAsync(OdysseyContext context, Guid locationId, CancellationToken cancellationToken) =>
        (from weather in context.WeatherLocations
         join location in context.Locations on weather.LocationId equals location.LocationId
         where weather.LocationId == locationId && weather.ForecastEnabled && weather.PurgeStartedAt == null && location.Archived == null
         select weather.LocationId).AnyAsync(cancellationToken);
}
