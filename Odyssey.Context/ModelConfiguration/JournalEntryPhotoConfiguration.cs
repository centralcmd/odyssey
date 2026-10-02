using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class JournalEntryPhotoConfiguration : IEntityTypeConfiguration<JournalEntryPhoto>
{
    public void Configure(EntityTypeBuilder<JournalEntryPhoto> entity)
    {
        entity.HasOne(photo => photo.JournalEntry)
            .WithMany(journalEntry => journalEntry.Photos)
            .HasForeignKey(photo => photo.JournalEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Photo Library unification (issue #321 v4, Phase C): one library photo per position in an
        // entry. The 1:1 FileId↔Photo mapping preserves the old (JournalEntryId, FileId) uniqueness.
        entity.HasIndex(photo => new { photo.JournalEntryId, photo.PhotoId }).IsUnique();

        // Now that Photos and Journal share one context, PhotoId is a real FK (no inverse nav on
        // Photo). Cascade matches the other link rows into Photo (PhotoAlbumItem/PhotoPerson/
        // PhotoTagLink): deleting a library photo sweeps its journal-entry links. Two independent
        // incoming cascades on JournalEntryPhotos (from JournalEntries and Photos) is valid on
        // MariaDB/InnoDB.
        entity.HasOne<Photo>()
            .WithMany()
            .HasForeignKey(photo => photo.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
