using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Originals;
using Odyssey.Core.Locations;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The calendar import's two-phase attachment pipeline against real MariaDB and a real volume (issue #521 v4 §1, v5,
/// implementation notes 1–5; PR #529 review A1, T1, T3, T4): the staging ceiling inside a component and between
/// batches, a disk refusal after a committed batch, a batch that fails outright, the reserve/attach split itself, an
/// expired reservation, the in-lock re-check, the interactive link write's retries, and an import racing edits.
/// </summary>
[Collection(MariaDbSharedDatabaseCollection.Name)]
public class CalendarAttachmentsRelationalTests(MariaDbFixture fixture)
{
    private const string User = "user";
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ A1, AC 21: the staging ceiling

    [SkippableFact]
    public async Task Once_a_component_reaches_the_staging_ceiling_its_further_new_content_is_skipped_and_counted()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var scope = host.Host.Scope();
        var calendarId = await CalendarAsync(scope);
        var service = Service(scope);
        service.BatchReservedBytesCeiling = 100;
        var files = Enumerable.Range(0, 3).Select(i => Bytes(60, i)).ToList();

        var result = await ImportAsync(service, calendarId, Vevent("ceiling", [.. files.Select(f => Inline(f, "a.txt", "text/plain", ""))]));

        // 0 → 60 → 120: the second crosses the ceiling (the "plus one attachment"), the third is skipped.
        Assert.Equal((2, 1), (result.StoredFileCount, result.SkippedAttachmentCount));
        Assert.Equal(CalendarAttachmentImporter.BatchBudgetReason, Assert.Single(result.Skipped).Reason);
        Assert.Empty(Assert.Single(result.Skipped).SampleTitles);
    }

    [SkippableFact]
    public async Task The_staging_ceiling_closes_a_batch_early_between_components()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var scope = host.Host.Scope();
        var calendarId = await CalendarAsync(scope);
        var service = Service(scope);
        service.BatchReservedBytesCeiling = 100;

        var result = await ImportAsync(service, calendarId,
            [.. Enumerable.Range(0, 3).Select(i => Vevent($"split-{i}", Inline(Bytes(60, i), "a.txt", "text/plain", "")))]);

        Assert.Equal((3, 3), (result.ImportedCount, result.StoredFileCount));
        Assert.Equal(2, service.LastImportCommittedBatches);
    }

    [SkippableFact]
    public async Task Too_little_temporary_disk_after_a_committed_batch_is_a_partial_507()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var scope = host.Host.Scope();
        var calendarId = await CalendarAsync(scope);
        var service = Service(scope);
        service.BatchReservedBytesCeiling = 1; // every component is a batch of its own
        var checks = 0;
        service.TempFreeSpace = _ => ++checks == 1 ? long.MaxValue : 0;

        var refused = await Assert.ThrowsAsync<CalendarIcsImportPartiallyAppliedException>(() => ImportAsync(service, calendarId,
            [.. Enumerable.Range(0, 3).Select(i => Vevent($"disk-{i}", Inline(Bytes(60, i), "a.txt", "text/plain", "")))]));

        Assert.Equal((507, 1), (refused.StatusCode, refused.CommittedBatches));
        Assert.IsType<FileStorageFullException>(refused.InnerException);
    }

    // ------------------------------------------------------------------ T1, AC 22: a batch that fails outright

    [SkippableFact]
    public async Task A_batch_that_fails_outright_leaves_only_pending_reservations_and_decoded_each_attachment_once()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var deadlock = new InjectedError("INSERT INTO `CalendarEvents`", MySqlErrorCode.LockDeadlock, "Deadlock (injected)", always: true);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString, retryOnFailure: true, interceptors: deadlock);
        await using var scope = host.Host.Scope();
        var calendarId = await CalendarAsync(scope);
        var service = Service(scope);
        var file = Bytes(80, 7);
        var photo = PhotoTestData.Jpeg(PhotoTestData.NextSeed());

        await Assert.ThrowsAsync<CalendarSeriesConflictException>(() => ImportAsync(service, calendarId, Vevent("outright",
            Inline(file, "a.txt", "text/plain", ""), Inline(photo, "p.jpg", "image/jpeg", ";X-ODYSSEY-KIND=PHOTO"))));

        Assert.Equal(2, service.AttachmentsDecoded);
        await using var check = Plain();
        var hashes = new[] { PhotoTestData.Sha256(file), PhotoTestData.Sha256(photo) };
        // The host starts with an empty store, so every row is this import's: its two reservations, never attached, and
        // carrying no hash until they would have been. The abandoned-write sweep removes them.
        var objects = await check.StoredObjects.AsNoTracking().ToListAsync();
        Assert.Equal(2, objects.Count);
        Assert.All(objects, o => Assert.Equal(StoredObjectState.Pending, o.State));
        Assert.False(await check.FileMetadata.AnyAsync(f => hashes.Contains(f.Sha256Hash)));
        Assert.False(await check.Photos.AnyAsync(p => hashes.Contains(p.Sha256Hash!)));
    }

    // ------------------------------------------------------------------ T3: the reserve/attach split

    [SkippableFact]
    public async Task A_prepared_file_is_attached_as_a_fresh_pending_row_per_attempt_and_an_abandoned_one_leaves_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var scope = host.Host.Scope();
        var writer = scope.ServiceProvider.GetRequiredService<StoredFileWriter>();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var bytes = Bytes(40, 1);
        var prepared = await writer.PrepareNewFileAsync(StoredFileArea.Journal, new MemoryStream(bytes), "a.txt", "text/plain", 1024, default);

        await using (var check = Plain())
        {
            Assert.Equal(StoredObjectState.Pending, (await check.StoredObjects.SingleAsync(o => o.Id == prepared.Reservation.Id)).State);
        }

        // Attempt 1 attaches and rolls back (here: the tracker is cleared unsaved); attempt 2 attaches a NEW row and saves.
        writer.AttachPrepared(context, prepared, User);
        context.ChangeTracker.Clear();
        var file = writer.AttachPrepared(context, prepared, User);
        await context.SaveChangesAsync();

        await using (var check = Plain())
        {
            Assert.Equal(StoredObjectState.Committed, (await check.StoredObjects.SingleAsync(o => o.Id == prepared.Reservation.Id)).State);
            Assert.Equal(prepared.Reservation.Id, (await check.FileMetadata.SingleAsync(f => f.Id == file.Id)).JournalStoredObjectId);
        }

        var abandoned = await writer.PrepareNewFileAsync(StoredFileArea.Journal, new MemoryStream(Bytes(40, 2)), "b.txt", "text/plain", 1024, default);
        var path = Path.Combine(host.VolumeRoot, abandoned.Reservation.RelativePath);
        Assert.True(File.Exists(path));
        await writer.AbandonPreparedAsync(abandoned, default);

        Assert.False(File.Exists(path));
        await using var after = Plain();
        Assert.False(await after.StoredObjects.AnyAsync(o => o.Id == abandoned.Reservation.Id));
    }

    [SkippableFact]
    public async Task A_prepared_photo_is_attached_with_its_thumbnail_job_in_the_callers_save()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var scope = host.Host.Scope();
        var photos = scope.ServiceProvider.GetRequiredService<PhotoService>();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var bytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var upload = new FileUpload("p.jpg", "image/jpeg", bytes.LongLength, () => new MemoryStream(bytes, writable: false));

        var prepared = await photos.PrepareUploadAsync(upload, "Fjord", null);
        await photos.AttachPreparedAsync(prepared, User);
        context.ChangeTracker.Clear();
        var photo = await photos.AttachPreparedAsync(prepared, User);
        await context.SaveChangesAsync();

        await using var check = Plain();
        var stored = await check.Photos.SingleAsync(p => p.PhotoId == photo.PhotoId);
        Assert.Equal(("Fjord", PhotoTestData.Sha256(bytes)), (stored.Title, stored.Sha256Hash));
        Assert.Equal(StoredObjectState.Committed, (await check.StoredObjects.SingleAsync(o => o.Id == stored.StoredObjectId)).State);
        Assert.True(await check.PhotoThumbnailJobs.AnyAsync(j => j.PhotoId == photo.PhotoId));
    }

    [SkippableFact]
    public async Task A_reservation_the_sweep_removed_fails_the_batch_with_a_503_never_the_busy_409()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var scope = host.Host.Scope();
        var calendarId = await CalendarAsync(scope);
        var service = Service(scope);
        var bytes = Bytes(50, 3);
        // The host starts with an empty store, so the only Pending row is this import's reservation (a reservation carries
        // no hash until it is attached). The sweep removes it as it would one older than an hour.
        service.BeforeBatchApply = async () =>
        {
            await using var sweep = Plain();
            Assert.Equal(1, await sweep.StoredObjects.CountAsync(o => o.State == StoredObjectState.Pending));
            await sweep.StoredObjects.Where(o => o.State == StoredObjectState.Pending).ExecuteDeleteAsync();
        };

        var refused = await Assert.ThrowsAsync<FileStorageUnavailableException>(() =>
            ImportAsync(service, calendarId, Vevent("expired", Inline(bytes, "a.txt", "text/plain", ""))));

        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(CalendarAttachmentImporter.ExpiredReservationMessage, refused.Message);
    }

    [SkippableFact]
    public async Task Content_stored_after_phase_a_is_linked_under_the_lock_and_the_reservation_released()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString);
        await using var scope = host.Host.Scope();
        var calendarId = await CalendarAsync(scope);
        var service = Service(scope);
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var fileBytes = Bytes(70, 9);
        Guid? uploadedPhoto = null;
        Guid? storedFile = null;
        service.BeforeBatchApply = async () =>
        {
            uploadedPhoto = (await host.UploadAsync(photoBytes)).PhotoId;
            await using var other = host.Host.Scope();
            var otherContext = other.ServiceProvider.GetRequiredService<OdysseyContext>();
            storedFile = (await other.ServiceProvider.GetRequiredService<StoredFileWriter>().StageNewFileAsync(
                otherContext, StoredFileArea.Journal, new MemoryStream(fileBytes), "raced.txt", "text/plain", User, null, 1024, default)).Id;
            await otherContext.SaveChangesAsync();
        };

        var result = await ImportAsync(service, calendarId, Vevent("raced",
            Inline(fileBytes, "a.txt", "text/plain", ""), Inline(photoBytes, "p.jpg", "image/jpeg", ";X-ODYSSEY-KIND=PHOTO")));

        Assert.Equal((0, 0, 1, 1), (result.StoredFileCount, result.StoredPhotoCount, result.ReusedFileCount, result.ReusedPhotoCount));
        await using var check = Plain();
        var eventId = (await check.CalendarEvents.SingleAsync(e => e.CalendarId == calendarId)).CalendarEventId;
        Assert.Equal(storedFile, (await check.CalendarEventAttachments.SingleAsync(a => a.CalendarEventId == eventId)).FileId);
        Assert.Equal(uploadedPhoto, (await check.CalendarEventPhotos.SingleAsync(p => p.CalendarEventId == eventId)).PhotoId);
        var hashes = new[] { PhotoTestData.Sha256(fileBytes), PhotoTestData.Sha256(photoBytes) };
        var objects = await check.StoredObjects.AsNoTracking().Where(o => hashes.Contains(o.Sha256Hash!)).ToListAsync();
        Assert.Equal(2, objects.Count); // the racing writers' two
        Assert.All(objects, o => Assert.Equal(StoredObjectState.Committed, o.State));
        Assert.False(await check.StoredObjects.AnyAsync(o => o.State == StoredObjectState.Pending)); // both reservations abandoned
    }

    // ------------------------------------------------------------------ T1, AC 22: the interactive link write

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_interactive_link_write_retries_a_deadlock_and_reports_links_busy_when_it_keeps_losing(bool always)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var deadlock = new InjectedError("INSERT INTO `CalendarEventAttachments`", MySqlErrorCode.LockDeadlock, "Deadlock (injected)", always);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString, retryOnFailure: true, interceptors: deadlock);
        var (calendarId, eventId, fileId) = await StandaloneWithFileAsync(host);

        await using var scope = host.Host.Scope();
        var events = Events(scope);
        var write = () => events.Update(eventId, Event(calendarId) with { AttachmentFileIds = [fileId] }, User,
            claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted);

        if (always)
        {
            var busy = await Assert.ThrowsAsync<CalendarAttachmentsException>(write);
            Assert.Equal(CalendarAttachmentsException.LinksBusyCode, busy.Code);
            Assert.True(deadlock.Count >= 3); // the interactive strategy's two retries
        }
        else
        {
            Assert.Equal(1, (await write())!.AttachmentCount);
        }

        await using var check = Plain();
        Assert.Equal(always ? 0 : 1, await check.CalendarEventAttachments.CountAsync(a => a.CalendarEventId == eventId));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_racing_identical_link_add_is_retried_once_then_reported_busy(bool always)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var race = new InjectedError("INSERT INTO `CalendarEventAttachments`", MySqlErrorCode.DuplicateKeyEntry,
            "Duplicate entry 'x' for key 'IX_CalendarEventAttachments_CalendarEventId_FileId'", always);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString, retryOnFailure: true, interceptors: race);
        var (calendarId, eventId, fileId) = await StandaloneWithFileAsync(host);

        await using var scope = host.Host.Scope();
        var write = () => Events(scope).Update(eventId, Event(calendarId) with { AttachmentFileIds = [fileId] }, User,
            claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted);

        if (always)
        {
            var busy = await Assert.ThrowsAsync<CalendarAttachmentsException>(write);
            Assert.Equal((CalendarAttachmentsException.LinksBusyCode, false, 2), (busy.Code, busy.IsLinkRace, race.Count));
        }
        else
        {
            Assert.Equal(1, (await write())!.AttachmentCount);
            Assert.Equal(1, race.Count);
        }
    }

    [SkippableFact]
    public async Task A_file_removed_between_the_check_and_the_save_is_a_422_on_the_files_array()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var gone = new InjectedError("INSERT INTO `CalendarEventAttachments`", MySqlErrorCode.NoReferencedRow2,
            "Cannot add or update a child row: a foreign key constraint fails (`odyssey`.`CalendarEventAttachments`, CONSTRAINT "
            + "`FK_CalendarEventAttachments_FileMetadata_FileId` FOREIGN KEY (`FileId`) REFERENCES `FileMetadata` (`Id`))", always: true);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString, interceptors: gone);
        var (calendarId, eventId, fileId) = await StandaloneWithFileAsync(host);

        await using var scope = host.Host.Scope();
        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(() => Events(scope).Update(eventId,
            Event(calendarId) with { AttachmentFileIds = [fileId] }, User, claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted));

        Assert.True(refused.Errors!.ContainsKey(CalendarLinkService.FilesField));
    }

    // ------------------------------------------------------------------ Import against interactive edits (v4 §3)

    [SkippableFact]
    public async Task An_import_and_link_edits_over_the_same_events_never_deadlock_into_a_500_or_a_duplicate_link()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString, retryOnFailure: true);
        await using var setup = host.Host.Scope();
        var calendarId = await CalendarAsync(setup);
        var context = setup.ServiceProvider.GetRequiredService<OdysseyContext>();
        var writer = setup.ServiceProvider.GetRequiredService<StoredFileWriter>();
        var shared = Bytes(30, 11);
        var file = await writer.StageNewFileAsync(context, StoredFileArea.Journal, new MemoryStream(shared), "s.txt", "text/plain", User, null, 1024, default);
        var events = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            events.Add((await Events(setup).Create(Event(calendarId) with { Title = $"E{i}" }, User,
                claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted)).CalendarEventId);
        }

        await context.SaveChangesAsync();
        var reference = $"ATTACH;VALUE=URI;X-ODYSSEY-SHA256={file.Sha256Hash};SIZE={file.SizeBytes}:odyssey-file:{file.Id:D}";
        var ics = events.Select(id => Vevent($"{id}@odyssey.local", reference)).ToArray();
        using var barrier = new Barrier(2);

        async Task<Exception?> Run(Func<Task> work)
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            try
            {
                await work();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var importTask = Task.Run(() => Run(async () =>
        {
            await using var scope = host.Host.Scope();
            for (var round = 0; round < 3; round++)
            {
                await ImportAsync(Service(scope), calendarId, ics);
            }
        }));
        var editTask = Task.Run(() => Run(async () =>
        {
            await using var scope = host.Host.Scope();
            foreach (var id in Enumerable.Reverse(events).Concat(events))
            {
                try
                {
                    await Events(scope).Update(id, Event(calendarId) with { AttachmentFileIds = [file.Id] }, User,
                        claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted);
                }
                catch (CalendarAttachmentsException busy) when (busy.Code == CalendarAttachmentsException.LinksBusyCode)
                {
                }
            }
        }));

        var failures = await Task.WhenAll(importTask, editTask);

        // A busy series or link write is a 409, which the edit loop tolerates and the import surfaces as such; anything
        // else (a 500 from an unmapped deadlock, a 1062 from a duplicate link) fails the test.
        Assert.All(failures, f => Assert.True(f is null or CalendarSeriesConflictException or CalendarAttachmentsException, f?.ToString()));
        await using var check = Plain();
        var links = await check.CalendarEventAttachments.AsNoTracking().Where(a => events.Contains(a.CalendarEventId)).ToListAsync();
        Assert.Equal(links.Count, links.Select(l => l.CalendarEventId).Distinct().Count());
    }

    // ------------------------------------------------------------------ helpers

    private OdysseyContext Plain() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString)).Options);

    private static byte[] Bytes(int size, int seed)
    {
        var text = $"content {seed} {Guid.NewGuid():N} ";
        return Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(text, (size / text.Length) + 1))[..size]);
    }

    private static NewCalendarEvent Event(Guid calendarId) => new()
    {
        CalendarId = calendarId, Title = "Meeting",
        StartDateTime = new DateTime(2030, 3, 2, 10, 0, 0), EndDateTime = new DateTime(2030, 3, 2, 11, 0, 0),
    };

    private static CalendarEventService Events(AsyncServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<OdysseyContext>(), new StubJournalLimitsLookup(), new FakeTimeProvider(Now));

    private static async Task<Guid> CalendarAsync(AsyncServiceScope scope)
    {
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var calendar = new Odyssey.Context.Calendar { Name = $"Pipeline {Guid.NewGuid():N}", CreatedByUserId = User };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return calendar.CalendarId;
    }

    private static async Task<(Guid CalendarId, Guid EventId, Guid FileId)> StandaloneWithFileAsync(PhotoStoreHost host)
    {
        await using var scope = host.Host.Scope();
        var calendarId = await CalendarAsync(scope);
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var file = await scope.ServiceProvider.GetRequiredService<StoredFileWriter>().StageNewFileAsync(
            context, StoredFileArea.Journal, new MemoryStream(Bytes(30, 5)), "f.txt", "text/plain", User, null, 1024, default);
        await context.SaveChangesAsync();
        var created = await Events(scope).Create(Event(calendarId), User, claims: LocationClaims.Trusted);
        return (calendarId, created.CalendarEventId, file.Id);
    }

    private static CalendarIcsService Service(AsyncServiceScope scope)
    {
        var services = scope.ServiceProvider;
        return new CalendarIcsService(
            services.GetRequiredService<OdysseyContext>(), NullLogger<CalendarIcsService>.Instance, new ImportLimits(),
            new StubJournalLimitsLookup(), new FakeTimeProvider(Now), services.GetRequiredService<StoredFileContentSource>(),
            services.GetRequiredService<PhotoOriginalSource>(), services.GetRequiredService<StoredFileWriter>(),
            new FileValidationService(), services.GetRequiredService<IUploadLimitsLookup>(), services.GetRequiredService<PhotoService>());
    }

    private static Task<IcsImportResult> ImportAsync(CalendarIcsService service, Guid calendarId, params string[] vevents)
    {
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";
        return service.ImportAsync(calendarId, new MemoryStream(Encoding.UTF8.GetBytes(ics)), Encoding.UTF8.GetByteCount(ics),
            "text/calendar", User, attachmentClaims: CalendarIcsImportClaims.Trusted);
    }

    private static string Vevent(string uid, params string[] attachments) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\nDTSTART:20300302T100000Z\r\nDTEND:20300302T110000Z\r\nSUMMARY:Imported\r\n"
        + string.Concat(attachments.Select(a => a.EndsWith("\r\n", StringComparison.Ordinal) ? a : a + "\r\n")) + "END:VEVENT\r\n";

    private static string Inline(byte[] content, string fileName, string contentType, string extra)
    {
        var base64 = Convert.ToBase64String(content);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 59) / 60)
            .Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - (i * 60)))));
        return $"ATTACH;FMTTYPE={contentType};ENCODING=BASE64;VALUE=BINARY;FILENAME=\"{fileName}\"{extra}:{folded}\r\n";
    }

    /// <summary>Fails a command carrying <paramref name="marker"/> with a MariaDB error — once, or every time.</summary>
    private sealed class InjectedError(string marker, MySqlErrorCode code, string message, bool always) : DbCommandInterceptor
    {
        private int count;

        public int Count => count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal) && (always || count == 0))
            {
                Interlocked.Increment(ref count);
                throw (MySqlException)Activator.CreateInstance(
                    typeof(MySqlException), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, [code, message], null)!;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ImportLimits : IImportExportLimitsLookup
    {
        public Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImportExportLimits(
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                null, null, 64L * 1024 * 1024, 64L * 1024 * 1024,
                200_000, Odyssey.Dtos.SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences, 92, 200, 100, 64L * 1024 * 1024,
                IsDegraded: false));
    }
}
