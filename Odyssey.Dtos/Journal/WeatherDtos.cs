using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Journal;

/// <summary>Who supplied a stored weather row. <c>0</c> is a permanent hole; an ordinal is never reused.</summary>
public enum WeatherProvider
{
    MetNorway = 1,
}

/// <summary>Why the last fetch of a kind failed (issue #348 §4). Serialized by name on the wire.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WeatherFetchFailureKind>))]
public enum WeatherFetchFailureKind
{
    Timeout = 1,
    ProviderRejected = 2,
    RateLimited = 3,
    ProviderError = 4,
    MalformedPayload = 5,
    NotConfigured = 6,
    ResponseTooLarge = 7,
}

/// <summary>The outcome every weather read reports beside its data (§5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WeatherStatus>))]
public enum WeatherStatus
{
    Ok = 1,
    NoPlace = 2,
    NoData = 3,
    NotConfigured = 4,
    OutOfRange = 5,
    WeatherDisabled = 6,
}

/// <summary>
/// Which step of the resolution order picked the point a weather read used (issue #387 §8): an explicit
/// <c>?weatherPointId</c>, the point the event's or entry's place links to, the caller's preference, or the
/// household default. Absent for <c>NoPlace</c>. Serialized by name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WeatherResolvedFrom>))]
public enum WeatherResolvedFrom
{
    Explicit = 1,
    Linked = 2,
    Preferred = 3,
    Household = 4,
}

/// <summary>
/// What a purge deletes (issue #387 §5 endpoints 7–8). Stored on the point beside its fence
/// (<c>WeatherPoints.PurgeMode</c>), so a resumed purge knows which one it was: an interrupted full delete may
/// only be completed by another full delete. The ordinals are a persistence contract.
/// </summary>
public enum WeatherPurgeMode
{
    /// <summary>Every row of the point, the point itself included.</summary>
    Full = 0,

    /// <summary>
    /// Only the measured data — the observations, the measured half of each day (a day with no forecast half
    /// goes entirely), the resolved stations and the schedule's observation markers. The forecast runs, the
    /// sites, the schedule and the point stay; the final transaction lifts the fence.
    /// </summary>
    HistoryOnly = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<WeatherTrackingSuspendedReason>))]
public enum WeatherTrackingSuspendedReason
{
    CapReached = 1,
}

/// <summary>Which day a measured value belongs to. Frost's daily values are UTC days.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WeatherDayDefinition>))]
public enum WeatherDayDefinition
{
    Utc = 1,
    /// <summary>06:00–06:00 UTC, the convention for Frost's daily precipitation sum.</summary>
    Utc06 = 2,
}

public enum WeatherPointSortBy
{
    Name,
    EnabledAt,

    /// <summary>How many places link to the point. Needs <c>locations.read</c> (<c>403</c> without): the order counts places.</summary>
    LinkedLocationCount,
}

/// <summary>
/// A non-blocking advisory on a weather-point write (issue #387 §8). Computed after the write commits; never
/// changes the status code. Serialized by name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WeatherAdvisoryCode>))]
public enum WeatherAdvisoryCode
{
    /// <summary>The place and the point's current site keep different time zones; weather days are in the point's.</summary>
    TimeZoneMismatch = 1,

    /// <summary>The place has no coordinates, so no distance can be given.</summary>
    NoLocationCoordinates = 2,

    /// <summary>The point's forecast is off: the place shows only stored history, with no fallback.</summary>
    PointNotSelectable = 3,

    /// <summary>A point with linked places moved; every linked place now reads the new position.</summary>
    LinkedLocationsFollowMove = 4,
}

/// <summary>
/// The shared limits and rules of the weather contract — one declaration named by the DTO attributes, the
/// services, the settings descriptors and the client (issue #348).
/// </summary>
public static class WeatherRules
{
    public const int StationIdMaxLength = 32;
    public const int StationNameMaxLength = 128;

