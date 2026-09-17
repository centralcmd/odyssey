using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// AC 23 and AC 24 — the three profile-picture rate-limit policies (issue #94 §10.12).
///
/// <para>
/// <b>Isolation strategy, stated rather than assumed.</b> Rate limiters are process-wide singletons
/// keyed by partition, and a fixed window does not reset between tests — so these tests do not share
/// an actor id. Each gets its own, which makes its partition key unique and its budget its own. The
/// alternative (waiting a window out, or exhausting a shared one) would make the class order-dependent
/// and leave later tests throttled for reasons they never caused.
/// </para>
/// </summary>
public class UserProfileImageRateLimitTests
{
    private static readonly string[] ImageReader = [PermissionClaims.ProfileImagesRead];

    /// <summary>
    /// AC 23. <b>The delete has its own budget.</b> Sharing one with the upload would mean a user who
    /// has exhausted it uploading cannot remove their picture — throttling the GDPR Art. 17
    /// self-service control itself, which is not an acceptable failure mode.
    /// </summary>
    [Fact]
    public async Task A_user_throttled_out_of_uploading_can_still_delete_their_picture()
    {
        const string actor = "rate-split-actor";

        await using var factory = new ApiFactory(
            ImageReader,
            actor,
            new Dictionary<string, string?>
            {
                ["RateLimiting:ProfileImageWrite:PermitLimit"] = "1",
                ["RateLimiting:ProfileImageWrite:WindowSeconds"] = "3600",
                ["RateLimiting:ProfileImageDelete:PermitLimit"] = "5",
                ["RateLimiting:ProfileImageDelete:WindowSeconds"] = "3600",
            });

        using var client = factory.CreateClient();
        await SeedEnabledUserAsync(factory, actor);

        // The one permitted upload, which leaves a picture to erase.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client)).StatusCode);

        // The write budget is now spent.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client)).StatusCode);

        // ...and the erasure control is still reachable. This is the whole point of the split.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/profile/image")).StatusCode);
    }

    /// <summary>
    /// AC 24. Exceeding a window is a <c>429</c>, and the rejection happens in the middleware before
    /// the action runs — so the over-limit call writes nothing.
    /// </summary>
    [Fact]
    public async Task Exceeding_the_write_window_is_a_429_and_the_over_limit_call_writes_nothing()
    {
        const string actor = "rate-write-actor";

        await using var factory = new ApiFactory(
            ImageReader,
            actor,
            new Dictionary<string, string?>
            {
                ["RateLimiting:ProfileImageWrite:PermitLimit"] = "2",
                ["RateLimiting:ProfileImageWrite:WindowSeconds"] = "3600",
            });

        using var client = factory.CreateClient();
        await SeedEnabledUserAsync(factory, actor);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, png: true)).StatusCode);

        var refused = await PostAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // The row still holds the SECOND upload: the third never reached the action.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(context.UserProfileImages, image => image.UserId == actor);
        Assert.Equal("image/png", row.ContentType);
    }

    [Fact]
    public async Task Exceeding_the_delete_window_is_a_429()
    {
        const string actor = "rate-delete-actor";

        await using var factory = new ApiFactory(
            ImageReader,
            actor,
            new Dictionary<string, string?>
            {
                ["RateLimiting:ProfileImageDelete:PermitLimit"] = "1",
                ["RateLimiting:ProfileImageDelete:WindowSeconds"] = "3600",
            });

        using var client = factory.CreateClient();
        await SeedEnabledUserAsync(factory, actor);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/profile/image")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.DeleteAsync("/api/profile/image")).StatusCode);
    }

    /// <summary>
    /// The read budget is a third, separate one — and a generous one, because a <c>/users</c> page at
    /// maximum size on a cold cache issues one conditional request per renderable row. It is asserted
    /// to be separate here rather than to a particular size: what matters is that spending the WRITE
    /// budget cannot blank an admin's user list.
    /// </summary>
    [Fact]
    public async Task The_read_budget_is_separate_from_the_write_budget()
    {
        const string actor = "rate-read-actor";

        await using var factory = new ApiFactory(
            ImageReader,
            actor,
            new Dictionary<string, string?>
            {
                ["RateLimiting:ProfileImageWrite:PermitLimit"] = "1",
                ["RateLimiting:ProfileImageWrite:WindowSeconds"] = "3600",
                ["RateLimiting:ProfileImageRead:PermitLimit"] = "50",
                ["RateLimiting:ProfileImageRead:WindowSeconds"] = "3600",
            });

        using var client = factory.CreateClient();
        await SeedEnabledUserAsync(factory, actor);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client)).StatusCode);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.GetAsync($"/api/profile-images/{actor}")).StatusCode);
        }
    }

    [Fact]
    public async Task Exceeding_the_read_window_is_a_429()
    {
        const string actor = "rate-read-cap-actor";

        await using var factory = new ApiFactory(
            ImageReader,
            actor,
            new Dictionary<string, string?>
            {
                ["RateLimiting:ProfileImageRead:PermitLimit"] = "3",
                ["RateLimiting:ProfileImageRead:WindowSeconds"] = "3600",
            });

        using var client = factory.CreateClient();
        await SeedEnabledUserAsync(factory, actor);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client)).StatusCode);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/profile-images/{actor}")).StatusCode);
        }

        // It is bounded — and to the user it fails SILENTLY: a 429 on an <img> reaches the error
        // handler, which swaps to the monogram and surfaces nothing. That is why the shipped default is
        // generous and why a rejected read has to be observable server-side instead.
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.GetAsync($"/api/profile-images/{actor}")).StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, bool png = false)
    {
        var content = new MultipartFormDataContent();
        var bytes = png ? ContactImageFixtures.BaselinePng() : ContactImageFixtures.BaselineJpeg();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(png ? "image/png" : "image/jpeg");
        content.Add(part, "file", "picture");
        return client.PostAsync("/api/profile/image", content);
    }

    /// <summary>
    /// Seeds the identity row and clears its lockout in a second save — <c>OdysseyContext</c> stamps
    /// the admin-approval sentinel onto every newly-added user, and a disabled subject's picture is a
    /// <c>404</c> by design.
    /// </summary>
    private static async Task SeedEnabledUserAsync(ApiFactory factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var user = new ApplicationUser
        {
            Id = userId,
            UserName = $"{userId}@example.com",
            NormalizedUserName = $"{userId}@EXAMPLE.COM",
            Email = $"{userId}@example.com",
            NormalizedEmail = $"{userId}@EXAMPLE.COM",
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        user.LockoutEnd = null;
        await context.SaveChangesAsync();
    }

    private sealed class ApiFactory(
        IReadOnlyCollection<string>? permissions,
        string actorUserId,
        IReadOnlyDictionary<string, string?>? configuration = null)
        : OdysseyApiFactory(permissions, actorUserId, configuration);
}
