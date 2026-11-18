using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.FileStorage;
using Xunit;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using ContextRecurrencePattern = Odyssey.Context.RecurrencePattern;
using DtoDays = Odyssey.Dtos.Journal.DaysOfWeekFlags;
using DtoFrequency = Odyssey.Dtos.Journal.RecurrenceFrequency;

namespace Odyssey.Api.Tests;

/// <summary>
/// Calendar event attachments (issue #521) over HTTP on EF InMemory: the link writes and their change-only claims and
/// cap, the read visibility, series delete, the <c>.ics</c> export's inline-once-then-reference rule and its claims, and
/// the import's dedupe, replace rule and claims. Cascades, locks and the 200,000-row copy run against MariaDB in
/// <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class CalendarAttachmentsApiTests
{
    private const string ActorUserId = "calendar-attachments-actor";
    private const string CalendarsPath = "/api/calendars";
    private const string EventsPath = "/api/calendar-events";
    private const string PatternsPath = "/api/recurrence-patterns";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Calendar =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate,
        PermissionClaims.CalendarDelete,
    ];

    private static readonly string[] WithReads = [.. Calendar, PermissionClaims.FilesRead, PermissionClaims.PhotosRead];

    private static readonly string[] WithUploads =
        [.. WithReads, PermissionClaims.FilesCreate, PermissionClaims.PhotosCreate, PermissionClaims.PhotosDelete];

    private static readonly byte[] AgendaBytes = Encoding.UTF8.GetBytes("Agenda: budget, travel, AOB.");

    // ------------------------------------------------------------------ AC 1: series and standalone links

    [Fact]
    public async Task A_series_links_show_on_every_occurrence_and_a_standalone_event_shows_its_own()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var first = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var second = await SeedFileAsync(factory, "minutes.txt", Encoding.UTF8.GetBytes("Minutes"));
        var photo = await SeedPhotoAsync(factory);
        var series = await CreatePatternAsync(client, Weekly(calendar.CalendarId, 3) with
        {
            AttachmentFileIds = [first, second], PhotoIds = [photo],
        });
        var standalone = await CreateEventAsync(client, Event(calendar.CalendarId) with { AttachmentFileIds = [first] });

        Assert.Equal((2, 1), (series.AttachmentCount, series.PhotoCount));
        foreach (var row in await SeriesRowsAsync(client, series.RecurrencePatternId))
        {
            Assert.True(row.LinksFromSeries);
            Assert.Equal((2, 1), (row.AttachmentCount, row.PhotoCount));
            Assert.Null(row.Attachments); // a list read carries counts only (A8)
            var detail = await GetEventAsync(client, row.CalendarEventId);
            Assert.Equal(new[] { first, second }.Order(), detail.Attachments!.Select(a => a.FileId).Order());
            Assert.Equal(photo, Assert.Single(detail.Photos!).PhotoId);
        }

        Assert.False(standalone.LinksFromSeries);
        Assert.Equal(first, Assert.Single(standalone.Attachments!).FileId);
        Assert.Equal("agenda.txt", standalone.Attachments![0].FileName);
        Assert.Empty(await CalendarEventLinkRowsAsync(factory, series.RecurrencePatternId)); // invariant 1
    }

    // ------------------------------------------------------------------ AC 2: null keeps, [] clears

    [Fact]
    public async Task A_put_with_null_arrays_keeps_the_links_and_an_empty_array_clears_them()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var photo = await SeedPhotoAsync(factory);
        var created = await CreateEventAsync(client, Event(calendar.CalendarId) with { AttachmentFileIds = [file], PhotoIds = [photo] });

        // A drag/resize PUT carries no arrays.
        var moved = await PutEventAsync(client, created.CalendarEventId, Event(calendar.CalendarId) with
        {
            StartDateTime = Wall(2026, 3, 2, 11), EndDateTime = Wall(2026, 3, 2, 12),
        });
        Assert.Equal((1, 1), (moved.AttachmentCount, moved.PhotoCount));

        var cleared = await PutEventAsync(client, created.CalendarEventId, Event(calendar.CalendarId) with { AttachmentFileIds = [] });
        Assert.Equal((0, 1), (cleared.AttachmentCount, cleared.PhotoCount));

        var series = await CreatePatternAsync(client, Weekly(calendar.CalendarId, 2) with { AttachmentFileIds = [file] });
        var templateOnly = await UpdatePatternAsync(client, series.RecurrencePatternId, Weekly(calendar.CalendarId, 2) with { Title = "Renamed" });
        Assert.Equal(1, templateOnly.Pattern.AttachmentCount);
    }

    // ------------------------------------------------------------------ AC 3: change-only claims

    [Fact]
    public async Task Without_files_read_a_put_may_keep_or_remove_files_but_not_add_one_and_the_same_for_photos()
    {
        await using var owner = new ApiFactory(WithReads);
        using var ownerClient = owner.CreateClient();
        var calendar = await CreateCalendarAsync(ownerClient);
        var kept = await SeedFileAsync(owner, "kept.txt", AgendaBytes);
        var other = await SeedFileAsync(owner, "other.txt", Encoding.UTF8.GetBytes("other"));
        var photo = await SeedPhotoAsync(owner);
        var otherPhoto = await SeedPhotoAsync(owner);
        var created = await CreateEventAsync(ownerClient, Event(calendar.CalendarId) with
        {
            AttachmentFileIds = [kept, other], PhotoIds = [photo],
        });

        await using var editor = new ApiFactory(Calendar, sharingStoreWith: owner);
        using var client = editor.CreateClient();
        var id = created.CalendarEventId;

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{EventsPath}/{id}", Event(calendar.CalendarId) with
        {
            AttachmentFileIds = [kept, other], PhotoIds = [photo],
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{EventsPath}/{id}", Event(calendar.CalendarId) with
        {
            AttachmentFileIds = [kept], PhotoIds = [],
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{EventsPath}/{id}", Event(calendar.CalendarId) with
        {
            AttachmentFileIds = [kept, other],
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{EventsPath}/{id}", Event(calendar.CalendarId) with
        {
            PhotoIds = [otherPhoto],
        })).StatusCode);

        var unknown = await ownerClient.PutAsJsonAsync($"{EventsPath}/{id}", Event(calendar.CalendarId) with
        {
            AttachmentFileIds = [Guid.NewGuid()],
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.True((await unknown.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.ContainsKey("attachmentFileIds"));
    }

    [Fact]
    public async Task A_photo_original_hidden_from_the_files_store_cannot_be_attached_as_a_file()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var (_, wrappedFileId) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, "beach.jpg");

        var response = await client.PostAsJsonAsync(EventsPath, Event(calendar.CalendarId) with { AttachmentFileIds = [wrappedFileId] });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Links_on_a_generated_occurrence_are_refused_but_null_and_empty_arrays_are_accepted()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var series = await CreatePatternAsync(client, Weekly(calendar.CalendarId, 2));
        var row = (await SeriesRowsAsync(client, series.RecurrencePatternId))[0];
        var body = Event(calendar.CalendarId) with { StartDateTime = row.StartDateTime, EndDateTime = row.EndDateTime };

        var refused = await client.PutAsJsonAsync($"{EventsPath}/{row.CalendarEventId}", body with { AttachmentFileIds = [file] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("edit the series", (await refused.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{EventsPath}/{row.CalendarEventId}", body with { AttachmentFileIds = [] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{EventsPath}/{row.CalendarEventId}", body)).StatusCode);
    }

    // ------------------------------------------------------------------ AC 4: cap

    [Fact]
    public async Task The_cap_refuses_only_a_rise_so_an_over_cap_event_still_saves_a_title_change_and_a_removal()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var files = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            files.Add(await SeedFileAsync(factory, $"f{i}.txt", Encoding.UTF8.GetBytes($"file {i}")));
        }

        var over = await CreateEventAsync(client, Event(calendar.CalendarId) with { AttachmentFileIds = [.. files] });
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.CalendarEventMaxLinksPerKind, "3");
        // As a settings save does: evict the journal limits the first write cached.
        factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>().Remove("system-settings:journal-request-caps");

        var fourth = await client.PostAsJsonAsync(EventsPath, Event(calendar.CalendarId) with { AttachmentFileIds = [.. files.Take(4)] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fourth.StatusCode);

        var renamed = await PutEventAsync(client, over.CalendarEventId, Event(calendar.CalendarId) with { Title = "Renamed" });
        Assert.Equal(5, renamed.AttachmentCount);
        var shrunk = await PutEventAsync(client, over.CalendarEventId, Event(calendar.CalendarId) with { AttachmentFileIds = [.. files.Take(4)] });
        Assert.Equal(4, shrunk.AttachmentCount);
    }

    // ------------------------------------------------------------------ AC 5: series delete

    [Fact]
    public async Task Deleting_a_series_gives_each_past_row_a_copy_of_its_links_and_removes_the_series_links()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var photo = await SeedPhotoAsync(factory);
        // Mondays from 1 December 2025: five past rows and one after the fixed clock.
        var series = await CreatePatternAsync(client, Weekly(calendar.CalendarId, 6) with
        {
            StartDateTime = Wall(2025, 12, 1, 9), EndDateTime = Wall(2025, 12, 1, 10), AttachmentFileIds = [file], PhotoIds = [photo],
        });
        var source = await SeriesLinkAsync(factory, series.RecurrencePatternId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{PatternsPath}/{series.RecurrencePatternId}")).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var rows = await db.CalendarEvents.AsNoTracking().Where(e => e.CalendarId == calendar.CalendarId).ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Null(r.RecurrencePatternId));
        var copies = await db.CalendarEventAttachments.AsNoTracking().ToListAsync();
        Assert.Equal(rows.Select(r => r.CalendarEventId).Order(), copies.Select(c => c.CalendarEventId).Order());
        Assert.All(copies, c => Assert.Equal((file, source.CreatedAt), (c.FileId, c.CreatedAt)));
        Assert.Equal(5, await db.CalendarEventPhotos.CountAsync(p => p.PhotoId == photo));
        Assert.False(await db.RecurrencePatternAttachments.AnyAsync());
        Assert.False(await db.RecurrencePatternPhotos.AnyAsync());

        var detail = await GetEventAsync(client, rows[0].CalendarEventId);
        Assert.Equal((1, 1, false), (detail.AttachmentCount, detail.PhotoCount, detail.LinksFromSeries));
    }

    // ------------------------------------------------------------------ AC 6: read visibility

    [Fact]
    public async Task Without_the_read_claims_names_types_sizes_and_titles_are_withheld_but_ids_positions_and_counts_stay()
    {
        await using var owner = new ApiFactory(WithReads);
        using var ownerClient = owner.CreateClient();
        var calendar = await CreateCalendarAsync(ownerClient);
        var file = await SeedFileAsync(owner, "agenda.txt", AgendaBytes);
        var photo = await SeedPhotoAsync(owner, title: "Fjord");
        var created = await CreateEventAsync(ownerClient, Event(calendar.CalendarId) with { AttachmentFileIds = [file], PhotoIds = [photo] });
        Assert.Equal("Fjord", created.Photos![0].Title);

        await using var reader = new ApiFactory([PermissionClaims.CalendarRead], sharingStoreWith: owner);
        using var client = reader.CreateClient();
        var seen = await GetEventAsync(client, created.CalendarEventId);

        var link = Assert.Single(seen.Attachments!);
        Assert.Equal(file, link.FileId);
        Assert.Equal((null, null, null, null), (link.FileName, link.ContentType, link.SizeBytes, (DateTime?)link.UploadedAtUtc));
        var photoLink = Assert.Single(seen.Photos!);
        Assert.Equal((photo, 0), (photoLink.PhotoId, photoLink.Position));
        Assert.Equal((null, null, false), (photoLink.Title, photoLink.FileName, photoLink.ThumbnailsReady));
        Assert.Equal((1, 1), (seen.AttachmentCount, seen.PhotoCount));
    }

    // ------------------------------------------------------------------ AC 7: export claims and omit

    [Fact]
    public async Task An_export_with_linked_files_or_photos_needs_their_read_claims_unless_attachments_are_omitted()
    {
        await using var owner = new ApiFactory(WithReads);
        using var ownerClient = owner.CreateClient();
        var calendar = await CreateCalendarAsync(ownerClient);
        var file = await SeedFileAsync(owner, "agenda.txt", AgendaBytes);
        var photo = await SeedPhotoAsync(owner);
        await CreateEventAsync(ownerClient, Event(calendar.CalendarId) with { AttachmentFileIds = [file] });
        await CreateEventAsync(ownerClient, Event(calendar.CalendarId) with { Title = "Photo", PhotoIds = [photo] });

        await using var reader = new ApiFactory([PermissionClaims.CalendarRead], sharingStoreWith: owner);
        using var client = reader.CreateClient();
        var path = $"{CalendarsPath}/{calendar.CalendarId}/ics";

        foreach (var url in new[] { path, path + "/summary" })
        {
            var refused = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var problem = (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!;
            Assert.Equal(CalendarAttachmentsException.ExportForbiddenCode, problem.Extensions["code"]?.ToString());
            Assert.Contains(PermissionClaims.FilesRead, problem.Detail, StringComparison.Ordinal);
            Assert.Contains(PermissionClaims.PhotosRead, problem.Detail, StringComparison.Ordinal);
        }

        var omitted = await client.GetStringAsync(path + "?attachments=omit");
        Assert.DoesNotContain("ATTACH", omitted, StringComparison.Ordinal);
        var summary = (await client.GetFromJsonAsync<CalendarIcsExportSummary>(path + "/summary?attachments=omit"))!;
        Assert.Equal((2, 0, 0, 0L), (summary.EventCount, summary.AttachmentCount, summary.PhotoCount, summary.AttachmentBytes));
    }

    // ------------------------------------------------------------------ AC 8: shared content written once

    [Fact]
    public async Task A_flattened_series_of_ten_rows_writes_its_file_inline_once_and_references_it_nine_times()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var patternId = await SeedLegacySeriesAsync(factory, calendar.CalendarId, 10);
        await LinkSeriesFileAsync(factory, patternId, file);

        var body = await client.GetStringAsync($"{CalendarsPath}/{calendar.CalendarId}/ics");
        var summary = (await client.GetFromJsonAsync<CalendarIcsExportSummary>($"{CalendarsPath}/{calendar.CalendarId}/ics/summary"))!;

        Assert.Equal(10, Count(body, "BEGIN:VEVENT"));
        Assert.Equal(1, Count(body, "ENCODING=BASE64"));
        Assert.Equal(9, Count(Unfold(body), $"odyssey-file:{file:D}"));
        Assert.Contains($"X-ODYSSEY-SHA256={Sha256(AgendaBytes)};SIZE={AgendaBytes.Length}", Unfold(body), StringComparison.Ordinal);
        Assert.Equal((1, (long)AgendaBytes.Length), (summary.AttachmentCount, summary.AttachmentBytes));
    }

    [Fact]
    public async Task The_aggregate_export_carries_no_attachments_and_its_summary_counts_none()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        await CreateEventAsync(client, Event(calendar.CalendarId) with { AttachmentFileIds = [file] });

        var body = await client.GetStringAsync($"{EventsPath}/ics");
        var summary = (await client.GetFromJsonAsync<CalendarIcsExportSummary>($"{EventsPath}/ics/summary"))!;

        Assert.DoesNotContain("ATTACH", body, StringComparison.Ordinal);
        Assert.Equal((0, 0, 0L), (summary.AttachmentCount, summary.PhotoCount, summary.AttachmentBytes));
    }

    [Fact]
    public async Task A_single_event_export_of_a_generated_occurrence_carries_the_series_links_inline()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var series = await CreatePatternAsync(client, Weekly(calendar.CalendarId, 2) with { AttachmentFileIds = [file] });
        var row = (await SeriesRowsAsync(client, series.RecurrencePatternId))[0];

        var body = await client.GetStringAsync($"{EventsPath}/{row.CalendarEventId}/ics");

        Assert.Equal(1, Count(body, "ENCODING=BASE64"));
        Assert.Contains(Convert.ToBase64String(AgendaBytes)[..20], Unfold(body), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ AC 9, 10: round trips

    [Fact]
    public async Task Re_importing_an_export_into_the_same_calendar_reuses_every_file_and_photo()
    {
        await using var factory = new ApiFactory(WithUploads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var photo = await SeedPhotoAsync(factory, title: "Fjord");
        var created = await CreateEventAsync(client, Event(calendar.CalendarId) with { AttachmentFileIds = [file], PhotoIds = [photo] });
        var series = await CreatePatternAsync(client, Weekly(calendar.CalendarId, 2) with { AttachmentFileIds = [file] });
        var filesBefore = (await FilesAsync(factory)).Count;

        var result = await ImportAsync(client, calendar.CalendarId, await client.GetStringAsync($"{CalendarsPath}/{calendar.CalendarId}/ics"));

        Assert.Equal((0, 0), (result.StoredFileCount, result.StoredPhotoCount));
        Assert.True(result.ReusedFileCount >= 1);
        Assert.Equal(0, result.SkippedAttachmentCount);
        Assert.Equal(filesBefore, (await FilesAsync(factory)).Count);
        Assert.Single(await PhotosAsync(factory));
        var detail = await GetEventAsync(client, created.CalendarEventId);
        Assert.Equal((file, photo), (Assert.Single(detail.Attachments!).FileId, Assert.Single(detail.Photos!).PhotoId));
        Assert.Equal(1, (await GetPatternAsync(client, series.RecurrencePatternId)).AttachmentCount);
        Assert.Equal(0, result.ReplacedLinkSetCount);
    }

    [Fact]
    public async Task Importing_an_export_into_another_instance_stores_each_piece_once_and_its_references_resolve()
    {
        await using var source = new ApiFactory(WithReads);
        using var sourceClient = source.CreateClient();
        var sourceCalendar = await CreateCalendarAsync(sourceClient);
        var file = await SeedFileAsync(source, "agenda.txt", AgendaBytes);
        var photo = await SeedPhotoAsync(source, PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "Sunset", "Over the fjord");
        await CreateEventAsync(sourceClient, Event(sourceCalendar.CalendarId) with { AttachmentFileIds = [file], PhotoIds = [photo] });
        await CreateEventAsync(sourceClient, Event(sourceCalendar.CalendarId) with { Title = "Second", AttachmentFileIds = [file], PhotoIds = [photo] });
        var exported = await sourceClient.GetStringAsync($"{CalendarsPath}/{sourceCalendar.CalendarId}/ics");
        Assert.Equal(1, Count(Unfold(exported), "ENCODING=BASE64;VALUE=BINARY;FILENAME=\"agenda.txt\""));

        await using var target = new ApiFactory(WithUploads);
        using var targetClient = target.CreateClient();
        var calendar = await CreateCalendarAsync(targetClient);
        var result = await ImportAsync(targetClient, calendar.CalendarId, exported);

        Assert.Equal((2, 1, 1), (result.ImportedCount, result.StoredFileCount, result.StoredPhotoCount));
        var stored = Assert.Single(await FilesAsync(target));
        Assert.Equal(("agenda.txt", Sha256(AgendaBytes)), (stored.FileName, stored.Sha256Hash));
        Assert.NotNull(stored.JournalStoredObjectId); // a new Journal-area file
        var library = Assert.Single(await PhotosAsync(target));
        Assert.Equal(("Sunset", "Over the fjord"), (library.Title, library.Caption));
        await using var scope = target.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(2, await db.CalendarEventAttachments.CountAsync(a => a.FileId == stored.Id));
        Assert.Equal(2, await db.CalendarEventPhotos.CountAsync(p => p.PhotoId == library.PhotoId));
        Assert.False(await db.StoredObjects.AnyAsync(o => o.State == StoredObjectState.Pending));
    }

    [Fact]
    public async Task Many_events_repeating_one_declared_inline_attachment_store_it_once_across_batches()
    {
        await using var factory = new ApiFactory(WithUploads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var attach = InlineAttach(AgendaBytes, "agenda.txt", "text/plain",
            $";SIZE={AgendaBytes.Length};X-ODYSSEY-SHA256={Sha256(AgendaBytes)}");
        var events = Enumerable.Range(0, CalendarIcsService.ImportBatchSize + 20)
            .Select(i => Vevent($"repeat-{i}", "DTSTART:20260302T100000", "DTEND:20260302T110000", $"SUMMARY:Repeat {i}", attach));

        var result = await ImportAsync(client, calendar.CalendarId, Vcalendar([.. events]));

        Assert.Equal((CalendarIcsService.ImportBatchSize + 20, 1), (result.ImportedCount, result.StoredFileCount));
        Assert.Equal(CalendarIcsService.ImportBatchSize + 19, result.ReusedFileCount);
        Assert.Single(await FilesAsync(factory));
    }

    // ------------------------------------------------------------------ AC 11, 19, 20: the replace rule

    [Fact]
    public async Task Re_importing_the_aggregate_export_keeps_every_link()
    {
        await using var factory = new ApiFactory(WithUploads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var created = await CreateEventAsync(client, Event(calendar.CalendarId) with { AttachmentFileIds = [file] });

        var result = await ImportAsync(client, calendar.CalendarId, await client.GetStringAsync($"{EventsPath}/ics"));

        Assert.Equal((1, 0), (result.UpdatedCount, result.ReplacedLinkSetCount));
        Assert.Equal(1, (await GetEventAsync(client, created.CalendarEventId)).AttachmentCount);
    }

    [Fact]
    public async Task A_google_style_https_attachment_keeps_the_links_and_needs_no_file_claim()
    {
        await using var owner = new ApiFactory(WithReads);
        using var ownerClient = owner.CreateClient();
        var calendar = await CreateCalendarAsync(ownerClient);
        var file = await SeedFileAsync(owner, "agenda.txt", AgendaBytes);
        var created = await CreateEventAsync(ownerClient, Event(calendar.CalendarId) with { AttachmentFileIds = [file] });

        await using var importer = new ApiFactory(Calendar, sharingStoreWith: owner);
        using var client = importer.CreateClient();
        var result = await ImportAsync(client, calendar.CalendarId, Vcalendar(Vevent(
            $"{created.CalendarEventId}@odyssey.local", "DTSTART:20260302T100000", "DTEND:20260302T110000", "SUMMARY:Meeting",
            "ATTACH;FMTTYPE=application/pdf:https://drive.google.com/file/d/abc")));

        Assert.Equal((1, 1, 0), (result.UpdatedCount, result.SkippedAttachmentCount, result.ReplacedLinkSetCount));
        Assert.Contains(result.Skipped, s => s.Reason == "Linked (URI) attachments are not imported." && s.SampleTitles.Count == 0);
        Assert.Equal(1, (await GetEventAsync(ownerClient, created.CalendarEventId)).AttachmentCount);
    }

    [Fact]
    public async Task An_event_whose_only_reference_is_unresolvable_keeps_its_links_but_a_partly_resolving_set_replaces()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var kept = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var other = await SeedFileAsync(factory, "other.txt", Encoding.UTF8.GetBytes("other"));
        var created = await CreateEventAsync(client, Event(calendar.CalendarId) with { AttachmentFileIds = [kept, other] });
        var uid = $"{created.CalendarEventId}@odyssey.local";
        var unknown = $"ATTACH;VALUE=URI;X-ODYSSEY-SHA256={new string('a', 64)};SIZE=3:odyssey-file:{Guid.NewGuid():D}";

        var first = await ImportAsync(client, calendar.CalendarId, Vcalendar(Vevent(uid,
            "DTSTART:20260302T100000", "DTEND:20260302T110000", "SUMMARY:Meeting", unknown)));
        Assert.Equal((0, 1), (first.ReplacedLinkSetCount, first.SkippedAttachmentCount));
        Assert.Equal(2, (await GetEventAsync(client, created.CalendarEventId)).AttachmentCount);

        var second = await ImportAsync(client, calendar.CalendarId, Vcalendar(Vevent(uid,
            "DTSTART:20260302T100000", "DTEND:20260302T110000", "SUMMARY:Meeting", unknown,
            $"ATTACH:odyssey-file:{kept:D}")));
        Assert.Equal((1, 1), (second.ReplacedLinkSetCount, second.RemovedLinkCount));
        Assert.Equal(kept, Assert.Single((await GetEventAsync(client, created.CalendarEventId)).Attachments!).FileId);
    }

    // ------------------------------------------------------------------ AC 12: import claims

    [Theory]
    [InlineData("file-ref")]
    [InlineData("photo-ref")]
    [InlineData("file-inline")]
    [InlineData("photo-inline")]
    public async Task An_import_carrying_attachments_without_their_claims_is_refused_whole_with_nothing_applied(string shape)
    {
        // Reads alone: a reference passes the read check, inline content also needs the create claims.
        var claims = shape.EndsWith("-ref", StringComparison.Ordinal) ? Calendar : WithReads;
        await using var factory = new ApiFactory(claims);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var attach = shape switch
        {
            "file-ref" => $"ATTACH:odyssey-file:{Guid.NewGuid():D}",
            "photo-ref" => $"ATTACH:odyssey-photo:{Guid.NewGuid():D}",
            "file-inline" => InlineAttach(AgendaBytes, "agenda.txt", "text/plain"),
            _ => InlineAttach(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg", "image/jpeg", ";X-ODYSSEY-KIND=PHOTO"),
        };
        var ics = Vcalendar(
            Vevent("plain", "DTSTART:20260302T100000", "DTEND:20260302T110000", "SUMMARY:Plain"),
            Vevent("linked", "DTSTART:20260303T100000", "DTEND:20260303T110000", "SUMMARY:Linked", attach));

        var response = await PostIcsAsync(client, calendar.CalendarId, ics);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(CalendarAttachmentsException.ImportForbiddenCode, problem.Extensions["code"]?.ToString());
        Assert.Empty(await StoredRowsAsync(factory, calendar.CalendarId));
    }

    // ------------------------------------------------------------------ AC 13: no oracle

    [Fact]
    public async Task An_unresolved_reference_reads_the_same_whether_its_content_is_unknown_or_not_visible()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var bytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var (_, hiddenFileId) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, "beach.jpg", bytes);
        string Reference(Guid id, string hash, long size) =>
            $"ATTACH;VALUE=URI;X-ODYSSEY-SHA256={hash};SIZE={size}:odyssey-file:{id:D}";

        var hidden = await ImportAsync(client, calendar.CalendarId, Vcalendar(Vevent("hidden",
            "DTSTART:20260302T100000", "DTEND:20260302T110000", "SUMMARY:Hidden", Reference(hiddenFileId, Sha256(bytes), bytes.Length))));
        var unknown = await ImportAsync(client, calendar.CalendarId, Vcalendar(Vevent("unknown",
            "DTSTART:20260302T100000", "DTEND:20260302T110000", "SUMMARY:Unknown", Reference(Guid.NewGuid(), new string('b', 64), 9))));

        Assert.Equal(
            hidden.Skipped.Select(s => (s.Reason, s.Count, s.SampleTitles.Count)),
            unknown.Skipped.Select(s => (s.Reason, s.Count, s.SampleTitles.Count)));
        Assert.Equal("Attached content is not in this file or this instance.", Assert.Single(hidden.Skipped).Reason);
    }

    [Theory]
    [InlineData("odyssey-file:not-a-guid")]
    [InlineData("odyssey-file:9b0f9d4e-5c1a-4c7e-9a52-1f5b7d6c0a11;X")]
    public async Task A_malformed_reference_is_skipped_with_its_own_reason(string value)
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);

        var result = await ImportAsync(client, calendar.CalendarId, Vcalendar(Vevent("bad",
            "DTSTART:20260302T100000", "DTEND:20260302T110000", "SUMMARY:Bad",
            $"ATTACH;VALUE=URI;X-ODYSSEY-SHA256=NOTHEX;SIZE=-1:{value}")));

        Assert.Equal(1, result.SkippedAttachmentCount);
        Assert.Equal(CalendarAttachmentImporterReasons.InvalidReference, Assert.Single(result.Skipped).Reason);
    }

    // ------------------------------------------------------------------ AC 14: occurrences

    [Fact]
    public async Task Attachments_on_a_modified_occurrence_or_on_an_event_resolving_to_a_generated_row_are_ignored_and_counted()
    {
        await using var factory = new ApiFactory(WithUploads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var file = await SeedFileAsync(factory, "agenda.txt", AgendaBytes);
        var series = await CreatePatternAsync(client, Weekly(calendar.CalendarId, 3));
        var row = (await SeriesRowsAsync(client, series.RecurrencePatternId))[1];

        var standalone = await ImportAsync(client, calendar.CalendarId, Vcalendar(Vevent(
            $"{row.CalendarEventId}@odyssey.local", "DTSTART:20260209T100000", "DTEND:20260209T110000", "SUMMARY:Moved",
            $"ATTACH:odyssey-file:{file:D}", InlineAttach(AgendaBytes, "agenda.txt", "text/plain"))));
        Assert.Equal((1, 2, 0), (standalone.UpdatedCount, standalone.IgnoredOccurrenceAttachmentCount, standalone.StoredFileCount));

        var masterUid = "series-with-override";
        var withOverride = await ImportAsync(client, calendar.CalendarId, Vcalendar(
            Vevent(masterUid, "DTSTART:20260302T090000", "DTEND:20260302T100000", "SUMMARY:Weekly", "RRULE:FREQ=WEEKLY;COUNT=3"),
            Vevent(masterUid, "RECURRENCE-ID:20260309T090000", "DTSTART:20260309T110000", "DTEND:20260309T120000", "SUMMARY:Moved",
                InlineAttach(AgendaBytes, "agenda.txt", "text/plain"))));
        Assert.Equal((1, 0), (withOverride.IgnoredOccurrenceAttachmentCount, withOverride.StoredFileCount));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var members = await db.CalendarEvents.AsNoTracking().Where(e => e.RecurrencePatternId != null).Select(e => e.CalendarEventId).ToListAsync();
        Assert.False(await db.CalendarEventAttachments.AnyAsync(a => members.Contains(a.CalendarEventId)));
        Assert.False(await db.StoredObjects.AnyAsync(o => o.State == StoredObjectState.Pending)); // nothing reserved, or abandoned
    }

    [Fact]
    public async Task An_attachment_found_only_on_an_ignored_modified_occurrence_still_needs_its_claims()
    {
        await using var factory = new ApiFactory(Calendar);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var uid = "series-claims";

        var response = await PostIcsAsync(client, calendar.CalendarId, Vcalendar(
            Vevent(uid, "DTSTART:20260302T090000", "DTEND:20260302T100000", "SUMMARY:Weekly", "RRULE:FREQ=WEEKLY;COUNT=3"),
            Vevent(uid, "RECURRENCE-ID:20260309T090000", "DTSTART:20260309T110000", "DTEND:20260309T120000", "SUMMARY:Moved",
                $"ATTACH:odyssey-file:{Guid.NewGuid():D}")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ AC 15: bounded parsing

    [Fact]
    public async Task One_event_with_thousands_of_reference_lines_parses_at_most_twice_the_cap()
    {
        await using var factory = new ApiFactory(WithReads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.CalendarEventMaxLinksPerKind, "5");
        var references = Enumerable.Range(0, 5_000).Select(_ => $"ATTACH:odyssey-file:{Guid.NewGuid():D}").ToArray();

        var result = await ImportAsync(client, calendar.CalendarId, Vcalendar(Vevent("many",
            ["DTSTART:20260302T100000", "DTEND:20260302T110000", "SUMMARY:Many", .. references])));

        var dropped = Assert.Single(result.Skipped, s => s.Reason.StartsWith("An event carries more attachment lines", StringComparison.Ordinal));
        Assert.Equal(5_000 - 10, dropped.Count);
        Assert.Equal(10, Assert.Single(result.Skipped, s => s.Reason == "Attached content is not in this file or this instance.").Count);
    }

    // ------------------------------------------------------------------ AC 16: injection

    [Fact]
    public async Task A_line_break_or_quote_in_a_file_name_title_or_caption_round_trips_safely()
    {
        await using var source = new ApiFactory(WithReads);
        using var sourceClient = source.CreateClient();
        var calendar = await CreateCalendarAsync(sourceClient);
        var file = await SeedFileAsync(source, "agenda\".txt", AgendaBytes);
        var photo = await SeedPhotoAsync(source, PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "Line\r\nBREAK:x", "Say \"cheese\"\nnow");
        await CreateEventAsync(sourceClient, Event(calendar.CalendarId) with { AttachmentFileIds = [file], PhotoIds = [photo] });
        var exported = await sourceClient.GetStringAsync($"{CalendarsPath}/{calendar.CalendarId}/ics");

        Assert.DoesNotContain("\r\nBREAK:x", exported, StringComparison.Ordinal);
        Assert.Equal(1, Count(exported, "BEGIN:VEVENT"));

        await using var target = new ApiFactory(WithUploads);
        using var targetClient = target.CreateClient();
        var targetCalendar = await CreateCalendarAsync(targetClient);
        var result = await ImportAsync(targetClient, targetCalendar.CalendarId, exported);

        Assert.Equal((1, 1, 1), (result.ImportedCount, result.StoredFileCount, result.StoredPhotoCount));
        var library = Assert.Single(await PhotosAsync(target));
        // Each CR and LF becomes a space (#515's SafeQuoted), and the quote is dropped: no line or parameter is injected.
        Assert.Equal(("Line  BREAK:x", "Say cheese now"), (library.Title, library.Caption));
    }

    // ------------------------------------------------------------------ AC 17: the file reference guard

    [Fact]
    public async Task Deleting_a_photo_keeps_its_file_while_a_calendar_event_also_links_it()
    {
        await using var factory = new ApiFactory(WithUploads);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client);
        var (photoId, fileId) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, "beach.jpg");
        var created = await CreateEventAsync(client, Event(calendar.CalendarId));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            db.CalendarEventAttachments.Add(new CalendarEventAttachment
            {
                CalendarEventId = created.CalendarEventId, FileId = fileId, CreatedAt = Now.UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        // The guard names the calendar link, so the delete is refused rather than taking the event's file with it.
        var response = await client.DeleteAsync($"/api/photos/{photoId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("calendar event attachment", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(await FilesAsync(factory), f => f.Id == fileId);
    }

    [Fact]
    public async Task The_import_limits_endpoint_publishes_the_calendar_link_cap()
    {
        await using var factory = new ApiFactory(Calendar);
        using var client = factory.CreateClient();
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.CalendarEventMaxLinksPerKind, "7");

        var limits = (await client.GetFromJsonAsync<Odyssey.Dtos.Application.ImportLimitsDto>("/api/import-limits"))!;

        Assert.Equal(7, limits.CalendarEventMaxLinksPerKind);
    }

    // ------------------------------------------------------------------ helpers

    private static DateTime Wall(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);

    private static string Unfold(string text) => text.Replace("\r\n ", string.Empty, StringComparison.Ordinal);

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    private static NewCalendarEvent Event(Guid calendarId) => new()
    {
        CalendarId = calendarId, Title = "Meeting", StartDateTime = Wall(2026, 3, 2, 10), EndDateTime = Wall(2026, 3, 2, 11),
    };

    private static NewRecurrencePattern Weekly(Guid calendarId, int count) => new()
    {
        CalendarId = calendarId, Title = "Standup", StartDateTime = Wall(2026, 2, 2, 9), EndDateTime = Wall(2026, 2, 2, 10),
        Frequency = DtoFrequency.Weekly, DaysOfWeek = DtoDays.Monday, OccurrenceCount = count,
    };

    private static async Task<ExistingCalendar> CreateCalendarAsync(HttpClient client)
    {
        var post = await client.PostAsJsonAsync(CalendarsPath, new NewCalendar { Name = "Work", Color = "#0369A1" });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
    }

    private static async Task<ExistingCalendarEvent> CreateEventAsync(HttpClient client, NewCalendarEvent request)
    {
        var post = await client.PostAsJsonAsync(EventsPath, request);
        Assert.True(post.IsSuccessStatusCode, await post.Content.ReadAsStringAsync());
        return (await post.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
    }

    private static async Task<ExistingCalendarEvent> PutEventAsync(HttpClient client, Guid id, NewCalendarEvent request)
    {
        var put = await client.PutAsJsonAsync($"{EventsPath}/{id}", request);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        return (await put.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
    }

    private static async Task<ExistingCalendarEvent> GetEventAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ExistingCalendarEvent>($"{EventsPath}/{id}"))!;

    private static async Task<ExistingRecurrencePattern> GetPatternAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ExistingRecurrencePattern>($"{PatternsPath}/{id}"))!;

    private static async Task<ExistingRecurrencePattern> CreatePatternAsync(HttpClient client, NewRecurrencePattern pattern)
    {
        var post = await client.PostAsJsonAsync(PatternsPath, pattern);
        Assert.True(post.IsSuccessStatusCode, await post.Content.ReadAsStringAsync());
        return (await post.Content.ReadFromJsonAsync<ExistingRecurrencePattern>())!;
    }

    private static async Task<RecurrencePatternUpdateResult> UpdatePatternAsync(HttpClient client, Guid id, NewRecurrencePattern pattern)
    {
        var put = await client.PutAsJsonAsync($"{PatternsPath}/{id}", pattern);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        return (await put.Content.ReadFromJsonAsync<RecurrencePatternUpdateResult>())!;
    }

    private static async Task<List<ExistingCalendarEvent>> SeriesRowsAsync(HttpClient client, Guid patternId) =>
        (await client.GetFromJsonAsync<PagedResult<ExistingCalendarEvent>>($"{PatternsPath}/{patternId}/events?limit=200"))!
        .Items.OrderBy(r => r.StartDateTime).ToList();

    private static async Task<List<ContextCalendarEvent>> StoredRowsAsync(WebApplicationFactory<Program> factory, Guid calendarId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarId == calendarId).ToListAsync();
    }

    private static async Task<List<CalendarEventAttachment>> CalendarEventLinkRowsAsync(WebApplicationFactory<Program> factory, Guid patternId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var members = await db.CalendarEvents.Where(e => e.RecurrencePatternId == patternId).Select(e => e.CalendarEventId).ToListAsync();
        return await db.CalendarEventAttachments.AsNoTracking().Where(a => members.Contains(a.CalendarEventId)).ToListAsync();
    }

    private static async Task<RecurrencePatternAttachment> SeriesLinkAsync(WebApplicationFactory<Program> factory, Guid patternId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().RecurrencePatternAttachments.AsNoTracking()
            .SingleAsync(a => a.RecurrencePatternId == patternId);
    }

    // Test seeding writes the link table directly: the single-writer lint covers production code only.
    private static async Task LinkSeriesFileAsync(WebApplicationFactory<Program> factory, Guid patternId, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        db.RecurrencePatternAttachments.Add(new RecurrencePatternAttachment
        {
            RecurrencePatternId = patternId, FileId = fileId, CreatedAt = Now.UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    // A series written before #519 (un-keyed), so every export flattens it to one VEVENT per row.
    private static async Task<Guid> SeedLegacySeriesAsync(WebApplicationFactory<Program> factory, Guid calendarId, int rows)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var pattern = new ContextRecurrencePattern
        {
            RecurrencePatternId = Guid.NewGuid(), CalendarId = calendarId, Title = "Legacy",
            StartDateTime = Wall(2026, 2, 2, 9), EndDateTime = Wall(2026, 2, 2, 10),
            Frequency = Odyssey.Context.RecurrenceFrequency.Weekly, DaysOfWeek = Odyssey.Context.DaysOfWeekFlags.Monday,
            OccurrenceCount = rows, CreatedAt = Now.UtcDateTime, UpdatedAt = Now.UtcDateTime, Version = Guid.NewGuid(),
        };
        db.RecurrencePatterns.Add(pattern);
        for (var i = 0; i < rows; i++)
        {
            var start = Wall(2026, 2, 2, 9).AddDays(7 * i);
            db.CalendarEvents.Add(new ContextCalendarEvent
            {
                CalendarId = calendarId, Title = "Legacy", StartDateTime = start, EndDateTime = start.AddHours(1),
                RecurrencePatternId = pattern.RecurrencePatternId, CreatedAt = Now.UtcDateTime, UpdatedAt = Now.UtcDateTime,
            });
        }

        await db.SaveChangesAsync();
        return pattern.RecurrencePatternId;
    }

    private static async Task<Guid> SeedFileAsync(WebApplicationFactory<Program> factory, string fileName, byte[] content)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = content };
        var id = Guid.NewGuid();
        db.FileBlob.Add(blob);
        db.FileMetadata.Add(new FileMetadata
        {
            Id = id, UploadedByUserId = ActorUserId, FileName = fileName, ContentType = "text/plain", SizeBytes = content.Length,
            Sha256Hash = Sha256(content), UploadedAtUtc = Now.UtcDateTime, FileBlobId = blob.Id, FileBlob = blob,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static Task<Guid> SeedPhotoAsync(WebApplicationFactory<Program> factory, string? title = null) =>
        SeedPhotoAsync(factory, PhotoTestData.Jpeg(PhotoTestData.NextSeed()), title, null);

    private static async Task<Guid> SeedPhotoAsync(WebApplicationFactory<Program> factory, byte[] bytes, string? title, string? caption)
    {
        var (photoId, _) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, "beach.jpg", bytes);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var photo = await db.Photos.SingleAsync(p => p.PhotoId == photoId);
        photo.Title = title;
        photo.Caption = caption;
        await db.SaveChangesAsync();
        return photoId;
    }

    private static async Task<List<FileMetadata>> FilesAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().FileMetadata.AsNoTracking().ToListAsync();
    }

    private static async Task<List<Photo>> PhotosAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Photos.AsNoTracking().ToListAsync();
    }

    private static string InlineAttach(byte[] content, string fileName, string contentType, string? extraParams = null)
    {
        var base64 = Convert.ToBase64String(content);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 59) / 60)
            .Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - (i * 60)))));
        return $"ATTACH;FMTTYPE={contentType};ENCODING=BASE64;VALUE=BINARY;FILENAME=\"{fileName}\"{extraParams}:{folded}";
    }

    private static string Vevent(string uid, params string[] lines) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    private static string Vcalendar(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private static async Task<IcsImportResult> ImportAsync(HttpClient client, Guid calendarId, string ics)
    {
        var response = await PostIcsAsync(client, calendarId, ics);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<IcsImportResult>())!;
    }

    private static async Task<HttpResponseMessage> PostIcsAsync(HttpClient client, Guid calendarId, string ics)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
        content.Add(file, "file", "calendar.ics");
        return await client.PostAsync($"{CalendarsPath}/{calendarId}/ics", content);
    }

    private static class CalendarAttachmentImporterReasons
    {
        public const string InvalidReference = "An attachment reference is malformed.";
    }

    private sealed class ApiFactory(IReadOnlyCollection<string> permissions, OdysseyApiFactory? sharingStoreWith = null)
        : OdysseyApiFactory(permissions, ActorUserId, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        }, sharingStoreWith: sharingStoreWith);
}
