using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PhotoAlbumItemConfiguration : IEntityTypeConfiguration<PhotoAlbumItem>
{
    public void Configure(EntityTypeBuilder<PhotoAlbumItem> entity)
    {
        entity.HasIndex(item => new { item.PhotoAlbumId, item.PhotoId }).IsUnique();

        entity.HasOne(item => item.PhotoAlbum)
            .WithMany(album => album.Items)
            .HasForeignKey(item => item.PhotoAlbumId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(item => item.Photo)
            .WithMany(photo => photo.Albums)
            .HasForeignKey(item => item.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
