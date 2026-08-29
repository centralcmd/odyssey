using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The degraded-read contract for the file-analysis snapshot (issue #439 §11), where the conservative
/// direction is neither <c>min</c> nor <c>max</c> but <strong>refuse</strong>.
///
/// <para>
/// Two fields and only two behave that way, and the scope of the refusal is the thing most at risk of
/// drifting. Substituting the default <em>model</em> would stamp <c>AnalyzerModel</c> with a model that
/// did not run — the provenance corruption issue #421 Non-Goal 6 named. Substituting the default
/// <em>base URL</em> would transfer a document to <c>api.anthropic.com</c> when the administrator had
/// deliberately pointed the deployment at a gateway. Everything else keeps resolve-to-default-and-flag,
/// so a blank processor row does not take analysis down with it.
/// </para>
///
/// <para>
/// The refusal is structural rather than conditional: <c>Model</c> and <c>BaseUrl</c> are
/// <em>nullable</em>, so <see cref="FileAnalysisTarget"/> cannot be constructed from a degraded snapshot
/// at all. A rule phrased against the single <c>IsDegraded</c> boolean could not say which field
/// degraded, and would land on one of two wrong answers — refuse on any degradation, or invent a
/// value-comparison heuristic that stops firing the moment an admin sets a value back to its default.
/// </para>
/// </summary>
public class FileAnalysisDegradedSettingsTests
{
    private const string DisclosurePath = "/api/file-analysis/disclosure";

    private static readonly string[] AnalysisClaims =
        [PermissionClaims.FileAnalysisRead, PermissionClaims.FileAnalysisCreate, PermissionClaims.AccountsRead];

    /// <summary>
    /// AC 22's type half. The nullability <em>is</em> the guarantee — a substituted default is
    /// unrepresentable rather than merely untested — so it is asserted at the type level, not inferred
    /// from behaviour that a later refactor could preserve by accident.
    /// </summary>
    [Fact]
    public void ModelAndBaseUrl_AreNullableReferenceTypes_OnTheSnapshot()
    {
        var context = new System.Reflection.NullabilityInfoContext();

        foreach (var name in new[] { nameof(FileAnalysisSettings.Model), nameof(FileAnalysisSettings.BaseUrl) })
        {
            var property = typeof(FileAnalysisSettings).GetProperty(name)!;
            Assert.Equal(
                System.Reflection.NullabilityState.Nullable,
                context.Create(property).ReadState);
        }

        // And the contrast: the fields that resolve-to-default are NOT nullable, so the distinction is
        // visible in the type rather than only in a comment.
        var processor = typeof(FileAnalysisSettings).GetProperty(nameof(FileAnalysisSettings.Processor))!;
        Assert.Equal(System.Reflection.NullabilityState.NotNull, context.Create(processor).ReadState);
    }

    /// <summary>
    /// AC 21 — every row absent while the query succeeds is <strong>healthy</strong>. Conflating absent
    /// with degraded would <c>503</c> the claim-free disclosure endpoint on every fresh in-memory and
    /// development database.
    /// </summary>
    [Fact]
    public async Task EveryRowAbsent_ResolvesToTheCompiledDefaults_AndIsNotDegraded()
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        foreach (var key in SystemSettingsKeys.AllKeys)
        {
            await factory.RemoveSystemSettingAsync(key);
        }

        using var scope = factory.Services.CreateScope();
        var settings = await scope.ServiceProvider
            .GetRequiredService<IFileAnalysisSettingsLookup>().GetAsync();

        Assert.False(settings.IsDegraded);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisModel, settings.Model);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisBaseUrl, settings.BaseUrl);
        Assert.Equal(SystemSettingsDefaults.FileAnalysisProcessor, settings.Processor);
    }

    /// <summary>AC 22 — a blank model row yields a null, not the shipped default.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankModelRow_YieldsNull_NeverTheDefault(string stored)
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisModel, stored);

        using var scope = factory.Services.CreateScope();
        var settings = await scope.ServiceProvider
            .GetRequiredService<IFileAnalysisSettingsLookup>().GetAsync();

        Assert.Null(settings.Model);
        Assert.True(settings.IsDegraded);
    }

    /// <summary>
    /// AC 23 — a base URL the write validator would have refused, planted directly. It yields a null
    /// and, crucially, does <strong>not</strong> fall back to <c>https://api.anthropic.com</c>: a
    /// transfer to a processor neither the administrator nor the consenting user chose would be worse
    /// than no transfer.
    /// </summary>
    [Theory]
    [InlineData("http://api.anthropic.com")]
    [InlineData("https://key:secret@gateway.internal")]
    [InlineData("https://gateway.internal/proxy")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task AnUnusableBaseUrlRow_YieldsNull_AndDoesNotFallBackToTheShippedHost(string stored)
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisBaseUrl, stored);

        using var scope = factory.Services.CreateScope();
        var settings = await scope.ServiceProvider
            .GetRequiredService<IFileAnalysisSettingsLookup>().GetAsync();

        Assert.Null(settings.BaseUrl);
        Assert.True(settings.IsDegraded);
    }

    /// <summary>
    /// AC 26 — the stored value never reaches the log line either. A base-URL row planted by a restore
    /// is exactly where a credential would be, and this read path exists to catch that shape, so it
    /// must not be the thing that publishes it.
    /// </summary>
    [Fact]
    public async Task ADegradedReadLogsTheKey_NeverTheStoredValue()
    {
        await using var factory = new LoggingApiFactory(AnalysisClaims);
        await factory.SetSystemSettingAsync(
            SystemSettingsKeys.FileAnalysisBaseUrl, "http://apikey:s3cr3t@leaky-host.internal/secret-path");

        using (var scope = factory.Services.CreateScope())
        {
            var settings = await scope.ServiceProvider
                .GetRequiredService<IFileAnalysisSettingsLookup>().GetAsync();
            Assert.Null(settings.BaseUrl);
        }

        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Message.Contains(SystemSettingsKeys.FileAnalysisBaseUrl, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry =>
            entry.Message.Contains("s3cr3t", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("secret-path", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <strong>AC 24 — the scope of the refusal, and the AC most worth keeping.</strong> A degradation
    /// in any of the other seven fields leaves analysis <em>working</em>, while still making the
    /// claim-free disclosure endpoint answer <c>503</c>. Without this, "refuse on a degraded model or
    /// base URL" can silently widen into "refuse on any degradation" — an unstated availability
    /// regression where a blank processor row takes the whole feature down.
    /// </summary>
    [Theory]
    [InlineData(SystemSettingsKeys.FileAnalysisProcessor, "")]
    [InlineData(SystemSettingsKeys.FileAnalysisMaxTokens, "not-a-number")]
    [InlineData(SystemSettingsKeys.FileAnalysisPrivacyNoticeUrl, "javascript:alert(1)")]
    public async Task ADegradationInAnyOtherField_LeavesTheTargetUsable_ButStill503sTheDisclosure(
        string key, string stored)
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        await factory.EnableFileAnalysisAsync();
        await factory.SetSystemSettingAsync(key, stored);

        using (var scope = factory.Services.CreateScope())
        {
            var settings = await scope.ServiceProvider
                .GetRequiredService<IFileAnalysisSettingsLookup>().GetAsync();

            Assert.True(settings.IsDegraded);
            // The two refusable fields are still usable, so the analysis is NOT refused.
            Assert.NotNull(settings.Model);
            Assert.NotNull(settings.BaseUrl);
        }

        using var client = factory.CreateClient();
        var disclosure = await client.GetAsync(DisclosurePath);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disclosure.StatusCode);
    }

    /// <summary>AC 41 — a degraded snapshot still 503s the disclosure endpoint, unchanged.</summary>
    [Fact]
    public async Task ADegradedModel_Also503sTheDisclosureEndpoint()
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisModel, "");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(DisclosurePath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Captures the API's log output, for the no-leak assertion above.</summary>
    private sealed class LoggingApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions)
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs));
        }
    }
}
