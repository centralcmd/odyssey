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
    }
}
