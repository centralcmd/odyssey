using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
            var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:" + row.CalendarEventId + "@odyssey.local\r\n"
                + "DTSTART:20260302T100000\r\nDTEND:20260302T110000\r\nSUMMARY:Moved\r\nATTACH:odyssey-file:" + files[0] + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
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
            var blob = new FileBlob { Id = Guid.NewGuid(), Content = bytes };
            var file = new FileMetadata
            {
                Id = Guid.NewGuid(), UploadedByUserId = User, FileName = $"f{i}.txt", ContentType = "text/plain",
                SizeBytes = bytes.Length, Sha256Hash = PhotoTestData.Sha256(bytes), FileBlobId = blob.Id, UploadedAtUtc = Now,
            };
            context.FileBlob.Add(blob);
            context.FileMetadata.Add(file);
            fileIds.Add(file.Id);
        }

        var photoIds = new List<Guid>();
        for (var i = 0; i < photos; i++)
        {
            photoIds.Add(PhotoTestData.StageLegacyPhoto(context, userId: User).Photo.PhotoId);
        }

        await context.SaveChangesAsync();
        return (fileIds, photoIds);
    }
}
