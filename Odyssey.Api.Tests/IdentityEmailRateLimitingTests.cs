using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// <c>/forgotPassword</c> and <c>/resendConfirmationEmail</c> send mail on every call, so they carry
/// a second, far tighter per-IP window on top of the group policy (issue #393). These tests pin that
/// the tighter limit bites, that it is confined to those two routes, and — the property that
/// constrains the whole design — that a throttled response says nothing about whether the address is
/// registered.
/// </summary>
public class IdentityEmailRateLimitingTests
{
    private const string MailRejectionMessage =
        "Too many requests. Please wait a few minutes before requesting another email.";

    private static OdysseyApiFactory FactoryWithMailLimit(int permitLimit, int identityPermitLimit = 1000) =>
        new(permissions: [], configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:IdentityEmail:PermitLimit"] = permitLimit.ToString(),
            ["RateLimiting:IdentityEmail:WindowSeconds"] = "900",
            // Deliberately generous, so a rejection can only have come from the mail policy.
            ["RateLimiting:Identity:PermitLimit"] = identityPermitLimit.ToString(),
            ["RateLimiting:Identity:WindowSeconds"] = "60",
        });

    private static Task<HttpResponseMessage> ForgotPasswordAsync(HttpClient client, string email = "nobody@example.com") =>
        client.PostAsJsonAsync("/forgotPassword", new { email });

    private static Task<HttpResponseMessage> ResendConfirmationAsync(HttpClient client) =>
        client.PostAsJsonAsync("/resendConfirmationEmail", new { email = "nobody@example.com" });

    [Fact]
    public async Task ForgotPasswordBeyondTheMailLimit_IsThrottled()
    {
        using var factory = FactoryWithMailLimit(permitLimit: 3);
        var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var allowed = await ForgotPasswordAsync(client);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var throttled = await ForgotPasswordAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task ResendConfirmationBeyondTheMailLimit_IsThrottled()
    {
        using var factory = FactoryWithMailLimit(permitLimit: 3);
        var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var allowed = await ResendConfirmationAsync(client);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var throttled = await ResendConfirmationAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task AThrottledMailRequest_AnswersWithProblemDetailsRetryAfterAndEmailWording()
    {
        using var factory = FactoryWithMailLimit(permitLimit: 1);
        var client = factory.CreateClient();

        await ForgotPasswordAsync(client);
        var throttled = await ForgotPasswordAsync(client);

        Assert.Equal("application/problem+json", throttled.Content.Headers.ContentType?.MediaType);
        Assert.True(throttled.Headers.TryGetValues("Retry-After", out _));

        var problem = JsonSerializer.Deserialize<ProblemDetails>(
            await throttled.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.Status);
        // The mail-specific wording, not the group policy's "wait a moment" — and it must stay free
        // of any hint about whether the address is registered.
        Assert.Equal(MailRejectionMessage, problem.Detail);
    }

    [Fact]
    public async Task TheTighterLimit_DoesNotApplyToLogin()
    {
        // One mail request allowed; the group policy is left generous. If /login shared the mail
        // bucket it would 429 on the second attempt.
        using var factory = FactoryWithMailLimit(permitLimit: 1);
        var client = factory.CreateClient();

        await ForgotPasswordAsync(client);
        await ForgotPasswordAsync(client);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/login", new { email = "nobody@example.com", password = "wrong-password" });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheGroupPolicy_StillCoversTheMailEndpoints()
    {
        // The mail endpoints are subject to BOTH limits, not just the tighter-by-default mail one:
        // with the mail limit left generous the group policy is the binding one. This is the
        // property that a second named policy would have quietly broken (a named policy replaces the
        // group's rather than adding to it), which is why the mail window rides on the global limiter.
        using var factory = FactoryWithMailLimit(permitLimit: 1000, identityPermitLimit: 2);
        var client = factory.CreateClient();

        await ForgotPasswordAsync(client);
        await ForgotPasswordAsync(client);
        var throttled = await ForgotPasswordAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task TheThrottledResponse_IsIdenticalForRegisteredAndUnregisteredAddresses()
    {
        const string registered = "registered@example.com";
        const string unregistered = "nobody@example.com";

        var forRegistered = await ThrottleAndCaptureAsync(registered, seedUser: true);
        var forUnregistered = await ThrottleAndCaptureAsync(unregistered, seedUser: false);

        Assert.Equal(HttpStatusCode.TooManyRequests, forRegistered.Status);
        Assert.Equal(forUnregistered.Status, forRegistered.Status);
        // Guards the header assertion below from passing vacuously if the capture ever stops
        // collecting anything.
        Assert.NotEmpty(forRegistered.Headers);
        Assert.Equal(forUnregistered.Body, forRegistered.Body);
        // Headers too, not just status and body: a difference confined to a header would leak
        // account existence just as effectively, and asserting only the body would not catch it.
        Assert.Equal(forUnregistered.Headers, forRegistered.Headers);
    }

    [Fact]
    public async Task TheLimit_IsReadFromConfigurationPerRequest()
    {
        // Same code path, two different configured ceilings — the limiter must honour whichever the
        // deployment supplied rather than a value captured at startup.
        using var strict = FactoryWithMailLimit(permitLimit: 1);
        var strictClient = strict.CreateClient();
        await ForgotPasswordAsync(strictClient);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ForgotPasswordAsync(strictClient)).StatusCode);

        using var relaxed = FactoryWithMailLimit(permitLimit: 6);
        var relaxedClient = relaxed.CreateClient();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await ForgotPasswordAsync(relaxedClient)).StatusCode);
        }
    }

    /// <summary>
    /// Burns the single permitted request against <paramref name="email"/>, then returns the status,
    /// headers and body of the request that gets rejected.
    /// </summary>
    /// <remarks>
    /// <c>Date</c> and <c>Retry-After</c> are dropped from the captured headers: both are timing
    /// artefacts that differ between two runs of the same request, so comparing them would make the
    /// test flaky without saying anything about enumeration. Every other header is compared verbatim.
    /// </remarks>
    private static async Task<ThrottledResponse> ThrottleAndCaptureAsync(string email, bool seedUser)
    {
        using var factory = FactoryWithMailLimit(permitLimit: 1);
        var client = factory.CreateClient();

        if (seedUser)
        {
            using var scope = factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.Users.Add(new ApplicationUser
            {
                Id = Guid.NewGuid().ToString(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
            });
            await context.SaveChangesAsync();
        }

        await ForgotPasswordAsync(client, email);
        var throttled = await ForgotPasswordAsync(client, email);

        return new ThrottledResponse(
            throttled.StatusCode,
            CaptureHeaders(throttled),
            await throttled.Content.ReadAsStringAsync());
    }

    private static IReadOnlyList<string> CaptureHeaders(HttpResponseMessage response) =>
        response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Retry-After"))
            .Select(header => $"{header.Key}: {string.Join(",", header.Value)}")
            .Order(StringComparer.Ordinal)
            .ToList();

    private sealed record ThrottledResponse(
        HttpStatusCode Status, IReadOnlyList<string> Headers, string Body);
}
