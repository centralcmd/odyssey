using System.Net;
using System.Net.Http.Json;
using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The contact-method label scope over real HTTP → real login → real cookie → real policy
/// (issue #47 §16.7): every write path in §7 is gated on the same <c>contacts.*</c> claim it always
/// was, and the new <c>422</c> is reached <b>only after</b> authorization.
/// </summary>
/// <remarks>
/// That ordering is what keeps the rejection from becoming an existence-and-type oracle: the message
/// names the parent contact's <c>Type</c> and the valid label set for it, so a principal that cannot
/// write must never see it. The injected-claim API tier cannot prove this — it never issues a real
/// cookie — which is why it is pinned here as well.
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class ContactMethodLabelScopeTests(ApiStackFixture fixture)
{
    /// <summary>
    /// One deliberately out-of-scope body per kind, aimed at a contact id that does not exist. A role
    /// holding the claim therefore lands on a <c>404</c> (or the <c>422</c>, had the contact existed) —
    /// never a write against the shared seeded database.
    /// </summary>
    private static readonly (string Path, string Claim, object Body)[] CreateEndpoints =
    [
        ($"/api/contacts/{Guid.NewGuid()}/addresses", PermissionClaims.ContactsCreate,
            new NewAddress { Label = AddressLabel.Home, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO" }),
        ($"/api/contacts/{Guid.NewGuid()}/emails", PermissionClaims.ContactsCreate,
            new NewEmailAddress { Label = EmailLabel.Home, Value = "post@example.com" }),
        ($"/api/contacts/{Guid.NewGuid()}/phones", PermissionClaims.ContactsCreate,
            new NewPhoneNumber { Label = PhoneLabel.Home, Value = "+47 22 00 00 00" }),
    ];

    private static readonly (string Path, string Claim, object Body)[] UpdateEndpoints =
    [
        ($"/api/contacts/{Guid.NewGuid()}/addresses/{Guid.NewGuid()}", PermissionClaims.ContactsUpdate,
            new NewAddress { Label = AddressLabel.Home, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO" }),
        ($"/api/contacts/{Guid.NewGuid()}/emails/{Guid.NewGuid()}", PermissionClaims.ContactsUpdate,
            new NewEmailAddress { Label = EmailLabel.Home, Value = "post@example.com" }),
        ($"/api/contacts/{Guid.NewGuid()}/phones/{Guid.NewGuid()}", PermissionClaims.ContactsUpdate,
            new NewPhoneNumber { Label = PhoneLabel.Home, Value = "+47 22 00 00 00" }),
    ];

    [SkippableFact]
    public async Task Each_role_clears_or_is_denied_the_contact_method_write_routes_per_its_real_claims()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        foreach (var user in DemoUsers.All)
        {
            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);
            var claims = ClaimsForRole(user.Role);

            foreach (var (path, claim, body) in CreateEndpoints)
                AssertGated(await client.PostAsJsonAsync(path, body), user.Role, "POST", path, claim, claims);

            foreach (var (path, claim, body) in UpdateEndpoints)
                AssertGated(await client.PutAsJsonAsync(path, body), user.Role, "PUT", path, claim, claims);
        }
    }

    /// <summary>
    /// The scope rejection is never what an unauthorized caller sees. Asserted explicitly rather than
    /// implied by the matrix: a <c>422</c> here would mean the check ran — and disclosed the parent's
    /// type — for a principal that may not write.
    /// </summary>
    [SkippableFact]
    public async Task An_unauthorized_caller_never_reaches_the_scope_rejection()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        foreach (var user in DemoUsers.All)
        {
            var claims = ClaimsForRole(user.Role);
            if (claims.Contains(PermissionClaims.ContactsCreate))
                continue;

            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);

            foreach (var (path, _, body) in CreateEndpoints)
            {
                var response = await client.PostAsJsonAsync(path, body);
                Assert.NotEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            }
        }
    }

    [SkippableFact]
    public async Task Anonymous_contact_method_writes_are_challenged_with_401()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        using var client = fixture.CreateAnonymousClient();

        foreach (var (path, _, body) in CreateEndpoints)
        {
            var response = await client.PostAsJsonAsync(path, body);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Anonymous POST {path}: expected 401, got {(int)response.StatusCode} {response.StatusCode}.");
        }
    }

    private static void AssertGated(
        HttpResponseMessage response, string role, string verb, string path, string claim, string[] claims)
    {
        if (claims.Contains(claim))
        {
            // Holds the claim: must clear authorization. The ids are random, so the handler's own answer
            // is a 404 — only that it is neither 401 nor 403 matters here.
            Assert.True(
                response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                $"{role} {verb} {path} (has '{claim}'): expected to pass authorization, " +
                $"got {(int)response.StatusCode} {response.StatusCode}.");
        }
        else
        {
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{role} {verb} {path} (lacks '{claim}'): expected 403 Forbidden, " +
                $"got {(int)response.StatusCode} {response.StatusCode}.");
        }
    }

    private static string[] ClaimsForRole(string role) => role switch
    {
        RoleDefinitions.Admin => RolePermissions.AdminClaims,
        RoleDefinitions.Owner => RolePermissions.OwnerClaims,
        RoleDefinitions.User => RolePermissions.UserClaims,
        RoleDefinitions.Guest => RolePermissions.GuestClaims,
        _ => throw new InvalidOperationException($"Unknown demo role '{role}'."),
    };
}
