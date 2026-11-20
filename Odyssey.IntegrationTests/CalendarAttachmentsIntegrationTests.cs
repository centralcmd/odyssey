using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Core.Locations;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.FileStorage;
using Xunit;
using Xunit.Abstractions;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using DtoDays = Odyssey.Dtos.Journal.DaysOfWeekFlags;
using DtoFrequency = Odyssey.Dtos.Journal.RecurrenceFrequency;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Calendar event attachments (issue #521) against real MariaDB: every new key's CASCADE (AC 18), invariant 1 after the
/// Spec B paths that touch series membership (AC 14), and the series-delete copy at its 200,000-row bound inside one
/// transaction (AC 5). The EF InMemory tiers run no cascade and no transaction, so none of this is visible there.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class CalendarAttachmentsIntegrationTests(MariaDbFixture fixture, ITestOutputHelper output)
{
    private const string Database = "odyssey_calendar_attachments";
    private const string User = "calendar-attachments-it";
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task Every_link_key_cascades_with_its_owner_its_file_and_its_photo()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options);
        var (files, photos) = await SeedContentAsync(options, 2, 2);
        Guid eventId;
        Guid patternId;
        await using (var context = new OdysseyContext(options))
        {
            var created = await new CalendarEventService(context, new StubJournalLimitsLookup(), new FakeTimeProvider(Now)).Create(
                Event(calendarId) with { AttachmentFileIds = [.. files], PhotoIds = [.. photos] }, User,
                claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted);
            eventId = created.CalendarEventId;
            var series = await Patterns(context).Create(
                Weekly(calendarId, 2) with { AttachmentFileIds = [.. files], PhotoIds = [.. photos] }, User,
                claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted);
            patternId = series.RecurrencePatternId;
        }

        await using var db = new OdysseyContext(options);
        // A pair is unique.
        var duplicate = await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO CalendarEventAttachments (CalendarEventAttachmentId, CalendarEventId, FileId, CreatedAt)
            VALUES ({Guid.NewGuid()}, {eventId}, {files[1]}, {Now})
            """));
        Assert.Equal(1062, duplicate.Number);

        // The file and the photo, deleted underneath the application.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM FileMetadata WHERE Id = {files[0]}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Photos WHERE PhotoId = {photos[0]}");
        Assert.False(await db.CalendarEventAttachments.AnyAsync(a => a.FileId == files[0]));
        Assert.False(await db.RecurrencePatternAttachments.AnyAsync(a => a.FileId == files[0]));
        Assert.False(await db.CalendarEventPhotos.AnyAsync(p => p.PhotoId == photos[0]));
        Assert.False(await db.RecurrencePatternPhotos.AnyAsync(p => p.PhotoId == photos[0]));

        // The owners.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM CalendarEvents WHERE CalendarEventId = {eventId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM RecurrencePatterns WHERE RecurrencePatternId = {patternId}");
        Assert.False(await db.CalendarEventAttachments.AnyAsync(a => a.CalendarEventId == eventId));
        Assert.False(await db.CalendarEventPhotos.AnyAsync(p => p.CalendarEventId == eventId));
        Assert.False(await db.RecurrencePatternAttachments.AnyAsync(a => a.RecurrencePatternId == patternId));
        Assert.False(await db.RecurrencePatternPhotos.AnyAsync(p => p.RecurrencePatternId == patternId));
    }

    [SkippableFact]
    public async Task No_series_member_holds_an_event_link_after_create_edit_import_or_regenerate()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options);
        var (files, photos) = await SeedContentAsync(options, 1, 1);
        Guid patternId;
        await using (var context = new OdysseyContext(options))
        {
            patternId = (await Patterns(context).Create(Weekly(calendarId, 4) with { AttachmentFileIds = [.. files], PhotoIds = [.. photos] },
                User, claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted)).RecurrencePatternId;
        }

        await using (var context = new OdysseyContext(options))
        {
            var row = await context.CalendarEvents.AsNoTracking().FirstAsync(e => e.RecurrencePatternId == patternId);
            var events = new CalendarEventService(context, new StubJournalLimitsLookup(), new FakeTimeProvider(Now));
            await Assert.ThrowsAsync<Odyssey.Core.DomainUnprocessableException>(() => events.Update(row.CalendarEventId,
                Event(calendarId) with { StartDateTime = row.StartDateTime, EndDateTime = row.EndDateTime, AttachmentFileIds = [.. files] },
                User, claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted));
            await events.Update(row.CalendarEventId, Event(calendarId) with { StartDateTime = row.StartDateTime, EndDateTime = row.EndDateTime, Title = "Edited" },
                User, claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted);
        }

        await using (var context = new OdysseyContext(options))
        {
            // Regenerate: a rule change reconciles the future rows.
            await Patterns(context).Update(patternId, Weekly(calendarId, 6), User, claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted);
        }

        await using (var context = new OdysseyContext(options))
        {
            // Import: a standalone VEVENT naming a generated row by its synthetic UID, carrying a reference.
            var row = await context.CalendarEvents.AsNoTracking().Where(e => e.RecurrencePatternId == patternId).OrderBy(e => e.StartDateTime).LastAsync();
            var stored = await context.FileMetadata.AsNoTracking().SingleAsync(f => f.Id == files[0]);
            var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:" + row.CalendarEventId + "@odyssey.local\r\n"
                + "DTSTART:20260302T100000\r\nDTEND:20260302T110000\r\nSUMMARY:Moved\r\nATTACH;VALUE=URI;X-ODYSSEY-SHA256=" + stored.Sha256Hash + ";SIZE=" + stored.SizeBytes
                + ":odyssey-file:" + files[0] + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            var result = await new CalendarIcsService(context, NullLogger<CalendarIcsService>.Instance, new ImportLimits(),
                new StubJournalLimitsLookup(), new FakeTimeProvider(Now)).ImportAsync(
                calendarId, new MemoryStream(Encoding.UTF8.GetBytes(ics)), ics.Length, "text/calendar", User,
                attachmentClaims: CalendarIcsImportClaims.Trusted);
            Assert.Equal(1, result.IgnoredOccurrenceAttachmentCount);
        }

        await using var db = new OdysseyContext(options);
        var members = await db.CalendarEvents.Where(e => e.RecurrencePatternId != null).Select(e => e.CalendarEventId).ToListAsync();
        Assert.NotEmpty(members);
        Assert.False(await db.CalendarEventAttachments.AnyAsync(a => members.Contains(a.CalendarEventId)));
        Assert.False(await db.CalendarEventPhotos.AnyAsync(p => members.Contains(p.CalendarEventId)));
        Assert.Equal(1, await db.RecurrencePatternAttachments.CountAsync(a => a.RecurrencePatternId == patternId));
    }

    [SkippableFact]
    public async Task Deleting_a_thousand_row_series_at_a_cap_of_one_hundred_copies_two_hundred_thousand_links_in_one_transaction()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await OptionsAsync();
        var calendarId = await SeedCalendarAsync(options);
        var (files, photos) = await SeedContentAsync(options, 100, 100);
        Guid patternId;
        var limits = new StubJournalLimitsLookup();
        limits.Limits = limits.Limits with { CalendarEventMaxLinksPerKind = 100 };
        await using (var context = new OdysseyContext(options))
        {
            // Daily for 1,000 days, all before the fixed clock: every row is detached and keeps a copy.
            var created = await new RecurrencePatternService(context, limits, new FakeTimeProvider(Now)).Create(new NewRecurrencePattern
            {
                CalendarId = calendarId, Title = "Daily", StartDateTime = new DateTime(2022, 1, 1, 9, 0, 0),
                EndDateTime = new DateTime(2022, 1, 1, 10, 0, 0), Frequency = DtoFrequency.Daily,
                RecurrenceEndDate = new DateTime(2022, 1, 1, 9, 0, 0).AddDays(999),
                AttachmentFileIds = [.. files], PhotoIds = [.. photos],
            }, User, claims: LocationClaims.Trusted, linkClaims: CalendarLinkClaims.Trusted);
            patternId = created.RecurrencePatternId;
            Assert.Equal(1000, created.GeneratedEventCount);
        }

        var clock = Stopwatch.StartNew();
        await using (var context = new OdysseyContext(options))
        {
            Assert.True(await new RecurrencePatternService(context, limits, new FakeTimeProvider(Now)).Delete(patternId));
        }

        clock.Stop();
        output.WriteLine($"Series delete with 200,000 link copies: {clock.Elapsed.TotalSeconds:F1} s.");

        await using var db = new OdysseyContext(options);
        Assert.Equal(100_000, await db.CalendarEventAttachments.CountAsync());
        Assert.Equal(100_000, await db.CalendarEventPhotos.CountAsync());
        Assert.False(await db.RecurrencePatternAttachments.AnyAsync());
        Assert.False(await db.RecurrencePatternPhotos.AnyAsync());
        Assert.Equal(1000, await db.CalendarEvents.CountAsync(e => e.CalendarId == calendarId && e.RecurrencePatternId == null));
        // Measured budget (v4 §6): 9.4 s on the Testcontainers MariaDB here; ~6x headroom so a slow CI container does
        // not flake, while a regression to per-row saves (minutes) still fails.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");
    }

    // AC 22 (v5 §1, implementation note 1): the strategy re-runs a batch that lost a deadlock. Phase A ran once, before
    // any attempt, so the retry only re-attaches FRESH rows for the same reservations: one file, one photo, one link of
    // each, and exactly two stored objects ever written — a second decode would have reserved a third and fourth.
    [SkippableFact]
    public async Task An_import_batch_with_inline_attachments_retried_after_a_deadlock_links_and_stores_each_once()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var deadlock = new FirstInsertDeadlock("INSERT INTO `CalendarEvents`");
        await using var host = await PhotoStoreHost.CreateAsync(fixture.OdysseyConnectionString, retryOnFailure: true, interceptors: deadlock);
        await using var scope = host.Host.Scope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<OdysseyContext>();
        var calendar = new Odyssey.Context.Calendar { Name = $"Retry {Guid.NewGuid():N}", CreatedByUserId = "user" };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var fileBytes = Encoding.UTF8.GetBytes($"Agenda {Guid.NewGuid()}");
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:retried-" + Guid.NewGuid() + "\r\n"
            + "DTSTART:20300302T100000Z\r\nDTEND:20300302T110000Z\r\nSUMMARY:Retried\r\n"
            + Inline(fileBytes, "agenda.txt", "text/plain", string.Empty)
            + Inline(photoBytes, "beach.jpg", "image/jpeg", ";X-ODYSSEY-KIND=PHOTO;X-ODYSSEY-TITLE=\"Fjord\"")
            + "END:VEVENT\r\nEND:VCALENDAR\r\n";
        var service = new CalendarIcsService(
            context, NullLogger<CalendarIcsService>.Instance, new ImportLimits(), new StubJournalLimitsLookup(),
            new FakeTimeProvider(Now), services.GetRequiredService<Odyssey.Core.FileStorage.Files.StoredFileContentSource>(),
            services.GetRequiredService<Odyssey.Core.Journal.Originals.PhotoOriginalSource>(),
            services.GetRequiredService<Odyssey.Core.FileStorage.Files.StoredFileWriter>(), new Odyssey.Core.Finance.FileValidationService(),
            services.GetRequiredService<Odyssey.Core.Finance.IUploadLimitsLookup>(), services.GetRequiredService<PhotoService>());

        var result = await service.ImportAsync(
            calendar.CalendarId, new MemoryStream(Encoding.UTF8.GetBytes(ics)), Encoding.UTF8.GetByteCount(ics), "text/calendar", "user",
            attachmentClaims: CalendarIcsImportClaims.Trusted);

        Assert.True(deadlock.Fired);
        Assert.Equal(2, service.AttachmentsDecoded); // phase A ran once: the retry decoded nothing again
        Assert.Equal((1, 1, 1, 0, 0), (result.ImportedCount, result.StoredFileCount, result.StoredPhotoCount, result.ReusedFileCount, result.SkippedAttachmentCount));
        await using var check = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString)).Options);
        var fileHash = PhotoTestData.Sha256(fileBytes);
        var photoHash = PhotoTestData.Sha256(photoBytes);
        var file = Assert.Single(await check.FileMetadata.Where(f => f.Sha256Hash == fileHash).ToListAsync());
        var photo = Assert.Single(await check.Photos.Where(p => p.Sha256Hash == photoHash).ToListAsync());
        Assert.Equal("Fjord", photo.Title);
        var eventId = (await check.CalendarEvents.SingleAsync(e => e.CalendarId == calendar.CalendarId)).CalendarEventId;
        Assert.Equal(file.Id, (await check.CalendarEventAttachments.SingleAsync(a => a.CalendarEventId == eventId)).FileId);
        Assert.Equal(photo.PhotoId, (await check.CalendarEventPhotos.SingleAsync(p => p.CalendarEventId == eventId)).PhotoId);
        var objects = await check.StoredObjects.AsNoTracking()
            .Where(o => o.Sha256Hash == fileHash || o.Sha256Hash == photoHash).ToListAsync();
        Assert.Equal(2, objects.Count);
        Assert.All(objects, o => Assert.Equal(StoredObjectState.Committed, o.State));
    }

    private static string Inline(byte[] content, string fileName, string contentType, string extra)
    {
        var base64 = Convert.ToBase64String(content);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 59) / 60)
            .Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - (i * 60)))));
        return $"ATTACH;FMTTYPE={contentType};ENCODING=BASE64;VALUE=BINARY;FILENAME=\"{fileName}\"{extra}:{folded}\r\n";
    }

    // The 122bcd53 injection: fails the first command carrying the marker with a 1213, as InnoDB would.
    private sealed class FirstInsertDeadlock(string marker) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int fired;

        public bool Fired => fired == 1;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal) && Interlocked.CompareExchange(ref fired, 1, 0) == 0)
            {
                throw (MySqlConnector.MySqlException)Activator.CreateInstance(
                    typeof(MySqlConnector.MySqlException), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, [MySqlConnector.MySqlErrorCode.LockDeadlock, "Deadlock found when trying to get lock (injected)"], null)!;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ImportLimits : Odyssey.Core.Journal.IImportExportLimitsLookup
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

    private static NewCalendarEvent Event(Guid calendarId) => new()
    {
        CalendarId = calendarId, Title = "Meeting",
        StartDateTime = new DateTime(2026, 3, 2, 10, 0, 0), EndDateTime = new DateTime(2026, 3, 2, 11, 0, 0),
    };

    private static NewRecurrencePattern Weekly(Guid calendarId, int count) => new()
    {
        CalendarId = calendarId, Title = "Standup",
        StartDateTime = new DateTime(2026, 2, 2, 9, 0, 0), EndDateTime = new DateTime(2026, 2, 2, 10, 0, 0),
        Frequency = DtoFrequency.Weekly, DaysOfWeek = DtoDays.Monday, OccurrenceCount = count,
    };

    private static RecurrencePatternService Patterns(OdysseyContext context) =>
        new(context, new StubJournalLimitsLookup(), new FakeTimeProvider(Now));

    private async Task<DbContextOptions<OdysseyContext>> OptionsAsync()
    {
        var connectionString = await fixture.CreateMigratedDatabaseAsync(Database);
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), OdysseyDatabaseRetry.Configure)
            .Options;
        await using var migrating = new OdysseyContext(options);
        await AttributionUsers.EnsureAsync(migrating, User);
        return options;
    }

    private static async Task<Guid> SeedCalendarAsync(DbContextOptions<OdysseyContext> options)
    {
        await using var context = new OdysseyContext(options);
        var calendar = new Odyssey.Context.Calendar { Name = $"Attachments {Guid.NewGuid():N}", CreatedByUserId = User };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        return calendar.CalendarId;
    }

    private static async Task<(List<Guid> Files, List<Guid> Photos)> SeedContentAsync(
        DbContextOptions<OdysseyContext> options, int files, int photos)
    {
        await using var context = new OdysseyContext(options);
        var fileIds = new List<Guid>();
        for (var i = 0; i < files; i++)
        {
            var bytes = Encoding.UTF8.GetBytes($"file {i} {Guid.NewGuid()}");
            var file = new FileMetadata
            {
                Id = Guid.NewGuid(), UploadedByUserId = User, FileName = $"f{i}.txt", ContentType = "text/plain",
                SizeBytes = bytes.Length, Sha256Hash = PhotoTestData.Sha256(bytes), FinanceStoredObjectId = StoredFileFixtures.AddStoredObjectRow(context), UploadedAtUtc = Now,
            };
            context.FileMetadata.Add(file);
            fileIds.Add(file.Id);
        }

        var photoIds = new List<Guid>();
        for (var i = 0; i < photos; i++)
        {
            var hash = StoredFileFixtures.Sha256(PhotoTestData.Jpeg(PhotoTestData.NextSeed()));
            var photo = new Photo
            {
                PhotoId = Guid.NewGuid(),
                StoredObjectId = StoredFileFixtures.AddStoredObjectRow(context, featureKey: Odyssey.Core.Journal.Originals.PhotoOriginalStorage.FeatureKey),
                FileName = $"p{i}.jpg", ContentType = "image/jpeg", SizeBytes = 3, Sha256Hash = hash,
                CreatedByUserId = User, CreatedAt = Now, UpdatedAt = Now,
            };
            context.Photos.Add(photo);
            photoIds.Add(photo.PhotoId);
        }

        await context.SaveChangesAsync();
        return (fileIds, photoIds);
    }
}
