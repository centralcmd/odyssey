using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class RecurrencePatternConfiguration : IEntityTypeConfiguration<RecurrencePattern>
{
    public void Configure(EntityTypeBuilder<RecurrencePattern> entity)
    {
        entity.HasOne(pattern => pattern.Calendar)
            .WithMany(calendar => calendar.RecurrencePatterns)
            .HasForeignKey(pattern => pattern.CalendarId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_RecurrencePatterns_AllDayHasNoZone", "`IsAllDay` = 0 OR `TimeZoneId` IS NULL"));
    }
}

internal sealed class RecurrenceExclusionConfiguration : IEntityTypeConfiguration<RecurrenceExclusion>
{
    public void Configure(EntityTypeBuilder<RecurrenceExclusion> entity)
    {
        // Issue #519 §4: an exclusion dies with its series. RecurrencePatternService.Delete also removes them by
        // tracked RemoveRange, so the EF InMemory tiers see the same result.
        entity.HasOne(exclusion => exclusion.RecurrencePattern)
            .WithMany(pattern => pattern.Exclusions)
            .HasForeignKey(exclusion => exclusion.RecurrencePatternId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