    /// <summary>
    /// How far a station may be for each measure group. They differ because the measures do: temperature holds
    /// over tens of kilometres on level ground, precipitation is local (showers especially) and MET's gauges are
    /// dense, and wind stations are few — mostly airports and the coast.
    /// </summary>
    public static double StationMaxDistanceKm(WeatherMeasureGroup group) => group switch
    {
        WeatherMeasureGroup.Precipitation => 15,
        WeatherMeasureGroup.Wind => 30,
        _ => 25,
    };

    /// <summary>
    /// A temperature station may differ in height from the place by at most this much: temperature falls about
    /// 0.65 °C per 100 m, so a near station 400 m higher describes the place worse than a farther one level with
    /// it. Applied only when both heights are known.
    /// </summary>
    public const int TemperatureStationMaxHeightDifferenceMeters = 300;
    public const int SymbolCodeMaxLength = 48;
    public const int ContactMaxLength = 254;

    /// <summary>Weather positions are stored and sent to 3 decimal places (≈ 100 m).</summary>
    public const int CoordinateScale = 3;

    public const int HourlyMaxDays = 10;

    /// <summary>A year, leap day included — what the temperature history's longest window and a custom range need.</summary>
    public const int DailyMaxDays = 366;
    public const int OptionsMax = 200;

    /// <summary>A weather point's name (issue #387 §8). Converted points inherit a location name (≤ 100).</summary>
    public const int PointNameMaxLength = 256;

    /// <summary>
    /// The coordinate bounds a weather point's <c>[Range]</c> attributes and the client's coordinate field name
    /// (issue #388 §8), so the two cannot drift.
    /// </summary>
    public const double LatitudeMin = -90;
    public const double LatitudeMax = 90;
    public const double LongitudeMin = -180;
    public const double LongitudeMax = 180;

    /// <summary>
    /// The one coordinate rounding weather uses: 3 decimals (≈ 100 m), away from zero. Here rather than in
    /// <c>Odyssey.Core</c> so the WASM client's position preview and "moved" check name the same symbol the
    /// server's snapshot does — a client copy of the rule is how the two would drift.
    /// </summary>
    public static decimal RoundCoordinate(decimal value) =>
        Math.Round(value, CoordinateScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The opaque value enable and apply send back (amendment 2): exactly the fields weather copies — the
    /// rounded position, the altitude and the time zone — so a rename never conflicts, while a coordinate or
    /// time-zone change always does.
    /// </summary>
    /// <summary>
    /// The fingerprint of a place with no coordinates (issue #380). It never matches a reviewed position, so
    /// the server refuses rather than turning weather on for a place it cannot locate.
    /// </summary>
    public const string NoCoordinatesFingerprint = "v1:none";

    public static string Fingerprint(decimal latitude, decimal longitude, int? altitudeMeters, string timeZoneId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"v1:{RoundCoordinate(latitude):0.000}:{RoundCoordinate(longitude):0.000}:{altitudeMeters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}:{timeZoneId}");

    /// <summary>True when a live position no longer matches the site weather is using.</summary>
    public static bool Moved(
        decimal latitude, decimal longitude, int? altitudeMeters, string timeZoneId,
        decimal siteLatitude, decimal siteLongitude, int? siteAltitudeMeters, string siteTimeZoneId) =>
        RoundCoordinate(latitude) != siteLatitude
        || RoundCoordinate(longitude) != siteLongitude
        || altitudeMeters != siteAltitudeMeters
        || !string.Equals(timeZoneId, siteTimeZoneId, StringComparison.Ordinal);

    /// <summary>The earliest date measured history may start from.</summary>
    public static readonly DateOnly HistoryFloor = new(1950, 1, 1);

    /// <summary>
    /// The <c>WeatherUserAgentContact</c> shape (§11): empty (not configured), or one email address or an
    /// absolute <c>https://</c> URL — printable ASCII only, since it is sent in the <c>User-Agent</c> header,
    /// where a CR/LF would split the header. Returns <see langword="null"/> when valid, else the message.
    /// Shared by the server's settings descriptor and the client's settings page, so the two cannot drift.
    /// </summary>
    public static string? ValidateContact(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length > ContactMaxLength)
        {
            return $"Must be {ContactMaxLength} characters or fewer.";
        }

        foreach (var c in value)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return "Use printable ASCII only — no line breaks or accented letters.";
            }
        }

        if (value.Contains(' ', StringComparison.Ordinal))
        {
            return "Enter an email address or a full https:// URL.";
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Contains('.', StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at > 0 && at == value.LastIndexOf('@') && value.IndexOf('.', at) > at + 1 && !value.EndsWith('.')
            && value.IndexOfAny([',', '<', '>', '(', ')', ';', ':', '\\', '"', '[', ']']) < 0)
        {
            return null;
        }

        return "Enter an email address or a full https:// URL.";
    }

    /// <summary><c>WeatherDefaultPointId</c> shape: empty, or a GUID. Selectability is checked server-side.</summary>
    public static string? ValidateDefaultPointId(string value) =>
        value.Length == 0 || Guid.TryParse(value, out _) ? null : "Choose a weather point from the list.";
}

