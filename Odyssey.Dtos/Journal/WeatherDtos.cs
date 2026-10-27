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

public enum WeatherLocationSortBy
{
    Name,
}

/// <summary>
/// The shared limits and rules of the weather contract — one declaration named by the DTO attributes, the
/// services, the settings descriptors and the client (issue #348).
/// </summary>
public static class WeatherRules
{
    public const int StationIdMaxLength = 32;
    public const int SymbolCodeMaxLength = 48;
    public const int ContactMaxLength = 254;

    /// <summary>Weather positions are stored and sent to 3 decimal places (≈ 100 m).</summary>
    public const int CoordinateScale = 3;

    public const int HourlyMaxDays = 10;
    public const int DailyMaxDays = 62;
    public const int OptionsMax = 200;

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

    /// <summary><c>WeatherDefaultLocationId</c> shape: empty, or a GUID. Selectability is checked server-side.</summary>
    public static string? ValidateDefaultLocationId(string value) =>
        value.Length == 0 || Guid.TryParse(value, out _) ? null : "Choose a location from the list.";
}

/// <summary>MET Norway's attribution, carried on every weather response (§5).</summary>
public sealed record WeatherAttribution
{
    public string Text { get; init; } = "Weather data from MET Norway";
    public string License { get; init; } = "NLOD 2.0 / CC BY 4.0";
    public string Url { get; init; } = "https://api.met.no/";

    public static WeatherAttribution MetNorway { get; } = new();
}

/// <summary>The only location shape a weather read embeds: no coordinates, no address.</summary>
public sealed record WeatherLocationRef
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
    public WeatherLocationRef? Location { get; init; }
    public WeatherStatus Status { get; init; }
    public bool Stale { get; init; }
    public WeatherStep? Step { get; init; }
    public WeatherDaySummary? Today { get; init; }
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

public sealed record WeatherHourlyResponse
{
    public WeatherLocationRef? Location { get; init; }
    public WeatherStatus Status { get; init; }
    public bool Stale { get; init; }
    public IReadOnlyList<WeatherStep> Steps { get; init; } = [];
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

/// <summary>
/// What measured history is stored for a location, independent of the requested range, so a caller holding
/// only <c>weather.read</c> can page through it without the Admin row.
/// </summary>
public sealed record WeatherHistoryBounds
{
    /// <summary>The oldest stored local date with a measured half; null when none is stored.</summary>
    public DateOnly? MeasuredFrom { get; init; }

    /// <summary>The newest stored local date with a measured half; null when none is stored.</summary>
    public DateOnly? MeasuredThrough { get; init; }

    /// <summary>Whether Frost has a station within range of the location's current weather position.</summary>
    public bool HasObservationStation { get; init; }
}

public sealed record WeatherDailyResponse
{
    public WeatherLocationRef? Location { get; init; }
    public WeatherStatus Status { get; init; }
    public bool Stale { get; init; }
    public IReadOnlyList<WeatherDaySummary> Days { get; init; } = [];

