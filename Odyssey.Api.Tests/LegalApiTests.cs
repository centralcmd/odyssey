using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The §7 endpoint contracts. These run on the shared <see cref="OdysseyApiFactory"/>, whose
/// <c>TestAuthHandler</c> principal never carries the pending-acceptance claim — so the gate is
/// transparent here and each test is about the endpoint itself. The gate's own behaviour lives in
/// <see cref="LegalComplianceGateTests"/>.
/// </summary>
public class LegalApiTests
{
    private const string ActorUserId = TestAuthHandler.DefaultActorUserId;

    /// <summary>AC 13 — both document reads are reachable without authentication (the registration page needs them).</summary>
    [Fact]
    public async Task TheDocumentReads_AreAnonymous()
    {
        await using var factory = new OdysseyApiFactory([]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/legal/license")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/legal/terms-of-service/current")).StatusCode);
    }

    /// <summary>The served digest is the digest of the served content — a client can verify what it accepted.</summary>
    [Fact]
    public async Task TheLicense_ServesContentThatHashesToTheServedDigest()
    {
        await using var factory = new OdysseyApiFactory([]);
        using var client = factory.CreateClient();

        var license = await client.GetFromJsonAsync<LicenseDocument>("/api/legal/license");

        Assert.NotNull(license);
        Assert.False(string.IsNullOrWhiteSpace(license!.Content));
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(license.Content))),
            license.Sha256);
    }

    /// <summary>AC 17 — "nothing published yet" is a normal 200-with-null response, not a 404 or an error.</summary>
    [Fact]
    public async Task WithNoVersionPublished_CurrentReturns200WithANullBody()
    {
        await using var factory = new OdysseyApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/legal/terms-of-service/current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("null", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task Status_ReportsTheCallersOwnComplianceAndTheCurrentVersionId()
    {
        await using var factory = new OdysseyApiFactory([]);
        var version = await PublishAsync(factory, "Terms v1");
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<LegalComplianceStatus>("/api/legal/status");

        Assert.NotNull(status);
        Assert.False(status!.LicenseCompliant);
        Assert.False(status.TosCompliant);
        Assert.Equal(version.Id, status.CurrentTosVersionId);
    }

    /// <summary>AC 7 — an omitted <c>accepted</c> is a 400, and nothing is written.</summary>
    [Fact]
    public async Task Respond_WithoutAccepted_Returns400AndWritesNoRow()
    {
        await using var factory = new OdysseyApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/legal/respond", new { documentType = "License" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithContextAsync(factory, async context =>
            Assert.False(await context.LicenseAcceptances.AnyAsync(row => row.UserId == ActorUserId)));
    }

    [Fact]
    public async Task Respond_ToTheLicense_RecordsAgainstTheServerResolvedDigest()
    {
        await using var factory = new OdysseyApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/legal/respond", new { documentType = "License", accepted = true });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var status = await client.GetFromJsonAsync<LegalComplianceStatus>("/api/legal/status");
        Assert.True(status!.LicenseCompliant);

        await WithContextAsync(factory, async context =>
        {
            var row = await context.LicenseAcceptances.SingleAsync(entry => entry.UserId == ActorUserId);
            Assert.Equal(LegalTestData.CurrentLicenseHash, row.LicenseHash);
            Assert.True(row.Accepted);
        });
    }

    /// <summary>AC 5 — a stale echoed version is a 409, not a silently mis-attributed acceptance.</summary>
    [Fact]
    public async Task Respond_WithAStaleTosVersionId_Returns409()
    {
        await using var factory = new OdysseyApiFactory([]);
        var superseded = await PublishAsync(factory, "Terms v1");
        await PublishAsync(factory, "Terms v2");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/legal/respond",
            new { documentType = "TermsOfService", accepted = true, tosVersionId = superseded.Id });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Respond_ToTheTermsOfServiceWithoutAVersionId_Returns400()
    {
        await using var factory = new OdysseyApiFactory([]);
        await PublishAsync(factory, "Terms v1");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/legal/respond", new { documentType = "TermsOfService", accepted = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>AC 26 — a tie on <c>PublishedAt</c> resolves to the higher id.</summary>
    [Fact]
    public async Task TwoVersionsSharingAPublishedAt_ResolveCurrentToTheHigherId()
    {
        await using var factory = new OdysseyApiFactory([]);
        var publishedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

        var ids = new List<int>();
        await WithContextAsync(factory, async context =>
        {
            foreach (var content in new[] { "Tied A", "Tied B" })
            {
                var version = new TermsOfServiceVersion { Content = content, PublishedAt = publishedAt };
                context.TermsOfServiceVersions.Add(version);
                await context.SaveChangesAsync();
                ids.Add(version.Id);
            }
        });

        using var client = factory.CreateClient();
        var current = await client.GetFromJsonAsync<TermsOfServiceDocument>("/api/legal/terms-of-service/current");

        Assert.Equal(ids.Max(), current!.Id);
    }

    [Fact]
    public async Task TheVersionEndpoints_RequireUsersManage()
    {
        await using var factory = new OdysseyApiFactory([]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/legal/terms-of-service/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/legal/terms-of-service/versions/1")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/legal/terms-of-service/versions", new { content = "x" })).StatusCode);
    }

    /// <summary>AC 25 — the list is metadata-only; the full text is a separate, on-demand fetch.</summary>
    [Fact]
    public async Task TheVersionList_CarriesMetadataAndNeverContent()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersManage]);
        await factory.SeedActorUserAsync(displayName: "Ada L.");
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(
            "/api/legal/terms-of-service/versions", new { content = "Terms v1 body" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await client.GetAsync("/api/legal/terms-of-service/versions");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = document.RootElement.EnumerateArray().Single();
        Assert.False(row.TryGetProperty("content", out _));
        Assert.Equal(ActorUserId, row.GetProperty("publishedByUserId").GetString());
        Assert.Equal("Ada L.", row.GetProperty("publishedByDisplayName").GetString());

        var detail = await client.GetFromJsonAsync<TermsOfServiceVersionDetail>(
            $"/api/legal/terms-of-service/versions/{row.GetProperty("id").GetInt32()}");
        Assert.Equal("Terms v1 body", detail!.Content);
        Assert.Equal("Ada L.", detail.PublishedByDisplayName);
    }

    /// <summary>AC 10 — publishing is purely additive: prior versions and acceptance rows are untouched.</summary>
    [Fact]
    public async Task Publishing_LeavesPriorVersionsAndAcceptancesIntact()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersManage]);
        var first = await PublishAsync(factory, "Terms v1");
        await WithContextAsync(factory, context => LegalTestData.AcceptAllAsync(context, ActorUserId));
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(
            "/api/legal/terms-of-service/versions", new { content = "Terms v2" });
        created.EnsureSuccessStatusCode();

        await WithContextAsync(factory, async context =>
        {
            Assert.Equal(2, await context.TermsOfServiceVersions.CountAsync());
            var retained = await context.TermsOfServiceVersions.SingleAsync(version => version.Id == first.Id);
            Assert.Equal("Terms v1", retained.Content);
            Assert.True(await context.TermsOfServiceAcceptances
                .AnyAsync(row => row.UserId == ActorUserId && row.TermsOfServiceVersionId == first.Id));
        });

        // ...and the newly published version is what everyone now owes a response to.
        var status = await client.GetFromJsonAsync<LegalComplianceStatus>("/api/legal/status");
        Assert.False(status!.TosCompliant);
    }

    [Fact]
    public async Task PublishingBlankContent_Returns400AndCreatesNoVersion()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersManage]);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/legal/terms-of-service/versions", new { content = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithContextAsync(factory, async context =>
            Assert.False(await context.TermsOfServiceVersions.AnyAsync()));
    }

    /// <summary>AC 14 — the 50,000-character cap is honoured, and one character more is rejected.</summary>
    [Fact]
    public async Task Content_AcceptsTheFullCapAndRejectsOneCharacterMore()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersManage]);
        using var client = factory.CreateClient();

        var atCap = await client.PostAsJsonAsync(
            "/api/legal/terms-of-service/versions",
            new { content = new string('x', LegalLimits.MaxTermsOfServiceContentLength) });
        Assert.Equal(HttpStatusCode.Created, atCap.StatusCode);

        var overCap = await client.PostAsJsonAsync(
            "/api/legal/terms-of-service/versions",
            new { content = new string('x', LegalLimits.MaxTermsOfServiceContentLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, overCap.StatusCode);
    }

    [Fact]
    public async Task AMissingVersion_Returns404()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.UsersManage]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/legal/terms-of-service/versions/4242");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<TermsOfServiceVersion> PublishAsync(OdysseyApiFactory factory, string content)
    {
        TermsOfServiceVersion? published = null;
        await WithContextAsync(factory, async context =>
        {
            published = new TermsOfServiceVersion { Content = content, PublishedAt = DateTime.UtcNow };
            context.TermsOfServiceVersions.Add(published);
            await context.SaveChangesAsync();
        });

        return published!;
    }

    private static async Task WithContextAsync(OdysseyApiFactory factory, Func<OdysseyContext, Task> work)
    {
        using var scope = factory.Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }
}