/// <summary>MET Norway's attribution, carried on every weather response (§5).</summary>
public sealed record WeatherAttribution
{
    public string Text { get; init; } = "Weather data from MET Norway";
    public string License { get; init; } = "NLOD 2.0 / CC BY 4.0";
    public string Url { get; init; } = "https://api.met.no/";

    public static WeatherAttribution MetNorway { get; } = new();
}

/// <summary>The only point shape a weather read embeds (issue #387): no coordinates, no site.</summary>
public sealed record WeatherPointRef
{
    public Guid WeatherPointId { get; init; }
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// The point a place links to, as <c>ExistingLocation.WeatherPoint</c> carries it (issue #387 §5 endpoint 11): the
/// id, the name and four flags — the two switches, whether any measured day is stored, and whether a purge is
/// running — so a location record can decide which weather sections to draw without a second read.
/// </summary>
public sealed record LinkedWeatherPointRef
{
    public Guid WeatherPointId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool ForecastEnabled { get; init; }
    public bool HistoryEnabled { get; init; }
    public bool HasStoredHistory { get; init; }
    public bool PurgeInProgress { get; init; }
}

/// <summary>
/// The place whose link answered an event or entry read (issue #387 §5 endpoint 13): id and name only, never an
/// address or coordinates. Filled only when the read resolved <c>Linked</c>.
/// </summary>
public sealed record WeatherLinkedLocationRef
{
    public Guid LocationId { get; init; }
    public string Name { get; init; } = string.Empty;
}

/// <summary>One effective forecast step (§3 Day composition).</summary>
public sealed record WeatherStep
{
    public DateTime TimeUtc { get; init; }
    public int PeriodHours { get; init; }
    public DateTime IssuedAtUtc { get; init; }
    public string? SymbolCode { get; init; }
    public decimal? AirTemperatureC { get; init; }
    public decimal? PrecipitationMm { get; init; }
    public decimal? PrecipitationProbabilityPct { get; init; }
    public decimal? WindSpeedMps { get; init; }
    public decimal? WindGustMps { get; init; }
    public decimal? WindFromDirectionDeg { get; init; }
    public decimal? RelativeHumidityPct { get; init; }
    public decimal? CloudAreaFractionPct { get; init; }
    public decimal? AirPressureHpa { get; init; }
    public decimal? UvIndexClearSky { get; init; }
    public decimal? TempMinC { get; init; }
    public decimal? TempMaxC { get; init; }
}

public sealed record WeatherDayForecast
{
    public string? SymbolCode { get; init; }
    public decimal? MinTempC { get; init; }
    public decimal? MaxTempC { get; init; }
    public decimal? PrecipitationMm { get; init; }
    public decimal? MeanWindSpeedMps { get; init; }
    public decimal? MaxWindGustMps { get; init; }
    public int CoverageHours { get; init; }
    public DateTime? IssuedAtUtc { get; init; }
}

public sealed record WeatherDayObserved
{
    public decimal? MinTempC { get; init; }
    public decimal? MaxTempC { get; init; }
    public decimal? MeanTempC { get; init; }
    public decimal? PrecipitationMm { get; init; }
    public decimal? MeanWindSpeedMps { get; init; }
    public decimal? MaxWindGustMps { get; init; }
    public WeatherDayDefinition DayDefinition { get; init; } = WeatherDayDefinition.Utc;
    public WeatherDayDefinition PrecipitationDayDefinition { get; init; } = WeatherDayDefinition.Utc06;
    public DateTime? FetchedAtUtc { get; init; }
}

/// <summary>One local date: the forecast half and the measured half, either of which may be absent.</summary>
public sealed record WeatherDaySummary
{
    public DateOnly Date { get; init; }
    public WeatherDayForecast? Forecast { get; init; }
    public WeatherDayObserved? Observed { get; init; }
}

public sealed record WeatherCurrentResponse
{
    public WeatherPointRef? Point { get; init; }
    public WeatherStatus Status { get; init; }

