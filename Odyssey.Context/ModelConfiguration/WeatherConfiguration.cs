using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

// Weather (issue #348 §4; re-keyed onto weather points by issue #387 §4). The settings-shaped tables (sites,
// schedule, stations) cascade with their point. The three bulk tables (runs, observations, days) reference the
// point with RESTRICT, so a point delete must go through the batched purger and can never become one unbounded
// cascade. WeatherForecastSteps carries the point id denormalized and deliberately no key: it is the largest
// table, and it already cascades with its run.

internal sealed class WeatherPointConfiguration : IEntityTypeConfiguration<WeatherPoint>
{
    public void Configure(EntityTypeBuilder<WeatherPoint> entity)
    {
        // No key to the current site (issue #387 §4, architect finding 1): the site depends on the point, so a
        // key back would be a cycle. The service writes both in one save; a MariaDB test pins the invariant.
        entity.Property(point => point.PurgeMode).HasConversion<int?>();

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_WeatherPoints_CurrentSiteVersionPositive", "`CurrentSiteVersion` >= 1");
            tb.HasCheckConstraint("CK_WeatherPoints_PurgeModeWithFence", "(`PurgeStartedAt` IS NULL) = (`PurgeMode` IS NULL)");
        });
    }
}

internal sealed class WeatherPointSiteConfiguration : IEntityTypeConfiguration<WeatherPointSite>
{
    public void Configure(EntityTypeBuilder<WeatherPointSite> entity)
    {
        entity.HasOne<WeatherPoint>()
            .WithMany()
            .HasForeignKey(site => site.WeatherPointId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.ToTable(tb =>
            tb.HasCheckConstraint("CK_WeatherPointSites_VersionPositive", "`SiteVersion` >= 1"));
    }
}

internal sealed class WeatherPointScheduleConfiguration : IEntityTypeConfiguration<WeatherPointSchedule>
{
    public void Configure(EntityTypeBuilder<WeatherPointSchedule> entity)
    {
        entity.HasOne<WeatherPoint>()
            .WithOne()
            .HasForeignKey<WeatherPointSchedule>(schedule => schedule.WeatherPointId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(schedule => schedule.ForecastFailureKind).HasConversion<int?>();
        entity.Property(schedule => schedule.ObservationFailureKind).HasConversion<int?>();
    }
}

internal sealed class WeatherStationConfiguration : IEntityTypeConfiguration<WeatherStation>
{
    public void Configure(EntityTypeBuilder<WeatherStation> entity)
    {
        // A station is a setting of the point, not history: it goes with the point.
        entity.HasOne<WeatherPoint>()
            .WithMany()
            .HasForeignKey(station => station.WeatherPointId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(station => station.Group).HasConversion<int>();
        entity.ToTable(tb => tb.HasCheckConstraint("CK_WeatherStations_GroupKnown", "`Group` IN (0, 1, 2)"));
    }
}

internal sealed class WeatherForecastRunConfiguration : IEntityTypeConfiguration<WeatherForecastRun>
{
    public void Configure(EntityTypeBuilder<WeatherForecastRun> entity)
    {
        entity.HasOne<WeatherPoint>()
            .WithMany()
            .HasForeignKey(run => run.WeatherPointId)
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
        entity.HasOne<WeatherPoint>()
            .WithMany()
            .HasForeignKey(observation => observation.WeatherPointId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(observation => observation.Provider).HasConversion<int>();
    }
}

internal sealed class WeatherDayConfiguration : IEntityTypeConfiguration<WeatherDay>
{
    public void Configure(EntityTypeBuilder<WeatherDay> entity)
    {
        entity.HasOne<WeatherPoint>()
            .WithMany()
            .HasForeignKey(day => day.WeatherPointId)
            .OnDelete(DeleteBehavior.Restrict);

        // A derived row exists only to carry one half or the other; an empty one is a rebuild bug.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_WeatherDays_HasData", "`ForecastCoverageHours` IS NOT NULL OR `ObservedFetchedAtUtc` IS NOT NULL"));
    }
}
