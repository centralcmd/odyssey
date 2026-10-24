using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Journal;

namespace Odyssey.Context;

/// <summary>
/// Weather for one household <see cref="Location"/> (issue #348), created by an Admin and 1:1 with the
/// location. The row holds the settings and the <b>current</b> frozen site; the sites themselves are the
/// append-only <see cref="WeatherLocationSite"/> history, so the time zone that owned a past date is never
/// lost (B1).
///
/// <para>
/// A move of the location changes nothing here. Weather keeps using the site an Admin last applied until an
/// Admin applies the move, which is what keeps a non-admin from being able to send coordinates to a third
/// party or to clear a provider pause (§3 Frozen site).
/// </para>
/// </summary>
public class WeatherLocation
{
    [Key]
    public Guid LocationId { get; set; }

    public bool Enabled { get; set; }

    public bool IsTracked { get; set; }

    public DateOnly HistoryFrom { get; set; }

    public DateTime EnabledAt { get; set; }

    /// <summary>The version of the current site. Composite FK to <see cref="WeatherLocationSite"/>, RESTRICT.</summary>
    public int CurrentSiteVersion { get; set; }

    /// <summary>Set while a purge is running; the location is fenced until the purge's final transaction.</summary>
    public DateTime? PurgeStartedAt { get; set; }

    [StringLength(WeatherRules.StationIdMaxLength)]
    public string? FrostStationId { get; set; }

    public DateTime? StationResolvedAtUtc { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One frozen site of a <see cref="WeatherLocation"/> — the position and time zone weather used from
/// <see cref="AppliedAt"/>. Append-only: never updated, removed only by the Admin purge.
/// </summary>
[PrimaryKey(nameof(LocationId), nameof(SiteVersion))]
public class WeatherLocationSite
{
    public Guid LocationId { get; set; }

    public int SiteVersion { get; set; }

    [Precision(6, 3)]
    public decimal Latitude { get; set; }

    [Precision(7, 3)]
    public decimal Longitude { get; set; }

    public int? AltitudeMeters { get; set; }

    [Required]
    [StringLength(64)]
    public required string TimeZoneId { get; set; }

    public DateTime AppliedAt { get; set; }

    public string? AppliedByUserId { get; set; }
}

/// <summary>
/// The scheduler's state for one location (1:1, cascades with <see cref="WeatherLocation"/>). Claiming and
/// write fencing both rest on <see cref="Version"/>: a replica claims a due job by rotating it, and every
/// ingest saves under the version it claimed with, so disable, apply and purge — which also rotate it — make
/// an in-flight fetch's save fail rather than land afterwards.
/// </summary>
public class WeatherLocationSchedule
{
    [Key]
    public Guid LocationId { get; set; }

    [ConcurrencyCheck]
    public Guid Version { get; set; }

    public DateTime? LeaseUntilUtc { get; set; }

    public DateTime? NextForecastFetchAfterUtc { get; set; }

    [StringLength(64)]
    public string? ForecastLastModified { get; set; }

    /// <summary>
    /// The last forecast response's <c>Expires</c>. Kept apart from <see cref="NextForecastFetchAfterUtc"/>
    /// (which also carries the refresh interval) so an Admin's refresh can honour MET's expiry and any pause
    /// without being held back by the interval.
    /// </summary>
    public DateTime? ForecastExpiresAtUtc { get; set; }

    public DateTime? LastForecastSuccessUtc { get; set; }

    public WeatherFetchFailureKind? ForecastFailureKind { get; set; }

    public int ForecastConsecutiveFailures { get; set; }

    public DateTime? NextObservationFetchAfterUtc { get; set; }

    public DateTime? LastObservationSuccessUtc { get; set; }

    public DateOnly? ObservationsCheckedThroughDate { get; set; }

    public DateOnly? ObservationsBackfilledFromDate { get; set; }

    public WeatherFetchFailureKind? ObservationFailureKind { get; set; }

    public int ObservationConsecutiveFailures { get; set; }

    public bool ArchivedSkipLogged { get; set; }
}

/// <summary>One forecast as MET issued it for one site. Append-only.</summary>
[Index(nameof(LocationId), nameof(SiteVersion), nameof(ProviderUpdatedAt), IsUnique = true)]
public class WeatherForecastRun
{
    [Key]
    public Guid WeatherForecastRunId { get; set; }

    public Guid LocationId { get; set; }

    public int SiteVersion { get; set; }

    public WeatherProvider Provider { get; set; }

    public DateTime ProviderUpdatedAt { get; set; }

    public DateTime FetchedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    [Precision(6, 3)]
    public decimal Latitude { get; set; }

    [Precision(7, 3)]
    public decimal Longitude { get; set; }

    public int? AltitudeMeters { get; set; }

    public List<WeatherForecastStep> Steps { get; set; } = [];
}

/// <summary>
/// One time step of a run. Append-only. The run's identity is denormalized onto the step so the
/// "effective step" pick (§3 Day composition) is one index range scan.
/// </summary>
[Index(nameof(WeatherForecastRunId), nameof(TimeUtc), IsUnique = true)]
[Index(nameof(LocationId), nameof(TimeUtc), nameof(SiteVersion), nameof(RunProviderUpdatedAt), nameof(RunFetchedAtUtc))]
public class WeatherForecastStep
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long WeatherForecastStepId { get; set; }

    public Guid WeatherForecastRunId { get; set; }

    public Guid LocationId { get; set; }

    public int SiteVersion { get; set; }

    public DateTime RunProviderUpdatedAt { get; set; }

    public DateTime RunFetchedAtUtc { get; set; }

    public DateTime TimeUtc { get; set; }

    /// <summary>1, 6 or 12 — the period the period values cover.</summary>
    public byte PeriodHours { get; set; }

    [Precision(4, 1)] public decimal? AirTemperatureC { get; set; }
    [Precision(4, 1)] public decimal? RelativeHumidityPct { get; set; }
    [Precision(4, 1)] public decimal? CloudAreaFractionPct { get; set; }
    [Precision(5, 1)] public decimal? AirPressureHpa { get; set; }
    [Precision(4, 1)] public decimal? WindSpeedMps { get; set; }
    [Precision(4, 1)] public decimal? WindGustMps { get; set; }
    [Precision(4, 1)] public decimal? WindFromDirectionDeg { get; set; }
    [Precision(3, 1)] public decimal? UvIndexClearSky { get; set; }

    [StringLength(WeatherRules.SymbolCodeMaxLength)]
    public string? SymbolCode { get; set; }

    [Precision(5, 1)] public decimal? PrecipitationMm { get; set; }
    [Precision(4, 1)] public decimal? PrecipitationProbabilityPct { get; set; }
    [Precision(4, 1)] public decimal? TempMinC { get; set; }
    [Precision(4, 1)] public decimal? TempMaxC { get; set; }
}

/// <summary>One measured day from a Frost station. Append-only; a changed value inserts a new row.</summary>
[Index(nameof(LocationId), nameof(LocalDate), nameof(SiteVersion), nameof(FetchedAtUtc))]
public class WeatherObservation
{
    [Key]
    public Guid WeatherObservationId { get; set; }