    /// <summary>Where the place came from (issue #380 §5.8); omitted when nothing resolved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeatherResolvedFrom? ResolvedFrom { get; init; }
    public bool Stale { get; init; }
    public WeatherStep? Step { get; init; }
    public WeatherDaySummary? Today { get; init; }
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

public sealed record WeatherHourlyResponse
{
    public WeatherPointRef? Point { get; init; }
    public WeatherStatus Status { get; init; }

    /// <summary>Where the place came from (issue #380 §5.8); omitted when nothing resolved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeatherResolvedFrom? ResolvedFrom { get; init; }
    public bool Stale { get; init; }
    public IReadOnlyList<WeatherStep> Steps { get; init; } = [];
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

/// <summary>
/// What measured history is stored for a point, independent of the requested range, so a caller holding
/// only <c>weather.read</c> can page through it without the Admin row.
/// </summary>
public sealed record WeatherHistoryBounds
{
    /// <summary>The oldest stored local date with a measured half; null when none is stored.</summary>
    public DateOnly? MeasuredFrom { get; init; }

    /// <summary>The newest stored local date with a measured half; null when none is stored.</summary>
    public DateOnly? MeasuredThrough { get; init; }

    /// <summary>
    /// False only once every measure group has been looked up and none found a station. A position never
    /// looked up yet — Frost not configured, the Worker not run, a fresh move — reads as true, so it is never
    /// reported as having no station when nobody has asked.
    /// </summary>
    public bool HasObservationStation { get; init; }

    /// <summary>
    /// The station each measure group is measured at, one entry per group looked up so far (none before the
    /// first lookup). An entry with no <see cref="WeatherStationRef.Name"/> is a group with no station in reach.
    /// </summary>
    public IReadOnlyList<WeatherStationRef> Stations { get; init; } = [];

    /// <summary>
    /// Whether measured history can be fetched at all: a MET contact address and a Frost client ID are both
    /// set. Lets the client say why nothing is measured instead of promising it will arrive.
    /// </summary>
    public bool ObservationsConfigured { get; init; }
}

/// <summary>The three kinds of measured values, each fetched from its own nearest suitable station.</summary>
public enum WeatherMeasureGroup
{
    /// <summary>Daily mean, minimum and maximum air temperature.</summary>
    Temperature = 0,

    /// <summary>Daily precipitation, 06:00 to 06:00 UTC.</summary>
    Precipitation = 1,

    /// <summary>Daily mean wind speed and strongest gust.</summary>
    Wind = 2,
}

/// <summary>
/// Where one measure group comes from: a public MET Norway station's name and its distance from the weather
/// position, rounded to whole kilometres. No name means no station in reach — <see cref="MaxDistanceKm"/>
/// says how far was searched.
/// </summary>
public sealed record WeatherStationRef
{
    [EnumDataType(typeof(WeatherMeasureGroup))]
    public WeatherMeasureGroup Group { get; init; }

