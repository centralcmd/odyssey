using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The kill switch's defining property (issue #439 §5.1): it is read <strong>live on every call</strong>
/// and never from the 30-second settings snapshot.
///
/// <para>
/// Why that matters enough to test directly. "I turned it off" has to mean the next request is refused,
/// not that the next request within 30 seconds may still transfer a document to a third party — and the
/// snapshot's eviction is instance-local, so on a multi-instance deployment a cached read would not even
/// bound the window to the TTL everywhere.
/// </para>
///
/// <para>
/// <strong>The tests below deliberately bypass the settings API.</strong> Driving the change through
/// <c>PUT /api/system-settings</c> evicts the snapshot synchronously on the writing instance, so such a
/// test would pass even if the switch <em>were</em> served from the cached snapshot — which is exactly
/// the design this rejects. Writing the row directly is the only way to tell the two apart.
/// </para>
/// </summary>
public class FileAnalysisKillSwitchTests
{
    private const string DisclosurePath = "/api/file-analysis/disclosure";

    private static readonly string[] AnalysisClaims =
    [
        PermissionClaims.FileAnalysisRead,
        PermissionClaims.FileAnalysisCreate,
        PermissionClaims.AccountsRead,
    ];

    /// <summary>
    /// AC 16 — turning it on and immediately calling through succeeds, with no wait. The disclosure
    /// endpoint is the observable surface: it serves <c>enabled</c> from the live read.
    /// </summary>
    [Fact]
    public async Task EnablingIt_TakesEffectImmediately_WithNoWaitForTheTtl()
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(DisclosurePath);
        Assert.False(before!.Enabled);

        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisEnabled, "true");

        var after = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(DisclosurePath);
        Assert.True(after!.Enabled);
    }

    /// <summary>
    /// <strong>AC 18 (security finding 6) — the load-bearing one.</strong> With the switch on and the
    /// snapshot warm, the row is set back to <c>false</c> <em>directly in the database</em>, so no cache
    /// eviction happens. The very next call, well inside the 30-second TTL, must refuse.
    ///
    /// <para>
    /// The v1 form of this AC drove the change through the settings API, which evicts synchronously on
    /// the writing instance — so it would have passed against a cached switch, which is the design §5.1
    /// rejects. Bypassing the API is the whole point of the test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DisablingItDirectlyInTheDatabase_BindsOnTheVeryNextCall_WithNoEviction()
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        using var client = factory.CreateClient();

        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisEnabled, "true");

        // Warm the cached snapshot: this read populates FileAnalysisSettingsLookup's 30s entry.
        var enabled = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(DisclosurePath);
        Assert.True(enabled!.Enabled);

        // Direct write — SystemSettingsService never runs, so nothing is evicted and the snapshot from
        // the line above is still warm.
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisEnabled, "false");

        var response = await client.GetAsync($"/api/file-analysis/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(FeatureDisabledException.FeatureCode, problem!.Extensions["code"]!.ToString());

        // And the disclosure endpoint agrees on the next call, again with no eviction in between.
        var afterDto = await client.GetFromJsonAsync<FileAnalysisDisclosureDto>(DisclosurePath);
        Assert.False(afterDto!.Enabled);
    }

    /// <summary>
    /// AC 19 — every call reads. Asserted against the lookup itself (the component that owns the
    /// caching decision) by flipping the stored value between consecutive calls with no eviction: a
    /// cached implementation would return the first answer twice.
    /// </summary>
    [Fact]
    public async Task ConsecutiveReads_EachObserveTheCurrentStoredValue()
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        // Resolving the lookup keeps this on the component under test rather than on an endpoint that
        // happens to call it, so a future caller-side cache cannot make this pass by accident.
        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IFileAnalysisSettingsLookup>();

        foreach (var expected in new[] { true, false, true, false })
        {
            await factory.SetSystemSettingAsync(
                SystemSettingsKeys.FileAnalysisEnabled, expected ? "true" : "false");
            Assert.Equal(expected, await lookup.IsEnabledAsync());
        }
    }

    /// <summary>AC 20 — an unusable stored value fails closed. Never true, never an exception.</summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TRUE-ish")]
    public async Task AnUnparseableStoredValue_ResolvesToDisabled(string stored)
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.FileAnalysisEnabled, stored);
        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IFileAnalysisSettingsLookup>();

        Assert.False(await lookup.IsEnabledAsync());
    }

    /// <summary>
    /// An absent row is HEALTHY, not degraded: it resolves to the compiled default (<c>false</c>), the
    /// same posture every other read here takes. Conflating absent with degraded would break every
    /// database whose rows have not been seeded.
    /// </summary>
    [Fact]
    public async Task AnAbsentRow_ResolvesToTheCompiledDefault_WithoutError()
    {
        await using var factory = new OdysseyApiFactory(AnalysisClaims);
        await factory.RemoveSystemSettingAsync(SystemSettingsKeys.FileAnalysisEnabled);
        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IFileAnalysisSettingsLookup>();

        Assert.False(await lookup.IsEnabledAsync());
    }

    /// <summary>
    /// AC 17 — the switch is <strong>not</strong> a member of <see cref="FileAnalysisSettings"/>, so no
    /// caller can consume a cached copy of it by accident. A type-level assertion, because the guarantee
    /// is structural: the moment it appears on that record, somebody will read it from the snapshot.
    /// </summary>
    [Fact]
    public void TheSwitchIsNotAMemberOfTheCachedSnapshot()
    {
        var members = typeof(FileAnalysisSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("Enabled", members);
        Assert.DoesNotContain("IsEnabled", members);
        Assert.DoesNotContain("FileAnalysisEnabled", members);
    }
}
