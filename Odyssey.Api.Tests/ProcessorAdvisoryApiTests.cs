using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The correspondence heuristic between the disclosed <c>FileAnalysisProcessor</c> and the
/// <c>FileAnalysisBaseUrl</c> setting (issue #421's deferred D1, built in #434, re-sourced in #439).
///
/// <para>
/// <strong>It is explicitly a heuristic and explicitly not a security control.</strong> Both failure
/// directions are accepted and stated in the copy the administrator sees: a legitimate Bedrock, Vertex
/// or corporate-gateway deployment will trip it, and a lookalike host will not. It exists to catch a
/// <em>stale</em> disclosure after someone repoints the base URL — which issue #439 made possible from
/// the UI, so the heuristic now matters more, not less. The actual controls on this surface are the
/// security claim, the audit line with its host-only projection, and the blocking https-only shape
/// validators on the base URL and the privacy notice.
/// </para>
///
/// <para>
/// The disclosure-leak test below is the one with teeth. A gateway URL is the exact shape this heuristic
/// is expected to trip on, and <c>https://apikey:secret@gateway.internal/v1</c> is a common spelling of
/// one — so the credential-bearing form is the likely case here, not an edge case. The advisory echoes
/// <c>Uri.Host</c> and nothing else. Such a value is PLANTED DIRECTLY in the settings row, bypassing
/// the API, because the write validator rejects it — which is precisely why the advisory cannot assume
/// the value it reads was ever validated.
/// </para>
/// </summary>
public class ProcessorAdvisoryApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "66666666-6666-6666-6666-666666666666";

    private static readonly string[] ReadAndSecurity =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate];

    private static string Field => nameof(SystemSettingsUpdate.FileAnalysisProcessor);

    [Fact]
    public async Task WhenTheHostDoesNotContainTheProcessorName_AnAdvisoryIsCarried()
    {
        await using var factory = await FactoryWithBaseUrlAsync("https://gateway.example.test/v1");
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        var advisory = Assert.Contains(Field, dto!.Warnings);
        Assert.Contains("gateway.example.test", advisory, StringComparison.Ordinal);
        // The copy states its own fallibility, so an administrator does not read it as a verdict.
        Assert.Contains("not a verification", advisory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheHostContainsTheProcessorName_ThereIsNoAdvisory()
    {
        await using var factory = await FactoryWithBaseUrlAsync("https://api.anthropic.com");
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.DoesNotContain(Field, dto!.Warnings.Keys);
    }

    /// <summary>
    /// Empty is the shipped default and means analysis is unconfigured — there is nothing to correspond
    /// to, and an advisory there would fire on every fresh install.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("://broken")]
    public async Task WhenTheBaseUrlIsEmptyOrUnparseable_ThereIsNoAdvisory(string baseUrl)
    {
        await using var factory = await FactoryWithBaseUrlAsync(baseUrl);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.DoesNotContain(Field, dto!.Warnings.Keys);
    }

    /// <summary>
    /// Only the host is echoed — never the path, the query, or the <c>userinfo</c> segment. The recipients
    /// hold <c>system-settings.read</c> (Admin-only) and a bare host is not a secret, but there is no
    /// reason to widen it, and a gateway URL is precisely where a credential would be.
    /// </summary>
    [Fact]
    public async Task TheAdvisoryEchoesTheHostOnly_NeverUserinfoPathOrQuery()
    {
        await using var factory = await FactoryWithBaseUrlAsync("https://apikey:secret@gateway.internal/v1?token=leaky");
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        var advisory = Assert.Contains(Field, dto!.Warnings);
        Assert.Contains("gateway.internal", advisory, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leaky", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/v1", advisory, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renaming the processor to something that no longer matches the host is a save that SUCCEEDS and
    /// carries an advisory. The heuristic is informational — it must never be able to refuse a write,
    /// least of all a write to a legal disclosure string.
    /// </summary>
    [Fact]
    public async Task RenamingTheProcessorAwayFromTheHost_StillSaves()
    {
        await using var factory = await FactoryWithBaseUrlAsync("https://api.anthropic.com");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisProcessor = "Acme Analysis GmbH" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal("Acme Analysis GmbH", dto!.FileAnalysisProcessor);
        Assert.Contains(Field, dto.Warnings.Keys);
    }

    /// <summary>
    /// Normalisation is lower-cased and non-alphanumerics-stripped on both sides, so punctuation and case
    /// in the disclosed name do not produce a spurious advisory on a correctly-configured install.
    /// </summary>
    [Theory]
    [InlineData("ANTHROPIC")]
    [InlineData("Anthropic.")]
    [InlineData("anthro pic")]
    public async Task ProcessorNamePunctuationAndCase_DoNotProduceASpuriousAdvisory(string processor)
    {
        await using var factory = await FactoryWithBaseUrlAsync("https://api.anthropic.com");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisProcessor = processor });

        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.DoesNotContain(Field, dto!.Warnings.Keys);
    }

    /// <summary>
    /// An <em>accepted</em> false positive, pinned so nobody "fixes" it by loosening the match into
    /// something that stops catching the real mistake. A legal name carrying extra words — "Anthropic,
    /// PBC" against <c>api.anthropic.com</c> — does trip it, because the whole normalised name has to
    /// appear in the host. The advisory copy says as much, and it is non-blocking, so the cost of the
    /// false positive is one line of text an administrator can read and disregard.
    /// </summary>
    [Fact]
    public async Task ALegalNameWithExtraWords_TripsTheHeuristic_AndThatIsAcceptedNotABug()
    {
        await using var factory = await FactoryWithBaseUrlAsync("https://api.anthropic.com");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisProcessor = "Anthropic, PBC" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Contains(Field, dto!.Warnings.Keys);
    }

    /// <summary>
    /// A factory whose <c>FileAnalysisBaseUrl</c> row is planted directly (issue #439). Direct rather
    /// than through the API on purpose: half these cases — an empty value, an unparseable one, one
    /// carrying <c>userinfo</c> and a query — are values the write validator refuses, and the advisory
    /// still has to behave when a restore or a hand edit has left one in the row.
    /// </summary>
    private static async Task<OdysseyApiFactory> FactoryWithBaseUrlAsync(string baseUrl)
    {
        var factory = new OdysseyApiFactory(ReadAndSecurity, ActorUserId);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisBaseUrl, baseUrl);
        return factory;
    }
}
