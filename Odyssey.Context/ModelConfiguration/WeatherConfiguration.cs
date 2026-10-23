using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

// Weather (issue #348 §4). Every weather table references Location with RESTRICT: a future hard delete of
// a location must purge its weather first (the Admin purge), and a raw DELETE is refused by the database
// rather than silently cascading away a household's measured history.

internal sealed class WeatherLocationConfiguration : IEntityTypeConfiguration<WeatherLocation>
{
    public void Configure(EntityTypeBuilder<WeatherLocation> entity)
    {
        entity.HasOne<Location>()
            .WithOne()
            .HasForeignKey<WeatherLocation>(weather => weather.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        // The current site must exist. RESTRICT (amendment 1): the purge's final transaction deletes the
        // schedule, then this row, then the current site, so the key never blocks a purge that runs in order.
        entity.HasOne<WeatherLocationSite>()
            .WithMany()
            .HasForeignKey(weather => new { weather.LocationId, weather.CurrentSiteVersion })
            .HasPrincipalKey(site => new { site.LocationId, site.SiteVersion })
            .OnDelete(DeleteBehavior.Restrict);

        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_WeatherLocations_CurrentSiteVersionPositive", "`CurrentSiteVersion` >= 1"));
    }
}

internal sealed class WeatherLocationSiteConfiguration : IEntityTypeConfiguration<WeatherLocationSite>
{
    public void Configure(EntityTypeBuilder<WeatherLocationSite> entity)
    {
        entity.HasOne<Location>()
            .WithMany()
            .HasForeignKey(site => site.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.ToTable(tb =>
            tb.HasCheckConstraint("CK_WeatherLocationSites_VersionPositive", "`SiteVersion` >= 1"));
    }
}

internal sealed class WeatherLocationScheduleConfiguration : IEntityTypeConfiguration<WeatherLocationSchedule>
{
    public void Configure(EntityTypeBuilder<WeatherLocationSchedule> entity)
    {
        entity.HasOne<WeatherLocation>()
            .WithOne()
            .HasForeignKey<WeatherLocationSchedule>(schedule => schedule.LocationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(schedule => schedule.ForecastFailureKind).HasConversion<int?>();
        entity.Property(schedule => schedule.ObservationFailureKind).HasConversion<int?>();
    }
}

internal sealed class WeatherForecastRunConfiguration : IEntityTypeConfiguration<WeatherForecastRun>
{
    public void Configure(EntityTypeBuilder<WeatherForecastRun> entity)
    {
        entity.HasOne<Location>()
            .WithMany()
            .HasForeignKey(run => run.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasMany(run => run.Steps)
            .WithOne()
            .HasForeignKey(step => step.WeatherForecastRunId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(run => run.Provider).HasConversion<int>();
        entity.ToTable(tb => tb.HasCheckConstraint("CK_WeatherForecastRuns_ProviderKnown", "`Provider` IN (1)"));
    }
}

internal sealed class WeatherObservationConfiguration : IEntityTypeConfiguration<WeatherObservation>
{
    public void Configure(EntityTypeBuilder<WeatherObservation> entity)
    {
        entity.HasOne<Location>()
            .WithMany()
            .HasForeignKey(observation => observation.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(observation => observation.Provider).HasConversion<int>();
    }
}

internal sealed class WeatherDayConfiguration : IEntityTypeConfiguration<WeatherDay>
{
    public void Configure(EntityTypeBuilder<WeatherDay> entity)
    {
        entity.HasOne<Location>()
            .WithMany()
            .HasForeignKey(day => day.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        // A derived row exists only to carry one half or the other; an empty one is a rebuild bug.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_WeatherDays_HasData", "`ForecastCoverageHours` IS NOT NULL OR `ObservedFetchedAtUtc` IS NOT NULL"));
    }
}

internal sealed class UserWeatherPreferenceConfiguration : IEntityTypeConfiguration<UserWeatherPreference>
{
    public void Configure(EntityTypeBuilder<UserWeatherPreference> entity)
    {
        // The caller's own row: it goes with the user, and a purged or removed location simply unsets it.
        entity.HasOne<ApplicationUser>()
            .WithOne()
            .HasForeignKey<UserWeatherPreference>(preference => preference.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Location>()
            .WithMany()
            .HasForeignKey(preference => preference.DefaultLocationId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
