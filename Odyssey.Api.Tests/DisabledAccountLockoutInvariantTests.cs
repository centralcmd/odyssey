using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Guards the one property the "disabled account" convention depends on and nothing enforces
/// structurally (issue #451 Phase 3): <see cref="AccountLockout.DisabledLockoutEnd"/> is stored in the
/// same column Identity's brute-force accounting writes, so a single
/// <c>UserManager.AccessFailedAsync</c> reaching a disabled user replaces the year-9999 sentinel with
/// <c>now + DefaultLockoutTimeSpan</c> — five minutes — and the account is enabled again when that
/// window passes. Nothing about that call looks dangerous at the call site, which is what makes it a
/// landmine rather than a bug.
/// </summary>
/// <remarks>
/// <para>
/// <c>POST /api/account/password</c> is the only place in the app that calls it. It is safe today
/// because the controller checks <c>IsLockedOutAsync</c> first and answers <c>423</c> — an ordering
/// that is easy to lose in a refactor, and whose loss is silent: the endpoint keeps working, and a
/// disabled account quietly re-enables itself five minutes after someone guesses at it. These tests
/// assert the sentinel survives, not just the status code, so reordering the check fails here even if
/// the response shape is preserved.
/// </para>
/// <para>
/// The user is authenticated through the real cookie pipeline before being disabled, which is the
/// reachable case: the admin action rotates the security stamp (issue #442), but revocation lands on the
/// one-minute validation interval, so a session that already exists outlives the switch for up to that
/// long. These fixtures write the sentinel directly rather than through the service, which holds the
/// session open for the whole test — the hazard under test is about the column, not about how long the
/// window lasts.
/// </para>
/// </remarks>
public class DisabledAccountLockoutInvariantTests
{
    private const string Email = "disabled@example.com";

    [Fact]
    public void ADisabledAccountAndAnOrdinaryLockoutAreTheSameColumn()
    {
        var now = DateTimeOffset.UtcNow;

        // The sentinel reads as disabled...
        Assert.False(AccountLockout.IsEnabled(AccountLockout.DisabledLockoutEnd, now));

        // ...but an ordinary five-minute lockout window written over it reads as enabled the moment it
        // lapses. That is the whole hazard, stated once: the convention has no marker of its own.
        var ordinaryLockout = now.AddMinutes(5);
        Assert.False(AccountLockout.IsEnabled(ordinaryLockout, now));
        Assert.True(AccountLockout.IsEnabled(ordinaryLockout, now.AddMinutes(6)));
    }

    [Fact]
    public async Task AWrongPasswordFromADisabledAccountDoesNotOverwriteTheSentinel()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(Email);
        using var client = await factory.LoginAsync(Email);
        await DisableAsync(factory, user.Id);

        var response = await ChangeAsync(client, "Wrong111!Passphrase", "Renewed111!Passphrase");

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        Assert.Equal(AccountLockout.DisabledLockoutEnd, await LockoutEndAsync(factory, user.Id));
        // The counter must not have moved either: reaching it at all means AccessFailedAsync ran.
        Assert.Equal(0, await factory.AccessFailedCountAsync(user.Id));
    }

    [Fact]
    public async Task TheCorrectPasswordFromADisabledAccountIsRefusedToo()
    {
        // The success branch calls ResetAccessFailedCountAsync and RefreshSignInAsync. Neither clears
        // LockoutEnd, so the sentinel would survive — but a disabled account must not be able to rotate
        // its own credential and refresh its session, so the same check has to cover this path.
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(Email);
        using var client = await factory.LoginAsync(Email);
        await DisableAsync(factory, user.Id);

        var response = await ChangeAsync(client, PasswordGateFactory.Password, "Renewed111!Passphrase");

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        Assert.Equal(AccountLockout.DisabledLockoutEnd, await LockoutEndAsync(factory, user.Id));
    }

    [Fact]
    public async Task RepeatedWrongPasswordsNeverShortenTheDisabledWindow()
    {
        // Identity's default threshold is five failures. Past it, an unguarded path would have replaced
        // the sentinel with a five-minute window — so running well beyond the threshold is what
        // distinguishes "the check is there" from "the counter simply hasn't tripped yet".
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(Email);
        using var client = await factory.LoginAsync(Email);
        await DisableAsync(factory, user.Id);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var response = await ChangeAsync(client, "Wrong111!Passphrase", "Renewed111!Passphrase");
            Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        }

        Assert.Equal(AccountLockout.DisabledLockoutEnd, await LockoutEndAsync(factory, user.Id));
    }

    private static Task<HttpResponseMessage> ChangeAsync(
        HttpClient client, string currentPassword, string newPassword) =>
        client.PostAsJsonAsync("/api/account/password", new { currentPassword, newPassword });

    /// <summary>
    /// Write the disable sentinel directly. The admin action also rotates the security stamp, which would
    /// end this session on the validation interval; the state under test is the column, so the fixture
    /// reproduces only that half.
    /// </summary>
    private static async Task DisableAsync(PasswordGateFactory factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
        user.LockoutEnd = AccountLockout.DisabledLockoutEnd;
        await context.SaveChangesAsync();
    }

    private static async Task<DateTimeOffset?> LockoutEndAsync(PasswordGateFactory factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.LockoutEnd)
            .SingleAsync();
    }
}