    /// <summary>Null when no location resolved or the range is out of range; otherwise always filled.</summary>
    public WeatherHistoryBounds? History { get; init; }
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

public sealed record CalendarEventWeatherResponse
{
    public WeatherLocationRef? Location { get; init; }
    public WeatherStatus Status { get; init; }
    public bool Stale { get; init; }
    public bool IsAllDay { get; init; }
    public IReadOnlyList<WeatherStep> Steps { get; init; } = [];
    public IReadOnlyList<WeatherDaySummary> Days { get; init; } = [];
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

public sealed record JournalEntryWeatherResponse
{
    public WeatherLocationRef? Location { get; init; }
    public WeatherStatus Status { get; init; }
    public bool Stale { get; init; }
    public DateOnly? Date { get; init; }
    public WeatherDaySummary? Day { get; init; }
    public WeatherAttribution Attribution { get; init; } = WeatherAttribution.MetNorway;
}

/// <summary>Binding model for the optional <c>locationId</c> every weather read takes.</summary>
public sealed class WeatherLocationQuery
{
    public Guid? LocationId { get; set; }
}

/// <summary><c>GET /api/weather/hourly</c>: <c>toUtc &gt; fromUtc</c>, span ≤ 10 days.</summary>
public sealed class WeatherHourlyQuery : IValidatableObject
{
    public Guid? LocationId { get; set; }

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

/// <summary><c>GET /api/weather/daily</c>: <c>to ≥ from</c>, span ≤ 62 days.</summary>
public sealed class WeatherDailyQuery : IValidatableObject
{
    public Guid? LocationId { get; set; }

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

/// <summary>The current frozen site, coarser than the location's own coordinates (3 decimals).</summary>
public sealed record WeatherLocationSiteDto
{
    public int Version { get; init; }
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public int? AltitudeMeters { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
    public DateTime AppliedAt { get; init; }
}

/// <summary>The Admin row (§5, endpoints 6–8).</summary>
public sealed record ExistingWeatherLocation
{
    public Guid LocationId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool IsTracked { get; init; }
    public bool TrackingSuspended { get; init; }
    public WeatherTrackingSuspendedReason? TrackingSuspendedReason { get; init; }
    public DateOnly HistoryFrom { get; init; }
    public WeatherLocationSiteDto Site { get; init; } = new();
    public bool LocationMoved { get; init; }
    public bool LocationArchived { get; init; }
    public DateTime LocationUpdatedAt { get; init; }

    /// <summary>
    /// Opaque: identifies the live location's rounded position, altitude and time zone — what weather copies.
    /// Sent back as <c>expectedLocationFingerprint</c> on enable and apply (amendment 2).
    /// </summary>
    public string LocationFingerprint { get; init; } = string.Empty;

    public bool PurgeInProgress { get; init; }
    public bool IsHouseholdDefault { get; init; }
    public bool HasObservationStation { get; init; }
    public DateTime? LastForecastSuccessUtc { get; init; }
    public WeatherFetchFailureKind? ForecastFailureKind { get; init; }
    public DateTime? LastObservationSuccessUtc { get; init; }
    public WeatherFetchFailureKind? ObservationFailureKind { get; init; }
    public DateTime EnabledAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class WeatherLocationsQueryParams : QueryParams<WeatherLocationSortBy>;

/// <summary><c>PUT /api/weather/locations/{locationId}</c> — create or update the setting.</summary>
public sealed record UpsertWeatherLocation : IValidatableObject
{
    [Required]
    public bool? Enabled { get; set; }

    [Required]
    public bool? IsTracked { get; set; }

    /// <summary><see langword="null"/>: enable date − 1 year on create, the stored value otherwise.</summary>
    public DateOnly? HistoryFrom { get; set; }

    /// <summary>Required on create; ignored on an existing row (re-enabling keeps its site).</summary>
    [StringLength(128)]
    public string? ExpectedLocationFingerprint { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (HistoryFrom is { } from)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (from < WeatherRules.HistoryFloor || from > today)
            {
                yield return new ValidationResult(
                    $"History can start between {WeatherRules.HistoryFloor:yyyy-MM-dd} and today.", [nameof(HistoryFrom)]);
            }
        }
    }
}

/// <summary><c>POST /api/weather/locations/{locationId}/apply-location</c>.</summary>
public sealed record ApplyWeatherLocation
{
    [Required]
    [StringLength(128)]
    public string? ExpectedLocationFingerprint { get; set; }
}

public sealed record WeatherRefreshResult
{
    public bool Fetched { get; init; }
    public DateTime? IssuedAtUtc { get; init; }
    public WeatherFetchFailureKind? ForecastFailureKind { get; init; }
}

public sealed record WeatherLocationOptions
{
    public IReadOnlyList<WeatherLocationRef> Locations { get; init; } = [];
    public Guid? HouseholdDefaultLocationId { get; init; }
}

public sealed record WeatherPreferences
{
    public WeatherLocationRef? DefaultLocation { get; init; }
    public WeatherLocationRef? HouseholdDefaultLocation { get; init; }
}

/// <summary><c>PUT /api/weather/preferences</c>. Takes no user id: the caller's own row only.</summary>
public sealed record UpdateWeatherPreferences
{
    public Guid? DefaultLocationId { get; set; }
}