    [StringLength(WeatherRules.StationNameMaxLength)]
    public string? Name { get; init; }

    public int? DistanceKm { get; init; }

    public int MaxDistanceKm { get; init; }
}

public sealed record WeatherDailyResponse
{
    public WeatherPointRef? Point { get; init; }
    public WeatherStatus Status { get; init; }

    /// <summary>Where the place came from (issue #380 §5.8); omitted when nothing resolved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeatherResolvedFrom? ResolvedFrom { get; init; }
    public bool Stale { get; init; }
    public IReadOnlyList<WeatherDaySummary> Days { get; init; } = [];

    /// <summary>Null when no point resolved or the range is out of range; otherwise always filled.</summary>
    public WeatherHistoryBounds? History { get; init; }
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

public sealed record CalendarEventWeatherResponse
{
    public WeatherPointRef? Point { get; init; }
    public WeatherStatus Status { get; init; }

    /// <summary>Where the place came from (issue #380 §5.8); omitted when nothing resolved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeatherResolvedFrom? ResolvedFrom { get; init; }
    public bool Stale { get; init; }
    /// <summary>The place whose link answered, when <see cref="ResolvedFrom"/> is <c>Linked</c>; else omitted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeatherLinkedLocationRef? LinkedLocation { get; init; }

    /// <summary>Whole kilometres from the linked place to the point's current site; omitted unless both are known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DistanceKm { get; init; }
    public bool IsAllDay { get; init; }
    public IReadOnlyList<WeatherStep> Steps { get; init; } = [];
    public IReadOnlyList<WeatherDaySummary> Days { get; init; } = [];
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

public sealed record JournalEntryWeatherResponse
{
    public WeatherPointRef? Point { get; init; }
    public WeatherStatus Status { get; init; }

    /// <summary>Where the place came from (issue #380 §5.8); omitted when nothing resolved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeatherResolvedFrom? ResolvedFrom { get; init; }
    public bool Stale { get; init; }
    /// <summary>The place whose link answered, when <see cref="ResolvedFrom"/> is <c>Linked</c>; else omitted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeatherLinkedLocationRef? LinkedLocation { get; init; }

    /// <summary>Whole kilometres from the linked place to the point's current site; omitted unless both are known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DistanceKm { get; init; }
    public DateOnly? Date { get; init; }
    public WeatherDaySummary? Day { get; init; }
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

/// <summary>Binding model for the optional <c>weatherPointId</c> every weather read takes.</summary>
public sealed class WeatherPointQuery
{
    public Guid? WeatherPointId { get; set; }
}

/// <summary><c>GET /api/weather/hourly</c>: <c>toUtc &gt; fromUtc</c>, span ≤ 10 days.</summary>
public sealed class WeatherHourlyQuery : IValidatableObject
{
    public Guid? WeatherPointId { get; set; }

    [Required]
    public DateTime? FromUtc { get; set; }

    [Required]
    public DateTime? ToUtc { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FromUtc is { } from && ToUtc is { } to)
        {
            if (to <= from)
            {
                yield return new ValidationResult("toUtc must be after fromUtc.", [nameof(ToUtc)]);
            }
            else if (to - from > TimeSpan.FromDays(WeatherRules.HourlyMaxDays))
            {
                yield return new ValidationResult(
                    $"The window may span at most {WeatherRules.HourlyMaxDays} days.", [nameof(ToUtc)]);
            }
        }
    }
}

/// <summary><c>GET /api/weather/daily</c>: <c>to ≥ from</c>, span ≤ <see cref="WeatherRules.DailyMaxDays"/> days.</summary>
public sealed class WeatherDailyQuery : IValidatableObject
{
    public Guid? WeatherPointId { get; set; }

    [Required]
    public DateOnly? From { get; set; }

