using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class JournalTaskTagLinkConfiguration : IEntityTypeConfiguration<JournalTaskTagLink>
{
    public void Configure(EntityTypeBuilder<JournalTaskTagLink> entity)
    {
        entity.HasIndex(link => new { link.JournalTaskId, link.JournalTaskTagId }).IsUnique();

        // Cascade so a task's tag links die with it.
        entity.HasOne(link => link.JournalTask)
            .WithMany(item => item.ItemTags)
            .HasForeignKey(link => link.JournalTaskId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict so an in-use tag cannot be hard-deleted out from under a task.
        entity.HasOne(link => link.JournalTaskTag)
            .WithMany(tag => tag.ItemTags)
            .HasForeignKey(link => link.JournalTaskTagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
