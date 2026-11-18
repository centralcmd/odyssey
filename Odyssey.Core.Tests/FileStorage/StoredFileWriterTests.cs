using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.FileStorage;

/// <summary>
/// Issue #448 AC 10 (deferred by #456): the writer stages a file and its stored object on the caller's context, unsaved,
/// so a save that fails after the bytes are on disk commits neither — and the abandoned-write sweep later removes the
/// reservation and its file.
/// </summary>
public sealed class StoredFileWriterTests : FileStorageTestBase
{
    private static readonly byte[] Pdf = "%PDF-1.7 a statement whose save fails"u8.ToArray();

    private readonly FailFileMetadataSave failure = new();

    protected override void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options)
    {
        base.UseDatabase(options);
        options.AddInterceptors(failure);
    }

    [Fact]
    public async Task A_save_failing_after_the_write_commits_nothing_and_the_sweep_removes_the_reservation()
    {
        var host = await NewFilesHostAsync();
        Guid fileId;

        await using (var scope = host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var writer = scope.ServiceProvider.GetRequiredService<StoredFileWriter>();
            var file = await writer.StageNewFileAsync(
                context, StoredFileArea.Finance, new MemoryStream(Pdf), "Lønnsslipp.pdf", "application/pdf", null, null, 1024, default);
            fileId = file.Id;

            failure.Armed = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            failure.Armed = false;
        }

        var reservation = await InScopeAsync(host, async sp =>
        {
            var context = sp.GetRequiredService<OdysseyContext>();
            Assert.False(await context.FileMetadata.AnyAsync(f => f.Id == fileId));
            Assert.False(await context.StoredObjects.AnyAsync(o => o.State == StoredObjectState.Committed));
            return await context.StoredObjects.AsNoTracking().SingleAsync();
        });
        Assert.Equal(StoredObjectState.Pending, reservation.State);
        var path = Path.Combine(PrimaryRoot, reservation.RelativePath);
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(path));

        // Not yet abandoned: a caller may still be about to save.
        Assert.Equal(0, await SweepAsync(host, s => s.SweepAbandonedWritesAsync(default)));
        Assert.True(File.Exists(path));

        Clock.Advance(FileStorageSweeper.AbandonedWriteAge + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await SweepAsync(host, s => s.SweepAbandonedWritesAsync(default)));

        Assert.False(File.Exists(path));
        Assert.Null(await RowAsync(host, reservation.Id));
    }

    /// <summary>Fails a save that would add a <see cref="FileMetadata"/> row, while armed — the caller's save, never the reservation's.</summary>
    private sealed class FailFileMetadataSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<FileMetadata>().Any(e => e.State == EntityState.Added))
            {
                throw new DbUpdateException("Simulated failure of the caller's save.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
