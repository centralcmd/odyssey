namespace Odyssey.Core.Weather;

/// <summary>
/// The four weather settings as the scheduler and the read path consume them (issue #348 §11).
/// </summary>
/// <param name="Contact">
/// The MET contact, or <see langword="null"/> when it is empty <b>or unusable</b>. Fail closed: a stored
/// value the shape check rejects must never reach a request header, so it reads exactly like "not set".
/// </param>
/// <param name="HouseholdDefaultLocationId">
/// The household default, or <see langword="null"/> when empty or not a GUID. Selectability is the
/// caller's question — an archived default is treated as unset on read.
/// </param>
public sealed record WeatherSettings(
    string? Contact,
    int RefreshIntervalMinutes,
    int MaxTrackedLocations,
    Guid? HouseholdDefaultLocationId,
    bool IsDegraded)
{
    public bool IsConfigured => Contact is not null;
}

/// <summary>
/// Reads <see cref="WeatherSettings"/>. The interface lives with the consuming domain so its tests can fake
/// it; the implementation (cache, clamp, degradation) lives in <c>Odyssey.Api</c> beside its siblings.
/// </summary>
public interface IWeatherSettingsLookup
{
    Task<WeatherSettings> GetAsync(CancellationToken cancellationToken = default);
}
