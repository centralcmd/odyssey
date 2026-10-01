using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class JournalEntryTagConfiguration : IEntityTypeConfiguration<JournalEntryTag>
{
    public void Configure(EntityTypeBuilder<JournalEntryTag> entity)
    {
        // Surrogate Guid PK (every entity owns its own id); the natural key is a unique index.
        entity.HasIndex(link => new { link.JournalEntryId, link.JournalTagId }).IsUnique();

        // Cascade so an entry's tag links die with it.
        entity.HasOne(link => link.JournalEntry)
            .WithMany(journalEntry => journalEntry.EntryTags)
            .HasForeignKey(link => link.JournalEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict so an in-use tag cannot be hard-deleted out from under an entry.
        entity.HasOne(link => link.JournalTag)
            .WithMany(tag => tag.EntryTags)
            .HasForeignKey(link => link.JournalTagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