    [Required]
    public DateOnly? To { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is { } from && To is { } to)
        {
            if (to < from)
            {
                yield return new ValidationResult("to must not be before from.", [nameof(To)]);
            }
            else if (to.DayNumber - from.DayNumber + 1 > WeatherRules.DailyMaxDays)
            {
                yield return new ValidationResult(
                    $"The range may span at most {WeatherRules.DailyMaxDays} days.", [nameof(To)]);
            }
        }
    }
}

/// <summary>A point's current frozen site, rounded to 3 decimals.</summary>
public sealed record WeatherPointSiteDto
{
    public int Version { get; init; }
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public int? AltitudeMeters { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
    public DateTime AppliedAt { get; init; }
}

/// <summary>The Admin row (issue #387 §5 endpoints 1–2).</summary>
public sealed record ExistingWeatherPoint
{
    public Guid WeatherPointId { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>The concurrency token a <c>PUT</c> sends back; rotated by Admin writes only.</summary>
    public Guid SettingsVersion { get; init; }

    /// <summary>The forecast half: MET Norway forecasts are fetched, and the point is selectable on the dashboard.</summary>
    public bool ForecastEnabled { get; init; }

    /// <summary>The history half: measured days are fetched from MET Norway's Frost service.</summary>
    public bool HistoryEnabled { get; init; }

    public bool IsTracked { get; init; }
    public bool TrackingSuspended { get; init; }
    public WeatherTrackingSuspendedReason? TrackingSuspendedReason { get; init; }
    public DateOnly HistoryFrom { get; init; }
    public WeatherPointSiteDto Site { get; init; } = new();
    public bool PurgeInProgress { get; init; }
    public bool IsHouseholdDefault { get; init; }

    /// <summary>How many places link to the point; null — never 0 — for a caller without <c>locations.read</c>.</summary>
    public int? LinkedLocationCount { get; init; }

    /// <summary>False only once every measure group was looked up and none found a station; an unresolved position reads as true.</summary>
    public bool HasObservationStation { get; init; }

    /// <summary>The station per measure group looked up so far; see <see cref="WeatherHistoryBounds.Stations"/>.</summary>
    public IReadOnlyList<WeatherStationRef> Stations { get; init; } = [];
    public DateTime? LastForecastSuccessUtc { get; init; }
    public WeatherFetchFailureKind? ForecastFailureKind { get; init; }
    public DateTime? LastObservationSuccessUtc { get; init; }
    public WeatherFetchFailureKind? ObservationFailureKind { get; init; }
    public DateTime EnabledAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Redacted: the name may be an address, and a record prints its members wherever it is logged.</summary>
    public override string ToString() => nameof(ExistingWeatherPoint) + " { (redacted) }";
}

public sealed class WeatherPointsQueryParams : QueryParams<WeatherPointSortBy>;

/// <summary>
/// <c>POST /api/weather/points</c> (issue #387 §5 endpoint 4). Exactly one source: explicit coordinates, or
/// <see cref="FromLocationId"/> with the <see cref="ExpectedLocationFingerprint"/> the Admin reviewed. The values
/// are copied; no place is linked.
/// </summary>
public sealed record NewWeatherPoint : IValidatableObject
{
    /// <summary>Required with explicit coordinates; with <see cref="FromLocationId"/> it defaults to the place's name.</summary>
    [StringLength(WeatherRules.PointNameMaxLength)]
    public string? Name { get; set; }

    [Range(WeatherRules.LatitudeMin, WeatherRules.LatitudeMax)]
    public decimal? Latitude { get; set; }

    [Range(WeatherRules.LongitudeMin, WeatherRules.LongitudeMax)]
    public decimal? Longitude { get; set; }

    /// <summary>Null: no altitude, so MET derives it.</summary>
    [Range(LocationRules.AltitudeMin, LocationRules.AltitudeMax)]
    public int? AltitudeMeters { get; set; }

    [StringLength(64)]
    public string? TimeZoneId { get; set; }

    public Guid? FromLocationId { get; set; }

