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

        entity.ToTable(tb =>
        {
            // Issue #428 §4.1: a photo is either legacy (a Files-store file) or owns its original — never both,
            // never neither.
            tb.HasCheckConstraint("CK_Photos_ExactlyOneSource", "(`FileId` IS NULL) <> (`StoredObjectId` IS NULL)");
            tb.HasCheckConstraint(
                "CK_Photos_OriginalComplete",
                "`StoredObjectId` IS NULL OR (`FileName` IS NOT NULL AND `ContentType` IS NOT NULL "
                + "AND `SizeBytes` IS NOT NULL AND `Sha256Hash` IS NOT NULL)");
        });
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

internal sealed class PhotoOriginalBackfillLeaseConfiguration : IEntityTypeConfiguration<PhotoOriginalBackfillLease>
{
    public void Configure(EntityTypeBuilder<PhotoOriginalBackfillLease> entity)
    {
        entity.Property(l => l.Id).ValueGeneratedNever();
        entity.ToTable(tb => tb.HasCheckConstraint("CK_PhotoOriginalBackfillLeases_Singleton", "`Id` = 1"));

        entity.HasData(new PhotoOriginalBackfillLease
        {
            Id = PhotoOriginalBackfillLease.SingletonId,
            Version = new Guid("8a1e6f2c-0b4d-4c7a-9e35-71d2c8f04b96"),
        });
    }
}

internal sealed class PhotoOriginalBackfillFailureConfiguration : IEntityTypeConfiguration<PhotoOriginalBackfillFailure>
{
    public void Configure(EntityTypeBuilder<PhotoOriginalBackfillFailure> entity)
    {
        entity.Property(f => f.PhotoId).ValueGeneratedNever();
        entity.Property(f => f.Code).IsUnicode(false);

        // CASCADE: a deleted photo needs no failure record.
        entity.HasOne<Photo>()
            .WithMany()
            .HasForeignKey(f => f.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_PhotoOriginalBackfillFailures_Code", "`Code` IN ('hash', 'read', 'too-large', 'error')"));
    }
}
