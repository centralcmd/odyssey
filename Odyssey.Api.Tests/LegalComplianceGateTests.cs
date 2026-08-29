using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.Api.Legal;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// AC 8 — the gate's real logic, exercised against a genuinely non-compliant session. Every other API
/// test authenticates through <c>TestAuthHandler</c>, whose principal never carries the
/// pending-acceptance claim, so the gate is a no-op there and would pass vacuously.
/// </summary>
public class LegalComplianceGateTests
{
    [Fact]
    public async Task ANonCompliantSession_IsRejectedWith451AndTheMachineReadableCode()
    {
        await using var factory = new LegalLoginFactory();
        await factory.CreateUserAsync("gate@example.com");
        using var client = await factory.LoginAsync("gate@example.com");

        var response = await client.PutAsJsonAsync("/api/profile", new { firstName = "A" });

        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(LegalComplianceMiddleware.ProblemCode, problem.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            LegalClaims.License,
            problem.RootElement.GetProperty("pendingDocuments").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>
    /// The allowlist is method-aware on purpose: the client needs the profile read to bootstrap the
    /// interstitial's shell, but the write on the same path must stay gated.
    /// </summary>
    [Fact]
    public async Task TheAllowlistIsMethodAware_ReadPassesWhileTheWriteOnTheSamePathIsGated()
    {
        await using var factory = new LegalLoginFactory();
        await factory.CreateUserAsync("methods@example.com");
        using var client = await factory.LoginAsync("methods@example.com");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(
            HttpStatusCode.UnavailableForLegalReasons,
            (await client.PutAsJsonAsync("/api/profile", new { firstName = "A" })).StatusCode);
    }

    [Theory]
    [InlineData("/api/legal/license")]
    [InlineData("/api/legal/terms-of-service/current")]
    [InlineData("/api/legal/status")]
    [InlineData("/auth/claims")]
    [InlineData("/auth/permissions")]
    public async Task TheFeaturesOwnEndpoints_StayReachableWhileGated(string path)
    {
        await using var factory = new LegalLoginFactory();
        await factory.CreateUserAsync("allowlist@example.com");
        using var client = await factory.LoginAsync("allowlist@example.com");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// AC 9 — the admin ToS-management endpoints are deliberately NOT allowlisted, so a non-compliant
    /// admin is routed through the interstitial before they can publish, rather than being waved through
    /// because they hold <c>users.manage</c>.
    /// </summary>
    [Fact]
    public async Task ANonCompliantAdmin_IsGatedOutOfTheVersionManagementEndpoints()
    {
        await using var factory = new LegalLoginFactory();
        await factory.CreateUserAsync("noncompliantadmin@example.com", RoleDefinitions.AdminId);
        using var client = await factory.LoginAsync("noncompliantadmin@example.com");

        var response = await client.GetAsync("/api/legal/terms-of-service/versions");

        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, response.StatusCode);
    }

    /// <summary>
    /// AC 5 — accepting refreshes the sign-in, so the gate lifts on the very next call rather than at the
    /// next 30-minute revalidation.
    /// </summary>
    [Fact]
    public async Task AcceptingEveryOutstandingDocument_LiftsTheGateImmediately()
    {
        await using var factory = new LegalLoginFactory();
        await factory.CreateUserAsync("accepts@example.com");
        var version = await factory.PublishTermsOfServiceAsync("Terms v1");
        using var client = await factory.LoginAsync("accepts@example.com");

        Assert.Equal(
            HttpStatusCode.UnavailableForLegalReasons,
            (await client.PutAsJsonAsync("/api/profile", new { firstName = "A" })).StatusCode);

        await RespondAsync(client, LegalDocumentType.License, accepted: true);
        await RespondAsync(client, LegalDocumentType.TermsOfService, accepted: true, tosVersionId: version.Id);

        // The profile PUT is incomplete, so a 400 is the expected answer — the point is that it is no
        // longer a 451: the request now reaches the controller at all.
        var afterAccepting = await client.PutAsJsonAsync("/api/profile", new { firstName = "A" });
        Assert.NotEqual(HttpStatusCode.UnavailableForLegalReasons, afterAccepting.StatusCode);
    }

    /// <summary>AC 6 — declining signs the session out; the account itself is untouched.</summary>
    [Fact]
    public async Task Declining_SignsTheSessionOutWithoutLockingTheAccount()
    {
        await using var factory = new LegalLoginFactory();
        var user = await factory.CreateUserAsync("declines@example.com");
        using var client = await factory.LoginAsync("declines@example.com");

        var declined = await RespondAsync(client, LegalDocumentType.License, accepted: false);
        Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/legal/status")).StatusCode);

        await factory.WithContextAsync(async context =>
        {
            var stored = await context.Users.FindAsync(user.Id);
            Assert.NotNull(stored);
            Assert.Null(stored!.LockoutEnd);
        });

        // Logging back in works — a decline is not an account lockout.
        using var secondSession = await factory.LoginAsync("declines@example.com");
        Assert.Equal(HttpStatusCode.OK, (await secondSession.GetAsync("/api/legal/status")).StatusCode);
    }

    /// <summary>An anonymous request carries no claim and must pass through untouched (AC 13).</summary>
    [Fact]
    public async Task AnAnonymousRequest_IsNeverGated()
    {
        await using var factory = new LegalLoginFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/legal/license")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    private static Task<HttpResponseMessage> RespondAsync(
        HttpClient client, LegalDocumentType documentType, bool accepted, int? tosVersionId = null) =>
        client.PostAsJsonAsync("/api/legal/respond", new
        {
            documentType = documentType.ToString(),
            accepted,
            tosVersionId,
        });
}
