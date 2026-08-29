using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Xunit;
using Odyssey.Dtos;

namespace Odyssey.Api.Tests;

/// <summary>
/// <c>GET /api/upload-limits</c> (issue #421 Wave 4) — the sibling of <c>/api/import-limits</c> that
/// lets every upload dialog pre-validate against the real cap.
///
/// <para>
/// It is deliberately <strong>claim-free</strong>: the dialogs are used by roles holding no
/// system-settings claim at all, and gating this would leave them pre-validating against a compiled
/// guess while the server enforced something else — the exact drift Wave 4 exists to remove.
/// </para>
/// </summary>
public class UploadLimitsApiTests
{
    private const string Path = "/api/upload-limits";

    /// <summary>Deliberately empty: the endpoint must serve a caller holding no claims whatsoever.</summary>
    private static readonly string[] NoClaims = [];

    [Fact]
    public async Task Get_ServesTheEffectiveCap_WithoutAnyClaim()
    {
        await using var factory = new ApiFactory(NoClaims);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<UploadLimitsDto>();
        Assert.Equal(SystemSettingsDefaults.FileStorageMaxUploadMegabytes, dto!.MaxUploadMegabytes);
        Assert.Equal(SystemSettingsDefaults.FileStorageMaxUploadMegabytes * 1024L * 1024L, dto.MaxUploadBytes);
    }

    /// <summary>
    /// A stored row the server cannot use is degraded, and a degraded read must not be presented as
    /// configuration — the client renders its own fallback instead of being told a cap nobody set.
    /// Enforcement still applies the conservative number; only this display surface fails closed.
    /// </summary>
    [Fact]
    public async Task Get_WhenTheStoredValueIsUnusable_IsServiceUnavailable()
    {
        await using var factory = new ApiFactory(NoClaims);
        await SystemSettingsSeed.SetAsync(factory.Services,
            SystemSettingsKeys.FileStorageMaxUploadMegabytes, "not-a-number");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// An ABSENT row is healthy, not degraded — it resolves to the compiled default, the same posture
    /// the settings service takes. Conflating the two would 503 on any database whose rows have not
    /// been seeded, which is every fresh in-memory test environment.
    /// </summary>
    [Fact]
    public async Task Get_WithNoStoredRow_ServesTheDefaultRatherThanDegrading()
    {
        await using var factory = new ApiFactory(NoClaims);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_ReflectsALoweredCap()
    {
        await using var factory = new ApiFactory(NoClaims);
        await SystemSettingsSeed.SetAsync(factory.Services,
            SystemSettingsKeys.FileStorageMaxUploadMegabytes, "5");
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<UploadLimitsDto>(Path);

        Assert.Equal(5, dto!.MaxUploadMegabytes);
        Assert.Equal(5 * 1024L * 1024L, dto.MaxUploadBytes);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions);
}
