using System.Net;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// Issue #430 AC 19: a full 50-id weather batch — the longest query string the client ever sends — passes through
/// the stack's reverse proxy and reaches the API. Read-only; unknown ids are omitted, so random GUIDs suffice.
/// </summary>
[Collection(ApiStackCollection.Name)]
public class WeatherBatchPassThroughTests(ApiStackFixture fixture)
{
    private static readonly DemoUser Admin = DemoUsers.All.First(user => user.Role == "Admin");

    [SkippableTheory]
    [InlineData("/api/journal-entries/weather")]
    [InlineData("/api/calendar-events/weather")]
    public async Task A_fifty_id_batch_passes_through_and_answers_200(string path)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var client = await fixture.CreateAuthenticatedClientAsync(Admin.Email, Admin.Password);
        var query = string.Join("&", Enumerable.Range(0, 50).Select(_ => $"ids={Guid.NewGuid():D}"));
        var response = await client.GetAsync($"{path}?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl is { Private: true, NoStore: true });
    }
}
