using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class UserProfileImageBlobConfiguration : IEntityTypeConfiguration<UserProfileImageBlob>
{
    public void Configure(EntityTypeBuilder<UserProfileImageBlob> entity)
    {
        entity.HasOne(blob => blob.Image)
            .WithOne(image => image!.Blob)
            .HasForeignKey<UserProfileImageBlob>(blob => blob.UserProfileImageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
