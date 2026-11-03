using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Weather;

/// <summary>
/// The cross-table rule behind the <c>WeatherDefaultPointId</c> setting (issue #348 §11; issue #387): a household
/// default must be <b>selectable</b> — the point exists, its forecast is on and it is not being purged. Asked by
/// <c>SystemSettingsService</c> inside its write transaction; the shape rule (a GUID or empty) stays on the
/// descriptor.
/// </summary>
public interface IWeatherPointRules
{
    Task<bool> IsSelectableAsync(OdysseyContext context, Guid pointId, CancellationToken cancellationToken);
}

public sealed class WeatherPointRules : IWeatherPointRules
{
    public Task<bool> IsSelectableAsync(OdysseyContext context, Guid pointId, CancellationToken cancellationToken) =>
        context.WeatherPoints.AnyAsync(
            point => point.WeatherPointId == pointId && point.ForecastEnabled && point.PurgeStartedAt == null,
            cancellationToken);
}
