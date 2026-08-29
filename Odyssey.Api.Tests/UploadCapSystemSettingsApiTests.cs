using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;
using Odyssey.Dtos;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the upload cap (issue #421 Wave 4).
///
/// <para>
/// It is <strong>tighten-only</strong> for the same reason the two photo caps are, by a different
/// mechanism: Kestrel's request-body limit and the multipart length limit are fixed at startup from
/// <c>FileStorage:MaxFileSizeBytes</c> and cannot be raised per request, so a setting above that would
/// be refused by the transport before any application code ran. The advertised <c>Range(1, 1024)</c> is
/// deliberately not the effective range — the ceiling on the read DTO is.
/// </para>
///
/// <para>
/// Unlike the nine Wave 3 caps it carries the <em>security</em> claim and is audited: it bounds a real
/// abuse surface (bulk storage consumption), not an ordinary per-request shape.
/// </para>
/// </summary>
public class UploadCapSystemSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "44444444-4444-4444-4444-444444444444";

    private static readonly string[] ReadAndSecurity =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate];

    /// <summary>Read plus only the ORDINARY claim — this one needs the stricter one.</summary>
    private static readonly string[] ReadAndOrdinaryOnly =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    [Fact]
    public async Task Get_ServesTheCompiledDefault()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.NotNull(dto);
        Assert.Equal(SystemSettingsDefaults.FileStorageMaxUploadMegabytes, dto!.FileStorageMaxUploadMegabytes);
    }

    /// <summary>
    /// Without the ceiling the client cannot bound its control, and would offer up to 1024 MB — a value
    /// the API is going to reject on every save.
    /// </summary>
    [Fact]
    public async Task Get_PublishesTheTransportCeiling()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        // The shipped FileStorage default is 64 MB, so the ceiling is 64 — equal to the compiled
        // default, which is what makes the out-of-the-box state "settable downwards only".
        Assert.Equal(64, dto!.UploadMegabytesCeiling);
    }

    [Fact]
    public async Task Put_LoweringTheCap_IsAllowed()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileStorageMaxUploadMegabytes = 8 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(8, dto!.FileStorageMaxUploadMegabytes);
    }

    [Fact]
    public async Task Put_AtTheCeilingExactly_IsAllowed()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileStorageMaxUploadMegabytes = 64 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The defect this rule exists to prevent: a value the administrator sets, the API accepts, and the
    /// transport then silently ignores.
    /// </summary>
    [Fact]
    public async Task Put_AboveTheTransportCeiling_IsRejected_AndNamesTheCeiling()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileStorageMaxUploadMegabytes = 512 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("64", body, StringComparison.Ordinal);
        Assert.Contains(nameof(SystemSettingsUpdate.FileStorageMaxUploadMegabytes), body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Out of the advertised range entirely — rejected by model validation before the ceiling check,
    /// which is a different code path and worth pinning separately.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public async Task Put_OutsideTheAdvertisedRange_IsRejected(int value)
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileStorageMaxUploadMegabytes = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The claim split, asserted rather than documented: the ordinary write claim is not enough for this
    /// one, unlike the nine Wave 3 caps beside it.
    /// </summary>
    [Fact]
    public async Task Put_WithOnlyTheOrdinaryClaim_IsForbidden_AndNamesTheField()
    {
        await using var factory = new ApiFactory(ReadAndOrdinaryOnly);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileStorageMaxUploadMegabytes = 8 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(SystemSettingsUpdate.FileStorageMaxUploadMegabytes), body, StringComparison.Ordinal);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
