using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PhotoOriginalConfiguration : IEntityTypeConfiguration<Photo>
{
    public void Configure(EntityTypeBuilder<Photo> entity)
    {
        // RESTRICT (#346 consumer rule): the original is released through StageRelease, never deleted from under
        // the photo; a cascade-deleted photo leaves it to the hourly unreferenced sweep.
        entity.HasOne<StoredObject>()
            .WithMany()
            .HasForeignKey(p => p.StoredObjectId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(p => p.ContentType).IsUnicode(false);
        entity.Property(p => p.Sha256Hash).IsUnicode(false).IsFixedLength();

        // Every photo owns its original: StoredObjectId and the four original columns are NOT NULL, which replaced
        // CK_Photos_ExactlyOneSource and CK_Photos_OriginalComplete when the legacy shape was removed.
    }
}

internal sealed class PhotoOriginalBucketConfiguration : IEntityTypeConfiguration<PhotoOriginalBucket>
{
    public void Configure(EntityTypeBuilder<PhotoOriginalBucket> entity)
    {
        entity.Property(b => b.Year).ValueGeneratedNever();
        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PhotoOriginalBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
            tb.HasCheckConstraint("CK_PhotoOriginalBuckets_PhotosWrittenRange", "`PhotosWritten` BETWEEN 0 AND 10000");
        });
    }
}
