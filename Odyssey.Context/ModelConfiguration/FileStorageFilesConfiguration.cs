using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

/// <summary>
/// <see cref="FileMetadata"/> on the object store (issue #448 §4.1). One stored-object column per feature, because the
/// store's invariant is one reference column per feature; each is RESTRICT (#346 consumer rule) and unique.
/// </summary>
internal sealed class FileMetadataStorageConfiguration : IEntityTypeConfiguration<FileMetadata>
{
    public void Configure(EntityTypeBuilder<FileMetadata> entity)
    {
        entity.HasOne<StoredObject>()
            .WithMany()
            .HasForeignKey(f => f.FinanceStoredObjectId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<StoredObject>()
            .WithMany()
            .HasForeignKey(f => f.JournalStoredObjectId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<StoredObject>()
            .WithMany()
            .HasForeignKey(f => f.ContactAvatarStoredObjectId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(f => f.FinanceStoredObjectId).IsUnique();
        entity.HasIndex(f => f.JournalStoredObjectId).IsUnique();
        entity.HasIndex(f => f.ContactAvatarStoredObjectId).IsUnique();

        // Exactly one feature's stored object is set: a file always has its bytes on the store.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_FileMetadata_ExactlyOneSource",
            "(`FinanceStoredObjectId` IS NOT NULL) + (`JournalStoredObjectId` IS NOT NULL)"
            + " + (`ContactAvatarStoredObjectId` IS NOT NULL) = 1"));
    }
}

internal sealed class StoredFileBucketConfiguration : IEntityTypeConfiguration<StoredFileBucket>
{
    public void Configure(EntityTypeBuilder<StoredFileBucket> entity)
    {
        entity.Property(b => b.FeatureKey).IsUnicode(false);
        entity.Property(b => b.Year).ValueGeneratedNever();
        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_StoredFileBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
            tb.HasCheckConstraint("CK_StoredFileBuckets_ObjectsWrittenRange", "`ObjectsWritten` BETWEEN 0 AND 10000");
        });
    }
}
