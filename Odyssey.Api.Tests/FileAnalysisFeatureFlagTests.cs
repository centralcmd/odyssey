using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// End-to-end (over HTTP, through the real middleware pipeline) coverage of the file-analysis
/// feature-disabled path. The controllers no longer catch <c>FileAnalysisDisabledException</c>; the
/// service throw now bubbles to <c>GlobalExceptionHandler</c>, which depends on the
/// <c>FeatureDisabledException</c> arm being ordered before the generic <c>DomainException</c> arm. A
/// controller-only test would bypass that pipeline, so these tests boot the API and assert the wire
/// contract (503 + <c>application/problem+json</c> + <c>code=feature_disabled</c>) — the regression
/// surface left by the deleted catch blocks. File analysis is disabled by default
/// (<see cref="Odyssey.Core.Finance.FileAnalysisOptions.Enabled"/> defaults to false), and every service
/// method runs its enabled-check first, so arbitrary route ids never reach a lookup.
/// </summary>
public class FileAnalysisFeatureFlagTests
{
    private static readonly string[] AllFileAnalysisClaims =
    [
        PermissionClaims.FileAnalysisRead,
        PermissionClaims.FileAnalysisImport,
        PermissionClaims.FileAnalysisCreate,
        PermissionClaims.AccountsRead,
    ];

    private static async Task AssertFeatureDisabledAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem!.Status);
        Assert.Equal("feature_disabled", problem.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task GetJob_WhenDisabled_IsNotGated()
    {
        // Issue #444: the job read stays open with the switch off, so a client can read that the switch
        // cancelled its queued job. An unknown id is therefore an ordinary 404, not the 503.
        await using var factory = new OdysseyApiFactory(AllFileAnalysisClaims);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/file-analysis/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ImportCandidates_WhenDisabled_BubblesTo503ProblemDetails()
    {
        await using var factory = new OdysseyApiFactory(AllFileAnalysisClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/file-analysis/{Guid.NewGuid()}/import",
            new ImportRequest { Candidates = new List<ImportCandidateRequest>() });

        await AssertFeatureDisabledAsync(response);
    }

    [Fact]
    public async Task MatchCandidates_WhenDisabled_BubblesTo503ProblemDetails()
    {
        await using var factory = new OdysseyApiFactory(AllFileAnalysisClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/file-analysis/{Guid.NewGuid()}/match", content: null);

        await AssertFeatureDisabledAsync(response);
    }

    [Fact]
    public async Task MatchCandidates_WithoutCreateClaim_ReturnsForbidden()
    {
        // Authorization runs before the action, so a caller lacking file-analysis.create is rejected
        // with 403 regardless of the feature flag — read-only claims here.
        await using var factory = new OdysseyApiFactory([PermissionClaims.FileAnalysisRead]);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/file-analysis/{Guid.NewGuid()}/match", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnalyzeAccountFile_WhenDisabled_BubblesTo503ProblemDetails()
    {
        await using var factory = new OdysseyApiFactory(AllFileAnalysisClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/accounts/{Guid.NewGuid()}/files/{Guid.NewGuid()}/analyze", content: null);

        await AssertFeatureDisabledAsync(response);
    }

    [Fact]
    public async Task GetResumableAnalysisJobs_WhenDisabled_IsNotGated()
    {
        // Issue #444: the resumable read stays open with the switch off (a pending review or a failure must
        // still be shown), so it never answers the feature-disabled 503.
        await using var factory = new OdysseyApiFactory(AllFileAnalysisClaims);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/accounts/{Guid.NewGuid()}/files/analysis/resumable");

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
