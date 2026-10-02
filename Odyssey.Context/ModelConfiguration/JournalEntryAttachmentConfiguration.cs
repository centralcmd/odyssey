using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class JournalEntryAttachmentConfiguration : IEntityTypeConfiguration<JournalEntryAttachment>
{
    public void Configure(EntityTypeBuilder<JournalEntryAttachment> entity)
    {
        entity.HasOne(attachment => attachment.JournalEntry)
            .WithMany(journalEntry => journalEntry.Attachments)
            .HasForeignKey(attachment => attachment.JournalEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(attachment => new { attachment.JournalEntryId, attachment.FileId }).IsUnique();
    }
}
