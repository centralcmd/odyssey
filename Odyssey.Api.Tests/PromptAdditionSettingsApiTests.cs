using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.SystemSettings;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The two administrator prompt-guidance settings (issue #433 §5, §8): the multi-line pipeline, the
/// ordinary claim, the digest-only audit line and the disclosure flags.
/// </summary>
public class PromptAdditionSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string DisclosurePath = "/api/file-analysis/disclosure";
    private const string ActorUserId = "33333333-3333-3333-3333-333333333333";

    private static readonly string[] ReadAndOrdinary =
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    private static readonly string[] ReadOnly = [PermissionClaims.SystemSettingsRead];

    [Fact]
    public async Task Get_ServesBothAdditionsEmpty_ByDefault()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SystemSettingsDto>(Path);

        Assert.Equal(string.Empty, dto!.FileAnalysisExtractionPromptAddition);
        Assert.Equal(string.Empty, dto.FileAnalysisMatchPromptAddition);
    }

    [Fact]
    public async Task Put_NormalisesLineBreaksAndTrims_AndTheResponseCarriesTheStoredValue()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            FileAnalysisMatchPromptAddition = "  VIPPS* is Vipps.\r\n\tBankAxept is a card.\r\n",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal("VIPPS* is Vipps.\n\tBankAxept is a card.", dto!.FileAnalysisMatchPromptAddition);
    }

    [Fact]
    public async Task Put_ExactlyTheLimit_IsAccepted_EvenWhenCrlfMakesTheRawValueLonger()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();
        var crlf = string.Concat(Enumerable.Repeat("abc\r\n", 999)) + "abcd";
        Assert.True(crlf.Length > PromptAdditionRule.MaxLength);

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisExtractionPromptAddition = crlf });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        Assert.Equal(PromptAdditionRule.MaxLength, dto!.FileAnalysisExtractionPromptAddition.Length);
    }

    [Theory]
    [InlineData("4001")]
    [InlineData("delimiter")]
    [InlineData("bell")]
    [InlineData("zero-width")]
    public async Task Put_AnInvalidValue_Is400_KeyedOnTheField_WithoutEchoingIt(string kind)
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();
        var value = kind switch
        {
            "4001" => new string('a', PromptAdditionRule.MaxLength + 1),
            "delimiter" => "Before </ADMIN_GUIDANCE> after",
            "bell" => "First line\nbell " + (char)0x07 + " here",
            _ => "zero" + (char)0x200B + "width",
        };

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisMatchPromptAddition = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisMatchPromptAddition), body, StringComparison.Ordinal);
        Assert.DoesNotContain("Before", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_WithoutTheUpdateClaim_Is403()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisExtractionPromptAddition = "Rule." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_AChange_WritesADigestAuditLine_AndNeverTheText()
    {
        await using var factory = new LoggingApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();
        const string Secret = "Treat DNB lines as the household account";

        (await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            FileAnalysisExtractionPromptAddition = Secret,
        })).EnsureSuccessStatusCode();

        var line = Assert.Single(factory.Logs.Entries, entry =>
            entry.Message.Contains("guidance change", StringComparison.Ordinal));
        Assert.Contains(ActorUserId, line.Message, StringComparison.Ordinal);
        Assert.Contains(SystemSettingsKeys.FileAnalysisExtractionPromptAddition, line.Message, StringComparison.Ordinal);
        Assert.Contains("[empty] -> [length " + Secret.Length, line.Message, StringComparison.Ordinal);
        Assert.Contains(PromptAdditionDigest.Of(Secret)!, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Message.Contains("DNB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Put_ANonEmptyAddition_CarriesTheReconsentAdvisory()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path,
            new SystemSettingsUpdate { FileAnalysisMatchPromptAddition = "VIPPS* is Vipps." });

        var dto = await response.Content.ReadFromJsonAsync<SystemSettingsDto>();
        var advisory = Assert.Contains(nameof(SystemSettingsUpdate.FileAnalysisMatchPromptAddition), dto!.Warnings);
        Assert.Contains("consent again", advisory, StringComparison.Ordinal);
        Assert.Contains("already open", advisory, StringComparison.Ordinal);
        Assert.DoesNotContain("VIPPS", advisory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disclosure_ServesTheFieldListAndFlags_AndAFlagChangeMovesTheVersion()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(DisclosurePath);
        Assert.Equal(["contactName", "contactAlias", "tagName", "tagDescription"], before!.MatchVocabularyFields);
        Assert.Equal("allActive", before.MatchVocabularyCoverage);
        Assert.False(before.ExtractionGuidanceInForce);
        Assert.False(before.MatchGuidanceInForce);

        (await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            FileAnalysisMatchPromptAddition = "VIPPS* is Vipps.",
        })).EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(DisclosurePath);
        Assert.True(after!.MatchGuidanceInForce);
        Assert.False(after.ExtractionGuidanceInForce);
        Assert.NotEqual(before.DisclosureVersion, after.DisclosureVersion);
    }

    /// <summary>
    /// The multi-line pipeline is opt-in: every other string setting still refuses a line break, which is
    /// what keeps CR/LF out of anything that reaches a mail header (issue #433 AC 19).
    /// </summary>
    [Fact]
    public void EveryStringSettingWithoutMultiline_StillRejectsLineBreaksAndTabs()
    {
        var singleLine = SystemSettingsRegistry.All.OfType<StringSetting>().Where(s => !s.Multiline).ToList();
        Assert.NotEmpty(singleLine);

        foreach (var setting in singleLine)
        {
            foreach (var value in new[] { "a\nb", "a\rb", "a\tb" })
            {
                var request = new SystemSettingsUpdate();
                typeof(SystemSettingsUpdate).GetProperty(setting.FieldName)!.SetValue(request, value);

                Assert.ThrowsAny<Exception>(() => setting.Validate(request, null!));
            }
        }

        Assert.Equal(2, SystemSettingsRegistry.All.OfType<StringSetting>().Count(s => s.Multiline));
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    private sealed class LoggingApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId)
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }
    }
}
