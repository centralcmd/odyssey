using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class JournalEntryContactConfiguration : IEntityTypeConfiguration<JournalEntryContact>
{
    public void Configure(EntityTypeBuilder<JournalEntryContact> entity)
    {
        entity.HasIndex(link => new { link.JournalEntryId, link.ContactId }).IsUnique();

        entity.HasOne(link => link.JournalEntry)
            .WithMany(journalEntry => journalEntry.Contacts)
            .HasForeignKey(link => link.JournalEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Contact now lives in this context (moved from Finance): the link is a real FK. Cascade so
        // an entry's contact links die with the contact, matching the other link-row conventions.
        entity.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(link => link.ContactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
