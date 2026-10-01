using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PhotoAlbumConfiguration : IEntityTypeConfiguration<PhotoAlbum>
{
    public void Configure(EntityTypeBuilder<PhotoAlbum> entity)
    {
        // Optional cover photo: a real in-context FK that nulls out when the referenced photo is
        // deleted, rather than dangling (§6). No inverse navigation on Photo.
        entity.HasOne(album => album.CoverPhoto)
            .WithMany()
            .HasForeignKey(album => album.CoverPhotoId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