    public Guid LocationId { get; set; }

    public int SiteVersion { get; set; }

    public WeatherProvider Provider { get; set; }

    [Required]
    [StringLength(WeatherRules.StationIdMaxLength)]
    public required string StationId { get; set; }

    public DateOnly LocalDate { get; set; }

    [Precision(4, 1)] public decimal? MeanTempC { get; set; }
    [Precision(4, 1)] public decimal? MinTempC { get; set; }
    [Precision(4, 1)] public decimal? MaxTempC { get; set; }
    [Precision(4, 1)] public decimal? MeanWindSpeedMps { get; set; }
    [Precision(4, 1)] public decimal? MaxWindGustMps { get; set; }
    [Precision(5, 1)] public decimal? PrecipitationMm { get; set; }

    public DateTime FetchedAtUtc { get; set; }
}

/// <summary>
/// The derived daily summary per location and local date — rebuildable from the append-only tables, so it
/// is upserted rather than appended. The forecast and observed halves are independent.
/// </summary>
[PrimaryKey(nameof(LocationId), nameof(LocalDate))]
public class WeatherDay
{
    public Guid LocationId { get; set; }

    public DateOnly LocalDate { get; set; }

    [StringLength(WeatherRules.SymbolCodeMaxLength)]
    public string? ForecastSymbolCode { get; set; }

    [Precision(4, 1)] public decimal? ForecastMinTempC { get; set; }
    [Precision(4, 1)] public decimal? ForecastMaxTempC { get; set; }
    [Precision(4, 1)] public decimal? ForecastMeanWindSpeedMps { get; set; }
    [Precision(4, 1)] public decimal? ForecastMaxWindGustMps { get; set; }
    [Precision(5, 1)] public decimal? ForecastPrecipitationMm { get; set; }

    public int? ForecastCoverageHours { get; set; }

    public DateTime? ForecastIssuedAtUtc { get; set; }

    public int? ForecastSiteVersion { get; set; }

    [Precision(4, 1)] public decimal? ObservedMinTempC { get; set; }
    [Precision(4, 1)] public decimal? ObservedMaxTempC { get; set; }
    [Precision(4, 1)] public decimal? ObservedMeanTempC { get; set; }
    [Precision(4, 1)] public decimal? ObservedMeanWindSpeedMps { get; set; }
    [Precision(4, 1)] public decimal? ObservedMaxWindGustMps { get; set; }
    [Precision(5, 1)] public decimal? ObservedPrecipitationMm { get; set; }

    public DateTime? ObservedFetchedAtUtc { get; set; }

    public int? ObservedSiteVersion { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
