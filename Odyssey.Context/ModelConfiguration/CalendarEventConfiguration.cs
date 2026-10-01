using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> entity)
    {
        // Cascade so a calendar's events die with it. In normal operation the service layer
        // blocks DELETE /api/calendars/{id} with 409 while the calendar has any events or
        // patterns — this cascade exists purely as a DB-level safety net.
        entity.HasOne(calendarEvent => calendarEvent.Calendar)
            .WithMany(calendar => calendar.Events)
            .HasForeignKey(calendarEvent => calendarEvent.CalendarId)
            .OnDelete(DeleteBehavior.Cascade);

        // SetNull: only relevant when a pattern is deleted directly (not via a calendar
        // cascade). RecurrencePatternService hard-deletes future generated events itself and
        // relies on this to detach (not destroy) past/current ones.
        entity.HasOne(calendarEvent => calendarEvent.RecurrencePattern)
            .WithMany(pattern => pattern.GeneratedEvents)
            .HasForeignKey(calendarEvent => calendarEvent.RecurrencePatternId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
