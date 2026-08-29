using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The per-actor limiter on <c>POST /api/account/password</c> (issue #406 §7). This is the one endpoint a
/// password-gated session can write to — deliberately, because it is the way out — and the password it
/// verifies may be the compromised one that caused the reset. Identity's lockout accounting is the primary
/// control; this bounds the slow drip that waits out each lockout window and tries again.
/// </summary>
/// <remarks>
/// It needs its own fixture because the two controls overlap and lockout is much tighter by default. The
/// gate tests set <c>PermitLimit</c> effectively to infinity so a lockout run isn't cut short by a 429;
/// these set it low and keep the password <em>correct</em>, so the only thing that can reject the later
/// calls is the limiter.
/// </remarks>
public class PasswordChangeRateLimitTests
{
    private const string Email = "throttled@example.com";

    [Fact]
    public async Task ExceedingThePerActorLimit_Is429()
    {
        // Correct current password every time and a fresh new one each call, so nothing here is refused
        // for being wrong, being a no-op, or tripping lockout — only the limiter can produce the 429.
        await using var factory = Limited(permitLimit: 2);
        await factory.CreateUserAsync(Email);
        using var client = await factory.LoginAsync(Email);

        var first = await ChangeAsync(client, PasswordGateFactory.Password, "Renewed111!Passphrase");
        var second = await ChangeAsync(client, "Renewed111!Passphrase", "Renewed222!Passphrase");
        var third = await ChangeAsync(client, "Renewed222!Passphrase", "Renewed333!Passphrase");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task TheRejectedCall_ChangesNothing()
    {
        // The limiter runs in middleware, before the action — so the over-limit call must not have
        // reached the password hasher at all. The previous password still signs in, which it would not
        // if the third change had been applied.
        await using var factory = Limited(permitLimit: 1);
        await factory.CreateUserAsync(Email);
        using var client = await factory.LoginAsync(Email);

        var allowed = await ChangeAsync(client, PasswordGateFactory.Password, "Renewed111!Passphrase");
        var rejected = await ChangeAsync(client, "Renewed111!Passphrase", "Renewed222!Passphrase");

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Odyssey.Context.ApplicationUser>>();
        var user = (await users.FindByEmailAsync(Email))!;
        Assert.True(await users.CheckPasswordAsync(user, "Renewed111!Passphrase"));
        Assert.False(await users.CheckPasswordAsync(user, "Renewed222!Passphrase"));
    }

    [Fact]
    public async Task TheRejection_IsTheSharedProblemDetailsShape()
    {
        // IdentityRateLimiting owns OnRejected for every policy in the app, so this endpoint inherits the
        // RFC 7807 body and Retry-After rather than emitting a bare 429 — the contract every other client
        // failure path already parses.
        await using var factory = Limited(permitLimit: 1);
        await factory.CreateUserAsync(Email);
        using var client = await factory.LoginAsync(Email);

        await ChangeAsync(client, PasswordGateFactory.Password, "Renewed111!Passphrase");
        var rejected = await ChangeAsync(client, "Renewed111!Passphrase", "Renewed222!Passphrase");

        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    [Fact]
    public async Task TheLimitIsPerActor_NotGlobal()
    {
        // Partitioned on the caller's NameIdentifier: one user exhausting their budget must not lock
        // every other user out of changing their own password, which a global limiter would do.
        await using var factory = Limited(permitLimit: 1);
        await factory.CreateUserAsync(Email);
        await factory.CreateUserAsync("other@example.com");
        using var noisy = await factory.LoginAsync(Email);
        using var quiet = await factory.LoginAsync("other@example.com");

        await ChangeAsync(noisy, PasswordGateFactory.Password, "Renewed111!Passphrase");
        var noisyRejected = await ChangeAsync(noisy, "Renewed111!Passphrase", "Renewed222!Passphrase");
        var otherUser = await ChangeAsync(quiet, PasswordGateFactory.Password, "Renewed333!Passphrase");

        Assert.Equal(HttpStatusCode.TooManyRequests, noisyRejected.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, otherUser.StatusCode);
    }

    [Fact]
    public async Task AZeroPermitLimit_FailsAtStartup()
    {
        // A misconfigured security control should fail at startup, not on the first request that needed
        // limiting — the posture every other rate-limit options class in the app takes
        // (ValidateDataAnnotations + ValidateOnStart). A silently-accepted 0 would be the worst outcome
        // of the three: it reads as "no limit" and behaves as "reject everything".
        await using var factory = Limited(permitLimit: 0);

        var exception = Assert.ThrowsAny<Exception>(
            () => factory.Services.GetRequiredService<IOptions<PasswordChangeRateLimitOptions>>().Value);

        Assert.Contains(nameof(PasswordChangeRateLimitOptions.PermitLimit), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultLimit_DoesNotImpedeOrdinaryUse()
    {
        // The negative control for the whole file: with the shipped default (10/hour), a user changing
        // their password once — the only thing anyone actually does here — is never limited. Without
        // this, a limiter accidentally set to 1 would satisfy every other test above.
        // Read from the options class rather than hardcoded, so this tracks the shipped default if it is
        // ever retuned. (The fixture always sets the key, so it cannot simply be left unset here.)
        await using var factory = Limited(new PasswordChangeRateLimitOptions().PermitLimit);
        await factory.CreateUserAsync(Email);
        using var client = await factory.LoginAsync(Email);

        var response = await ChangeAsync(client, PasswordGateFactory.Password, "Renewed111!Passphrase");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string current, string next) =>
        client.PostAsJsonAsync("/api/account/password", new { currentPassword = current, newPassword = next });

    private static PasswordGateFactory Limited(int permitLimit) =>
        new(new Dictionary<string, string?>
        {
            ["RateLimiting:PasswordChange:PermitLimit"] = permitLimit.ToString(),
            ["RateLimiting:PasswordChange:WindowSeconds"] = "3600",
        });
}
