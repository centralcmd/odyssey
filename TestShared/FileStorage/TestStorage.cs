using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Testing.FileStorage;

/// <summary>
/// The test-only storage features (issue #346 Goal 12): the production catalogue is empty in v1, so these are
/// what exercise every rule of the store. Each owns one reference table on <see cref="TestStorageContext"/>,
/// keyed RESTRICT to <c>StoredObjects</c> — the consumer rule a real feature follows.
/// </summary>
public static class TestStorageFeatures
{
    /// <summary>Original names, graced, sharded by an owner id: the thumbnail spec's shape.</summary>
    public static readonly StorageFeature Files = StorageFeature.Create<Guid>(
        "TestFiles", "test-files",
        owner => [.. StorageLayout.Shard(owner), owner.ToString("N")],
        StorageReleaseMode.Graced,
        StorageFeatureReference.To<TestFileAttachment>(a => a.StoredObjectId));

    /// <summary>Opaque names and immediate release: personal data.</summary>
    public static readonly StorageFeature Secrets = StorageFeature.Create<Guid>(
        "TestSecrets", "test-secrets",
        owner => StorageLayout.Shard(owner),
        StorageReleaseMode.Immediate,
        StorageFeatureReference.To<TestSecretAttachment>(a => a.StoredObjectId),
        StorageNamingMode.Opaque);

    /// <summary>One unsharded date segment, for the reconciliation walk over a flat layout.</summary>
    public static readonly StorageFeature Dated = StorageFeature.Create<string>(
        "TestDated", "test-dated",
        day => [day],
        StorageReleaseMode.Graced,
        StorageFeatureReference.To<TestDatedAttachment>(a => a.StoredObjectId));

    public static IReadOnlyList<StorageFeature> All { get; } = [Files, Secrets, Dated];
}

public class TestFileAttachment
{
    public Guid Id { get; set; }

    public Guid? StoredObjectId { get; set; }
}

public class TestSecretAttachment
{
    public Guid Id { get; set; }

    public Guid? StoredObjectId { get; set; }
}

public class TestDatedAttachment
{
    public Guid Id { get; set; }

    public Guid? StoredObjectId { get; set; }
}

/// <summary>
/// <see cref="OdysseyContext"/> plus the test features' reference tables. It is registered AS
/// <see cref="OdysseyContext"/> in the store's test hosts, so the sweeps read the reference columns through it;
/// the reservation factory still builds the base context, which shares every table it touches.
/// </summary>
public class TestStorageContext(DbContextOptions<OdysseyContext> options) : OdysseyContext(options)
{
    public DbSet<TestFileAttachment> TestFileAttachments => Set<TestFileAttachment>();

    public DbSet<TestSecretAttachment> TestSecretAttachments => Set<TestSecretAttachment>();

    public DbSet<TestDatedAttachment> TestDatedAttachments => Set<TestDatedAttachment>();

    /// <summary>The DDL the relational tier runs after migrating, since these tables are in no migration.</summary>
    public static readonly string[] CreateTablesSql =
    [
        .. new[] { "TestFileAttachments", "TestSecretAttachments", "TestDatedAttachments" }.Select(table =>
            $"CREATE TABLE IF NOT EXISTS `{table}` (`Id` char(36) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL PRIMARY KEY, `StoredObjectId` char(36) CHARACTER SET ascii COLLATE ascii_general_ci NULL, "
            + $"CONSTRAINT `FK_{table}_StoredObjects` FOREIGN KEY (`StoredObjectId`) REFERENCES `StoredObjects` (`Id`) ON DELETE RESTRICT)"),
    ];

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<TestFileAttachment>(e => e.HasOne<StoredObject>().WithMany().HasForeignKey(a => a.StoredObjectId).OnDelete(DeleteBehavior.Restrict));
        modelBuilder.Entity<TestSecretAttachment>(e => e.HasOne<StoredObject>().WithMany().HasForeignKey(a => a.StoredObjectId).OnDelete(DeleteBehavior.Restrict));
        modelBuilder.Entity<TestDatedAttachment>(e => e.HasOne<StoredObject>().WithMany().HasForeignKey(a => a.StoredObjectId).OnDelete(DeleteBehavior.Restrict));
    }
}
