using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class UserProfileImageConfiguration : IEntityTypeConfiguration<UserProfileImage>
{
    public void Configure(EntityTypeBuilder<UserProfileImage> entity)
    {
        // The user profile picture (issue #94 §6). Two tables that share NOTHING with the domain file
        // store — no relationship in either direction, so no files.* claim reaches them and no
        // FileService code path touches them.
        //
        // Three things here are load-bearing and were each got backwards at least once in review:
        //
        //  1. ImageVersion is the CONCURRENCY TOKEN. A replace updates the row in place, so the unique
        //     index can only fire for two concurrent FIRST uploads; without the token two
        //     concurrent REPLACES both succeed and the later silently wins. The version is regenerated
        //     per write, so a stale-version UPDATE affects zero rows and raises
        //     DbUpdateConcurrencyException at no extra cost. It is also what makes a DELETE racing a
        //     POST a 409 rather than a 500.
        //  2. The blob is the DEPENDENT, inverting FileMetadata/FileBlob. That is what makes
        //     AspNetUsers → UserProfileImage → UserProfileImageBlob an unbroken cascade chain, so a
        //     deleted account takes its facial image bytes with it rather than orphaning them
        //     permanently. Its PK is its FK, so it needs no separate index.
        //  3. Both cascades are declared explicitly rather than left to EF's default for a required
        //     relationship, because the erasure guarantee rests on them.
        entity.HasOne<ApplicationUser>()
            .WithOne()
            .HasForeignKey<UserProfileImage>(image => image.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(image => image.ImageVersion).IsConcurrencyToken();
    }
}
