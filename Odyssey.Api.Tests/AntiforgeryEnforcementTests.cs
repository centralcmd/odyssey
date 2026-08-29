using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Locks the CSRF antiforgery contract that <see cref="OdysseyApiFactory"/>'s "Testing" environment
/// is deliberately exempt from — so the shared suite (which only ever runs in "Testing") can't prove
/// enforcement. These tests boot the same in-memory API in a non-"Testing" environment, which
/// activates the RequireAntiforgeryToken metadata + UseAntiforgery middleware on the controllers.
/// </summary>
public class AntiforgeryEnforcementTests
{
    private const string ContactsPath = "/api/contacts";
    private const string TokenPath = "/api/antiforgery/token";
    private const string AntiforgeryRejection = "Invalid or missing antiforgery token.";

    private static readonly string[] ContactWrite =
        [PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate];

    private sealed record TokenResponse(string Token);

    private static NewContact NewContact(string name) =>
        new() { Type = ContactType.Organization, Archived = false, OrganizationDetails = new() { LegalName = name } };

    /// <summary>
    /// <see cref="OdysseyApiFactory"/> rehosted in a non-"Testing" environment, which flips on the
    /// antiforgery enforcement the base "Testing" environment is exempt from. Because the in-memory
    /// database is otherwise selected by that same "Testing" check, and the base factory's
    /// <c>UseInMemoryDatabase=true</c> config is applied after <c>AddDatabases</c> already ran, we
    /// set it as an environment variable in the constructor — early enough for the host builder's
    /// <c>AddEnvironmentVariables</c> to pick it up. (Benign and never reset: every suite wants the
    /// in-memory database; the "Testing" suites simply ignore the flag.)
    /// </summary>
    private sealed class EnforcedFactory : OdysseyApiFactory
    {
        public EnforcedFactory(IReadOnlyCollection<string>? permissions) : base(permissions) =>
            Environment.SetEnvironmentVariable("UseInMemoryDatabase", "true");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("AntiforgeryEnforced");
        }
    }

    [Fact]
    public async Task TokenEndpoint_ReturnsRequestToken()
    {
        await using var factory = new EnforcedFactory(ContactWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(TokenPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Token));
    }

    [Fact]
    public async Task UnsafeRequest_WithoutToken_Returns400()
    {
        await using var factory = new EnforcedFactory(ContactWrite);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(ContactsPath, NewContact("no-token"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The rejection is an RFC 7807 problem body, matching every other error path.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(StatusCodes.Status400BadRequest, problem?.Status);
        Assert.Equal(AntiforgeryRejection, problem?.Detail);
    }

    [Fact]
    public async Task UnsafeRequest_WithToken_IsAccepted()
    {
        await using var factory = new EnforcedFactory(ContactWrite);
        using var client = factory.CreateClient();

        // The GET both returns the request token and sets the paired secret cookie; the client's
        // cookie container carries that cookie onto the POST.
        var token = (await (await client.GetAsync(TokenPath))
            .Content.ReadFromJsonAsync<TokenResponse>())!.Token;

        var request = new HttpRequestMessage(HttpMethod.Post, ContactsPath)
        {
            Content = JsonContent.Create(NewContact("with-token")),
        };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SafeRequest_WithoutToken_IsUnaffected()
    {
        await using var factory = new EnforcedFactory(ContactWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ContactsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
