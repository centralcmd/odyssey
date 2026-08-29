using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// These tests pin the behaviour of ASP.NET Identity's built-in 2FA surface that the
/// Odyssey client wires itself to — the <c>POST /manage/2fa</c> management endpoint and
/// the <c>POST /login</c> two-factor challenge produced by <c>MapIdentityApi</c>. We
/// deliberately do not ship a custom controller/migration for 2FA (Identity already
/// implements the whole flow), so these act as the contract guard for that decision.
/// </summary>
public class TwoFactorAuthenticationTests
{
    private const string Password = "Password123!Safe";

    [Fact]
    public async Task ManageTwoFactor_WithoutSession_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/manage/2fa", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Exercises the exact <see cref="UserManager{TUser}"/> calls the built-in
    /// <c>/manage/2fa</c> endpoint makes, proving the client's assumptions: the shared
    /// key is base32 the authenticator app can consume, a standard TOTP code verifies,
    /// recovery codes are single-use, and resetting the key invalidates the old one.
    /// </summary>
    [Fact]
    public async Task IdentityTwoFactorContract_FullLifecycle_BehavesAsClientExpects()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await CreateUserAsync(scope.ServiceProvider, "contract@example.com");

        // setup → shared key (generated on first read, just like POST /manage/2fa {}).
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = await userManager.GetAuthenticatorKeyAsync(user);
        }
        Assert.False(string.IsNullOrEmpty(key));

        // enable → our TOTP generator must match Identity's verifier.
        var code = Totp.Generate(key!);
        Assert.True(
            await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code),
            "A standard RFC-6238 TOTP code computed from the shared key should verify.");

        await userManager.SetTwoFactorEnabledAsync(user, true);
        var recoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        Assert.Equal(10, recoveryCodes.Length);
        Assert.Equal(10, await userManager.CountRecoveryCodesAsync(user));

        // a recovery code is single-use (acceptance criterion #6).
        var firstRedeem = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCodes[0]);
        Assert.True(firstRedeem.Succeeded);
        var secondRedeem = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCodes[0]);
        Assert.False(secondRedeem.Succeeded);
        Assert.Equal(9, await userManager.CountRecoveryCodesAsync(user));

        // regenerate replaces the whole set.
        var regenerated = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        Assert.Equal(10, await userManager.CountRecoveryCodesAsync(user));
        Assert.False(await userManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCodes[1]) is { Succeeded: true },
            "Codes from the previous set must stop working after regeneration.");
        Assert.True((await userManager.RedeemTwoFactorRecoveryCodeAsync(user, regenerated[0])).Succeeded);

        // reset key (acceptance criterion #9): a new key, and the old code no longer verifies.
        await userManager.ResetAuthenticatorKeyAsync(user);
        var newKey = await userManager.GetAuthenticatorKeyAsync(user);
        Assert.False(string.IsNullOrEmpty(newKey));
        Assert.NotEqual(key, newKey);
        Assert.False(await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code),
            "A code from the old shared key must not verify after a key reset.");
    }

    /// <summary>
    /// Full HTTP round-trip the client performs: password login on a 2FA-enabled account
    /// is refused with <c>RequiresTwoFactor</c>, and a second login carrying the TOTP code
    /// (with Identity's pending cookie flowing between the two calls) completes the sign-in.
    /// </summary>
    [Fact]
    public async Task Login_TwoFactorEnabled_RefusesPasswordOnlyThenAcceptsCode()
    {
        await using var factory = new ApiFactory();

        string key;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await CreateUserAsync(scope.ServiceProvider, "login2fa@example.com");
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = (await userManager.GetAuthenticatorKeyAsync(user))!;
            await userManager.SetTwoFactorEnabledAsync(user, true);
            await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        }

        // The same client keeps a cookie container, so Identity's TwoFactorUserId pending
        // cookie set by the first call is sent on the second — exactly like the browser.
        using var client = factory.CreateClient();

        var passwordOnly = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = "login2fa@example.com", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, passwordOnly.StatusCode);
        var body = await passwordOnly.Content.ReadAsStringAsync();
        Assert.Contains("RequiresTwoFactor", body);

        var withCode = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = "login2fa@example.com", password = Password, twoFactorCode = Totp.Generate(key) });
        Assert.True(withCode.IsSuccessStatusCode, $"2FA login should succeed; got {withCode.StatusCode}.");

        // The application cookie now grants access to an authorized endpoint.
        var me = await client.GetAsync("/manage/info");
        Assert.True(me.IsSuccessStatusCode);
    }

    /// <summary>
    /// Pins the remember-this-device behaviour the login page must manage: a TOTP sign-in over a
    /// persistent cookie remembers the browser (Identity's <c>rememberClient</c> follows
    /// <c>useCookies</c>), so a later password-only login skips the challenge — and
    /// <c>POST /manage/2fa { forgetMachine = true }</c> reverses it. The client only keeps the
    /// remembered state when the user explicitly opts in.
    /// </summary>
    [Fact]
    public async Task Login_RemembersMachineOnPersistentSignIn_UntilForgotten()
    {
        await using var factory = new ApiFactory();

        string key;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await CreateUserAsync(scope.ServiceProvider, "remember@example.com");
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = (await userManager.GetAuthenticatorKeyAsync(user))!;
            await userManager.SetTwoFactorEnabledAsync(user, true);
            await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        }

        using var client = factory.CreateClient();

        // Password-only → challenge.
        var passwordOnly = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = "remember@example.com", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, passwordOnly.StatusCode);

        // Complete with TOTP over a persistent cookie → Identity remembers this client.
        var withCode = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = "remember@example.com", password = Password, twoFactorCode = Totp.Generate(key) });
        Assert.True(withCode.IsSuccessStatusCode);

        // Password-only is now silently accepted — the gap the UI must guard.
        var remembered = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = "remember@example.com", password = Password });
        Assert.True(remembered.IsSuccessStatusCode, "A remembered client should skip the 2FA challenge.");

        // Forgetting the machine restores the challenge.
        var forget = await client.PostAsJsonAsync("/manage/2fa", new { forgetMachine = true });
        Assert.True(forget.IsSuccessStatusCode);

        var afterForget = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = "remember@example.com", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, afterForget.StatusCode);
        Assert.Contains("RequiresTwoFactor", await afterForget.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Guards the re-enable case: when Identity still has recovery codes on file (2FA was
    /// disabled/reset, not removed), a plain enable returns <c>recoveryCodes: null</c>, so setup
    /// would finish without showing fallback codes. The client sends <c>resetRecoveryCodes: true</c>
    /// with enable so a fresh set is always returned.
    /// </summary>
    [Fact]
    public async Task Enable_WithResetRecoveryCodes_ReturnsFreshCodesEvenWhenCodesAlreadyOnFile()
    {
        await using var factory = new ApiFactory();

        string key;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await CreateUserAsync(scope.ServiceProvider, "reenable@example.com");
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = (await userManager.GetAuthenticatorKeyAsync(user))!;
            // Codes already on file while 2FA is currently off — the re-enable scenario.
            await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            Assert.Equal(10, await userManager.CountRecoveryCodesAsync(user));
            Assert.False(await userManager.GetTwoFactorEnabledAsync(user));
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = "reenable@example.com", password = Password });
        Assert.True(login.IsSuccessStatusCode);

        // A plain enable hands back no codes here (count > 0) — the gap the client works around.
        var plain = await client.PostAsJsonAsync(
            "/manage/2fa",
            new { enable = true, twoFactorCode = Totp.Generate(key) });
        plain.EnsureSuccessStatusCode();
        var plainResult = (await plain.Content.ReadFromJsonAsync<TwoFactorResponse>())!;
        Assert.True(plainResult.IsTwoFactorEnabled);
        Assert.True(plainResult.RecoveryCodes is null or { Length: 0 });

        // The client's enable call adds resetRecoveryCodes:true → always a fresh, showable set.
        var withReset = await client.PostAsJsonAsync(
            "/manage/2fa",
            new { enable = true, twoFactorCode = Totp.Generate(key), resetRecoveryCodes = true });
        withReset.EnsureSuccessStatusCode();
        var resetResult = (await withReset.Content.ReadFromJsonAsync<TwoFactorResponse>())!;
        Assert.True(resetResult.IsTwoFactorEnabled);
        Assert.Equal(10, resetResult.RecoveryCodes!.Length);
    }

    private sealed record TwoFactorResponse(bool IsTwoFactorEnabled, int RecoveryCodesLeft, string[]? RecoveryCodes);

    /// <summary>
    /// Creates a confirmed, loginable user. The License acceptance is what keeps these tests about 2FA:
    /// without it the legal gate (issue #354) answers /manage/2fa with a 451 for every fresh account.
    /// </summary>
    private static async Task<ApplicationUser> CreateUserAsync(IServiceProvider scopedServices, string email)
    {
        var userManager = scopedServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            LockoutEnabled = true,
        };
        var result = await userManager.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        // The require-admin-approval gate disables every newly added account, with no first-user
        // exemption since issue #290, so "loginable" now has to be made explicit.
        user.LockoutEnd = null;
        await userManager.UpdateAsync(user);

        await LegalTestData.AcceptAllAsync(scopedServices.GetRequiredService<OdysseyContext>(), user.Id);
        return user;
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"TwoFactorTests-{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UseInMemoryDatabase"] = "true",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
                services.RemoveAll<ILookupNormalizer>();
                services.AddSingleton<ILookupNormalizer, LowerInvariantLookupNormalizer>();
            });
        }
    }

    private sealed class LowerInvariantLookupNormalizer : ILookupNormalizer
    {
        public string? NormalizeName(string? name) => name?.ToLowerInvariant();
        public string? NormalizeEmail(string? email) => email?.ToLowerInvariant();
    }

    /// <summary>
    /// Minimal RFC-6238 TOTP generator matching the parameters ASP.NET Identity's
    /// authenticator provider verifies with: HMAC-SHA1, 30-second step, 6 digits, no
    /// modifier. Used only to drive the contract tests above.
    /// </summary>
    private static class Totp
    {
        public static string Generate(string base32Key, DateTimeOffset? when = null)
        {
            var key = Base32Decode(base32Key);
            var timestep = (when ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;

            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(counter, timestep);

            using var hmac = new HMACSHA1(key);
            var hash = hmac.ComputeHash(counter.ToArray());

            var offset = hash[^1] & 0x0f;
            var binary = ((hash[offset] & 0x7f) << 24)
                | ((hash[offset + 1] & 0xff) << 16)
                | ((hash[offset + 2] & 0xff) << 8)
                | (hash[offset + 3] & 0xff);

            return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }

        private static byte[] Base32Decode(string input)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var sanitized = input.Replace(" ", string.Empty).TrimEnd('=').ToUpperInvariant();

            var bits = 0;
            var value = 0;
            var output = new List<byte>(sanitized.Length * 5 / 8);
            foreach (var c in sanitized)
            {
                var index = alphabet.IndexOf(c);
                if (index < 0)
                {
                    continue;
                }

                value = (value << 5) | index;
                bits += 5;
                if (bits >= 8)
                {
                    output.Add((byte)((value >> (bits - 8)) & 0xff));
                    bits -= 8;
                }
            }

            return output.ToArray();
        }
    }
}
