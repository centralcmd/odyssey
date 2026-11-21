using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

// Issue #521 §4: every key CASCADEs, as the journal's link tables do. A link dies with its owner and with the file or
// photo it names. The EF InMemory provider runs no cascade, so CalendarLinkService also removes an owner's rows by
// tracked RemoveRange on every delete path the fast tiers exercise.

internal sealed class CalendarEventAttachmentConfiguration : IEntityTypeConfiguration<CalendarEventAttachment>
{
    public void Configure(EntityTypeBuilder<CalendarEventAttachment> entity)
    {
        entity.HasOne<CalendarEvent>()
            .WithMany()
            .HasForeignKey(link => link.CalendarEventId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<FileMetadata>()
            .WithMany()
            .HasForeignKey(link => link.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(link => new { link.CalendarEventId, link.FileId }).IsUnique();
    }
}

internal sealed class CalendarEventPhotoConfiguration : IEntityTypeConfiguration<CalendarEventPhoto>
{
    public void Configure(EntityTypeBuilder<CalendarEventPhoto> entity)
    {
        entity.HasOne<CalendarEvent>()
            .WithMany()
            .HasForeignKey(link => link.CalendarEventId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Photo>()
            .WithMany()
            .HasForeignKey(link => link.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(link => new { link.CalendarEventId, link.PhotoId }).IsUnique();
    }
}

internal sealed class RecurrencePatternAttachmentConfiguration : IEntityTypeConfiguration<RecurrencePatternAttachment>
{
    public void Configure(EntityTypeBuilder<RecurrencePatternAttachment> entity)
    {
        entity.HasOne<RecurrencePattern>()
            .WithMany()
            .HasForeignKey(link => link.RecurrencePatternId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<FileMetadata>()
            .WithMany()
            .HasForeignKey(link => link.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(link => new { link.RecurrencePatternId, link.FileId }).IsUnique();
    }
}

internal sealed class RecurrencePatternPhotoConfiguration : IEntityTypeConfiguration<RecurrencePatternPhoto>
{
    public void Configure(EntityTypeBuilder<RecurrencePatternPhoto> entity)
    {
        entity.HasOne<RecurrencePattern>()
            .WithMany()
            .HasForeignKey(link => link.RecurrencePatternId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Photo>()
            .WithMany()
            .HasForeignKey(link => link.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(link => new { link.RecurrencePatternId, link.PhotoId }).IsUnique();
    }
}
