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
/// HTTP-level coverage for the four transactional-email settings (issue #421 Wave 2).
///
/// <para>
/// All four sit behind <c>system-settings.security.update</c>, not the ordinary write claim: the sender
/// identity is what recipients see and what the relay authorises, and the throttle is the only thing
/// standing between a rotating-IP source and an unbounded mailbomb at one address. The claim split is
/// asserted here rather than assumed, because a field silently gated on the weaker claim is exactly the
/// bug class the descriptor registry exists to prevent.
/// </para>
/// </summary>
public class EmailSystemSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "22222222-2222-2222-2222-222222222222";

    private static readonly string[] ReadAndSecurity =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate];

    /// <summary>Read plus the ORDINARY write claim — enough for the caps, not for these.</summary>
    private static readonly string[] ReadAndOrdinary =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    [Fact]
    public async Task Get_ServesTheCompiledDefaults_WhenNoRowsExist()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.NotNull(dto);
        Assert.Equal(SystemSettingsDefaults.EmailFromAddress, dto!.EmailFromAddress);
        Assert.Equal(SystemSettingsDefaults.EmailFromName, dto.EmailFromName);
        Assert.Equal(SystemSettingsDefaults.EmailPerRecipientLimit, dto.EmailPerRecipientLimit);
        Assert.Equal(SystemSettingsDefaults.EmailPerRecipientWindowMinutes, dto.EmailPerRecipientWindowMinutes);
    }

    [Fact]
    public async Task Put_RoundTripsAllFour()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            EmailFromAddress = "billing@acme.test",
            EmailFromName = "Acme Billing",
            EmailPerRecipientLimit = 7,
            EmailPerRecipientWindowMinutes = 120,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();

        Assert.Equal("billing@acme.test", dto!.EmailFromAddress);
        Assert.Equal("Acme Billing", dto.EmailFromName);
        Assert.Equal(7, dto.EmailPerRecipientLimit);
        Assert.Equal(120, dto.EmailPerRecipientWindowMinutes);
    }

    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.EmailFromAddress))]
    [InlineData(nameof(SystemSettingsUpdate.EmailFromName))]
    [InlineData(nameof(SystemSettingsUpdate.EmailPerRecipientLimit))]
    [InlineData(nameof(SystemSettingsUpdate.EmailPerRecipientWindowMinutes))]
    public async Task Put_WithOnlyTheOrdinaryWriteClaim_IsForbidden_AndNamesTheField(string field)
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var update = new SystemSettingsUpdate();
        switch (field)
        {
            case nameof(SystemSettingsUpdate.EmailFromAddress): update.EmailFromAddress = "a@b.test"; break;
            case nameof(SystemSettingsUpdate.EmailFromName): update.EmailFromName = "Acme"; break;
            case nameof(SystemSettingsUpdate.EmailPerRecipientLimit): update.EmailPerRecipientLimit = 9; break;
            default: update.EmailPerRecipientWindowMinutes = 9; break;
        }

        var response = await client.PutAsJsonAsync(Path, update);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    // A display name belongs to the other setting; accepting one here would give it two sources.
    [InlineData("Odyssey <no-reply@odyssey.test>")]
    // The envelope sender is one address, and a list is a header-shaped surprise.
    [InlineData("a@b.test, c@d.test")]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Put_RejectsAFromAddressThatIsNotASingleBareMailbox(string value)
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { EmailFromAddress = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_RejectsAFromNameCarryingALineBreak()
    {
        // MimeKit encodes display names correctly, so this is defence in depth rather than the only
        // barrier against header injection — but a control character has no business in a display name.
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { EmailFromName = "Acme\r\nBcc: victim@example.test" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Put_RejectsAnOutOfRangeRecipientLimit(int limit)
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { EmailPerRecipientLimit = limit });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_TrimsTheStoredSenderIdentity()
    {
        // Untrimmed values make a GET-then-PUT of unchanged data stop being a no-op.
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { EmailFromAddress = "  billing@acme.test  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal("billing@acme.test", dto!.EmailFromAddress);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
