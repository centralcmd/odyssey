using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>The measure groups a place looks a station up for, and which of them is due a lookup.</summary>
public static class WeatherStationGroups
{
    /// <summary>In lookup order: temperature first, since it is what the section opens on.</summary>
    public static readonly IReadOnlyList<WeatherMeasureGroup> All =
        [WeatherMeasureGroup.Temperature, WeatherMeasureGroup.Precipitation, WeatherMeasureGroup.Wind];

    /// <summary>A group that found nothing in reach is asked again after this long.</summary>
    public static readonly TimeSpan NoneRetry = TimeSpan.FromHours(24);

    /// <summary>
    /// The group to look up next: the first never looked up, else the first whose lookup found nothing and is at
    /// least <see cref="NoneRetry"/> old; null when every group is settled.
    /// </summary>
    public static WeatherMeasureGroup? Pending(IReadOnlyCollection<WeatherStation> stations, DateTime now)
    {
        foreach (var group in All)
        {
            if (stations.FirstOrDefault(st => st.Group == group) is not { } row)
            {
                return group;
            }
        }

        foreach (var group in All)
        {
            var row = stations.First(st => st.Group == group);
            if (row.StationId is null && row.ResolvedAtUtc <= now - NoneRetry)
            {
                return group;
            }
        }

        return null;
    }

    /// <summary>What a read shape says about the stations: one entry per group looked up, in group order.</summary>
    public static IReadOnlyList<WeatherStationRef> Refs(IEnumerable<WeatherStation> stations) =>
        [.. stations.OrderBy(st => st.Group).Select(st => new WeatherStationRef
        {
            Group = st.Group,
            Name = st.StationId is null ? null : DisplayName(st.Name ?? st.StationId),
            DistanceKm = st.StationId is null || st.DistanceKm is not { } km ? null : (int)Math.Round(km, MidpointRounding.AwayFromZero),
            MaxDistanceKm = (int)WeatherRules.StationMaxDistanceKm(st.Group),
        })];

    /// <summary>
    /// False only once every group has been looked up and none found a station. A place never looked up reads as
    /// having one, so it is never reported as without a station when nobody has asked.
    /// </summary>
    public static bool HasAny(DateTime? resolvedAtUtc, IEnumerable<WeatherStation> stations) =>
        resolvedAtUtc is null || stations.Any(st => st.StationId is not null);

    /// <summary>Frost names stations in capitals ("OSLO - BLINDERN"); shown as a name is ("Oslo - Blindern").</summary>
    internal static string DisplayName(string name) =>
        name.Any(char.IsLower) ? name : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
}
