using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class JournalTaskAttachmentConfiguration : IEntityTypeConfiguration<JournalTaskAttachment>
{
    public void Configure(EntityTypeBuilder<JournalTaskAttachment> entity)
    {
        entity.HasOne(attachment => attachment.JournalTask)
            .WithMany(item => item.Attachments)
            .HasForeignKey(attachment => attachment.JournalTaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(attachment => new { attachment.JournalTaskId, attachment.FileId }).IsUnique();
    }
}
