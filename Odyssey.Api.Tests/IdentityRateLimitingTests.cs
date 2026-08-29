using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The anonymous Identity endpoints are the app's only unauthenticated write surface, so they carry a
/// per-IP fixed-window limiter (issue #382). These tests pin that the limit actually bites, that it
/// answers in the same RFC 7807 shape as every other error path, and that it is scoped to the Identity
/// group rather than the whole API.
/// </summary>
public class IdentityRateLimitingTests
{
    private static OdysseyApiFactory FactoryWithLimit(int permitLimit) =>
        new(permissions: [], configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:Identity:PermitLimit"] = permitLimit.ToString(),
            ["RateLimiting:Identity:WindowSeconds"] = "60",
        });

    private static Task<HttpResponseMessage> AttemptLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/login", new { email = "nobody@example.com", password = "wrong-password" });

    [Fact]
    public async Task LoginAttemptsBeyondTheWindowLimit_AreThrottled()
    {
        using var factory = FactoryWithLimit(permitLimit: 3);
        var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var allowed = await AttemptLoginAsync(client);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var throttled = await AttemptLoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task AThrottledRequest_AnswersWithProblemDetailsAndRetryAfter()
    {
        using var factory = FactoryWithLimit(permitLimit: 1);
        var client = factory.CreateClient();

        await AttemptLoginAsync(client);
        var throttled = await AttemptLoginAsync(client);

        Assert.Equal("application/problem+json", throttled.Content.Headers.ContentType?.MediaType);
        Assert.True(throttled.Headers.TryGetValues("Retry-After", out _));

        var problem = JsonSerializer.Deserialize<ProblemDetails>(
            await throttled.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.Status);
        // Curated wording — never a hint about whether the account exists.
        Assert.Equal("Too many requests. Please wait a moment before trying again.", problem.Detail);
    }

    [Fact]
    public async Task TheLimitIsScopedToTheIdentityGroup_NotTheWholeApi()
    {
        using var factory = FactoryWithLimit(permitLimit: 1);
        var client = factory.CreateClient();

        // Burn the identity budget, then confirm an ordinary controller endpoint is unaffected. It 403s
        // (the factory grants no permissions) — the point is only that it never 429s.
        await AttemptLoginAsync(client);
        await AttemptLoginAsync(client);

        for (var request = 1; request <= 5; request++)
        {
            var response = await client.GetAsync("/api/accounts");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }
}
