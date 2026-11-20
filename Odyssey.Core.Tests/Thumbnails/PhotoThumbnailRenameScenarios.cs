using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Imaging;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>
/// What a rename and a delete do to a photo's thumbnails (issue #362 §3.4, §4.1; AC 17, 18). Inherited by
/// <c>Odyssey.IntegrationTests</c>, which reruns every case on real MariaDB.
/// </summary>
public abstract class PhotoThumbnailRenameScenarios : PhotoThumbnailTestBase
{
    [SkippableFact]
    public async Task A_rename_releases_both_thumbnails_graced_and_requeues_a_done_job_behind_the_cooldown()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        await Job(host).RunOnceAsync(default);
        var objectIds = (await ThumbnailsAsync(host, photoId)).Select(t => t.StoredObjectId).ToList();
        var claimedAt = (await JobRowAsync(host, photoId)).LastClaimedAtUtc!.Value;
        Clock.Advance(TimeSpan.FromMinutes(1));

        await RenameAsync(host, photoId, "Holiday.gif");

        Assert.Empty(await ThumbnailsAsync(host, photoId));
        foreach (var id in objectIds)
        {
            var stored = await StoredAsync(host, id);
            Assert.Equal((StoredObjectState.PendingDelete, false), (stored.State, stored.DeleteImmediately));
        }

        var job = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Pending, PhotoThumbnailJobPriority.Background, 1, 0), (job.State, job.Priority, job.Attempts, job.LeaseExpiries));
        Assert.Equal(claimedAt + PhotoThumbnailTiming.RenameCooldown, job.NextAttemptAtUtc);
        Assert.Equal("Holiday.gif", await FileNameAsync(host, photoId));
    }

    [SkippableFact]
    public async Task Renaming_ten_times_in_a_minute_never_brings_the_rerun_forward()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        await Job(host).RunOnceAsync(default);
        var due = default(DateTime);
        for (var i = 0; i < 10; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(6));
            await RenameAsync(host, photoId, $"Name {i}.gif");
            var job = await JobRowAsync(host, photoId);
            due = i == 0 ? job.NextAttemptAtUtc : due;
            Assert.Equal(due, job.NextAttemptAtUtc);
            Assert.Equal(1, job.Attempts);
        }

        Assert.Null(await Job(host).ClaimAsync(default));
        Clock.Advance(due - Clock.GetUtcNow().UtcDateTime);
        Assert.NotNull(await Job(host).ClaimAsync(default));
    }

    [SkippableFact]
    public async Task Saving_the_same_name_changes_nothing()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", fileName: "Same.gif");
        await Job(host).RunOnceAsync(default);

        await RenameAsync(host, photoId, "Same.gif");

        Assert.Equal(2, (await ThumbnailsAsync(host, photoId)).Count);
        Assert.Equal(PhotoThumbnailJobState.Done, (await JobRowAsync(host, photoId)).State);
    }

    [SkippableTheory]
    [InlineData(PhotoThumbnailJobState.Unsupported)]
    [InlineData(PhotoThumbnailJobState.Failed)]
    public async Task A_finished_unsupported_or_failed_job_is_left_alone_and_never_rendered_again(PhotoThumbnailJobState state)
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        await WithContextAsync(host, async c =>
        {
            var job = await c.PhotoThumbnailJobs.SingleAsync();
            job.State = state;
            job.FailureCode = state == PhotoThumbnailJobState.Unsupported ? PhotoThumbnailFailure.Crash : PhotoThumbnailFailure.Exhausted;
            job.AttemptedRecipeVersion = PhotoThumbnailRecipe.Version;
            await c.SaveChangesAsync();
        });
        var before = await JobRowAsync(host, photoId);

        await RenameAsync(host, photoId, "Other.gif");
        await Job(host).RunOnceAsync(default);

        var after = await JobRowAsync(host, photoId);
        Assert.Equal((before.State, before.Generation, before.NextAttemptAtUtc), (after.State, after.Generation, after.NextAttemptAtUtc));
        Assert.Empty(Renderer.Calls);
    }

    [SkippableFact]
    public async Task When_the_regeneration_ends_unsupported_no_file_with_the_old_stem_remains_after_the_sweep()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", fileName: "Secret Name.gif");
        await Job(host).RunOnceAsync(default);
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, "photo-thumbnails", "400", "1", "Secret Name.webp")));

        await RenameAsync(host, photoId, "Plain.gif");
        Renderer.Behaviour = _ => ThumbnailRenderResult.Failed(PhotoThumbnailFailure.Format);
        Clock.Advance(PhotoThumbnailTiming.RenameCooldown);
        await Job(host).RunOnceAsync(default);
        Assert.Equal(PhotoThumbnailJobState.Unsupported, (await JobRowAsync(host, photoId)).State);
        // Thumbnails are graced since issue #428 §3.5: the old-stem files go once the grace has run.
        Clock.Advance(TimeSpan.FromHours(24));
        await InScopeAsync(host, sp => sp.GetRequiredService<FileStorageSweeper>().SweepDeferredDeletesAsync(24, default));

        Assert.Empty(Directory.GetFiles(Path.Combine(PrimaryRoot, "photo-thumbnails"), "Secret Name*", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public async Task Deleting_a_photo_releases_both_thumbnails_graced_in_the_same_save()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        await Job(host).RunOnceAsync(default);
        var objectIds = (await ThumbnailsAsync(host, photoId)).Select(t => t.StoredObjectId).ToList();

        await using (var scope = host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await scope.ServiceProvider.GetRequiredService<PhotoThumbnailService>().StageDeleteAsync(photoId, default);
            context.Photos.Remove(await context.Photos.SingleAsync(p => p.PhotoId == photoId));
            await context.SaveChangesAsync();
        }

        foreach (var id in objectIds)
        {
            Assert.Equal((StoredObjectState.PendingDelete, false), ((await StoredAsync(host, id)).State, (await StoredAsync(host, id)).DeleteImmediately));
        }

        Assert.Equal(0, await WithContextAsync(host, c => c.PhotoThumbnailJobs.CountAsync()));
    }

    /// <summary>
    /// A rename through the photo (issue #428 §5.4), as <c>PhotoService.Update</c> applies it: only a real change of name
    /// writes <c>Photo.FileName</c> and stages the thumbnail rule, in the update's own save.
    /// </summary>
    private static async Task RenameAsync(FileStorageTestHost host, Guid photoId, string name)
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var photo = await context.Photos.SingleAsync(p => p.PhotoId == photoId);
        if (!string.Equals(photo.FileName, name, StringComparison.Ordinal))
        {
            photo.FileName = name;
            await scope.ServiceProvider.GetRequiredService<PhotoThumbnailService>().StageRenameForPhotoAsync(photoId, default);
        }

        await context.SaveChangesAsync();
    }

    private static Task<string> FileNameAsync(FileStorageTestHost host, Guid photoId) =>
        WithContextAsync(host, c => c.Photos.Where(p => p.PhotoId == photoId).Select(p => p.FileName).SingleAsync());
}