    /// <summary><c>ExistingLocation.WeatherFingerprint</c> as the Admin reviewed it; required with <see cref="FromLocationId"/>.</summary>
    [StringLength(128)]
    public string? ExpectedLocationFingerprint { get; set; }

    [Required]
    public bool? ForecastEnabled { get; set; }

    [Required]
    public bool? HistoryEnabled { get; set; }

    /// <summary>Scheduled forecast refresh; meaningful only with <see cref="ForecastEnabled"/>.</summary>
    [Required]
    public bool? IsTracked { get; set; }

    /// <summary><see langword="null"/>: the create date − 1 year.</summary>
    public DateOnly? HistoryFrom { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var anyCoordinate = Latitude is not null || Longitude is not null || TimeZoneId is not null || AltitudeMeters is not null;
        if (FromLocationId is not null)
        {
            if (anyCoordinate)
            {
                yield return new ValidationResult(
                    "Give coordinates or a place to copy them from, not both.", [nameof(FromLocationId)]);
            }

            if (string.IsNullOrEmpty(ExpectedLocationFingerprint))
            {
                yield return new ValidationResult(
                    "Review the place's position before copying it.", [nameof(ExpectedLocationFingerprint)]);
            }
        }
        else
        {
            if (Latitude is null || Longitude is null || string.IsNullOrWhiteSpace(TimeZoneId))
            {
                yield return new ValidationResult(
                    "Give the latitude, longitude and time zone, or a place to copy them from.", [nameof(Latitude)]);
            }

            if (string.IsNullOrWhiteSpace(Name))
            {
                yield return new ValidationResult("Give the weather point a name.", [nameof(Name)]);
            }
        }

        foreach (var result in WeatherPointValidation.Common(TimeZoneId, HistoryFrom))
        {
            yield return result;
        }
    }

    /// <summary>Redacted: the name and coordinates may describe a home.</summary>
    public override string ToString() => nameof(NewWeatherPoint) + " { (redacted) }";
}

/// <summary>
/// <c>PUT /api/weather/points/{weatherPointId}</c> (issue #387 §5 endpoint 5). Not an upsert. A changed rounded
/// position, altitude or time zone starts a new site version.
/// </summary>
public sealed record UpdateWeatherPoint : IValidatableObject
{
    [Required]
    [StringLength(WeatherRules.PointNameMaxLength)]
    public string? Name { get; set; }

    [Required]
    [Range(WeatherRules.LatitudeMin, WeatherRules.LatitudeMax)]
    public decimal? Latitude { get; set; }

    [Required]
    [Range(WeatherRules.LongitudeMin, WeatherRules.LongitudeMax)]
    public decimal? Longitude { get; set; }

    /// <summary>Null means no altitude, so MET derives it — the same meaning as on create.</summary>
    [Range(LocationRules.AltitudeMin, LocationRules.AltitudeMax)]
    public int? AltitudeMeters { get; set; }

    [Required]
    [StringLength(64)]
    public string? TimeZoneId { get; set; }

    [Required]
    public bool? ForecastEnabled { get; set; }

    [Required]
    public bool? HistoryEnabled { get; set; }

    [Required]
    public bool? IsTracked { get; set; }

    /// <summary><see langword="null"/> keeps the stored value.</summary>
    public DateOnly? HistoryFrom { get; set; }

    [Required]
    public Guid? SettingsVersion { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Name is not null && string.IsNullOrWhiteSpace(Name))
        {
            yield return new ValidationResult("Give the weather point a name.", [nameof(Name)]);
        }

        foreach (var result in WeatherPointValidation.Common(TimeZoneId, HistoryFrom))
        {
            yield return result;
        }
    }

    /// <summary>Redacted: the name and coordinates may describe a home.</summary>
    public override string ToString() => nameof(UpdateWeatherPoint) + " { (redacted) }";
}

/// <summary>The rules both point writes share.</summary>
internal static class WeatherPointValidation
{
    public static IEnumerable<ValidationResult> Common(string? timeZoneId, DateOnly? historyFrom)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && !IanaTimeZones.IsValid(timeZoneId))
        {
            yield return new ValidationResult("The time zone must be an IANA time zone, e.g. Europe/Oslo.", ["TimeZoneId"]);
        }

