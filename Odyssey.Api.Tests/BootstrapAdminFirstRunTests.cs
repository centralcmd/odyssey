using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.Api.Legal;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The seeded bootstrap administrator's first run (issue #290) against the real cookie pipeline.
/// </summary>
/// <remarks>
/// This verifies the <i>reuse</i> of issue #406's machinery, not the machinery itself — that is
/// <see cref="PasswordChangeRequiredGateTests"/>. What is new here is the account's shape: unlike an
/// admin-reset target, a freshly seeded administrator owes <b>every</b> first-run gate at once — no
/// password of its own, no License response, no profile — so the question is whether it can still get
/// through. It models exactly what <c>BootstrapAdminSeeder</c> produces: confirmed, enabled, in Admin,
/// flagged, and carrying neither acceptance rows nor a profile.
/// </remarks>
public class BootstrapAdminFirstRunTests
{
    private const string Email = "seeded-admin@example.com";
    private const string NewPassword = "Chosen!Properly2Now";

    [Fact]
    public async Task TheApp_IsRefused_ButTheProfileReadThatDrivesTheGateIsNot()
    {
        await using var factory = new PasswordGateFactory();
        using var client = await SeededAdminSessionAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/accounts")).StatusCode);

        // The one round trip the client already makes on boot. ProfileService returns an empty,
        // incomplete DTO carrying the flag rather than a 404 when no UserProfile row exists — which is
        // what makes the gate fire for an account that has never had a profile.
        var profile = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.True(profile!.MustChangePassword);
    }

    /// <summary>
    /// The no-deadlock property. The one-time password is the only credential this account has, so if
    /// the legal gate could 451 the change-password call the account would be unrecoverable — and it is
    /// the only administrator, which is precisely what makes that unrecoverable for the instance too.
    /// </summary>
    [Fact]
    public async Task ThePasswordChange_Succeeds_EvenThoughTheLicenseIsAlsoOutstanding()
    {
        await using var factory = new PasswordGateFactory();
        using var client = await SeededAdminSessionAsync(factory);

        var response = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = NewPassword,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AfterTheChange_TheFlagIsClear_AndTheSameSessionSurvives_MeetingTheLegalGateNext()
    {
        await using var factory = new PasswordGateFactory();
        var userId = await SeedAdminAsync(factory);
        using var client = await factory.LoginAsync(Email);

        await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = NewPassword,
        });

        Assert.False(await factory.MustChangePasswordAsync(userId));

        // Same cookie, no re-authentication: the password gate has stepped aside and the next gate in
        // the chain answers instead — the server-side half of the client's password → legal →
        // onboarding order.
        var afterwards = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, afterwards.StatusCode);
        using var problem = JsonDocument.Parse(await afterwards.Content.ReadAsStringAsync());
        Assert.Equal(LegalComplianceMiddleware.ProblemCode, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpClient> SeededAdminSessionAsync(PasswordGateFactory factory)
    {
        await SeedAdminAsync(factory);
        return await factory.LoginAsync(Email);
    }

    private static async Task<string> SeedAdminAsync(PasswordGateFactory factory)
    {
        var user = await factory.CreateUserAsync(Email, RoleDefinitions.AdminId);
        await factory.SetMustChangePasswordAsync(user.Id);
        // The fixture accepts the legal documents for convenience; a seeded administrator has responded
        // to nothing, so take that back.
        await factory.RevokeLegalAcceptanceAsync(user.Id);
        return user.Id;
    }
}
