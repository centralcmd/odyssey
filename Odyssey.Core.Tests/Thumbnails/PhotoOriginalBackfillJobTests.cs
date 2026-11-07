using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal.Originals;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>
/// Issue #428 §3.4: the worker job that moves a legacy photo's original out of <c>FileBlob</c> onto the photo store. It
/// verifies before it changes the database, keeps a file something else still uses, parks a photo it cannot move for a
/// deterministic reason and never retries it, and keeps a name the user gave the photo while it was legacy.
/// </summary>
public sealed class PhotoOriginalBackfillJobTests : PhotoThumbnailTestBase
{
    private static PhotoOriginalBackfillJob Backfill(FileStorageTestHost host) =>
        ActivatorUtilities.CreateInstance<PhotoOriginalBackfillJob>(host.Services);

    private async Task<(Guid PhotoId, Guid FileId)> AddLegacyAsync(FileStorageTestHost host, byte[] bytes, string fileName = "Fjord.jpg")
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var (photo, file) = PhotoTestData.StageLegacyPhoto(context, bytes, fileName, createdAt: new DateTime(2019, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        await context.SaveChangesAsync();
        return (photo.PhotoId, file.Id);
    }

    [Fact]
    public async Task An_unshared_legacy_photo_moves_and_its_file_is_removed()
    {
        var host = await NewThumbnailHostAsync();
        var bytes = PhotoTestData.Jpeg(1);
        var (photoId, fileId) = await AddLegacyAsync(host, bytes);

        await Backfill(host).RunOnceAsync(default);

        var photo = await WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));
        Assert.Null(photo.FileId);
        Assert.Equal(fileId, photo.LegacyFileId);
        Assert.Equal(("Fjord.jpg", "image/jpeg", bytes.LongLength, PhotoTestData.Sha256(bytes)),
            (photo.FileName, photo.ContentType, photo.SizeBytes, photo.Sha256Hash));
        var stored = await StoredAsync(host, photo.StoredObjectId!.Value);
        Assert.Equal((StoredObjectState.Committed, PhotoOriginalStorage.FeatureKey), (stored.State, stored.FeatureKey));
        // Upload year + bucket (§3.2): a photo created in 2019 lands under 2019, whatever year the move runs in.
        Assert.StartsWith("photo-originals/2019/", stored.RelativePath, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(Root("primary"), stored.RelativePath)));
        Assert.False(await WithContextAsync(host, c => c.FileMetadata.AnyAsync(f => f.Id == fileId)));
        Assert.False(await WithContextAsync(host, c => c.FileBlob.AnyAsync()));
    }

    [Fact]
    public async Task A_file_attached_elsewhere_is_copied_and_kept()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(2));
        await WithContextAsync(host, async c =>
        {
            c.JournalEntryAttachments.Add(new JournalEntryAttachment { JournalEntryId = Guid.NewGuid(), FileId = fileId, CreatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        var photo = await WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));
        Assert.NotNull(photo.StoredObjectId);
        Assert.Null(photo.FileId);
        Assert.True(await WithContextAsync(host, c => c.FileMetadata.AnyAsync(f => f.Id == fileId)));
        var lease = await WithContextAsync(host, c => c.PhotoOriginalBackfillLeases.AsNoTracking().SingleAsync());
        Assert.Equal(1, lease.CopiedSharedCount);
    }

    [Fact]
    public async Task A_rename_made_while_legacy_keeps_its_name()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(3), "IMG_0001.jpg");
        await WithContextAsync(host, async c =>
        {
            (await c.Photos.SingleAsync(p => p.PhotoId == photoId)).FileName = "Summit.jpg";
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        var photo = await WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));
        Assert.NotNull(photo.StoredObjectId);
        Assert.Equal("Summit.jpg", photo.FileName);
    }

    [Fact]
    public async Task A_hash_mismatch_parks_the_photo_writes_nothing_and_is_never_retried()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(4));
        await WithContextAsync(host, async c =>
        {
            (await c.FileMetadata.SingleAsync(f => f.Id == fileId)).Sha256Hash = new string('0', 64);
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        var failure = await WithContextAsync(host, c => c.PhotoOriginalBackfillFailures.AsNoTracking().SingleAsync());
        Assert.Equal((photoId, PhotoOriginalBackfillFailureCodes.Hash), (failure.PhotoId, failure.Code));
        var photo = await WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));
        Assert.Equal((fileId, (Guid?)null), (photo.FileId!.Value, photo.StoredObjectId));
        Assert.False(await WithContextAsync(host, c => c.StoredObjects.AnyAsync(o => o.FeatureKey == PhotoOriginalStorage.FeatureKey)));
        Assert.Empty(await Backfill(host).NextCandidatesAsync(default));
    }

    [Fact]
    public async Task A_type_outside_the_photo_allow_list_parks_as_unreadable()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(5));
        await WithContextAsync(host, async c =>
        {
            (await c.FileMetadata.SingleAsync(f => f.Id == fileId)).ContentType = "application/pdf";
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        Assert.Equal(PhotoOriginalBackfillFailureCodes.Read,
            (await WithContextAsync(host, c => c.PhotoOriginalBackfillFailures.AsNoTracking().SingleAsync(f => f.PhotoId == photoId))).Code);
    }

    [Fact]
    public async Task A_second_holder_cannot_claim_a_live_lease()
    {
        var host = await NewThumbnailHostAsync();
        var job = Backfill(host);

        Assert.True(await job.TryClaimAsync(Guid.NewGuid(), default));
        Assert.False(await job.TryClaimAsync(Guid.NewGuid(), default));

        Clock.Advance(PhotoOriginalBackfillJob.LeaseDuration + TimeSpan.FromSeconds(1));
        Assert.True(await job.TryClaimAsync(Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(9, 60)]
    public void Environmental_failures_back_off_1_5_15_then_60_minutes(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), PhotoOriginalBackfillJob.Backoff(failures));

    /// <summary>
    /// §3.4.1: the counter is model-derived, so the columns it queries are every key to <c>FileMetadata</c> except the
    /// photo's own. A known set is pinned too, so a regression in the discovery itself (rather than a new table) fails.
    /// </summary>
    [Fact]
    public void The_reference_counter_queries_every_key_to_file_metadata_but_the_photos_own()
    {
        using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase("ref-counter").Options);
        var references = FileReferenceCounter.Discover(context);
        var keys = context.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(FileMetadata))
            .Select(fk => (fk.DeclaringEntityType.ClrType, fk.Properties.Single().Name))
            .ToHashSet();

        Assert.DoesNotContain(references, r => r.EntityType == typeof(Photo) && r.PropertyName == nameof(Photo.FileId));
        Assert.Equal(keys.Count - 1, references.Count);
        Assert.All(references, r => Assert.Contains((r.EntityType, r.PropertyName), keys));
        Assert.Contains(new FileReference(typeof(JournalEntryAttachment), nameof(JournalEntryAttachment.FileId)), references);
        Assert.Contains(new FileReference(typeof(Contact), nameof(Contact.AvatarFileId)), references);
    }

    /// <summary>
    /// §3.5: every production storage feature is <c>Graced</c>, so a release always passes through the one deletion
    /// delay an administrator sets. A new feature declared <c>Immediate</c> fails here until that is a decision.
    /// </summary>
    [Fact]
    public void Every_production_storage_feature_is_graced()
    {
        var features = typeof(PhotoOriginalStorage).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(f => f.FieldType == typeof(StorageFeature))
            .Select(f => (StorageFeature)f.GetValue(null)!)
            .ToList();

        Assert.Equal(
            [PhotoOriginalStorage.FeatureKey, Journal.Thumbnails.PhotoThumbnailStorage.FeatureKey],
            features.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.All(features, f => Assert.Equal(StorageReleaseMode.Graced, f.ReleaseMode));
    }
}
