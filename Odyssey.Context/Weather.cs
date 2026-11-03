using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Journal;

namespace Odyssey.Context;

/// <summary>
/// A weather point (issue #387): a named position with its own coordinates, fetched once, which any number of
/// household <see cref="Location"/>s link to through <see cref="Location.WeatherPointId"/>. It owns the settings
/// and the <b>current</b> frozen site; the sites themselves are the append-only <see cref="WeatherPointSite"/>
/// history, so the time zone that owned a past date is never lost. A point has no key to a location: archiving,
/// merging or deleting a place never touches it.
/// </summary>
/// <remarks>
/// A point converted from a former per-location weather row keeps that location's GUID. Nothing may resolve a
/// bare id across <c>Locations</c> and <c>WeatherPoints</c>; every endpoint names which table its id belongs to.
/// </remarks>
[Index(nameof(Name), IsUnique = true)]
public class WeatherPoint
{
    [Key]
    public Guid WeatherPointId { get; set; }

    [Required]
    [StringLength(WeatherRules.PointNameMaxLength)]
    public required string Name { get; set; }

    /// <summary>Forecasts are fetched, and the point is selectable on the dashboard.</summary>
    public bool ForecastEnabled { get; set; }

    /// <summary>Measured days are fetched from MET Norway's Frost service.</summary>
    public bool HistoryEnabled { get; set; }

    public bool IsTracked { get; set; }

    public DateOnly HistoryFrom { get; set; }

    public DateTime EnabledAt { get; set; }

    /// <summary>
    /// The version of the current site. Deliberately <b>no</b> foreign key: the site depends on the point, so a key
    /// back would be a cycle neither InnoDB nor EF can insert or delete. The point service writes the site and the
    /// point in one <c>SaveChangesAsync</c>; a real-MariaDB test pins that the site always exists.
    /// </summary>
    public int CurrentSiteVersion { get; set; }

    /// <summary>Set while a purge is running; the point is fenced until the purge's final transaction.</summary>
    public DateTime? PurgeStartedAt { get; set; }

    /// <summary>Which purge holds the fence; null exactly when <see cref="PurgeStartedAt"/> is.</summary>
    public WeatherPurgeMode? PurgeMode { get; set; }

    /// <summary>
    /// When every measure group had been looked up at least once for the current site; null until then. Written by
    /// the worker, which never rotates <see cref="SettingsVersion"/>.
    /// </summary>
    public DateTime? StationResolvedAtUtc { get; set; }

    /// <summary>
    /// The Admin's concurrency token, rotated only by Admin writes (create, <c>PUT</c>, purge start). The fetch fence
    /// is <see cref="WeatherPointSchedule.Version"/>; the two guard different writers, so a background fetch never
    /// makes an Admin's <c>PUT</c> a spurious <c>409</c>.
    /// </summary>
    [ConcurrencyCheck]
    public Guid SettingsVersion { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One frozen site of a <see cref="WeatherPoint"/> — the position and time zone weather used from
/// <see cref="AppliedAt"/>. Append-only: never updated, removed only by the Admin purge. Cascades with the point.
/// </summary>
[PrimaryKey(nameof(WeatherPointId), nameof(SiteVersion))]
public class WeatherPointSite
{
    public Guid WeatherPointId { get; set; }

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
/// The scheduler's state for one point (1:1, cascades with <see cref="WeatherPoint"/>). Claiming and
/// write fencing both rest on <see cref="Version"/>: a replica claims a due job by rotating it, and every
/// ingest saves under the version it claimed with, so disable, apply and purge — which also rotate it — make
/// an in-flight fetch's save fail rather than land afterwards.
/// </summary>
public class WeatherPointSchedule
{
    [Key]
    public Guid WeatherPointId { get; set; }

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
}

/// <summary>One forecast as MET issued it for one site. Append-only.</summary>
[Index(nameof(WeatherPointId), nameof(SiteVersion), nameof(ProviderUpdatedAt), IsUnique = true)]
public class WeatherForecastRun
{
    [Key]
    public Guid WeatherForecastRunId { get; set; }

    public Guid WeatherPointId { get; set; }

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
[Index(nameof(WeatherPointId), nameof(TimeUtc), nameof(SiteVersion), nameof(RunProviderUpdatedAt), nameof(RunFetchedAtUtc))]
public class WeatherForecastStep
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long WeatherForecastStepId { get; set; }

    public Guid WeatherForecastRunId { get; set; }

    public Guid WeatherPointId { get; set; }

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

/// <summary>
/// The Frost station one measure group of a point is measured at, for the current site. Each group has its
/// own because the measures differ in how far they hold and in how many stations report them. A row with no
/// <see cref="StationId"/> is a lookup that found nothing in reach; it is retried at most once per 24 h. The rows
/// go when the site changes or history is purged, and with the <see cref="WeatherPoint"/> (CASCADE).
/// </summary>
[PrimaryKey(nameof(WeatherPointId), nameof(Group))]
public class WeatherStation
{
    public Guid WeatherPointId { get; set; }

    public WeatherMeasureGroup Group { get; set; }

    [StringLength(WeatherRules.StationIdMaxLength)]
    public string? StationId { get; set; }

    [StringLength(WeatherRules.StationNameMaxLength)]
    public string? Name { get; set; }

    [Precision(5, 1)] public decimal? DistanceKm { get; set; }

    public int? AltitudeMeters { get; set; }

    public DateTime ResolvedAtUtc { get; set; }
}

/// <summary>One measured day from a Frost station. Append-only; a changed value inserts a new row.</summary>
[Index(nameof(WeatherPointId), nameof(LocalDate), nameof(SiteVersion), nameof(FetchedAtUtc))]
public class WeatherObservation
{
    [Key]
    public Guid WeatherObservationId { get; set; }

    public Guid WeatherPointId { get; set; }

    public int SiteVersion { get; set; }

    public WeatherProvider Provider { get; set; }

    /// <summary>The station each group's values came from; null where the group has no station.</summary>
    [StringLength(WeatherRules.StationIdMaxLength)]
    public string? TemperatureStationId { get; set; }

    [StringLength(WeatherRules.StationIdMaxLength)]
    public string? PrecipitationStationId { get; set; }

    [StringLength(WeatherRules.StationIdMaxLength)]
    public string? WindStationId { get; set; }

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
/// The derived daily summary per point and local date — rebuildable from the append-only tables, so it
/// is upserted rather than appended. The forecast and observed halves are independent.
/// </summary>
[PrimaryKey(nameof(WeatherPointId), nameof(LocalDate))]
public class WeatherDay
{
    public Guid WeatherPointId { get; set; }

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
