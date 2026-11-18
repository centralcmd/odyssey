using System.Net;
using System.Net.Http.Json;
using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// A calendar export over its byte cap, over real HTTP and through whatever sits in front of the API at
/// <c>E2E_BASE_URL</c> — the Compose client's nginx, whose <c>.ics</c> location streams unbuffered (issue #516,
/// implementation note 9). The API aborts the connection once the cap is passed mid-stream; the assertion is that the
/// client sees an incomplete transfer, never a complete <c>200</c> body that a browser would save as a whole file.
/// </summary>
/// <remarks>
/// It mutates the shared stack and puts everything back: the export size setting is restored and the calendar it
/// creates is deleted, in a <c>finally</c>.
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class CalendarIcsExportAbortTests(ApiStackFixture fixture)
{
    [SkippableFact]
    public async Task An_export_over_the_byte_cap_reaches_the_client_as_an_incomplete_transfer()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var admin = DemoUsers.All.Single(u => u.Role == RoleDefinitions.Admin);
        var client = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);

        var settings = (await client.GetFromJsonAsync<SystemSettingsDto>("/api/system-settings"))!;
        var calendar = (await (await fixture.PostWithAntiforgeryAsync(client, "/api/calendars",
            new NewCalendar { Name = $"E2E export abort {Guid.NewGuid():N}", Color = "#0369A1" }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ExistingCalendar>())!;
        try
        {
            (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { CalendarIcsMaxExportMegabytes = 1 }))
                .EnsureSuccessStatusCode();

            // 600 occurrences carrying a 2,000-character description, flattened by one edited occurrence: ~1.3 MB.
            var start = new DateTime(2031, 1, 1, 9, 0, 0, DateTimeKind.Utc);
            var series = (await (await fixture.PostWithAntiforgeryAsync(client, "/api/recurrence-patterns", new NewRecurrencePattern
            {
                CalendarId = calendar.CalendarId, Title = "Bulky", Description = new string('x', 2000), StartDateTime = start,
                EndDateTime = start.AddHours(1), Frequency = RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 600,
            })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ExistingRecurrencePattern>())!;
            var first = (await client.GetFromJsonAsync<PagedResult<ExistingCalendarEvent>>(
                $"/api/recurrence-patterns/{series.RecurrencePatternId}/events?limit=1"))!.Items.Single();
            (await client.PutAsJsonAsync($"/api/calendar-events/{first.CalendarEventId}", new NewCalendarEvent
            {
                CalendarId = calendar.CalendarId, Title = "Edited", StartDateTime = first.StartDateTime, EndDateTime = first.EndDateTime,
            })).EnsureSuccessStatusCode();

            var summary = await client.GetAsync($"/api/recurrence-patterns/{series.RecurrencePatternId}/ics/summary");
            Assert.Equal(HttpStatusCode.OK, summary.StatusCode);

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var response = await client.GetAsync(
                    $"/api/recurrence-patterns/{series.RecurrencePatternId}/ics", HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await response.Content.ReadAsByteArrayAsync();
            });
        }
        finally
        {
            await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
            {
                CalendarIcsMaxExportMegabytes = settings.CalendarIcsMaxExportMegabytes,
            });
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/calendars/{calendar.CalendarId}");
        }
    }
}
