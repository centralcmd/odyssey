using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.SystemSettings;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the four transport settings issue #8 moved out of configuration
/// (ACs 1, 2, 7, 8, 9, 16).
///
/// <para>
/// The credential-clearing behaviour deliberately does <strong>not</strong> live here. It needs a real
/// transaction and a real execution strategy, neither of which the EF InMemory provider models, so
/// this tier would pass a <c>PUT</c> that throws on its first real exercise —
/// <c>EmailTransportCredentialClearTests</c> in <c>Odyssey.IntegrationTests</c> owns it (AC 4d).
/// </para>
/// </summary>
public class EmailTransportSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "88888888-8888-8888-8888-888888888888";

    private static readonly string[] ReadAndBoth =
    [
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    /// <summary>Read plus only the ORDINARY write claim — not enough for any of the four.</summary>
    private static readonly string[] ReadAndOrdinaryOnly =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    private static readonly string[] NoRead =
        [PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate];

    // ── AC 1 — the read shape and its gate ───────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ServesAllFourCompiledDefaults()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.NotNull(dto);
        Assert.Equal(SystemSettingsDefaults.EmailSmtpHost, dto.EmailSmtpHost);
        Assert.Equal(SystemSettingsDefaults.EmailSmtpPort, dto.EmailSmtpPort);
        Assert.Equal(SystemSettingsDefaults.EmailUseStartTls, dto.EmailUseStartTls);
        Assert.Equal(SystemSettingsDefaults.EmailClientBaseUrl, dto.EmailClientBaseUrl);
    }

    [Fact]
    public async Task Get_WithoutTheReadClaim_IsForbidden()
    {
        await using var factory = new ApiFactory(NoRead);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    // ── AC 2 — the write gate, per field ─────────────────────────────────────────────────────────

    public static TheoryData<SystemSettingsUpdate> SecurityGatedWrites() =>
    [
        new SystemSettingsUpdate { EmailSmtpHost = "smtp.example.test" },
        new SystemSettingsUpdate { EmailSmtpPort = 465 },
        new SystemSettingsUpdate { EmailUseStartTls = false },
        new SystemSettingsUpdate { EmailClientBaseUrl = "https://odyssey.example.test" },
    ];

    /// <summary>
    /// All four take the STRICTER claim, and the ordinary write claim buys none of them. The assertion
    /// that nothing was written matters as much as the status code: a 403 that had already persisted
    /// one field would be the failure this endpoint's wholesale rejection exists to prevent.
    /// </summary>
    [Theory]
    [MemberData(nameof(SecurityGatedWrites))]
    public async Task Put_WithOnlyTheOrdinaryWriteClaim_IsForbidden_AndWritesNothing(SystemSettingsUpdate request)
    {
        await using var factory = new ApiFactory(ReadAndOrdinaryOnly);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty(await context.SystemSettings.AsNoTracking().ToListAsync());
    }

    // ── ACs 7, 8, 9 — validation ─────────────────────────────────────────────────────────────────

    public static TheoryData<string> RejectedHosts() =>
    [
        "smtp.example.net\r\nDATA",
        "smtp.example\rnet",
        "smtp.example.net\0",
        "smtps://smtp.example.net",
        "smtp.example.net/submit",
        "smtp.example.net:587",
        "user:pass@smtp.example.net",
    ];

    /// <summary>
    /// AC 7. Two rules reject these between them — <c>StringSetting</c>'s shared control-character ban
    /// catches the CR/LF/NUL cases before the per-field rule runs — and the test does not care which,
    /// only that a 400 comes back and the message does not echo what was submitted. The message is
    /// where an operator would paste a value into a ticket.
    ///
    /// <para>
    /// <strong>One deliberate deviation from AC 7's literal wording.</strong> It says a host
    /// "containing <c>\r</c>, <c>\n</c>, <c>\0</c>" is rejected; a value whose ONLY newline is
    /// trailing is accepted instead, because <c>StringSetting</c> trims before it checks and the value
    /// that reaches storage is therefore clean. What the AC exists to prevent — a control character in
    /// a stored host, which would then reach a log line and a connection diagnostic — is satisfied,
    /// and refusing a pasted line ending would be hostile for no gain. NUL is not whitespace, so it
    /// survives the trim and is refused on its own merits; the interior cases above cover the rest.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(RejectedHosts))]
    public async Task Put_WithAMalformedHost_Is400_AndNeverEchoesTheValue(string host)
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailSmtpHost = host });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(host.Trim(), body, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 10's write half: empty is ACCEPTED and means unconfigured. Without this the two mail string
    /// rows would inherit <c>StringSetting</c>'s ordinary "must not be empty" rejection, and
    /// configuring mail would be a one-way door — null already means "leave unchanged", so <c>""</c> is
    /// the only spelling of "turn it off" available.
    /// </summary>
    [Fact]
    public async Task Put_WithAnEmptyHost_IsAccepted_AndMeansUnconfigured()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailSmtpHost = "smtp.example.test" }))
            .EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailSmtpHost = string.Empty });

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(string.Empty, dto!.EmailSmtpHost);

        // Stored as an empty row, never as a missing one: the two read identically, but only one is
        // what an administrator clearing the field actually produces.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await context.SystemSettings.AsNoTracking()
            .SingleAsync(setting => setting.Key == SystemSettingsKeys.EmailSmtpHost);
        Assert.Equal(string.Empty, row.Value);
    }

    /// <summary>The canonical form is what is stored, so a case-only edit is not a host CHANGE.</summary>
    [Fact]
    public async Task Put_CanonicalisesTheHostBeforeStoringIt()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            Path, new SystemSettingsUpdate { EmailSmtpHost = "  SMTP.Example.NET.  " });

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal("smtp.example.net", dto!.EmailSmtpHost);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Put_WithAnOutOfRangePort_Is400(int port)
    {
        // AC 8's write half — [Range] on the DTO, applied by model validation before the service runs.
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailSmtpPort = port });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// AC 8's read half. A row outside the pair never passed <c>[Range]</c> — it was hand-edited or
    /// restored — and is CLAMPED to the nearer bound rather than refused, because a port that parses is
    /// a usable number. "0" is this case, not the unparseable one.
    /// </summary>
    [Theory]
    [InlineData("0", SystemSettingsBounds.EmailSmtpPortMin)]
    [InlineData("99999", SystemSettingsBounds.EmailSmtpPortMax)]
    public async Task Get_ClampsAStoredOutOfRangePort_AndReportsIt(string stored, int expected)
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.EmailSmtpPort, stored);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.Equal(expected, dto!.EmailSmtpPort);

        // Reported, not silently corrected: a clamped row is a fault the administrator did not cause
        // and can see nowhere else in the product.
        Assert.Equal(
            SettingFaultKind.Clamped, dto.ProjectionFaults[nameof(SystemSettingsUpdate.EmailSmtpPort)]);
    }

    public static TheoryData<string> RejectedBaseUrls() =>
    [
        "http://odyssey.example.test",
        "https://token@odyssey.example.test",
        "https://odyssey.example.test?code=leaky",
        "https://odyssey.example.test#fragment",
        "ftp://odyssey.example.test",
    ];

    [Theory]
    [MemberData(nameof(RejectedBaseUrls))]
    public async Task Put_WithARejectedClientBaseUrl_Is400(string value)
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailClientBaseUrl = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// AC 9's accepted half, and the deliberate deviation from <c>FileAnalysisBaseUrlRule</c>'s
    /// zero-exception posture: <c>http</c> is allowed for LOOPBACK hosts so the dev and Aspire stacks
    /// keep working with no environment variable. A loopback link resolves on the recipient's own
    /// machine, so setting one is a denial-of-reset rather than an interception.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5199")]
    [InlineData("https://odyssey.example.test")]
    [InlineData("https://odyssey.example.test/app")]
    public async Task Put_WithAnAcceptedClientBaseUrl_Succeeds(string value)
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailClientBaseUrl = value });

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(value.TrimEnd('/'), dto!.EmailClientBaseUrl);
    }

    /// <summary>
    /// AC 13b's read half. A stored value the rule rejects is reported as a FAULT on the settings page
    /// rather than rendering as healthy — without it, an <c>http://</c> public host planted by a
    /// restore would fail every send closed while the row looked fine, on the field issue #8 §10.2
    /// calls the weakest point in the feature.
    /// </summary>
    [Fact]
    public async Task Get_ReportsAStoredClientBaseUrlTheRuleRejects()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.EmailClientBaseUrl, "http://attacker.example.test");
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.Equal(
            SettingFaultKind.Unreadable,
            dto!.ProjectionFaults[nameof(SystemSettingsUpdate.EmailClientBaseUrl)]);

        // As stored, so the administrator can see and correct what is actually there. The advisory
        // beside it never echoes the value.
        Assert.Equal("http://attacker.example.test", dto.EmailClientBaseUrl);
        Assert.DoesNotContain(
            "attacker.example.test",
            dto.Warnings[nameof(SystemSettingsUpdate.EmailClientBaseUrl)],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 12's HTTP half: a changed host is readable immediately, with no cache to wait out. These four
    /// carry no <c>CacheKeyToEvict</c> at all, which is the mechanism — there is nothing to evict
    /// because nothing caches them.
    /// </summary>
    [Fact]
    public async Task Put_TakesEffectOnTheNextRead()
    {
        await using var factory = new ApiFactory(ReadAndBoth);
        using var client = factory.CreateClient();

        (await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailSmtpHost = "smtp.first.test" }))
            .EnsureSuccessStatusCode();
        Assert.Equal("smtp.first.test", (await client.GetFromJsonAsync<SystemSettingsDto>(Path))!.EmailSmtpHost);

        (await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailSmtpHost = "smtp.second.test" }))
            .EnsureSuccessStatusCode();
        Assert.Equal("smtp.second.test", (await client.GetFromJsonAsync<SystemSettingsDto>(Path))!.EmailSmtpHost);
    }

    // ── AC 16 — the completeness guard ───────────────────────────────────────────────────────────

    /// <summary>
    /// Each of the four is declared on every side that has to know about it. The registry's own guard
    /// tests already cross-check the whole catalogue; this names the four explicitly so a partial
    /// revert — dropping one key from <c>AllKeys</c> while leaving its descriptor, say — fails against
    /// this feature rather than only against a count somewhere else.
    /// </summary>
    [Theory]
    [InlineData(SystemSettingsKeys.EmailSmtpHost, nameof(SystemSettingsUpdate.EmailSmtpHost))]
    [InlineData(SystemSettingsKeys.EmailSmtpPort, nameof(SystemSettingsUpdate.EmailSmtpPort))]
    [InlineData(SystemSettingsKeys.EmailUseStartTls, nameof(SystemSettingsUpdate.EmailUseStartTls))]
    [InlineData(SystemSettingsKeys.EmailClientBaseUrl, nameof(SystemSettingsUpdate.EmailClientBaseUrl))]
    public void EachKeyIsDeclaredEverywhere(string key, string fieldName)
    {
        Assert.Contains(key, SystemSettingsKeys.AllKeys);

        var descriptor = SystemSettingsRegistry.ByKey[key];
        Assert.Equal(fieldName, descriptor.FieldName);
        Assert.Equal(PermissionClaims.SystemSettingsSecurityUpdate, descriptor.RequiredClaim);

        Assert.NotNull(typeof(SystemSettingsUpdate).GetProperty(fieldName));
        Assert.NotNull(typeof(SystemSettingsDto).GetProperty(fieldName));

        // The client catalogue's half of this is asserted in Odyssey.Client.Tests, which is the only
        // tier that can see Odyssey.Client — see EmailTransportCatalogueTests.
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
