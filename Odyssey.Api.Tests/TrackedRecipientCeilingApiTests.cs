using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The tracked-recipient ceiling as it actually reaches <see cref="IEmailSendThrottle"/>, from both real
/// call sites (issue #434 key 14).
///
/// <para>
/// <strong>Why this file exists, when the direction is already covered elsewhere.</strong> Every other
/// one of the fifteen settings has its degraded/clamped direction proven by calling the production lookup
/// end-to-end. This one had no such test: <c>TuningLookupDegradationTests</c> read the raw stored value
/// and then re-derived <c>Math.Max</c> <em>inline in the test</em>, which asserts arithmetic rather than
/// asserting the code. A regression flipping either real call site's <c>Math.Max</c> to <c>Math.Min</c>
/// would have been caught by nothing — and this is the one key where that inversion is the dangerous
/// direction, because the throttle <strong>fails open</strong> once its table is full, so a smaller table
/// weakens the anti-mailbomb control rather than tightening it. Raised by the test reviewer on PR #436.
/// </para>
///
/// <para>
/// <strong>Each direction is pinned by a pair, and the pair is the point.</strong> A below-floor row
/// alone would pass against <c>return SystemSettingsDefaults.EmailMaxTrackedRecipients;</c> — or against
/// a <c>Math.Min</c>, or a hardcoded constant. An above-floor row alone would pass against no clamp at
/// all. Only both together pin <c>Math.Max</c> exactly.
/// </para>
/// </summary>
public class TrackedRecipientCeilingApiTests
{
    private const string Registered = "registered@example.com";
    private const string AdminActorUserId = "tracked-ceiling-admin";
    private const string TargetUserId = "tracked-ceiling-target";
    private const string TargetEmail = "target@example.com";

    /// <summary>Comfortably above the shipped floor, so a clamp in either direction is distinguishable.</summary>
    private const int RaisedCeiling = 50_000;

    /// <summary>Far below the floor — the value an administrator cannot set through the API, but a
    /// restore, a hand edit or a future adoption step could plant.</summary>
    private const string PlantedBelowFloor = "10";

    // ── SmtpEmailSender: the anonymous /forgotPassword path ──────────────────────────────────────

    [Fact]
    public async Task TheSelfServicePath_ClampsAPlantedBelowFloorRowUpToTheShippedFloor()
    {
        var throttle = new RecordingThrottle();
        await using var factory = await CreateSenderFactoryAsync(throttle);
        await SeedConfirmedUserAsync(factory);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.EmailMaxTrackedRecipients, PlantedBelowFloor);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/forgotPassword", new { email = Registered });

