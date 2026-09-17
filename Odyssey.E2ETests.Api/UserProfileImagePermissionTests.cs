using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Odyssey.Dtos.Application;
using Odyssey.TestData;
using Odyssey.TestData.Catalog;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// AC 26 — the widest decision in issue #94, asserted against the <b>actually seeded</b> role claims
/// rather than a minted principal: a <b>Guest</b>, signed in for real, can read another user's profile
/// picture.
/// </summary>
/// <remarks>
/// <para>
/// This is the one criterion that genuinely needs this tier. <c>TestAuthHandler</c> mints an arbitrary
/// permission set, so the fast tiers can prove the endpoint honours the claim but not that any real
/// role <i>holds</i> it — the role→claim mapping is compiled into <c>RolePermissions</c> and guarded by
/// <c>AuthorizationPolicyTests</c>, so "revoke the claim from a role" cannot be committed as a fixture
/// at all.
/// </para>
/// <para>
/// And because this tier is <c>[SkippableFact]</c> — it reports success having never run without a live
/// stack — it must not be the only place any <i>other</i> guarantee lives. The claim matrix, the header
/// set (CORP included) and the rate-limit split are all asserted on <c>Odyssey.Api.Tests</c> instead.
/// </para>
/// <para>
/// What the claim buys is not narrower access — its effective reach equals "any authenticated caller".
/// It is a <b>revocation lever that exists before release</b>: claim values are baked into the auth
/// cookie at sign-in, so retrofitting one later de-authorizes live sessions. Revoking it from Guest is
/// the supported way to narrow who sees colleagues' pictures.
/// </para>
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class UserProfileImagePermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserFor(string role) => DemoUsers.All.First(user => user.Role == role);

    /// <summary>
    /// AC 26. A Guest — the narrowest seeded role — can fetch another user's picture. The picture
    /// identifies a person the caller already meets by name on shared records, and gating it on
    /// <c>users.read</c> instead would make it visible to administrators only, which is not the feature.
    /// </summary>
    [SkippableFact]
    public async Task A_guest_can_read_another_users_profile_picture()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // An Owner sets their own picture — the only way a picture exists at all, since there is no
        // administrator write path by design.
        var owner = UserFor("Owner");
        var ownerClient = await fixture.CreateAuthenticatedClientAsync(owner.Email, owner.Password);

        var subjectId = await OwnUserIdAsync(ownerClient);
        var version = await SetPictureAsync(ownerClient);

        try
        {
            var guest = UserFor("Guest");
            var guestClient = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);

            var read = await guestClient.GetAsync($"/api/profile-images/{subjectId}?v={version}");

            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("image/jpeg", read.Content.Headers.ContentType?.MediaType);
            Assert.Equal("same-site", read.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        }
        finally
        {
            await ownerClient.DeleteAsync("/api/profile/image");
        }
    }

    /// <summary>
    /// The other half of §10.1, over real HTTP: the write endpoints take no user id at all, so a Guest
    /// writing reaches only their <b>own</b> row — there is no cross-user write path to attempt.
    /// </summary>
    [SkippableFact]
    public async Task A_guests_own_write_reaches_only_their_own_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var guest = UserFor("Guest");
        var guestClient = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);

        var owner = UserFor("Owner");
        var ownerClient = await fixture.CreateAuthenticatedClientAsync(owner.Email, owner.Password);
        var ownerId = await OwnUserIdAsync(ownerClient);

        try
        {
            // Claim-free and self-scoped, like PUT /api/profile — a Guest may set their own picture.
            var version = await SetPictureAsync(guestClient);
            Assert.NotEqual(Guid.Empty, version);

            var guestId = await OwnUserIdAsync(guestClient);
            Assert.NotEqual(ownerId, guestId);

            // The Owner's row is untouched: there was no id for the Guest's write to name.
            var ownerProfile = await ownerClient.GetFromJsonAsync<ProfileDto>("/api/profile");
            Assert.Null(ownerProfile!.ProfileImageVersion);

            var guestProfile = await guestClient.GetFromJsonAsync<ProfileDto>("/api/profile");
            Assert.Equal(version, guestProfile!.ProfileImageVersion);
        }
        finally
        {
            await guestClient.DeleteAsync("/api/profile/image");
        }
    }

    private static async Task<Guid> SetPictureAsync(HttpClient client)
    {
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(ContactImageFixtures.BaselineJpeg());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(part, "file", "picture.jpg");

        var response = await client.PostAsync("/api/profile/image", content);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ProfileImageVersionDto>();
        return body!.ImageVersion;
    }

    /// <summary>
    /// The caller's own id, read from the claims endpoint the client itself uses — there is no
    /// "who am I" on the profile resource, and the seeded ids are not fixed.
    /// </summary>
    private static async Task<string> OwnUserIdAsync(HttpClient client)
    {
        var claims = await client.GetFromJsonAsync<List<ClaimRow>>("/auth/claims");
        var row = claims!.First(claim =>
            claim.Type.EndsWith("nameidentifier", StringComparison.OrdinalIgnoreCase)
            || claim.Type == "sub");

        return row.Value;
    }

    private sealed record ClaimRow(string Type, string Value);
}
