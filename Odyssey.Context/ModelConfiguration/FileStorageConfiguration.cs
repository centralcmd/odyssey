using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class StoredObjectConfiguration : IEntityTypeConfiguration<StoredObject>
{
    public void Configure(EntityTypeBuilder<StoredObject> entity)
    {
        // RESTRICT: a volume that still holds objects cannot be removed.
        entity.HasOne<FileStorageVolume>()
            .WithMany()
            .HasForeignKey(o => o.VolumeId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(o => o.State).HasConversion<byte>();

        // The path stays reserved in every state, because its file may exist until it is swept. The column
        // keeps the database's default case- and accent-insensitive collation on purpose: `Photo.JPG` and
        // `photo.jpg` collide, which keeps a volume portable to a case-insensitive filesystem.
        entity.HasIndex(o => new { o.VolumeId, o.RelativePath }).IsUnique();
        entity.HasIndex(o => new { o.State, o.CreatedAtUtc });
        entity.HasIndex(o => new { o.State, o.DeleteRequestedAtUtc });
        entity.HasIndex(o => new { o.State, o.LastVerifiedAtUtc });
        entity.HasIndex(o => new { o.VolumeId, o.IntegrityFaultAtUtc });
        // Covering, for the status endpoint's per-feature counts and byte sums.
        entity.HasIndex(o => new { o.VolumeId, o.FeatureKey, o.State, o.SizeBytes });

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_StoredObjects_SizeNonNegative", "`SizeBytes` >= 0");
            tb.HasCheckConstraint("CK_StoredObjects_StateRange", "`State` IN (0, 1, 2)");
            tb.HasCheckConstraint("CK_StoredObjects_HashUnlessPending", "`State` = 0 OR `Sha256Hash` IS NOT NULL");
            tb.HasCheckConstraint(
                "CK_StoredObjects_DeleteRequestedWhenPendingDelete", "`State` <> 2 OR `DeleteRequestedAtUtc` IS NOT NULL");
        });
    }
}

internal sealed class FileStorageOrphanSightingConfiguration : IEntityTypeConfiguration<FileStorageOrphanSighting>
{
    public void Configure(EntityTypeBuilder<FileStorageOrphanSighting> entity)
    {
        // CASCADE: a sighting is bookkeeping about the volume, and means nothing without it.
        entity.HasOne<FileStorageVolume>()
            .WithMany()
            .HasForeignKey(s => s.VolumeId)
            .OnDelete(DeleteBehavior.Cascade);

        // The same case-insensitive collation as StoredObjects, so a stray file that differs from a live row
        // only by case counts as matched and is never quarantined.
        entity.HasIndex(s => new { s.VolumeId, s.RelativePath }).IsUnique();
    }
}

internal sealed class FileStorageMaintenanceLeaseConfiguration : IEntityTypeConfiguration<FileStorageMaintenanceLease>
{
    public void Configure(EntityTypeBuilder<FileStorageMaintenanceLease> entity)
    {
        entity.Property(l => l.Id).ValueGeneratedNever();
        entity.ToTable(tb => tb.HasCheckConstraint("CK_FileStorageMaintenanceLeases_Singleton", "`Id` = 1"));

        entity.HasData(new FileStorageMaintenanceLease
        {
            Id = FileStorageMaintenanceLease.SingletonId,
            Version = new Guid("3c8f0d4e-5a8b-4f6e-9a51-2b7d1e0c9f34"),
        });
    }
}