        if (historyFrom is { } from)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (from < WeatherRules.HistoryFloor || from > today)
            {
                yield return new ValidationResult(
                    $"History can start between {WeatherRules.HistoryFloor:yyyy-MM-dd} and today.", ["HistoryFrom"]);
            }
        }
    }
}

/// <summary>One advisory on a point or link write (issue #387 §8). The message never names a place or a coordinate.</summary>
public sealed record WeatherAdvisory
{
    public WeatherAdvisoryCode Code { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary><c>LinkedLocationsFollowMove</c> only: how many places follow, for a <c>locations.read</c> holder.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; init; }
}

/// <summary>The <c>PUT /api/weather/points/{weatherPointId}</c> response.</summary>
public sealed record WeatherPointUpdateResult
{
    public ExistingWeatherPoint Point { get; init; } = new();
    public IReadOnlyList<WeatherAdvisory> Warnings { get; init; } = [];
}

/// <summary>One place linked to a point (issue #387 §5 endpoint 3): id, name, whole-km distance. Never an address.</summary>
public sealed record WeatherPointLinkedLocation
{
    public Guid LocationId { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Null when the place has no coordinates.</summary>
    public int? DistanceKm { get; init; }

    public override string ToString() => nameof(WeatherPointLinkedLocation) + " { (redacted) }";
}

/// <summary><c>GET /api/weather/points/options</c>'s query.</summary>
public sealed class WeatherPointOptionsQuery
{
    /// <summary>Sort by distance from this place (then truncate); needs <c>locations.read</c>.</summary>
    public Guid? NearLocationId { get; set; }
}

/// <summary>One picker entry (issue #387 §5 endpoint 9). No coordinates, no site, no schedule.</summary>
public sealed record WeatherPointOption
{
    public Guid WeatherPointId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool ForecastEnabled { get; init; }
    public bool HistoryEnabled { get; init; }

    /// <summary>Whole kilometres from <c>nearLocationId</c>; null without it or when that place has no coordinates.</summary>
    public int? DistanceKm { get; init; }
}

public sealed record WeatherPointOptions
{
    public IReadOnlyList<WeatherPointOption> Points { get; init; } = [];

    /// <summary>The household default, when it is one of the selectable points; else null.</summary>
    public Guid? HouseholdDefaultPointId { get; init; }
}

/// <summary>
/// <c>PUT /api/locations/{id}/weather-point</c> (issue #387 §5 endpoint 10). The key is required; its value may
/// be null, which clears the link. A nested point object is not part of the contract and is ignored.
/// </summary>
public sealed record SetLocationWeatherPoint
{
    [JsonRequired]
    public Guid? WeatherPointId { get; set; }
}

/// <summary>The link response.</summary>
public sealed record LocationWeatherPointLink
{
    public Guid LocationId { get; init; }
    public WeatherPointRef? WeatherPoint { get; init; }

    /// <summary>Whole kilometres from the place to the point's current site; null when unlinked or either end lacks coordinates.</summary>
    public int? DistanceKm { get; init; }

    public IReadOnlyList<WeatherAdvisory> Warnings { get; init; } = [];
}

public sealed record WeatherRefreshResult
{
    public bool Fetched { get; init; }
    public DateTime? IssuedAtUtc { get; init; }
    public WeatherFetchFailureKind? ForecastFailureKind { get; init; }
}

public sealed record WeatherPreferences
{
    public WeatherPointRef? DefaultPoint { get; init; }
    public WeatherPointRef? HouseholdDefaultPoint { get; init; }
}

/// <summary><c>PUT /api/weather/preferences</c>. Takes no user id: the caller's own row only.</summary>
public sealed record UpdateWeatherPreferences
{
    public Guid? DefaultPointId { get; set; }
}
