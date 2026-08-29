using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos.Authorization;
using Xunit;
using Odyssey.Dtos;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the nine per-request caps (issue #421 Wave 3).
///
/// <para>
/// The interesting pair is the two photo caps, which are <strong>tighten-only</strong>. Their
/// compile-time value also feeds <c>[MaxLength]</c> on ten photo request DTOs, so model validation
/// rejects an over-cap request before the service check is reached — meaning a setting raised above the
/// constant would change nothing. Shipping that would be the exact "I raised the limit and it did not
/// take effect" failure the feature refuses (it is why rate limits were excluded outright), so a raise
/// is rejected and the ceiling is published on the read DTO instead.
/// </para>
/// </summary>
public class RequestCapSystemSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "33333333-3333-3333-3333-333333333333";

    private static readonly string[] ReadAndOrdinary =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    /// <summary>Read plus only the STRICTER claim — these nine need the ordinary one.</summary>
    private static readonly string[] ReadAndSecurityOnly =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate];

    [Fact]
    public async Task Get_ServesTheCompiledDefaults()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.NotNull(dto);
        Assert.Equal(SystemSettingsDefaults.ContractMaxPartiesPerContract, dto!.ContractMaxPartiesPerContract);
        Assert.Equal(SystemSettingsDefaults.ContractMaxFilesPerContract, dto.ContractMaxFilesPerContract);
        Assert.Equal(SystemSettingsDefaults.ContractMaxSummaryContracts, dto.ContractMaxSummaryContracts);
        Assert.Equal(SystemSettingsDefaults.InsuranceMaxRenewalsPerPolicy, dto.InsuranceMaxRenewalsPerPolicy);
        Assert.Equal(SystemSettingsDefaults.InsuranceMaxFilesPerParent, dto.InsuranceMaxFilesPerParent);
        Assert.Equal(SystemSettingsDefaults.PhotoMaxLinksPerKind, dto.PhotoMaxLinksPerKind);
        Assert.Equal(SystemSettingsDefaults.PhotoMaxAlbumMembers, dto.PhotoMaxAlbumMembers);
        Assert.Equal(SystemSettingsDefaults.JournalEntryMaxLinksPerKind, dto.JournalEntryMaxLinksPerKind);
        Assert.Equal(SystemSettingsDefaults.JournalTaskMaxLinksPerKind, dto.JournalTaskMaxLinksPerKind);
    }

    /// <summary>
    /// The ceilings must be published, or the client cannot bound its control and would offer a value
    /// the API is going to reject.
    /// </summary>
    [Fact]
    public async Task Get_PublishesTheTightenOnlyCeilings()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.Equal(PhotoLimits.MaxLinksPerKind, dto!.PhotoMaxLinksPerKindCeiling);
        Assert.Equal(PhotoLimits.MaxAlbumMembers, dto.PhotoMaxAlbumMembersCeiling);
    }

    [Fact]
    public async Task Put_RoundTripsEveryCap()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            ContractMaxPartiesPerContract = 10,
            ContractMaxFilesPerContract = 11,
            ContractMaxSummaryContracts = 12,
            InsuranceMaxRenewalsPerPolicy = 13,
            InsuranceMaxFilesPerParent = 14,
            PhotoMaxLinksPerKind = 15,
            PhotoMaxAlbumMembers = 16,
            JournalEntryMaxLinksPerKind = 17,
            JournalTaskMaxLinksPerKind = 18,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();

        Assert.Equal(10, dto!.ContractMaxPartiesPerContract);
        Assert.Equal(14, dto.InsuranceMaxFilesPerParent);
        Assert.Equal(15, dto.PhotoMaxLinksPerKind);
        Assert.Equal(18, dto.JournalTaskMaxLinksPerKind);
    }

    [Fact]
    public async Task Put_LoweringATightenOnlyCap_IsAllowed()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { PhotoMaxLinksPerKind = PhotoLimits.MaxLinksPerKind - 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_AtTheCeilingExactly_IsAllowed()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { PhotoMaxAlbumMembers = PhotoLimits.MaxAlbumMembers });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_RaisingPhotoLinksAboveItsCeiling_IsRejected_AndNamesTheCeiling()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { PhotoMaxLinksPerKind = PhotoLimits.MaxLinksPerKind + 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(PhotoLimits.MaxLinksPerKind.ToString(), body, StringComparison.Ordinal);
        Assert.Contains(nameof(SystemSettingsUpdate.PhotoMaxLinksPerKind), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_RaisingAlbumMembersAboveItsCeiling_IsRejected()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { PhotoMaxAlbumMembers = PhotoLimits.MaxAlbumMembers + 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The unconstrained caps have no such ceiling — only the two whose value is mirrored into a
    /// request-DTO attribute do. Asserting this keeps the tighten-only rule from spreading by copy.
    /// </summary>
    [Fact]
    public async Task Put_RaisingAnUnconstrainedCap_IsAllowed()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { JournalEntryMaxLinksPerKind = 5000 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(5000, dto!.JournalEntryMaxLinksPerKind);
    }

    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.ContractMaxPartiesPerContract))]
    [InlineData(nameof(SystemSettingsUpdate.PhotoMaxAlbumMembers))]
    [InlineData(nameof(SystemSettingsUpdate.JournalTaskMaxLinksPerKind))]
    public async Task Put_WithOnlyTheSecurityClaim_IsForbidden_AndNamesTheField(string field)
    {
        // The mirror image of the email settings: these nine take the ORDINARY write claim, so holding
        // only the stricter one is not enough. Asserted so the split cannot drift either way.
        await using var factory = new ApiFactory(ReadAndSecurityOnly);
        using var client = factory.CreateClient();

        var update = new SystemSettingsUpdate();
        switch (field)
        {
            case nameof(SystemSettingsUpdate.ContractMaxPartiesPerContract):
                update.ContractMaxPartiesPerContract = 5; break;
            case nameof(SystemSettingsUpdate.PhotoMaxAlbumMembers):
                update.PhotoMaxAlbumMembers = 5; break;
            default:
                update.JournalTaskMaxLinksPerKind = 5; break;
        }

        var response = await client.PutAsJsonAsync(Path, update);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public async Task Put_RejectsAnOutOfRangeCap(int value)
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { ContractMaxFilesPerContract = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
