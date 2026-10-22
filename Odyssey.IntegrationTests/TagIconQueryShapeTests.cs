using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The query-shape guard for the journal, task and photo display icon (issue #345 §10, AC 17).
///
/// <para>
/// The display icon is only affordable because the tag name and icon ride on the sub-select that already
/// produced <c>TagIds</c>; a per-record tag fetch would not fail, it would just cost one query per row. So
/// the cost is asserted as a COMMAND COUNT on real MariaDB — the EF InMemory provider never invokes a
/// <see cref="DbCommandInterceptor"/>, so a fast-tier version would pass whatever the query did. The
/// expected counts are the ones each read issued before the change; a single-record read may add one,
/// the batched tag lookup.
/// </para>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class TagIconQueryShapeTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_tag_icon_query_shape";
    private const int RecordCount = 20;

    [SkippableFact]
    public async Task List_and_detail_reads_add_no_per_record_tag_query()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            var seeded = await SeedAsync();

            var entries = await CountAsync(context => Entries(context).ListAsync(new JournalEntriesQueryParams { Limit = RecordCount }));
            Assert.Equal(RecordCount, entries.Result.Items.Count);
            Assert.All(entries.Result.Items, entry => Assert.Equal("beach_access", entry.DisplayIcon));
            Assert.Equal(ListEntryCommands, entries.Commands);

            var tasks = await CountAsync(context => Tasks(context).ListAsync(new JournalTasksQueryParams { Limit = RecordCount }));
            Assert.Equal(RecordCount, tasks.Result.Items.Count);
            Assert.All(tasks.Result.Items, task => Assert.Equal("beach_access", task.DisplayIcon));
            Assert.Equal(ListTaskCommands, tasks.Commands);

            var photos = await CountAsync(context => Photos(context).ListAsync(new PhotosQueryParams { Limit = RecordCount }));
            Assert.Equal(RecordCount, photos.Result.Items.Count);
            Assert.All(photos.Result.Items, photo => Assert.Equal("beach_access", photo.DisplayIcon));
            Assert.Equal(ListPhotoCommands, photos.Commands);

            var entry = await CountAsync(context => Entries(context).Get(seeded.EntryId));
            Assert.Equal("beach_access", entry.Result!.DisplayIcon);
            Assert.Equal([seeded.Art, seeded.Trip, seeded.Zoo], entry.Result.TagIds);
            Assert.InRange(entry.Commands, GetEntryCommandsBefore, GetEntryCommandsBefore + 1);

            var task = await CountAsync(context => Tasks(context).Get(seeded.TaskId));
            Assert.Equal("beach_access", task.Result!.DisplayIcon);
            Assert.InRange(task.Commands, GetTaskCommandsBefore, GetTaskCommandsBefore + 1);

            var photo = await CountAsync(context => Photos(context).Get(seeded.PhotoId));
            Assert.Equal("beach_access", photo.Result!.DisplayIcon);
            Assert.InRange(photo.Commands, GetPhotoCommandsBefore, GetPhotoCommandsBefore + 1);
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// Issue #345 AC 17, the write half: a create or update response carries the display icon through
    /// one batched tag lookup, so it may issue at most one command more than it did before — never one
    /// per tag.
    /// </summary>
    [SkippableFact]
    public async Task Create_and_update_responses_add_at_most_one_command()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            var seeded = await SeedAsync();
            await using (var users = NewContext(null))
            {
                await AttributionUsers.EnsureAsync(users, Author);
            }

            var journalTagIds = await TagIdsAsync(context => context.JournalTags.Select(t => t.JournalTagId));
            var taskTagIds = await TagIdsAsync(context => context.JournalTaskTags.Select(t => t.JournalTaskTagId));

            var entry = await CountAsync(context => Entries(context).Create(new NewJournalEntry
            {
                Title = "New", Content = "Dear journal.", EntryDate = DateTime.UtcNow, TagIds = journalTagIds,
            }, Author));
            Assert.Equal("beach_access", entry.Result.DisplayIcon);
            Assert.Equal([seeded.Art, seeded.Trip, seeded.Zoo], entry.Result.TagIds);
            Assert.InRange(entry.Commands, CreateEntryCommandsBefore, CreateEntryCommandsBefore + 1);

            var updatedEntry = await CountAsync(context => Entries(context).Update(entry.Result.JournalEntryId, new UpdateJournalEntry
            {
                Title = "New", Content = "Dear journal.", EntryDate = DateTime.UtcNow, TagIds = [seeded.Zoo],
            }, Author));
            Assert.Equal("pets", updatedEntry.Result!.DisplayIcon);
            Assert.InRange(updatedEntry.Commands, UpdateEntryCommandsBefore, UpdateEntryCommandsBefore + 1);

            var task = await CountAsync(context => Tasks(context).Create(new NewJournalTask { Title = "New", TagIds = taskTagIds }, Author));
            Assert.Equal("beach_access", task.Result.DisplayIcon);
            Assert.InRange(task.Commands, CreateTaskCommandsBefore, CreateTaskCommandsBefore + 1);

            var updatedTask = await CountAsync(context => Tasks(context).Update(task.Result.JournalTaskId, new UpdateJournalTask
            {
                Title = "New", TagIds = [taskTagIds[0]],
            }, Author));
            Assert.NotNull(updatedTask.Result);
            Assert.InRange(updatedTask.Commands, UpdateTaskCommandsBefore, UpdateTaskCommandsBefore + 1);
        }
        finally
        {
            await DropAsync();
        }
    }

    private const string Author = "tag-icon-shape-author";

    private async Task<Guid[]> TagIdsAsync(Func<OdysseyContext, IQueryable<Guid>> query)
    {
        await using var context = NewContext(null);
        return await query(context).ToArrayAsync();
    }

    // Measured against the services before issue #345 (the write-path rows by the same method).
    private const int CreateEntryCommandsBefore = 3;
    private const int UpdateEntryCommandsBefore = 3;
    private const int CreateTaskCommandsBefore = 4;
    private const int UpdateTaskCommandsBefore = 3;

    // Measured against the services before issue #345. Each list is ToPagedResultAsync's count plus the
    // window query (the entry list's photo-link lookup is skipped when no entry carries a photo); each
    // detail read was the one Include query.
    private const int ListEntryCommands = 2;
    private const int ListTaskCommands = 2;
    private const int ListPhotoCommands = 2;
    private const int GetEntryCommandsBefore = 1;
    private const int GetTaskCommandsBefore = 1;
    private const int GetPhotoCommandsBefore = 1;

    private sealed record Seeded(Guid EntryId, Guid TaskId, Guid PhotoId, Guid Art, Guid Trip, Guid Zoo);

    private async Task<Seeded> SeedAsync()
    {
        await using var context = NewContext(null);
        var now = DateTime.UtcNow;

        var journal = new[] { ("art", (string?)null), ("Trip", "beach_access"), ("zoo", "pets") }
            .Select(spec => new JournalTag { JournalTagId = Guid.NewGuid(), Name = spec.Item1, Icon = spec.Item2 }).ToList();
        var task = new[] { ("art", (string?)null), ("Trip", "beach_access"), ("zoo", "pets") }
            .Select(spec => new JournalTaskTag { JournalTaskTagId = Guid.NewGuid(), Name = spec.Item1, Icon = spec.Item2 }).ToList();
        var photo = new[] { ("art", (string?)null), ("Trip", "beach_access"), ("zoo", "pets") }
            .Select(spec => new PhotoTag { PhotoTagId = Guid.NewGuid(), Name = spec.Item1, Icon = spec.Item2 }).ToList();
        context.JournalTags.AddRange(journal);
        context.JournalTaskTags.AddRange(task);
        context.PhotoTags.AddRange(photo);

        Guid entryId = default, taskId = default, photoId = default;
        for (var i = 0; i < RecordCount; i++)
        {
            var entry = new JournalEntry
            {
                JournalEntryId = Guid.NewGuid(), ExternalUid = Guid.NewGuid().ToString(), Title = $"Entry {i}",
                Content = "Dear journal.", EntryDate = now.AddDays(-i), CreatedAt = now, UpdatedAt = now,
            };
            foreach (var tag in journal.AsEnumerable().Reverse())
            {
                entry.EntryTags.Add(new JournalEntryTag { JournalTagId = tag.JournalTagId });
            }

            var item = new JournalTask
            {
                JournalTaskId = Guid.NewGuid(), ExternalUid = Guid.NewGuid().ToString(), Title = $"Task {i}",
                Position = i, CreatedAt = now, UpdatedAt = now,
            };
            foreach (var tag in task.AsEnumerable().Reverse())
            {
                item.ItemTags.Add(new JournalTaskTagLink { JournalTaskTagId = tag.JournalTaskTagId });
            }

            var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
            var file = new FileMetadata
            {
                Id = Guid.NewGuid(), FileName = $"p{i}.jpg", ContentType = "image/jpeg", SizeBytes = 3,
                Sha256Hash = Guid.NewGuid().ToString("N"), UploadedAtUtc = now, FileBlobId = blob.Id, FileBlob = blob,
            };
            var picture = new Photo { PhotoId = Guid.NewGuid(), FileId = file.Id, CreatedAt = now, UpdatedAt = now };
            foreach (var tag in photo.AsEnumerable().Reverse())
            {
                picture.Tags.Add(new PhotoTagLink { PhotoTagId = tag.PhotoTagId });
            }

            context.JournalEntries.Add(entry);
            context.JournalTasks.Add(item);
            context.FileBlob.Add(blob);
            context.FileMetadata.Add(file);
            context.Photos.Add(picture);
            (entryId, taskId, photoId) = (entry.JournalEntryId, item.JournalTaskId, picture.PhotoId);
        }

        await context.SaveChangesAsync();
        return new Seeded(entryId, taskId, photoId, journal[0].JournalTagId, journal[1].JournalTagId, journal[2].JournalTagId);
    }

    private async Task<(T Result, int Commands)> CountAsync<T>(Func<OdysseyContext, Task<T>> read)
    {
        var counter = new CommandCounter();
        await using var context = NewContext(counter);
        var result = await read(context);
        return (result, counter.Count);
    }

    private static JournalEntryService Entries(OdysseyContext context)
    {
        var files = new FileLookup(context);
        return new JournalEntryService(
            context, new ContactLookup(context), files, new PhotoLookup(context, files, null!), new StubJournalLimitsLookup());
    }

    private static JournalTaskService Tasks(OdysseyContext context) =>
        new(context, new FileLookup(context), new StubJournalLimitsLookup());

    private static PhotoService Photos(OdysseyContext context) =>
        new(context, new ContactLookup(context), new FileLookup(context), new FileReferenceGuard(context), null!,
            new StubJournalLimitsLookup(), NullLogger<PhotoService>.Instance);

    private OdysseyContext NewContext(CommandCounter? counter)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString));
        if (counter is not null)
        {
            builder.AddInterceptors(counter);
        }

        return new OdysseyContext(builder.Options);
    }

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using (var server = ServerContext())
        {
            await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        await using var context = NewContext(null);
        await MigrationRunner.MigrateAsync(context, CancellationToken.None);
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int count;

        public int Count => count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
    }
}
