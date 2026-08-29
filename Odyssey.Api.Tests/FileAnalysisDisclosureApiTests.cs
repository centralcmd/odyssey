using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Odyssey.Dtos;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for <c>GET /api/file-analysis/disclosure</c> (issue #421 Wave 1, AC 19).
///
/// <para>
/// This endpoint is authenticated but deliberately <strong>claim-free</strong>, which is a widening
/// that has to be justified field by field — so the tests here pin both halves of the bargain: any
/// signed-in user can read it, and it exposes exactly the documented values and nothing else. The
/// second assertion is the one that matters, because the failure it guards against is someone later
/// "simplifying" this to return the admin <c>SystemSettingsDto</c>, which would hand every user the
/// security toggles, the volume caps and the last administrator's identity.
/// </para>
/// </summary>
public class FileAnalysisDisclosureApiTests
{
    private const string Path = "/api/file-analysis/disclosure";
    private const string ActorUserId = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// The complete public surface. Adding a field here is a deliberate act, not a refactor.
    ///
    /// <para>
    /// Note what is <em>absent</em> and must stay absent: the file-analysis base URL, and the host it
    /// names. Issue #421 justified the claim-free widening field by field on the grounds that each
    /// value is disclosed to the user anyway; the base URL is not — it is deployment infrastructure, it
    /// can name an internal host, and since issue #439 it is admin-editable.
    /// </para>
    /// </summary>
    private static readonly string[] DocumentedProperties =
        ["processor", "processorRegion", "lawfulBasis", "privacyNoticeUrl", "model", "enabled", "disclosureVersion"];

    [Fact]
    public async Task AuthenticatedWithNoClaims_ReturnsOk()
    {
        // The consent gate is shown to ordinary users while the settings API is Admin-only, so a
        // caller holding no permissions at all must still be able to read the disclosure.
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Body_ContainsExactlyTheDocumentedProperties()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(Path);
        var properties = JsonDocument.Parse(json).RootElement
            .EnumerateObject().Select(property => property.Name).ToList();

        Assert.Equal(
            DocumentedProperties.OrderBy(name => name, StringComparer.Ordinal),
            properties.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Body_ServesTheSeededValues()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(Path);

        Assert.NotNull(dto);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisProcessor, dto!.Processor);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisProcessorRegion, dto.ProcessorRegion);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisLawfulBasis, dto.LawfulBasis);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl, dto.PrivacyNoticeUrl);
    }

    /// <summary>
    /// An admin edit must reach the gate. This is the whole point of the wave: the values used to be
    /// compile-time constants on both sides, so no edit could ever reach it.
    /// </summary>
    [Fact]
    public async Task AnAdminEditIsServedToOrdinaryUsers()
    {
        await using var factory = new ApiFactory([PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate]);
        using var client = factory.CreateClient();

        var update = await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            FileAnalysisProcessor = "Contoso AI",
            FileAnalysisProcessorRegion = "European Union",
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var dto = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(Path);

        Assert.NotNull(dto);
        Assert.Equal("Contoso AI", dto!.Processor);
        Assert.Equal("European Union", dto.ProcessorRegion);
    }

    /// <summary>
    /// AC 17's HTTP half: a value planted straight into the database — bypassing the API's validator
    /// entirely — must never reach the client, because the panel renders it into an <c>href</c> and
    /// Blazor does not sanitise <c>href</c>. A present-but-invalid value is a degraded read, so the
    /// endpoint refuses rather than quietly substituting a default and calling it configuration.
    /// </summary>
    [Fact]
    public async Task ADatabasePlantedJavascriptUrl_IsRefusedNotServed()
    {
        await using var factory = new ApiFactory([]);

        using (var scope = factory.Services.CreateScope())
        {
            // Added, not mutated: the factory's in-memory database is not seeded (only the one test
            // that calls EnsureCreated itself sees the HasData rows), so there is nothing to update.
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingsKeys.FileAnalysisPrivacyNoticeUrl,
                Value = "javascript:alert(1)",
                UpdatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // And belt to those braces: the value must not appear anywhere in the response either.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("javascript:", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A degraded read returns <c>503</c> rather than presenting a fallback as authoritative — the same
    /// rule <c>ImportLimitsController</c> follows, and a stronger case here because this is legal
    /// disclosure text: telling a user the wrong processor is worse than telling them nothing.
    /// </summary>
    [Fact]
    public async Task ADegradedRead_ReturnsServiceUnavailable()
    {
        await using var factory = new ApiFactory([]);

        using (var scope = factory.Services.CreateScope())
        {
            // A row PRESENT with an unusable value is the degradation — not an absent row, which is
            // "should not happen post-migration" and resolves to the compiled default as healthy.
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingsKeys.FileAnalysisMaxFutureTransactionDays,
                Value = "not-a-number",
                UpdatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// The distinction that matters: an unseeded database is healthy, not degraded. Getting this wrong
    /// 503s the consent gate on every fresh in-memory and development environment.
    /// </summary>
    [Fact]
    public async Task AnUnseededDatabase_IsHealthyAndServesTheCompiledDefaults()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<FileAnalysisDisclosureDto>();
        Assert.Equal(SystemSettingsDefaults.FileAnalysisProcessor, dto!.Processor);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    // ── issue #439: the live switch and the consent-binding version ──────────────────────────────

    /// <summary>
    /// AC 39 — <c>enabled</c> reflects the LIVE switch and <c>model</c> comes from the settings
    /// snapshot. The gate uses the first to decide whether the Analyze affordance is offered at all, so
    /// no consent is ever collected for a transfer that cannot happen.
    /// </summary>
    [Fact]
    public async Task Body_CarriesTheLiveSwitchAndTheSettingsModel()
    {
        await using var factory = new ApiFactory([]);
        // Written before the first read, so the snapshot is cold and picks the model up. The model
        // comes from the CACHED snapshot; only the switch is live, which is the split the next two
        // assertions demonstrate.
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisModel, "claude-opus-5");
        using var client = factory.CreateClient();

        var off = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(Path);
        Assert.False(off!.Enabled);
        Assert.Equal("claude-opus-5", off.Model);

        // A direct write evicts nothing, so the warm snapshot from the read above is still in play —
        // and `enabled` flips anyway, because it is not served from it.
        await factory.EnableFileAnalysisAsync();

        var on = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(Path);
        Assert.True(on!.Enabled);
        Assert.Equal("claude-opus-5", on.Model);
    }

    /// <summary>
    /// AC 35 — the version changes with every disclosure fact, including the <em>host</em> of the base
    /// URL, and does <strong>not</strong> change with <c>enabled</c>. That exclusion is deliberate:
    /// availability is not a disclosure fact, and including it would 409 every open consent gate on an
    /// unrelated toggle.
    ///
    /// <para>
    /// Driven through the settings API rather than by writing rows, because the disclosure endpoint
    /// serves the four processor strings from the 30-second cached snapshot — a direct write would
    /// leave that snapshot warm and prove nothing about the version.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(DisclosureFactChanges))]
    public async Task DisclosureVersion_ChangesWithEveryDisclosureFact_ButNotWithTheSwitch(
        string _, SystemSettingsUpdate change, bool expectChange)
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate]);
        using var client = factory.CreateClient();

        var before = (await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(Path))!.DisclosureVersion;
        Assert.False(string.IsNullOrWhiteSpace(before));

        (await client.PutAsJsonAsync("/api/system-settings", change)).EnsureSuccessStatusCode();

        var after = (await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(Path))!.DisclosureVersion;

        if (expectChange)
        {
            Assert.NotEqual(before, after);
        }
        else
        {
            Assert.Equal(before, after);
        }
    }

    public static TheoryData<string, SystemSettingsUpdate, bool> DisclosureFactChanges() => new()
    {
        { "processor", new SystemSettingsUpdate { FileAnalysisProcessor = "Acme Analysis GmbH" }, true },
        { "region", new SystemSettingsUpdate { FileAnalysisProcessorRegion = "Norway" }, true },
        { "lawfulBasis", new SystemSettingsUpdate { FileAnalysisLawfulBasis = "Contract \u00b7 GDPR Art. 6(1)(b)" }, true },
        { "privacyNoticeUrl", new SystemSettingsUpdate { FileAnalysisPrivacyNoticeUrl = "https://example.test/privacy" }, true },
        { "model", new SystemSettingsUpdate { FileAnalysisModel = "claude-opus-5" }, true },
        // The HOST of the base URL is in the tuple; the rest of the URL never is, so the version moves
        // when the destination moves without the hash input carrying a path or a credential.
        { "baseUrl host", new SystemSettingsUpdate { FileAnalysisBaseUrl = "https://gateway.internal" }, true },
        // Deliberately excluded — see the remarks.
        { "enabled", new SystemSettingsUpdate { FileAnalysisEnabled = true }, false },
    };

    /// <summary>
    /// AC 40 — the version is a HASH, not a reversible encoding of the tuple, and the response carries
    /// no base URL or host under any settings state. It is an integrity token for the gate, not a
    /// secret; what it must not do is become a channel for the one field that is deliberately absent.
    /// </summary>
    [Fact]
    public async Task TheResponse_NeverCarriesTheBaseUrlOrItsHost_AndTheVersionIsNotReversible()
    {
        await using var factory = new ApiFactory([]);
        await factory.EnableFileAnalysisAsync();
        await factory.SetSystemSettingAsync(
            SystemSettingsKeys.FileAnalysisBaseUrl, "https://very-distinctive-gateway.internal");
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(Path);

        Assert.DoesNotContain("very-distinctive-gateway", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileAnalysisBaseUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baseUrl", json, StringComparison.OrdinalIgnoreCase);

        // Base64url of a truncated SHA-256: fixed length, and carrying none of its inputs verbatim.
        var dto = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(Path);
        Assert.Equal(16, dto!.DisclosureVersion.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", dto.DisclosureVersion);
        Assert.DoesNotContain(dto.Processor, dto.DisclosureVersion, StringComparison.OrdinalIgnoreCase);
    }

}
