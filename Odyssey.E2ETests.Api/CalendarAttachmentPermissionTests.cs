using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The calendar attachment claim matrix over real HTTP, against the seeded role users (issue #522 AC 10, #521 v4).
/// </summary>
/// <remarks>
/// <para>
/// The seeded roles reach two of the matrix's states only. Admin, Owner and User hold the whole calendar module with
/// <c>files.read</c>/<c>.create</c> and <c>photos.read</c>/<c>.create</c>, so each links a file and a photo to an event,
/// reads them hydrated, exports them inline and re-imports the export with every piece matched rather than stored again.
/// Guest holds no <c>calendar.*</c> claim at all (#522 v4 C1), so every calendar surface is a <c>403</c> for it. The
/// no-read-claim states (hidden but kept, count only, export <c>403</c>) belong to no seeded role and are covered with a
/// custom principal in <c>Odyssey.Api.Tests</c> and with a fake one in <c>Odyssey.Client.Tests</c>.
/// </para>
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class CalendarAttachmentPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserFor(string role) => DemoUsers.All.First(user => user.Role == role);

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Owner")]
    [InlineData("User")]
    public async Task A_role_with_the_calendar_file_and_photo_claims_links_exports_and_reimports_attachments(string role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var actor = UserFor(role);
        var client = await fixture.CreateAuthenticatedClientAsync(actor.Email, actor.Password);
        var file = (await client.GetFromJsonAsync<PagedResult<FileListItem>>("/api/files?limit=1"))!.Items.FirstOrDefault();
        var photo = (await client.GetFromJsonAsync<PagedResult<PhotoSummary>>("/api/photos?limit=1"))!.Items.FirstOrDefault();
        Skip.If(file is null || photo is null, "The seeded stack has no visible file or photo to link.");

        var calendar = (await (await fixture.PostWithAntiforgeryAsync(client, "/api/calendars",
            new NewCalendar { Name = $"E2E attachments {role} {Guid.NewGuid():N}", Color = "#0369A1" }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ExistingCalendar>())!;
        Guid? eventId = null;
        try
        {
            var start = new DateTime(2031, 3, 2, 10, 0, 0, DateTimeKind.Utc);
            var created = await fixture.PostWithAntiforgeryAsync(client, "/api/calendar-events", new NewCalendarEvent
            {
                CalendarId = calendar.CalendarId, Title = "Lease signing", StartDateTime = start, EndDateTime = start.AddHours(1),
                AttachmentFileIds = [file!.Id], PhotoIds = [photo!.PhotoId],
            });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            eventId = (await created.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!.CalendarEventId;

            // The detail read is hydrated for a holder of both read claims.
            var detail = (await client.GetFromJsonAsync<ExistingCalendarEvent>($"/api/calendar-events/{eventId}"))!;
            Assert.Equal((1, 1), (detail.AttachmentCount, detail.PhotoCount));
            Assert.Equal(file.Id, Assert.Single(detail.Attachments!).FileId);
            Assert.NotNull(detail.Attachments![0].FileName);
            Assert.NotNull(Assert.Single(detail.Photos!).FileName);

            // A rename with null arrays keeps both kinds (#521 §5.1).
            var renamed = await client.PutAsJsonAsync($"/api/calendar-events/{eventId}", new NewCalendarEvent
            {
                CalendarId = calendar.CalendarId, Title = "Lease signing (moved)", StartDateTime = start, EndDateTime = start.AddHours(1),
            });
            Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync());
            var kept = (await client.GetFromJsonAsync<ExistingCalendarEvent>($"/api/calendar-events/{eventId}"))!;
            Assert.Equal((1, 1), (kept.AttachmentCount, kept.PhotoCount));

            // The export summary counts them, and the export carries them inline.
            var summary = (await client.GetFromJsonAsync<CalendarIcsExportSummary>($"/api/calendar-events/{eventId}/ics/summary"))!;
            Assert.Equal((1, 1), (summary.AttachmentCount, summary.PhotoCount));
            var ics = await client.GetByteArrayAsync($"/api/calendar-events/{eventId}/ics");
            Assert.Contains("ATTACH", System.Text.Encoding.UTF8.GetString(ics), StringComparison.Ordinal);
            var omitted = await client.GetByteArrayAsync($"/api/calendar-events/{eventId}/ics?attachments=omit");
            Assert.DoesNotContain("ATTACH", System.Text.Encoding.UTF8.GetString(omitted), StringComparison.Ordinal);

            // Re-importing the export matches every piece to what is stored, so nothing is stored twice.
            using var form = new MultipartFormDataContent();
            var body = new ByteArrayContent(ics);
            body.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
            form.Add(body, "file", "event.ics");
            var imported = await fixture.PostContentWithAntiforgeryAsync(client, $"/api/calendars/{calendar.CalendarId}/ics", form);
            Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());
            var result = (await imported.Content.ReadFromJsonAsync<IcsImportResult>())!;
            Assert.Equal((0, 0), (result.StoredFileCount, result.StoredPhotoCount));
            Assert.Equal((1, 1), (result.ReusedFileCount, result.ReusedPhotoCount));
            Assert.Equal(0, result.SkippedAttachmentCount);
        }
        finally
        {
            // A calendar holding events refuses deletion (409), so the event goes first.
            if (eventId is { } id)
                await fixture.DeleteWithAntiforgeryAsync(client, $"/api/calendar-events/{id}");
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/calendars/{calendar.CalendarId}");
        }
    }

    [SkippableFact]
    public async Task Guest_holds_no_calendar_claim_so_every_attachment_surface_is_forbidden()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var guest = UserFor("Guest");
        var client = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);
        var anyId = Guid.NewGuid();
        var start = new DateTime(2031, 3, 2, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/calendars")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostWithAntiforgeryAsync(client, "/api/calendar-events", new NewCalendarEvent
        {
            CalendarId = anyId, Title = "Nope", StartDateTime = start, EndDateTime = start.AddHours(1), AttachmentFileIds = [anyId],
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/calendar-events/{anyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/calendar-events/{anyId}/ics/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/calendar-events/{anyId}/ics?attachments=omit")).StatusCode);

        using var form = new MultipartFormDataContent();
        var body = new ByteArrayContent("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"u8.ToArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
        form.Add(body, "file", "empty.ics");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await fixture.PostContentWithAntiforgeryAsync(client, $"/api/calendars/{anyId}/ics", form)).StatusCode);
    }
}
