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

        // Exactly one of the four sources is set: the legacy blob, or one feature's stored object.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_FileMetadata_ExactlyOneSource",
            "(`FileBlobId` IS NOT NULL) + (`FinanceStoredObjectId` IS NOT NULL) + (`JournalStoredObjectId` IS NOT NULL)"
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

internal sealed class FileBlobBackfillLeaseConfiguration : IEntityTypeConfiguration<FileBlobBackfillLease>
{
    public void Configure(EntityTypeBuilder<FileBlobBackfillLease> entity)
    {
        entity.Property(l => l.Id).ValueGeneratedNever();
        entity.ToTable(tb => tb.HasCheckConstraint("CK_FileBlobBackfillLeases_Singleton", "`Id` = 1"));

        entity.HasData(new FileBlobBackfillLease
        {
            Id = FileBlobBackfillLease.SingletonId,
            Version = new Guid("3c9b7e51-6a2f-4d08-b1e4-95f0a27c6d13"),
        });
    }
}

internal sealed class FileBlobBackfillFailureConfiguration : IEntityTypeConfiguration<FileBlobBackfillFailure>
{
    public void Configure(EntityTypeBuilder<FileBlobBackfillFailure> entity)
    {
        entity.Property(f => f.FileId).ValueGeneratedNever();
        entity.Property(f => f.Code).IsUnicode(false);
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_FileBlobBackfillFailures_Code", "`Code` IN ('hash', 'read', 'too-large', 'type', 'error')"));
    }
}
