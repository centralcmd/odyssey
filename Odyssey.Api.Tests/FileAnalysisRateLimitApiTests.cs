using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #433 §5: with the vocabulary cap retired, per-user limits on the two calls that reach the
/// processor bound third-party spend. The limiter runs before the action, so even a request the action
/// would refuse spends a permit — which is what lets this test reach the limit without a real document.
/// </summary>
public class FileAnalysisRateLimitApiTests
{
    [Fact]
    public async Task TheEleventhAnalyzeInAnHour_Is429_WithRetryAfter()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileAnalysisCreate]);
        using var client = factory.CreateClient();
        var path = $"/api/accounts/{Guid.NewGuid()}/files/{Guid.NewGuid()}/analyze";

        for (var i = 0; i < 10; i++)
        {
            var allowed = await client.PostAsJsonAsync(path, new { consentAcknowledged = true });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var refused = await client.PostAsJsonAsync(path, new { consentAcknowledged = true });

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
    }

    [Fact]
    public async Task TheThirtyFirstMatchInAnHour_Is429()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileAnalysisCreate]);
        using var client = factory.CreateClient();
        var path = $"/api/file-analysis/{Guid.NewGuid()}/match";

        for (var i = 0; i < 30; i++)
        {
            var allowed = await client.PostAsync(path, null);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var refused = await client.PostAsync(path, null);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    /// <summary>
    /// The limits are per user and per call: exhausting one user's match budget leaves another user's
    /// match and the same user's analyze untouched (senior-tester review on PR #440).
    /// </summary>
    [Fact]
    public async Task TheMatchLimit_IsPerUser_AndSeparateFromAnalyze()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileAnalysisCreate]);
        using var client = factory.CreateClient();
        var match = $"/api/file-analysis/{Guid.NewGuid()}/match";

        for (var i = 0; i < 31; i++)
        {
            await client.PostAsync(match, null);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync(match, null)).StatusCode);

        using var other = new HttpRequestMessage(HttpMethod.Post, match);
        other.Headers.Add(TestAuthHandler.ActorOverrideHeader, "another-user");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(other)).StatusCode);

        var analyze = await client.PostAsJsonAsync(
            $"/api/accounts/{Guid.NewGuid()}/files/{Guid.NewGuid()}/analyze", new { consentAcknowledged = true });
        Assert.NotEqual(HttpStatusCode.TooManyRequests, analyze.StatusCode);
    }
}
