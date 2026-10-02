using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PhotoTagLinkConfiguration : IEntityTypeConfiguration<PhotoTagLink>
{
    public void Configure(EntityTypeBuilder<PhotoTagLink> entity)
    {
        entity.HasIndex(link => new { link.PhotoId, link.PhotoTagId }).IsUnique();

        // Cascade so a photo's tag links die with it.
        entity.HasOne(link => link.Photo)
            .WithMany(photo => photo.Tags)
            .HasForeignKey(link => link.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict so an in-use tag cannot be hard-deleted out from under a photo.
        entity.HasOne(link => link.PhotoTag)
            .WithMany(tag => tag.PhotoTags)
            .HasForeignKey(link => link.PhotoTagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