        var call = Assert.Single(throttle.Calls);
        Assert.Equal(SystemSettingsDefaults.EmailMaxTrackedRecipients, call.MaxTrackedRecipients);
    }

    /// <summary>
    /// The other half of the pair: a value ABOVE the floor is passed through untouched. Without this, the
    /// test above is satisfied by a clamp in the wrong direction, or by no clamp at all.
    /// </summary>
    [Fact]
    public async Task TheSelfServicePath_PassesARaisedCeilingThroughUnchanged()
    {
        var throttle = new RecordingThrottle();
        await using var factory = await CreateSenderFactoryAsync(throttle);
        await SeedConfirmedUserAsync(factory);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.EmailMaxTrackedRecipients, $"{RaisedCeiling}");
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/forgotPassword", new { email = Registered });

        var call = Assert.Single(throttle.Calls);
        Assert.Equal(RaisedCeiling, call.MaxTrackedRecipients);
    }

    /// <summary>With no row at all the compiled default reaches the throttle — absent is healthy.</summary>
    [Fact]
    public async Task TheSelfServicePath_WithNoRow_PassesTheCompiledDefault()
    {
        var throttle = new RecordingThrottle();
        await using var factory = await CreateSenderFactoryAsync(throttle);
        await SeedConfirmedUserAsync(factory);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/forgotPassword", new { email = Registered });

        var call = Assert.Single(throttle.Calls);
        Assert.Equal(SystemSettingsDefaults.EmailMaxTrackedRecipients, call.MaxTrackedRecipients);
    }

    // ── UserAdministrationService: the admin-initiated reset path ────────────────────────────────
    //
    // A SECOND, independent clamp in a different class. Parameterising only one of the two would leave
    // the other reading whatever the row said, so both need their own pair.

    [Fact]
    public async Task TheAdminResetPath_ClampsAPlantedBelowFloorRowUpToTheShippedFloor()
    {
        var throttle = new RecordingThrottle();
        await using var factory = await CreateAdminFactoryAsync(throttle);
        await CreateTargetUserAsync(factory);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.EmailMaxTrackedRecipients, PlantedBelowFloor);
        using var client = factory.CreateClient();

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        var call = Assert.Single(throttle.Calls);
        Assert.Equal(SystemSettingsDefaults.EmailMaxTrackedRecipients, call.MaxTrackedRecipients);
    }

    [Fact]
    public async Task TheAdminResetPath_PassesARaisedCeilingThroughUnchanged()
    {
        var throttle = new RecordingThrottle();
        await using var factory = await CreateAdminFactoryAsync(throttle);
        await CreateTargetUserAsync(factory);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.EmailMaxTrackedRecipients, $"{RaisedCeiling}");
        using var client = factory.CreateClient();

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        var call = Assert.Single(throttle.Calls);
        Assert.Equal(RaisedCeiling, call.MaxTrackedRecipients);
    }

    /// <summary>
    /// The two sibling throttle settings ride along on the same snapshot, so a send observing a clamped
    /// ceiling and a stale limit would be a split read. Asserted on the same call the ceiling is.
    /// </summary>
    [Fact]
    public async Task TheThrottleReceivesOneConsistentSnapshotOfAllThreeValues()
    {
        var throttle = new RecordingThrottle();
        await using var factory = await CreateSenderFactoryAsync(throttle);
        await SeedConfirmedUserAsync(factory);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.EmailPerRecipientLimit, "7");
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.EmailPerRecipientWindowMinutes, "120");
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.EmailMaxTrackedRecipients, $"{RaisedCeiling}");
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/forgotPassword", new { email = Registered });

        var call = Assert.Single(throttle.Calls);
        Assert.Equal(7, call.Limit);
        Assert.Equal(120, call.WindowMinutes);
        Assert.Equal(RaisedCeiling, call.MaxTrackedRecipients);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    // Async because the SMTP host is a database row now, not configuration (issue #8), and it has to be
    // written before the first request. Without a host the sender short-circuits to its dev
    // link-logging path before any throttle decision, so the throttle would never be consulted at all —
    // and the old configuration key is read by nothing, so it would fail exactly that way, silently.

    private static async Task<OdysseyApiFactory> CreateSenderFactoryAsync(IEmailSendThrottle throttle)
    {
        var factory = CreateSenderFactory(throttle);
        await SystemSettingsSeed.SetTransportAsync(
            factory.Services, SmtpEmailSenderTestHarness.UnreachableHost);
        return factory;
    }

    private static async Task<OdysseyApiFactory> CreateAdminFactoryAsync(IEmailSendThrottle throttle)
    {
        var factory = CreateAdminFactory(throttle);
        await SystemSettingsSeed.SetTransportAsync(
            factory.Services,
            SmtpEmailSenderTestHarness.UnreachableHost,
            clientBaseUrl: SmtpEmailSenderTestHarness.ClientBaseUrl);
        return factory;
    }

    private static OdysseyApiFactory CreateSenderFactory(IEmailSendThrottle throttle) =>
        new(
            permissions: [],
            configuration: new Dictionary<string, string?>
            {
                ["Email:FromAddress"] = "no-reply@odyssey.test",
                // Generous per-IP limits, so nothing here is refused by the other layer.
                ["RateLimiting:Identity:PermitLimit"] = "1000",
                ["RateLimiting:IdentityEmail:PermitLimit"] = "1000",
            },
            configureServices: services =>
            {
                services.RemoveAll<IEmailSendThrottle>();
                services.AddSingleton(throttle);
            });

    private static OdysseyApiFactory CreateAdminFactory(IEmailSendThrottle throttle) =>
        new(
            permissions: [PermissionClaims.UsersUpdate],
            actorUserId: AdminActorUserId,
            configuration: new Dictionary<string, string?>
            {
                ["Email:FromAddress"] = "no-reply@odyssey.test",
                ["RateLimiting:AdminPasswordReset:PermitLimit"] = "1000",
            },
            configureServices: services =>
            {
                services.RemoveAll<IEmailSendThrottle>();
                services.AddSingleton(throttle);
            });

    private static async Task SeedConfirmedUserAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.Users.Add(new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = Registered,
            NormalizedUserName = Registered.ToUpperInvariant(),
            Email = Registered,
            NormalizedEmail = Registered.ToUpperInvariant(),
            EmailConfirmed = true,
        });
        await context.SaveChangesAsync();
    }

    private static async Task CreateTargetUserAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            Id = TargetUserId,
            UserName = TargetEmail,
            Email = TargetEmail,
            EmailConfirmed = true,
        };

        var created = await users.CreateAsync(user, "Password123!Safe");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
    }

    /// <summary>
    /// Records the full argument tuple. The existing doubles in the mail test files record only the
    /// recipient or a call count, which is why the derived ceiling was invisible to them.
    /// </summary>
    private sealed class RecordingThrottle : IEmailSendThrottle
    {
        public List<(string Recipient, int Limit, int WindowMinutes, int MaxTrackedRecipients)> Calls { get; } = [];

        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey)
        {
            Calls.Add((emailAddress, limit, windowMinutes, maxTrackedRecipients));
            return true;
        }
    }
}
